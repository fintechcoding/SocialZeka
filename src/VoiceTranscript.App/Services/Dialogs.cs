using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace VoiceTranscript.App.Services;

/// <summary>
/// In-window Fluent dialogs, replacing the Win32-gray MessageBox wherever a window can host
/// one.
///
/// A window opts in by carrying a ContentPresenter named "DialogHost" over its root grid;
/// the helpers find it through the owner. Windows without a host — and the startup errors
/// that fire before any window exists — fall back to MessageBox, because a fallback that
/// silently shows nothing would be worse than an ugly dialog.
/// </summary>
public static class Dialogs
{
    /// <summary>Asks a yes/no question. True only on the affirmative.</summary>
    public static async Task<bool> ConfirmAsync(
        Window? owner, string title, string message,
        string okText = "Evet", string cancelText = "Vazgeç")
    {
        if (HostOf(owner) is not { } host)
        {
            return System.Windows.MessageBox.Show(
                       message, title, System.Windows.MessageBoxButton.YesNo,
                       System.Windows.MessageBoxImage.Question)
                   == System.Windows.MessageBoxResult.Yes;
        }

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = Wrapped(message),
            PrimaryButtonText = okText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Tells the user one thing, with a single button.</summary>
    public static async Task InfoAsync(Window? owner, string title, string message)
    {
        if (HostOf(owner) is not { } host)
        {
            System.Windows.MessageBox.Show(
                message, title, System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = Wrapped(message),
            CloseButtonText = "Tamam",
        };

        await dialog.ShowAsync();
    }

    private static ContentPresenter? HostOf(Window? owner)
        => owner?.FindName("DialogHost") as ContentPresenter;

    private static System.Windows.Controls.TextBlock Wrapped(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
    };
}
