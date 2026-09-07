namespace VoiceTranscript.Core.Text;

/// <summary>
/// The ruler for dates: four ways of writing a day, and no fifth.
///
/// One conversation row used to be written four different ways on four screens — "4 Eylül,
/// 14:32" on the home screen, "4 Eylül 2026, 14:32" in the contact window, "4 Eyl 2026, 14:32"
/// on the processing page, "4 Eylül 2026" in search — so a reader comparing two screens could
/// not tell whether they were looking at the same call. The shapes were not chosen; they
/// accumulated, one format string at a time, and each one looked reasonable where it was
/// written.
///
/// Four names, because four is what the interface actually needs: a day in a list that already
/// says which year it is in, a day that has to stand on its own, a moment, and a month heading.
/// Anything a screen wants that is not one of these four is a question about the ruler, not
/// about the screen — which is the point of having a ruler.
///
/// The formats themselves are unchanged from the ones already most used, so adopting a name
/// here never moves a character on screen. Current culture on purpose: these are read by a
/// person, and the same call renders "4 Eylül" or "4 September" depending on the language the
/// interface is speaking.
/// </summary>
public static class Dates
{
    private const string DayFormat = "d MMM";
    private const string DayAndYearFormat = "d MMMM yyyy";
    private const string MomentFormat = "d MMMM, HH:mm";
    private const string MonthFormat = "MMMM yyyy";

    /// <summary>"4 Eyl" — a day inside something that already says the year.</summary>
    public static string Day(DateOnly day) => Day(day.ToDateTime(TimeOnly.MinValue));

    /// <inheritdoc cref="Day(DateOnly)"/>
    public static string Day(DateTime at) => at.ToString(DayFormat);

    /// <inheritdoc cref="Day(DateOnly)"/>
    public static string Day(DateTimeOffset at) => at.ToString(DayFormat);

    /// <summary>"4 Eylül 2026" — a day that has to be unambiguous on its own.</summary>
    public static string DayAndYear(DateOnly day) => DayAndYear(day.ToDateTime(TimeOnly.MinValue));

    /// <inheritdoc cref="DayAndYear(DateOnly)"/>
    public static string DayAndYear(DateTime at) => at.ToString(DayAndYearFormat);

    /// <inheritdoc cref="DayAndYear(DateOnly)"/>
    public static string DayAndYear(DateTimeOffset at) => at.ToString(DayAndYearFormat);

    /// <summary>"4 Eylül, 14:32" — when something happened, to the minute.</summary>
    public static string Moment(DateTime at) => at.ToString(MomentFormat);

    /// <inheritdoc cref="Moment(DateTime)"/>
    public static string Moment(DateTimeOffset at) => at.ToString(MomentFormat);

    /// <summary>"Eylül 2026" — a month, as a heading over the days inside it.</summary>
    public static string Month(DateOnly day) => Month(day.ToDateTime(TimeOnly.MinValue));

    /// <inheritdoc cref="Month(DateOnly)"/>
    public static string Month(DateTime at) => at.ToString(MonthFormat);

    /// <inheritdoc cref="Month(DateOnly)"/>
    public static string Month(DateTimeOffset at) => at.ToString(MonthFormat);

    /// <summary>The four formats themselves, for the test that holds every screen to them.</summary>
    public static IReadOnlyList<string> Formats { get; } =
        [DayFormat, DayAndYearFormat, MomentFormat, MonthFormat];
}
