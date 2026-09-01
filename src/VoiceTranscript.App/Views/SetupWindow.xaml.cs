using System.Windows;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class SetupWindow
{
    private readonly SetupViewModel _viewModel;

    public SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        Services.EscapeCloses.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;

        // Starts working the moment it opens rather than waiting to be told to.
        //
        // There is exactly one correct answer to "is Python installed" and exactly one correct
        // response to "no". Presenting that as a decision only makes somebody responsible for a
        // choice they have no information about, and the previous version's five buttons were
        // five chances to close the window with nothing installed.
        Loaded += async (_, _) => await _viewModel.RunAllAsync();
    }

    /// <summary>True when the user finished rather than skipping, so first run is not repeated.</summary>
    public bool Completed { get; private set; }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HardwareReport is not { } report) return;

        try
        {
            Clipboard.SetText(report.ToPlainText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open. Not worth an error dialog over.
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        Completed = true;
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        // Skipping is allowed on purpose. Someone using the cloud route needs none of this, and
        // trapping them behind a wizard for prerequisites they will never use would be wrong.
        DialogResult = false;
        Close();
    }
}
