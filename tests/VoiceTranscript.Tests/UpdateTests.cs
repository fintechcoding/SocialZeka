using VoiceTranscript.Core.Update;

namespace VoiceTranscript.Tests;

/// <summary>
/// Comparing versions.
///
/// Two failure modes, both invisible to the user and both permanent until somebody notices:
/// an application that never offers an update it should, or one that offers the same update
/// forever. Neither produces an error message, so neither gets reported — they get lived with.
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1", 1, 0, 0)]
    public void ReadsTheNumbers(string text, int major, int minor, int patch)
    {
        var version = AppVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    /// <summary>
    /// Build metadata says which commit a build came from, not which release it is. Treating it as
    /// part of identity would let a release offer an update to itself.
    /// </summary>
    [Fact]
    public void BuildMetadataIsNotPartOfTheVersion()
    {
        Assert.Equal(AppVersion.Parse("1.2.0"), AppVersion.Parse("1.2.0+a3763dd"));
        Assert.Null(AppVersion.Parse("1.2.0+a3763dd").Prerelease);
    }

    /// <summary>
    /// The rule people get backwards. Backwards means everybody on a release candidate is told
    /// they are up to date, permanently.
    /// </summary>
    [Fact]
    public void APrereleaseIsOlderThanItsOwnFinalRelease()
    {
        Assert.True(AppVersion.Parse("1.2.0-rc.1") < AppVersion.Parse("1.2.0"));
        Assert.True(AppVersion.Parse("1.2.0") > AppVersion.Parse("1.2.0-rc.1"));
    }

    /// <summary>
    /// Compared as text, rc.10 is older than rc.9 — and that only shows up once there have been
    /// ten candidates, long after anybody is still watching for it.
    /// </summary>
    [Fact]
    public void NumericPrereleaseIdentifiersCompareAsNumbers()
    {
        Assert.True(AppVersion.Parse("1.2.0-rc.9") < AppVersion.Parse("1.2.0-rc.10"));
        Assert.True(AppVersion.Parse("1.2.0-rc.2") < AppVersion.Parse("1.2.0-rc.11"));
    }

    [Fact]
    public void OrdinaryOrderingHolds()
    {
        Assert.True(AppVersion.Parse("1.2.0") < AppVersion.Parse("1.2.1"));
        Assert.True(AppVersion.Parse("1.2.0") < AppVersion.Parse("1.3.0"));
        Assert.True(AppVersion.Parse("1.9.0") < AppVersion.Parse("2.0.0"));
        Assert.Equal(0, AppVersion.Parse("1.2.0").CompareTo(AppVersion.Parse("1.2.0")));
    }

    /// <summary>
    /// A local build sorts below every release, so a developer running from the checkout is never
    /// offered "an update" to a version they are already ahead of.
    /// </summary>
    [Fact]
    public void ADevelopmentBuildIsOlderThanEveryRelease()
    {
        var dev = AppVersion.Parse("0.0.0-dev");

        Assert.True(dev.IsDevelopmentBuild);
        Assert.True(dev < AppVersion.Parse("0.0.1"));
        Assert.True(dev < AppVersion.Parse("1.0.0"));
    }

    /// <summary>
    /// An unreadable version must not take down a background check. It becomes something older
    /// than everything, which is refused elsewhere rather than thrown about here.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("banana.split")]
    public void AnUnreadableVersionDegradesRatherThanThrows(string? text)
    {
        var version = AppVersion.Parse(text);

        Assert.True(version.IsDevelopmentBuild);
        Assert.True(version < AppVersion.Parse("0.0.1"));
    }

    [Fact]
    public void RoundTripsThroughItsOwnText()
    {
        Assert.Equal("1.2.3", AppVersion.Parse("1.2.3").ToString());
        Assert.Equal("1.2.3-rc.1", AppVersion.Parse("v1.2.3-rc.1+abc").ToString());
    }
}

/// <summary>
/// Deciding whether a published release is something this application may install.
///
/// Every rule here refuses rather than guesses, and the reason is narrow: the failure of a guess
/// is not a missing feature, it is the application downloading and running the wrong executable on
/// somebody's machine. A refusal costs a delay.
/// </summary>
public class ReleaseAssetsTests
{
    private static string Payload(
        string tag = "v1.2.0",
        bool draft = false,
        bool prerelease = false,
        string? installerName = null,
        bool withChecksums = true,
        string? extraAsset = null)
    {
        var installer = installerName ?? $"VoiceTranscript-Setup-{tag[1..]}-win-x64.exe";

        var assets = new List<string>
        {
            $$"""{"name":"{{installer}}","browser_download_url":"https://example/{{installer}}","size":68000000}""",
        };

        if (withChecksums)
            assets.Add("""{"name":"SHA256SUMS","browser_download_url":"https://example/SHA256SUMS","size":120}""");

        if (extraAsset is not null)
            assets.Add($$"""{"name":"{{extraAsset}}","browser_download_url":"https://example/{{extraAsset}}","size":1}""");

        return $$"""
        {
          "tag_name": "{{tag}}",
          "body": "Yenilikler",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{string.Join(",", assets)}}]
        }
        """;
    }

    [Fact]
    public void AWellFormedReleaseIsAccepted()
    {
        var (release, rejection) = ReleaseAssets.Read(Payload());

        Assert.Equal(ReleaseRejection.None, rejection);
        Assert.NotNull(release);
        Assert.Equal("1.2.0", release!.Version.ToString());
        Assert.Equal("VoiceTranscript-Setup-1.2.0-win-x64.exe", release.InstallerName);
        Assert.Contains("Yenilikler", release.Notes);
        Assert.Equal(68000000, release.SizeBytes);
    }

    /// <summary>
    /// GitHub's /releases/latest already skips these, but the same payload arrives from other calls
    /// and a release candidate must never be pushed to somebody who did not ask for one.
    /// </summary>
    [Fact]
    public void ADraftOrPrereleaseIsRefusedEvenWhenHandedOverDirectly()
    {
        Assert.Equal(ReleaseRejection.Draft, ReleaseAssets.Read(Payload(draft: true)).Rejection);
        Assert.Equal(ReleaseRejection.Prerelease, ReleaseAssets.Read(Payload(prerelease: true)).Rejection);
    }

    [Fact]
    public void AReleaseWithNoInstallerIsRefused()
    {
        var json = """
        {"tag_name":"v1.2.0","draft":false,"prerelease":false,
         "assets":[{"name":"SHA256SUMS","browser_download_url":"https://example/SHA256SUMS"}]}
        """;

        Assert.Equal(ReleaseRejection.NoInstaller, ReleaseAssets.Read(json).Rejection);
    }

    /// <summary>Two installers means picking one is a coin flip about which binary gets run.</summary>
    [Fact]
    public void AReleaseCarryingTwoInstallersIsRefused()
    {
        var json = ReleaseAssets.Read(
            Payload(extraAsset: "VoiceTranscript-Setup-1.2.0-win-arm64.exe"));

        Assert.Equal(ReleaseRejection.SeveralInstallers, json.Rejection);
    }

    /// <summary>
    /// An asset left over from an earlier release — re-uploaded, or attached to the wrong tag —
    /// would install something older, which would then offer itself as an update again: a loop the
    /// user cannot break from inside the application.
    /// </summary>
    [Fact]
    public void AnInstallerBuiltFromADifferentTagIsRefused()
    {
        var json = Payload(tag: "v1.2.0", installerName: "VoiceTranscript-Setup-1.1.0-win-x64.exe");

        Assert.Equal(ReleaseRejection.InstallerFromAnotherTag, ReleaseAssets.Read(json).Rejection);
    }

    [Fact]
    public void AReleaseWithoutChecksumsIsRefused()
        => Assert.Equal(ReleaseRejection.NoChecksumFile,
            ReleaseAssets.Read(Payload(withChecksums: false)).Rejection);

    [Fact]
    public void RubbishIsRefusedRatherThanThrown()
    {
        Assert.Equal(ReleaseRejection.NotJson, ReleaseAssets.Read("not json").Rejection);
        Assert.Equal(ReleaseRejection.NotJson, ReleaseAssets.Read("").Rejection);
        Assert.Equal(ReleaseRejection.NotJson, ReleaseAssets.Read(null).Rejection);
    }

    [Fact]
    public void EveryRefusalHasSomethingToTellTheUser()
    {
        foreach (var rejection in Enum.GetValues<ReleaseRejection>())
        {
            var explanation = ReleaseAssets.Explain(rejection);

            if (rejection == ReleaseRejection.None) Assert.Null(explanation);
            else Assert.False(string.IsNullOrWhiteSpace(explanation), $"{rejection} açıklamasız");
        }
    }

    // ---- checksums ----------------------------------------------------------

    [Fact]
    public void FindsTheChecksumForAFile()
    {
        var listing =
            "0000000000000000000000000000000000000000000000000000000000000000 *other.exe\n"
            + "e69a26e17e831314be156d9a352c804b04af91ae0141a9d89a5b326b945e0044 *VoiceTranscript-Setup-1.2.0-win-x64.exe\n";

        Assert.Equal(
            "e69a26e17e831314be156d9a352c804b04af91ae0141a9d89a5b326b945e0044",
            ReleaseAssets.ChecksumFor(listing, "VoiceTranscript-Setup-1.2.0-win-x64.exe"));
    }

    /// <summary>Both sha256sum shapes: two spaces for text mode, space-star for binary.</summary>
    [Fact]
    public void BothChecksumLineShapesAreUnderstood()
    {
        var hash = new string('a', 64);

        Assert.Equal(hash, ReleaseAssets.ChecksumFor($"{hash}  file.exe", "file.exe"));
        Assert.Equal(hash, ReleaseAssets.ChecksumFor($"{hash} *file.exe", "file.exe"));
    }

    /// <summary>
    /// Matched on the whole name, never a prefix. A checksum that verifies the wrong file is worse
    /// than no checksum at all — it turns "unverified" into "verified, incorrectly".
    /// </summary>
    [Fact]
    public void TheChecksumIsFoundByWholeFileNameNotByPrefix()
    {
        var listing = $"{new string('b', 64)} *VoiceTranscript-Setup-1.2.0-win-x64.exe.sig";

        Assert.Null(ReleaseAssets.ChecksumFor(listing, "VoiceTranscript-Setup-1.2.0-win-x64.exe"));
    }

    [Fact]
    public void SomethingThatIsNotAHashIsNotAChecksum()
    {
        Assert.Null(ReleaseAssets.ChecksumFor("zzzz *file.exe", "file.exe"));
        Assert.Null(ReleaseAssets.ChecksumFor("", "file.exe"));
        Assert.Null(ReleaseAssets.ChecksumFor(null, "file.exe"));
    }

    /// <summary>
    /// The client recognises the asset by name, and the installer script produces that name. If
    /// the two ever disagree, updates stop working with no symptom other than an application that
    /// says it is up to date forever.
    /// </summary>
    [Fact]
    public void TheInstallerScriptProducesTheNameTheClientLooksFor()
    {
        var script = File.ReadAllText(RepositoryFile("installer/VoiceTranscript.iss"));

        Assert.Contains("OutputBaseFilename=VoiceTranscript-Setup-{#AppVersion}-win-x64", script);

        // Which, filled in, is exactly what the client expects.
        Assert.Equal(
            "VoiceTranscript-Setup-1.2.0-win-x64.exe",
            ReleaseAssets.InstallerNameFor(AppVersion.Parse("1.2.0")));
    }

    /// <summary>
    /// The installer waits for the running application and never closes it.
    ///
    /// CloseApplications=yes would let Restart Manager terminate a tray recorder mid-call — and it
    /// cannot even do it cleanly, because MainWindow.OnClosing cancels the close, so a kill is the
    /// only outcome. That ends a recording with its WAV headers unwritten and its row never
    /// completed: the conversation is lost while the application is being helpfully upgraded.
    /// </summary>
    [Fact]
    public void TheInstallerWaitsForTheRunningApplicationRatherThanClosingIt()
    {
        var script = File.ReadAllText(RepositoryFile("installer/VoiceTranscript.iss"));

        Assert.Contains("CloseApplications=no", script);
        Assert.Contains("AppMutex=Global\\VoiceTranscript.SingleInstance", script);
    }

    /// <summary>
    /// The installer and the application must name the same mutex, or the installer waits for
    /// nothing and replaces files under a running process.
    /// </summary>
    [Fact]
    public void TheInstallerAndTheApplicationNameTheSameMutex()
    {
        var script = File.ReadAllText(RepositoryFile("installer/VoiceTranscript.iss"));
        var app = File.ReadAllText(RepositoryFile("src/VoiceTranscript.App/App.xaml.cs"));

        Assert.Contains("AppMutex=Global\\VoiceTranscript.SingleInstance", script);
        Assert.Contains(@"Global\VoiceTranscript.SingleInstance", app);
    }

    /// <summary>
    /// A silent install must leave a recorder running.
    ///
    /// Both original [Run] entries carry skipifsilent, which is right for them — a silent install
    /// should not offer a checkbox nobody sees. But it meant an update finished with nothing
    /// started: the application had closed itself to let the installer through, and the machine was
    /// left with no recorder until somebody opened it by hand, which for a tray application could
    /// be weeks.
    /// </summary>
    [Fact]
    public void ASilentInstallStartsTheApplicationAgain()
    {
        var script = File.ReadAllText(RepositoryFile("installer/VoiceTranscript.iss"));

        Assert.Contains("Check: WizardSilent", script);
    }

    /// <summary>Walks up to the repository root, so the test does not care where it is run from.</summary>
    internal static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Depoda bulunamadı: {relative}");
    }
}

/// <summary>
/// Deciding whether now is a moment the application may be replaced.
///
/// An update is the one routine operation that stops the process while it holds something
/// irreplaceable. Everything else can be retried; a conversation interrupted halfway cannot be,
/// and neither can one that was never detected because the recorder was being reinstalled.
/// </summary>
public class UpdateGuardTests
{
    private static UpdateGuard Ready() => new()
    {
        IsRecording = false,
        IsProcessing = false,
        QueueDepth = 0,
        DataDirectoryOverridden = false,
        InstalledNormally = true,
        FreeDiskBytes = 10L * 1024 * 1024 * 1024,
        InstallerBytes = 68L * 1024 * 1024,
        RestorePending = false,
    };

    [Fact]
    public void AnIdleInstalledCopyMayUpdate()
    {
        Assert.True(Ready().MayUpdate);
        Assert.Null(Ready().Explain());
    }

    /// <summary>The one that would destroy something. Checked first, and by itself.</summary>
    [Fact]
    public void AnUpdateIsRefusedWhileACallIsBeingRecorded()
    {
        var guard = Ready() with { IsRecording = true };

        Assert.Equal(UpdateBlock.Recording, guard.Evaluate());
        Assert.Contains("kaydediliyor", guard.Explain());
    }

    [Fact]
    public void AnUpdateIsRefusedWhileARecordingIsBeingProcessed()
        => Assert.Equal(UpdateBlock.Processing, (Ready() with { IsProcessing = true }).Evaluate());

    /// <summary>
    /// Queued work is durable, but installing over it means the queue is drained by a build the
    /// user has not seen behave yet. Waiting costs minutes.
    /// </summary>
    [Fact]
    public void AnUpdateIsRefusedWhileRecordingsAreWaiting()
        => Assert.Equal(UpdateBlock.QueueNotEmpty, (Ready() with { QueueDepth = 3 }).Evaluate());

    /// <summary>
    /// A redirected data directory means a development build. Installing a release over it would
    /// replace the executable and leave the data where the installed copy never looks.
    /// </summary>
    [Fact]
    public void AnUpdateIsRefusedWhenTheDataDirectoryWasOverridden()
        => Assert.Equal(UpdateBlock.DataDirectoryOverridden,
            (Ready() with { DataDirectoryOverridden = true }).Evaluate());

    /// <summary>
    /// A copy run from a build output has nothing to upgrade: the installer would put a second one
    /// elsewhere and leave this one running and stale, offering the same update forever.
    /// </summary>
    [Fact]
    public void AnUpdateIsRefusedForACopyTheInstallerDidNotPlace()
        => Assert.Equal(UpdateBlock.NotInstalled, (Ready() with { InstalledNormally = false }).Evaluate());

    /// <summary>
    /// Room for the installer twice over, plus a margin: it is downloaded and then unpacked, and
    /// running out midway leaves an application directory with some files replaced and some not.
    /// </summary>
    [Fact]
    public void AnUpdateIsRefusedWithoutRoomForTheInstallerTwiceOver()
    {
        var guard = Ready() with { InstallerBytes = 100L * 1024 * 1024, FreeDiskBytes = 150L * 1024 * 1024 };

        Assert.Equal(UpdateBlock.NoDiskSpace, guard.Evaluate());
        Assert.Contains("yer yok", guard.Explain());
    }

    [Fact]
    public void AnUpdateIsRefusedWhileARestoreIsWaiting()
        => Assert.Equal(UpdateBlock.RestorePending, (Ready() with { RestorePending = true }).Evaluate());

    /// <summary>Recording outranks everything: it is the only loss that cannot be undone.</summary>
    [Fact]
    public void RecordingIsReportedAheadOfEveryOtherReason()
    {
        var guard = Ready() with
        {
            IsRecording = true,
            IsProcessing = true,
            QueueDepth = 5,
            InstalledNormally = false,
            RestorePending = true,
        };

        Assert.Equal(UpdateBlock.Recording, guard.Evaluate());
    }

    [Fact]
    public void EveryRefusalHasSomethingToTellTheUser()
    {
        var guards = new[]
        {
            Ready() with { IsRecording = true },
            Ready() with { IsProcessing = true },
            Ready() with { QueueDepth = 1 },
            Ready() with { DataDirectoryOverridden = true },
            Ready() with { InstalledNormally = false },
            Ready() with { InstallerBytes = long.MaxValue / 4, FreeDiskBytes = 0 },
            Ready() with { RestorePending = true },
        };

        foreach (var guard in guards)
            Assert.False(string.IsNullOrWhiteSpace(guard.Explain()), $"{guard.Evaluate()} açıklamasız");
    }
}
