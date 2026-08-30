using System.Globalization;
using System.Text;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Export;

public sealed record ObsidianOptions
{
    public required string VaultPath { get; init; }

    /// <summary>Folder inside the vault. Everything written lives under this one place.</summary>
    public string Folder { get; init; } = "Görüşmeler";

    /// <summary>Write the full transcript, not just the summary and the ledger.</summary>
    public bool IncludeTranscript { get; init; } = true;

    /// <summary>Link to the audio file so it can be played from inside the vault.</summary>
    public bool LinkAudio { get; init; } = true;
}

/// <summary>
/// Writes calls into an Obsidian vault as plain markdown.
///
/// Obsidian is the local option, which is why it is the default: the vault is a folder of text
/// files on the same disk as everything else, so exporting does not send a single word anywhere.
/// The files stay readable and greppable even if Obsidian is never installed, which matters for
/// an archive meant to outlast whatever tool is fashionable.
///
/// One note per call, one page per contact. The contact page is regenerated from the database
/// rather than appended to, so it always reflects the current state of the ledger — a
/// commitment the user dismissed disappears from it instead of lingering as a stale accusation.
/// </summary>
public sealed class ObsidianExporter(Repository repository, ObsidianOptions options)
{
    public string CallsDirectory => Path.Combine(options.VaultPath, options.Folder);

    public string ContactsDirectory => Path.Combine(options.VaultPath, options.Folder, "Kişiler");

    /// <summary>
    /// Writes the note for one call and refreshes its contact page.
    /// Returns the path of the call note.
    /// </summary>
    public string ExportCall(long callId)
    {
        var call = repository.GetCall(callId)
            ?? throw new InvalidOperationException($"Call {callId} not found.");

        var contact = call.ContactId is { } id ? repository.GetContact(id) : null;
        var segments = repository.GetSegments(callId);
        var summary = repository.GetSummary(callId);
        var flags = contact is null ? [] : repository.GetFlags(contact.Id);

        Directory.CreateDirectory(CallsDirectory);

        var path = Path.Combine(CallsDirectory, $"{FileNameFor(call, contact)}.md");
        WriteAtomically(path, RenderCall(call, contact, segments, summary, flags.Where(f => f.CallId == callId).ToList()));

        if (contact is not null) ExportContact(contact.Id);

        return path;
    }

    /// <summary>Regenerates a contact page from what the database currently holds.</summary>
    public string ExportContact(long contactId)
    {
        var contact = repository.GetContact(contactId)
            ?? throw new InvalidOperationException($"Contact {contactId} not found.");

        Directory.CreateDirectory(ContactsDirectory);

        var path = Path.Combine(ContactsDirectory, $"{Sanitise(contact.Name)}.md");
        WriteAtomically(path, RenderContact(contact));

        return path;
    }

    private string RenderCall(
        Call call,
        Contact? contact,
        IReadOnlyList<Segment> segments,
        CallSummary? summary,
        IReadOnlyList<Flag> flags)
    {
        var builder = new StringBuilder();
        var local = call.StartedAt.ToLocalTime();

        builder.AppendLine("---");
        builder.AppendLine($"tarih: {local:yyyy-MM-dd}");
        builder.AppendLine($"saat: {local:HH:mm}");
        builder.AppendLine($"kisi: {(contact is null ? "\"\"" : Quote(contact.Name))}");
        builder.AppendLine($"uygulama: {call.App}");
        builder.AppendLine($"sure: {Duration(call.Duration)}");
        builder.AppendLine($"tur: {(call.Kind == CallKind.Group ? "grup" : "birebir")}");
        builder.AppendLine("tags:");
        builder.AppendLine("  - görüşme");
        if (contact is not null) builder.AppendLine($"  - {Tag(contact.Name)}");
        builder.AppendLine("---");
        builder.AppendLine();

        builder.AppendLine($"# {(contact?.Name ?? "Bilinmeyen kişi")} — {local:d MMMM yyyy HH:mm}");
        builder.AppendLine();

        if (contact is not null) builder.AppendLine($"Kişi: [[{contact.Name}]]");
        builder.AppendLine($"Süre: {Duration(call.Duration)}");

        if (options.LinkAudio && call.MicPath is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Ses kayıtları");
            builder.AppendLine($"- Ben: `{call.MicPath}`");
            if (call.FarPath is not null) builder.AppendLine($"- Karşı taraf: `{call.FarPath}`");
        }

        if (call.LikelyNoHeadphones)
        {
            builder.AppendLine();
            builder.AppendLine(
                "> [!warning] Kulaklık kullanılmamış görünüyor. Karşı tarafın sesi mikrofona da " +
                "karıştığı için konuşmacı ayrımı bu kayıtta güvenilir olmayabilir.");
        }

        if (call.Kind == CallKind.Group)
        {
            builder.AppendLine();
            builder.AppendLine(
                "> [!info] Grup araması. Karşı taraftaki kişiler tek bir ses akışında karıştığı " +
                "için yazıya dökülmedi; yalnızca ses kaydı saklandı.");
        }

        if (summary is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Özet");
            builder.AppendLine();
            builder.AppendLine(summary.Summary.Trim());
        }

        if (flags.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Dikkat çekenler");
            builder.AppendLine();

            foreach (var flag in flags) AppendFlag(builder, flag);
        }

        if (options.IncludeTranscript && segments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Konuşma");
            builder.AppendLine();

            foreach (var segment in segments)
            {
                var speaker = segment.IsMe ? "**Ben**" : $"**{contact?.Name ?? "Karşı taraf"}**";
                var mark = segment.LowConfidence ? " ⚠️" : "";
                builder.AppendLine($"`{Timestamp(segment.StartMs)}` {speaker}: {segment.Text.Trim()}{mark}");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static void AppendFlag(StringBuilder builder, Flag flag)
    {
        // Callouts rather than plain text so the ledger stays scannable in the vault.
        var kind = flag.Kind switch
        {
            FlagKind.OverdueCommitment => "warning",
            FlagKind.MovedDeadline => "warning",
            FlagKind.ChangedAmount => "warning",
            FlagKind.Contradiction => "danger",
            FlagKind.ScamPattern => "danger",
            _ => "note",
        };

        builder.AppendLine($"> [!{kind}] {flag.Summary}");
        builder.AppendLine($"> `{Timestamp(flag.QuoteStartMs)}` \"{flag.Quote.Trim()}\"");

        if (flag.CounterQuote is not null)
            builder.AppendLine($"> Önceki: `{Timestamp(flag.CounterQuoteStartMs ?? 0)}` \"{flag.CounterQuote.Trim()}\"");

        // Heuristics are labelled wherever they appear, so a keyword match is never mistaken
        // for something the model concluded.
        if (flag.IsHeuristic)
            builder.AppendLine("> *(anahtar kelime eşleşmesi — kesin bir tespit değildir)*");

        if (flag.LowConfidence)
            builder.AppendLine("> *(ses net değil, bu kayıt şüpheli olabilir)*");

        builder.AppendLine();
    }

    private string RenderContact(Contact contact)
    {
        var calls = repository.ListCalls(contact.Id);
        var commitments = repository.GetOpenCommitments(contact.Id);
        var flags = repository.GetFlags(contact.Id);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var builder = new StringBuilder();

        builder.AppendLine("---");
        builder.AppendLine($"ad: {Quote(contact.Name)}");
        builder.AppendLine($"uygulama: {contact.App}");
        builder.AppendLine($"gorusme_sayisi: {calls.Count}");
        if (contact.LastCallAt is { } last) builder.AppendLine($"son_gorusme: {last.ToLocalTime():yyyy-MM-dd}");
        builder.AppendLine("tags:");
        builder.AppendLine("  - kişi");
        builder.AppendLine("---");
        builder.AppendLine();

        builder.AppendLine($"# {contact.Name}");
        builder.AppendLine();
        builder.AppendLine($"{calls.Count} görüşme" +
                           (contact.LastCallAt is { } l ? $", son görüşme {l.ToLocalTime():d MMMM yyyy}" : ""));

        if (commitments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Açık sözler");
            builder.AppendLine();

            foreach (var commitment in commitments)
            {
                var who = commitment.ByMe ? "Ben" : contact.Name;
                var due = commitment.DeadlineDate is { } date
                    ? $" — {date:d MMMM yyyy}"
                    : commitment.DeadlineRaw is { } raw ? $" — {raw}" : "";

                var overdue = commitment.IsOverdue(today) ? " ⚠️ **süresi geçti**" : "";
                var conditional = commitment.IsConditional ? " *(koşullu)*" : "";

                builder.AppendLine($"- **{who}**: {commitment.Obligation}{due}{overdue}{conditional}");
                builder.AppendLine($"  - \"{commitment.Quote.Trim()}\"");
            }
        }

        if (flags.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Dikkat çekenler");
            builder.AppendLine();

            foreach (var flag in flags.Take(30)) AppendFlag(builder, flag);
        }

        builder.AppendLine();
        builder.AppendLine("## Görüşmeler");
        builder.AppendLine();

        foreach (var call in calls)
        {
            var local = call.StartedAt.ToLocalTime();
            builder.AppendLine($"- [[{FileNameFor(call, contact)}|{local:d MMMM yyyy HH:mm}]] — {Duration(call.Duration)}");
        }

        return builder.ToString();
    }

    private static string FileNameFor(Call call, Contact? contact)
    {
        var local = call.StartedAt.ToLocalTime();
        var who = Sanitise(contact?.Name ?? "Bilinmeyen");
        return $"{local:yyyy-MM-dd HHmm} {who}";
    }

    /// <summary>
    /// Writes via a temporary file and a rename.
    ///
    /// The vault is very likely open in Obsidian while this runs. A rename is atomic, so the
    /// editor either sees the old file or the complete new one, never a half-written note.
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    /// <summary>Strips characters Windows forbids in filenames, keeping Turkish letters intact.</summary>
    private static string Sanitise(string name)
    {
        var builder = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var ch in name.Trim())
            builder.Append(invalid.Contains(ch) ? '-' : ch);

        var cleaned = builder.ToString().Trim().Trim('.');
        return cleaned.Length == 0 ? "Bilinmeyen" : cleaned;
    }

    private static string Tag(string name) =>
        Text.TurkishText.NormalizeForSearch(name).Replace(' ', '-');

    private static string Quote(string value) => $"\"{value.Replace("\"", "'")}\"";

    private static string Duration(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Timestamp(int milliseconds)
    {
        var total = milliseconds / 1000;
        return $"{total / 60:00}:{total % 60:00}";
    }
}
