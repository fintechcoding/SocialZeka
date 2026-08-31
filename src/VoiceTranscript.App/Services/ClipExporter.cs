using System.IO;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Turns a ledger entry into a small audio file of the moment it rests on.
///
/// Every flag already carries a verbatim quote and a timestamp so the user can check it. This is
/// the step past checking: a file they can play to the person it is about.
///
/// That is worth building rather than saying "seek to 12:04" because a claim about a conversation
/// is worth what its evidence is worth. "You said eighteen thousand" is an argument. Eight
/// seconds of somebody saying it is not.
///
/// Cut from the mixed recording, never from one side. A promise with the question that prompted
/// it removed is a different promise, and a clip that quietly drops half the exchange is the kind
/// of evidence this product exists not to produce.
/// </summary>
public sealed class ClipExporter(Repository repository)
{
    /// <summary>What happened, in terms the interface can say out loud.</summary>
    public sealed record Result(bool Ok, string Message, string? Path = null);

    /// <summary>
    /// Writes the moment behind one flag to <paramref name="destination"/>.
    ///
    /// The end of the quote is taken from the transcript segment containing it rather than
    /// guessed from the number of words. A guess is wrong in both directions and both are bad: a
    /// clip cut short stops mid-sentence, and one cut long carries in whatever was said next,
    /// which is somebody else's conversation attached to a quote about them.
    /// </summary>
    public Result ExportFlag(long callId, int quoteStartMs, string destination)
    {
        var call = repository.GetCall(callId);

        if (call is null)
            return new Result(false, "Bu kayda ait görüşme bulunamadı.");

        if (call.MicPath is null && call.FarPath is null)
            return new Result(false, "Bu görüşmenin ses kaydı silinmiş.");

        var source = ConversationMix.Ensure(call.MicPath, call.FarPath);

        if (source is null)
            return new Result(false, "Görüşmenin iki tarafı birleştirilemedi.");

        var start = TimeSpan.FromMilliseconds(quoteStartMs);
        var end = EndOfQuote(callId, quoteStartMs);

        if (!AudioClip.Extract(source, start, end, destination))
            return new Result(false, "Ses kesiti çıkarılamadı — kayıt eksik ya da bozuk olabilir.");

        var seconds = (end - start + 2 * AudioClip.DefaultPadding).TotalSeconds;

        return new Result(true,
            $"{Math.Round(seconds)} saniyelik kesit kaydedildi: {Path.GetFileName(destination)}",
            destination);
    }

    /// <summary>
    /// Writes one stretch of a conversation — a line and the replies that followed it.
    ///
    /// The difference from <see cref="ExportFlag"/> is what is being kept. A flag's clip is
    /// evidence for a single sentence somebody said. This is an exchange: a question and its
    /// answer, which is the unit people actually argue about. "You said you would" means nothing
    /// without what was asked, and a clip of the answer alone invites precisely the reply that it
    /// was taken out of context.
    ///
    /// A transcript is written beside the audio, carrying the date, the person and the words. The
    /// audio is the proof; the text is what makes it findable a year later, when the file is one
    /// of thirty in a folder and nobody remembers which conversation it came from.
    /// </summary>
    public Result ExportExchange(
        long callId, int fromMs, int toMs, string destination, string? contactName)
    {
        var call = repository.GetCall(callId);

        if (call is null)
            return new Result(false, "Bu kayda ait görüşme bulunamadı.");

        if (call.MicPath is null && call.FarPath is null)
            return new Result(false, "Bu görüşmenin ses kaydı silinmiş.");

        var source = ConversationMix.Ensure(call.MicPath, call.FarPath);

        if (source is null)
            return new Result(false, "Görüşmenin iki tarafı birleştirilemedi.");

        var start = TimeSpan.FromMilliseconds(fromMs);
        var end = TimeSpan.FromMilliseconds(Math.Max(toMs, fromMs + 1000));

        if (!AudioClip.Extract(source, start, end, destination))
            return new Result(false, "Ses kesiti çıkarılamadı — kayıt eksik ya da bozuk olabilir.");

        var written = WriteTranscript(callId, call.StartedAt, contactName, fromMs, toMs, destination);

        var seconds = Math.Round((end - start + 2 * AudioClip.DefaultPadding).TotalSeconds);

        return new Result(true,
            $"{seconds} saniyelik kesit kaydedildi: {Path.GetFileName(destination)}"
            + (written ? $"{Environment.NewLine}Konuşma metni de yanına yazıldı." : ""),
            destination);
    }

    /// <summary>
    /// Writes the words of the exported stretch next to the audio, as plain text.
    ///
    /// Best effort, and deliberately not fatal: the audio is the thing that was asked for, and
    /// refusing to export a clip because a text file could not be written beside it would be the
    /// tail wagging the dog.
    /// </summary>
    private bool WriteTranscript(
        long callId, DateTimeOffset startedAt, string? contactName,
        int fromMs, int toMs, string destination)
    {
        try
        {
            var lines = repository
                .GetSegments(callId)
                .Where(s => s.EndMs > fromMs && s.StartMs < toMs)
                .OrderBy(s => s.StartMs)
                .Select(s =>
                    $"[{TimeSpan.FromMilliseconds(s.StartMs):mm\\:ss}] "
                    + $"{(s.IsMe ? "Ben" : contactName ?? "Karşı taraf")}: {s.Text}")
                .ToList();

            if (lines.Count == 0) return false;

            var header = new[]
            {
                $"{contactName ?? "İsimsiz"} ile görüşme",
                $"{startedAt.ToLocalTime():d MMMM yyyy dddd, HH:mm}",
                $"Kesit: {TimeSpan.FromMilliseconds(fromMs):mm\\:ss} – {TimeSpan.FromMilliseconds(toMs):mm\\:ss}",
                new string('-', 40),
                "",
            };

            File.WriteAllLines(
                Path.ChangeExtension(destination, ".txt"),
                header.Concat(lines),
                System.Text.Encoding.UTF8);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Suggested file name for a clip taken at a moment. Carries no contact name.</summary>
    public string NameFor(long callId, int quoteStartMs)
    {
        var call = repository.GetCall(callId);

        return AudioClip.SuggestedName(
            call?.StartedAt ?? DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(quoteStartMs));
    }

    /// <summary>
    /// Where the quoted sentence finishes, from the transcript.
    ///
    /// Falls back to four seconds when no segment covers the timestamp — which happens when a
    /// transcript has been re-run since the flag was raised, and is better answered with a short
    /// clip than with nothing.
    /// </summary>
    private TimeSpan EndOfQuote(long callId, int quoteStartMs)
    {
        var covering = repository
            .GetSegments(callId)
            .FirstOrDefault(s => s.StartMs <= quoteStartMs && s.EndMs > quoteStartMs);

        return covering is not null
            ? TimeSpan.FromMilliseconds(covering.EndMs)
            : TimeSpan.FromMilliseconds(quoteStartMs + 4000);
    }
}
