using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Where a row's own words came from. The product's three grounds, and nothing else.
///
/// Stated on the row rather than inferred from the screen it is on, because a screen is allowed
/// to put all three side by side — what it may never do is put them inside one card
/// (PLAN-SOSYALZEKA §3.1). A reader who cannot tell a counted fact from a model's impression
/// from a sentence they typed themselves has no way to weigh any of the three.
/// </summary>
public enum Ground
{
    /// <summary>A quote and a millisecond: deterministic, or verified against the transcript.</summary>
    Evidence,

    /// <summary>The model's reading. Subjective, signed, dated, and switchable off.</summary>
    ModelOpinion,

    /// <summary>The user's own writing. The machine never puts anything here.</summary>
    UserWriting,
}

/// <summary>
/// Every glyph the interface draws in front of a row, in one place.
///
/// The same four concepts were drawn with emoji literals in three different view models, which
/// is how a reminder ends up wearing the clock face that a missed deadline already wears — the
/// two were written months apart by people reading different files. A badge is a word in the
/// interface's vocabulary, and a vocabulary kept in three copies is three vocabularies.
///
/// The rule this file exists to make testable is deliberately blunt: the App layer contains no
/// badge glyph outside this class. That is a source scan, so it stays true whether or not
/// anybody remembers the rule.
/// </summary>
public static class RowBadges
{
    /// <summary>⌂ evidence, ≈ the model's reading, ✎ the user's own hand.</summary>
    public static string Of(Ground ground) => ground switch
    {
        Ground.Evidence => "⌂",
        Ground.ModelOpinion => "≈",
        _ => "✎",
    };

    /// <summary>What one to-do line came from.</summary>
    public static string Todo(TodoEntryKind kind) => kind switch
    {
        TodoEntryKind.Action => "💡",
        TodoEntryKind.Reminder => "⏰",
        _ => "☐",
    };

    /// <summary>What one day's entry is, in the month view.</summary>
    public static string Calendar(CalendarEntryKind kind) => kind switch
    {
        CalendarEntryKind.Reminder => "🔔",
        CalendarEntryKind.OwnPromise or CalendarEntryKind.TheirPromise => "🤝",
        CalendarEntryKind.Birthday => "🎂",

        // Hollow on purpose: a suggestion the user never confirmed must read weaker than
        // anything they wrote themselves.
        _ => "○",
    };

    /// <summary>What kind of finding the ledger is showing.</summary>
    public static string Flag(FlagKind kind) => kind switch
    {
        FlagKind.OverdueCommitment => "⏰",
        FlagKind.MovedDeadline => "📅",
        FlagKind.ChangedAmount => "₺",
        FlagKind.Contradiction => "⚠",
        FlagKind.EvadedQuestion => "?",
        FlagKind.PressureTactic => "!",
        FlagKind.ScamPattern => "⚑",
        FlagKind.TimelineMismatch => "🕐",
        FlagKind.VagueShift => "≈",
        _ => "•",
    };

    /// <summary>
    /// Every glyph this class can hand out, for the scan that keeps them here.
    ///
    /// Built by asking the three badge sets rather than by listing the characters again — a
    /// hand-kept second list is the very thing this class replaces.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. Enum.GetValues<Ground>().Select(Of),
        .. Enum.GetValues<TodoEntryKind>().Select(Todo),
        .. Enum.GetValues<CalendarEntryKind>().Select(Calendar),
        .. Enum.GetValues<FlagKind>().Select(Flag),
    ];
}
