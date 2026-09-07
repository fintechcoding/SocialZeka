using System.Windows;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>
/// "Getirilmeyenler" — every decision an import could not carry, waiting for one answer each.
///
/// Opened from the sentence an import writes and from the card beside the import button, both on
/// Sağlık → Veriler. Nothing navigates here, because there is nothing to come and look at until a
/// backup has actually been merged: somebody who uses one computer never opens this at all.
/// </summary>
public partial class LeftoversWindow
{
    public LeftoversWindow(Repository repository)
    {
        InitializeComponent();
        DataContext = new LeftoversViewModel(repository);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
