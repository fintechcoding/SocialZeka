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

    /// <summary>
    /// Asks for a password, without echoing it.
    ///
    /// Returns null when the dialog was dismissed, and the empty string when somebody deliberately
    /// left it blank — the two mean different things where a password is optional, and collapsing
    /// them would turn "no thanks, leave it unencrypted" and "I changed my mind" into one answer.
    ///
    /// There is no confirm-the-password box on purpose. It is offered where the file is about to
    /// be written, so a typo is discovered by opening the file rather than years later; and when a
    /// backup is being restored, asking twice for something the user is reading off a note would
    /// be nothing but friction. A "show" toggle does the same job as a second box, honestly.
    /// </summary>
    public static async Task<string?> AskPasswordAsync(
        Window? owner, string title, string message, string okText = "Tamam")
    {
        var box = new Wpf.Ui.Controls.PasswordBox { Margin = new Thickness(0, 12, 0, 0), MinWidth = 280 };

        var reveal = new System.Windows.Controls.CheckBox
        {
            Content = "Parolayı göster",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var shown = new Wpf.Ui.Controls.TextBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            MinWidth = 280,
            Visibility = Visibility.Collapsed,
        };

        reveal.Checked += (_, _) =>
        {
            shown.Text = box.Password;
            box.Visibility = Visibility.Collapsed;
            shown.Visibility = Visibility.Visible;
            shown.Focus();
        };

        reveal.Unchecked += (_, _) =>
        {
            box.Password = shown.Text;
            shown.Visibility = Visibility.Collapsed;
            box.Visibility = Visibility.Visible;
            box.Focus();
        };

        var panel = new StackPanel();
        panel.Children.Add(Wrapped(message));
        panel.Children.Add(box);
        panel.Children.Add(shown);
        panel.Children.Add(reveal);

        if (HostOf(owner) is not { } host) return null;

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = okText,
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.Loaded += (_, _) => box.Focus();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        return reveal.IsChecked == true ? shown.Text : box.Password;
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
