using System.Globalization;
using System.Reflection;

namespace VoiceTranscript.Core.Update;

/// <summary>
/// A semantic version, only as much of one as this application needs.
///
/// Written rather than taken from a package because the whole of it is a hundred lines and the
/// decision it drives — "is the thing on GitHub newer than the thing running" — is one nobody
/// should have to read a dependency's source to verify. Getting it wrong has two failure modes and
/// both are bad in a way the user cannot diagnose: an application that never offers an update it
/// should, or one that offers the same update forever.
///
/// Comparison follows semver 2.0 where it matters and ignores the parts that do not arise here:
///
///   <b>A pre-release is older than its own final release.</b> 1.2.0-rc.1 comes before 1.2.0. This
///   is the rule people get backwards, and getting it backwards means everybody on a release
///   candidate is told they are up to date forever.
///
///   <b>Numeric identifiers compare as numbers.</b> rc.10 is newer than rc.9, which string
///   comparison gets wrong the moment a tenth candidate exists.
///
///   <b>Build metadata is not part of identity.</b> The +sha of a build says which commit it came
///   from, not which release it is.
/// </summary>
public sealed record AppVersion : IComparable<AppVersion>
{
    private AppVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The part after the hyphen, or null for a final release.</summary>
    public string? Prerelease { get; }

    /// <summary>True for a build that was never released — the default a local checkout carries.</summary>
    public bool IsDevelopmentBuild => Major == 0 && Minor == 0 && Patch == 0;

    /// <summary>
    /// Reads a version from a tag, an assembly attribute, or anything shaped like one.
    ///
    /// Tolerant of a leading "v" because that is how tags are written, and of build metadata
    /// because that is what the .NET SDK appends to the informational version. Never throws:
    /// an unreadable version has to degrade to "older than everything" rather than take down a
    /// background update check.
    /// </summary>
    public static AppVersion Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unknown;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // Build metadata identifies the build, not the release. Two builds of 1.2.0 from different
        // commits are the same version, and treating them otherwise would offer an update from a
        // release to itself.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var hyphen = value.IndexOf('-');
        var prerelease = hyphen >= 0 ? value[(hyphen + 1)..] : null;
        if (hyphen >= 0) value = value[..hyphen];

        var parts = value.Split('.');
        if (parts.Length == 0) return Unknown;

        if (!TryNumber(parts, 0, out var major)) return Unknown;
        TryNumber(parts, 1, out var minor);
        TryNumber(parts, 2, out var patch);

        return new AppVersion(major, minor, patch, string.IsNullOrWhiteSpace(prerelease) ? null : prerelease);
    }

    /// <summary>What an unreadable version becomes: older than every real one, and never offered.</summary>
    public static AppVersion Unknown { get; } = new(0, 0, 0, "unknown");

    private static bool TryNumber(string[] parts, int index, out int value)
    {
        value = 0;

        return index < parts.Length
               && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The version of the running application.
    ///
    /// Read from the informational version rather than the assembly version, because that is the
    /// one that carries a pre-release suffix — the assembly version is four numbers and cannot
    /// express "1.2.0-rc.1", so a release candidate would be indistinguishable from its own final
    /// release and would never be updated off.
    /// </summary>
    public static AppVersion OfRunningApplication()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Parse(informational ?? assembly.GetName().Version?.ToString());
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;

        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // 1.2.0-rc.1 is older than 1.2.0. Backwards here means everybody on a release candidate is
        // told they are current, permanently.
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;

            var numericA = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numericB = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var nb);

            // rc.10 is newer than rc.9. Compared as text it is older, and that only shows up once
            // there have been ten candidates — long after anybody is still watching for it.
            if (numericA && numericB)
            {
                if (na != nb) return na.CompareTo(nb);
                continue;
            }

            // Semver: numeric identifiers rank below alphanumeric ones.
            if (numericA) return -1;
            if (numericB) return 1;

            var text = string.CompareOrdinal(a[i], b[i]);
            if (text != 0) return text < 0 ? -1 : 1;
        }

        return 0;
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
