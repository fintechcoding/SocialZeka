namespace VoiceTranscript.Core.Update;

/// <summary>What the application is doing that makes now a bad moment to replace it.</summary>
public enum UpdateBlock
{
    None = 0,

    /// <summary>A conversation is being recorded right now.</summary>
    Recording,

    /// <summary>A recording is being transcribed or analysed.</summary>
    Processing,

    /// <summary>Recordings are waiting to be processed.</summary>
    QueueNotEmpty,

    /// <summary>The data directory was overridden, so this is a development build.</summary>
    DataDirectoryOverridden,

    /// <summary>Not installed by the installer, so there is nothing for it to replace.</summary>
    NotInstalled,

    /// <summary>Not enough room to download the installer and let it unpack.</summary>
    NoDiskSpace,

    /// <summary>A backup is staged and will be applied on the next start.</summary>
    RestorePending,
}

/// <summary>
/// Decides whether the application may be replaced right now.
///
/// An update is the one routine operation that stops the process while it is holding something
/// irreplaceable. Everything else the application does can be retried; a conversation interrupted
/// halfway through recording cannot be, and neither can one that was never detected because the
/// recorder was being reinstalled at the time.
///
/// So this refuses generously. Every refusal below costs the user a delay of minutes and prevents
/// a loss that is permanent, and the asymmetry is not close.
///
/// Pure and free of Windows, so every rule can be tested — which matters because these are exactly
/// the conditions that are awkward to reproduce by hand.
/// </summary>
public sealed record UpdateGuard
{
    /// <summary>A call is being recorded, by detection or by hand.</summary>
    public required bool IsRecording { get; init; }

    /// <summary>A recording is being transcribed or analysed.</summary>
    public required bool IsProcessing { get; init; }

    /// <summary>How many recordings are waiting to be processed.</summary>
    public required int QueueDepth { get; init; }

    /// <summary>
    /// True when <c>--data</c> or the DataRoot setting moved the archive.
    ///
    /// A build running against a redirected directory is a development build by definition, and
    /// installing a release over it would replace the executable while leaving the data somewhere
    /// the installed copy will never look.
    /// </summary>
    public required bool DataDirectoryOverridden { get; init; }

    /// <summary>
    /// True when the running executable sits where the installer puts it.
    ///
    /// A copy run from a build output or a folder somebody unzipped has nothing for the installer
    /// to upgrade: it would install a second copy elsewhere and leave this one running and stale,
    /// which then keeps offering the same update forever.
    /// </summary>
    public required bool InstalledNormally { get; init; }

    /// <summary>Free bytes on the drive the installer would be downloaded to.</summary>
    public required long FreeDiskBytes { get; init; }

    /// <summary>Size of the installer that would be downloaded.</summary>
    public required long InstallerBytes { get; init; }

    /// <summary>True when a restore is staged and waiting for the next start.</summary>
    public required bool RestorePending { get; init; }

    /// <summary>
    /// Room for the installer twice over, plus a margin.
    ///
    /// Twice because the file is downloaded and then unpacked, and a margin because filling the
    /// system drive to the last byte breaks far more than this application. Running out midway
    /// leaves a half-written installer and, if it had already started, an application directory
    /// with some files replaced and some not.
    /// </summary>
    public long RequiredDiskBytes => InstallerBytes * 2 + 200L * 1024 * 1024;

    /// <summary>The first reason this is a bad moment, or <see cref="UpdateBlock.None"/>.</summary>
    public UpdateBlock Evaluate()
    {
        // Ordered by how much the user loses. Recording first: that is a conversation happening
        // right now, and it is the only one of these that cannot be had again.
        if (IsRecording) return UpdateBlock.Recording;
        if (RestorePending) return UpdateBlock.RestorePending;
        if (IsProcessing) return UpdateBlock.Processing;
        if (QueueDepth > 0) return UpdateBlock.QueueNotEmpty;
        if (DataDirectoryOverridden) return UpdateBlock.DataDirectoryOverridden;
        if (!InstalledNormally) return UpdateBlock.NotInstalled;
        if (InstallerBytes > 0 && FreeDiskBytes < RequiredDiskBytes) return UpdateBlock.NoDiskSpace;

        return UpdateBlock.None;
    }

    public bool MayUpdate => Evaluate() == UpdateBlock.None;

    /// <summary>Why the update is not being offered, in words the user can act on.</summary>
    public string? Explain()
    {
        var megabytes = RequiredDiskBytes / (1024.0 * 1024.0);

        return Evaluate() switch
        {
            UpdateBlock.None => null,

            UpdateBlock.Recording =>
                "Şu anda bir görüşme kaydediliyor. Güncelleme kayıt bitince yapılabilir.",

            UpdateBlock.Processing =>
                "Bir görüşme yazıya dökülüyor. Güncelleme bu iş bitince yapılabilir.",

            UpdateBlock.QueueNotEmpty =>
                $"{QueueDepth} görüşme işlenmeyi bekliyor. Güncelleme sıra boşalınca yapılabilir.",

            UpdateBlock.DataDirectoryOverridden =>
                "Bu kopya --data ile farklı bir veri klasöründe çalışıyor. Geliştirme kopyaları "
                + "kurulum paketiyle güncellenmez.",

            UpdateBlock.NotInstalled =>
                "Bu kopya kurulum paketiyle kurulmamış. Güncellemeyi elle indirip kurman gerekiyor.",

            UpdateBlock.NoDiskSpace =>
                $"Diskte yeterli yer yok. Güncelleme için yaklaşık {megabytes:0} MB boş alan gerekiyor.",

            UpdateBlock.RestorePending =>
                "Bir yedek geri yüklenmeyi bekliyor. Önce uygulamayı yeniden başlat.",

            _ => "Şu an güncelleme yapılamaz.",
        };
    }
}
