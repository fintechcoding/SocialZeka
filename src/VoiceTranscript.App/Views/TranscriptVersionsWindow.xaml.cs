using System.Globalization;
using System.Windows;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Every transcript a conversation has had, so two engines can be compared on it.
///
/// The question this answers — which engine hears my calls better — was unanswerable while each
/// run overwrote the last. Answering it by hand meant reading a log and re-transcribing one of
/// them again, and on the call that prompted this the audio had been rewritten between the two
/// runs, so the comparison was meaningless and nothing said so.
/// </summary>
public partial class TranscriptVersionsWindow
{
    private readonly Repository _repository;
    private readonly long _callId;

    /// <summary>True when a stored transcript was put back and the caller should re-read.</summary>
    public bool Restored { get; private set; }

    public TranscriptVersionsWindow(Repository repository, long callId)
    {
        InitializeComponent();

        _repository = repository;
        _callId = callId;

        Load();
    }

    /// <summary>One version as the list shows it.</summary>
    public sealed record Row(long Id, string Engine, string When, string Figures, bool IsCurrent)
    {
        /// <summary>The one already in use is not offered again.</summary>
        public bool CanRestore => !IsCurrent;
    }

    private void Load()
    {
        var versions = _repository.ListTranscriptVersions(_callId);

        Versions.ItemsSource = versions.Select(ToRow).ToList();
        EmptyState.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Row ToRow(TranscriptVersion version)
    {
        var uncertain = version.SegmentCount == 0
            ? 0
            : 100.0 * version.LowConfidenceCount / version.SegmentCount;

        var figures = $"{version.SegmentCount} satır · {version.WordCount} kelime · "
                      + $"{version.LowConfidenceCount} belirsiz (%{uncertain:0})";

        if (version.SpeechCoverage is { } coverage)
            figures += $" · konuşmanın %{coverage * 100:0}'i";

        figures += $" · {TimeSpan.FromMilliseconds(version.SpokenMs):mm\\:ss} konuşma";

        return new Row(
            version.Id,
            version.Engine,
            version.CreatedAt.ToLocalTime().ToString("d MMMM HH:mm", CultureInfo.CurrentCulture),
            figures,
            version.IsCurrent);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;

        if (!_repository.RestoreTranscriptVersion(row.Id)) return;

        // The list no longer records that a restore happened — it is a history of transcriptions,
        // and going back to one is a reading decision. This is where that decision is kept.
        Services.AppLog.Write("veri", $"görüşme #{_callId} · döküm geri yüklendi: {row.Engine}");

        Restored = true;
        Load();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
