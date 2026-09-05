using System.Globalization;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Core.Update;

/// <summary>What was published, once it has been checked over.</summary>
/// <param name="Version">The version the tag names.</param>
/// <param name="Notes">The release body, shown to the user before they agree to anything.</param>
/// <param name="InstallerName">File name of the installer, used to find its checksum.</param>
/// <param name="InstallerUrl">Where to download it.</param>
/// <param name="ChecksumUrl">Where to download the SHA256SUMS file that covers it.</param>
/// <param name="SizeBytes">Installer size, so the disk can be checked before downloading.</param>
public sealed record Release(
    AppVersion Version,
    string Notes,
    string InstallerName,
    string InstallerUrl,
    string ChecksumUrl,
    long SizeBytes);

/// <summary>Why a published release was not offered.</summary>
public enum ReleaseRejection
{
    None = 0,
    NotJson,
    Draft,
    Prerelease,
    NoInstaller,
    SeveralInstallers,
    InstallerFromAnotherTag,
    NoChecksumFile,
    UnreadableVersion,
}

/// <summary>
/// Reads a GitHub release and decides whether it is something this application may install.
///
/// Pure, and separated from the code that does the downloading for one reason: this is where an
/// update goes wrong in a way nobody notices until it has already run an executable. Every rule
/// below refuses rather than guesses, because the failure of a guess here is not a missing feature
/// — it is the application fetching and running the wrong binary on the user's machine.
///
/// The rules, and what each one is for:
///
///   <b>Drafts and pre-releases are refused</b> even when handed over directly. GitHub's
///   <c>/releases/latest</c> already skips them, but the same payload arrives from other calls and
///   a release candidate must never be pushed to somebody who did not ask for one.
///
///   <b>Exactly one installer.</b> Two means the release is malformed or was built twice, and
///   picking either is a coin flip about which binary the user ends up running.
///
///   <b>The installer's name must carry the tag's version.</b> An asset left over from a previous
///   release — re-uploaded, or attached to the wrong tag — would otherwise be offered as the new
///   version and would install something older, which then offers itself as an update again.
///
///   <b>A checksum file must be present.</b> Not because it stops a compromised repository — it
///   does not, and this is stated plainly in the documentation — but because a truncated download
///   that installs is far more likely than an attacker, and there is no way to tell the two apart
///   afterwards.
/// </summary>
public static class ReleaseAssets
{
    /// <summary>The file the checksums live in. The name follows the <c>sha256sum</c> convention.</summary>
    public const string ChecksumFileName = "SHA256SUMS";

    /// <summary>
    /// Builds the installer's file name for a version.
    ///
    /// One function, used by the client to recognise the asset and asserted against the installer
    /// script by a test — because if the two ever disagree, updates stop working silently and the
    /// only symptom is an application that says it is up to date forever.
    /// </summary>
    public static string InstallerNameFor(AppVersion version) =>
        $"SocialZeka-Setup-{version}-win-x64.exe";

    /// <summary>
    /// Reads a release payload, or says why it will not be offered.
    /// </summary>
    /// <param name="json">The body of a GitHub releases API response.</param>
    public static (Release? Release, ReleaseRejection Rejection) Read(string? json)
    {
        JsonNode? node;

        try
        {
            node = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            node = null;
        }

        if (node is null) return (null, ReleaseRejection.NotJson);

        if (Flag(node, "draft")) return (null, ReleaseRejection.Draft);
        if (Flag(node, "prerelease")) return (null, ReleaseRejection.Prerelease);

        var version = AppVersion.Parse(Text(node, "tag_name"));

        // A tag that does not parse, or one that names the development version, is not a release
        // anybody should be sent to.
        if (version.IsDevelopmentBuild) return (null, ReleaseRejection.UnreadableVersion);

        var assets = node["assets"] as JsonArray ?? [];

        var installers = assets
            .Where(a => a is not null)
            .Select(a => (Name: Text(a, "name") ?? "", Url: Text(a, "browser_download_url") ?? "",
                          Size: Number(a, "size")))
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && a.Name.StartsWith("SocialZeka-Setup-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (installers.Count == 0) return (null, ReleaseRejection.NoInstaller);
        if (installers.Count > 1) return (null, ReleaseRejection.SeveralInstallers);

        var installer = installers[0];

        // The asset has to belong to this tag. An installer carried over from an earlier release
        // would install an older version, which would then offer itself as an update — a loop the
        // user cannot break from inside the application.
        if (!string.Equals(installer.Name, InstallerNameFor(version), StringComparison.OrdinalIgnoreCase))
            return (null, ReleaseRejection.InstallerFromAnotherTag);

        var checksum = assets
            .Where(a => a is not null)
            .Select(a => (Name: Text(a, "name") ?? "", Url: Text(a, "browser_download_url") ?? ""))
            .FirstOrDefault(a => string.Equals(a.Name, ChecksumFileName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(checksum.Url)) return (null, ReleaseRejection.NoChecksumFile);

        return (new Release(
            version,
            Text(node, "body") ?? "",
            installer.Name,
            installer.Url,
            checksum.Url,
            installer.Size), ReleaseRejection.None);
    }

    /// <summary>
    /// Finds one file's checksum in a <c>sha256sum</c> listing.
    ///
    /// Matched on the whole file name rather than a prefix. "SocialZeka-Setup-1.2.0-win-x64.exe"
    /// is a prefix of nothing here today, but a release that ever carried both that and a
    /// ".exe.sig" — or a 1.2.0 beside a 1.2.0-rc.1 — would let a prefix match return the wrong
    /// line, and a checksum that verifies the wrong file is worse than no checksum at all.
    /// </summary>
    public static string? ChecksumFor(string? listing, string fileName)
    {
        if (string.IsNullOrWhiteSpace(listing)) return null;

        foreach (var line in listing.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            // "<hash>  <name>" or "<hash> *<name>" — two spaces for text mode, space-star for binary.
            var split = trimmed.IndexOf(' ');
            if (split <= 0) continue;

            var hash = trimmed[..split];
            var name = trimmed[(split + 1)..].TrimStart(' ', '*');

            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;

            return LooksLikeSha256(hash) ? hash.ToLowerInvariant() : null;
        }

        return null;
    }

    private static bool LooksLikeSha256(string value)
    {
        if (value.Length != 64) return false;

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        return true;
    }

    /// <summary>What to tell the user when a release was refused. Null when it was not.</summary>
    public static string? Explain(ReleaseRejection rejection) => rejection switch
    {
        ReleaseRejection.None => null,
        ReleaseRejection.NotJson => "Güncelleme bilgisi okunamadı.",
        ReleaseRejection.Draft => "Yayın hâlâ taslak durumda.",
        ReleaseRejection.Prerelease => "Yayın bir ön sürüm; kararlı sürümler bekleniyor.",
        ReleaseRejection.NoInstaller => "Yayında kurulum dosyası yok.",
        ReleaseRejection.SeveralInstallers => "Yayında birden fazla kurulum dosyası var; hangisi olduğu belirsiz.",
        ReleaseRejection.InstallerFromAnotherTag =>
            "Yayındaki kurulum dosyası bu sürüme ait değil.",
        ReleaseRejection.NoChecksumFile =>
            $"Yayında {ChecksumFileName} yok; indirilenin bütünlüğü doğrulanamaz.",
        ReleaseRejection.UnreadableVersion => "Yayının sürüm numarası okunamadı.",
        _ => "Güncelleme doğrulanamadı.",
    };

    private static string? Text(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return null;

        try
        {
            return value.GetValue<string>();
        }
        catch (Exception)
        {
            return value.ToString();
        }
    }

    private static bool Flag(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return false;

        try
        {
            return value.GetValue<bool>();
        }
        catch (Exception)
        {
            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }
    }

    private static long Number(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return 0;

        try
        {
            return value.GetValue<long>();
        }
        catch (Exception)
        {
            return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }
    }
}
