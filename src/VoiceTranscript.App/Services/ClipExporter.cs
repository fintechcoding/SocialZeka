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

    /// <summary>Suggested file name for a flag's clip. Carries no contact name — see AudioClip.</summary>
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
