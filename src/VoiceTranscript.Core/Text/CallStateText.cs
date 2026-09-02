using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Text;

/// <summary>
/// One vocabulary for what has happened to a call.
///
/// The same state used to be worded differently on every screen — a failed call was "Başarısız"
/// on the first screen, "işlenemedi" in the contact window's chip, "İşlenemedi" in its list and
/// "Yapılamadı" on the processing page — so the user learned four words for one fact and could
/// not tell whether they were the same fact. Every screen now asks here.
/// </summary>
public static class CallStateText
{
    /// <summary>
    /// The short form for a row. Null for the ordinary finished state: most rows are finished,
    /// and a column that says "Hazır" on every line teaches the eye to skip it — and then to
    /// skip "İşlenemedi" too.
    /// </summary>
    public static string? Short(Call call) => call.Kind == CallKind.Group
        ? "Grup — sadece ses"
        : call.State switch
        {
            ProcessingState.Recorded or ProcessingState.Queued => "Sırada",
            ProcessingState.Transcribing => "Yazıya dökülüyor",
            ProcessingState.Transcribed => "Çözümlenmedi",
            ProcessingState.Analysing => "Çözümleniyor",
            ProcessingState.Analysed => null,
            ProcessingState.Failed => "İşlenemedi",
            ProcessingState.Skipped => Skipped(call.FailureReason),
            _ => null,
        };

    /// <summary>The same, with the finished state named — for filters, counters and columns.</summary>
    public static string Label(Call call) => Short(call) ?? "Hazır";

    /// <summary>
    /// "Atlandı" covered three different things: a recording too short to keep (its audio is
    /// already gone), a group call kept as audio only, and a call the user stopped mid-way
    /// (fully resumable). The reason column tells them apart; the word should too.
    /// </summary>
    public static string Skipped(string? reason)
    {
        if (reason is null) return "Atlandı";
        if (reason.StartsWith("Çok kısa", StringComparison.CurrentCultureIgnoreCase)) return "Çok kısa — ses silindi";
        if (reason.Contains("durdurdu", StringComparison.CurrentCultureIgnoreCase)) return "Durduruldu — yeniden işlenebilir";
        return "Atlandı";
    }

    /// <summary>The system brush a state's text is drawn with: red for failed, accent while working, faint while waiting.</summary>
    public static string BrushKey(ProcessingState state) => state switch
    {
        ProcessingState.Failed => "SystemFillColorCriticalBrush",
        ProcessingState.Transcribing or ProcessingState.Analysing => "AccentTextFillColorPrimaryBrush",
        ProcessingState.Recorded or ProcessingState.Queued => "TextFillColorTertiaryBrush",
        ProcessingState.Transcribed => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    };
}

/// <summary>
/// How the two people on a call are named.
///
/// The user was "Ben" in transcripts and search hits, "Sen"/"SEN" in the ledger and the player,
/// and the other party was "Karşı taraf" in one place and the contact's name in the next. The
/// application addresses the user as "sen" everywhere else, so the user is "Sen" here too, and
/// the other party is their name whenever it is known.
/// </summary>
public static class SpeakerText
{
    public const string Self = "Sen";

    public static string Other(string? contactName) =>
        string.IsNullOrWhiteSpace(contactName) ? "karşı taraf" : contactName;

    public static string For(bool isMe, string? contactName) => isMe ? Self : Other(contactName);
}
