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
    /// <param name="confirm">
    /// Ask for it twice and refuse until the two agree.
    ///
    /// True when a password is being SET, false when one is being entered to open something that
    /// already has one. The asymmetry is the whole point: a mistyped password on the way in is a
    /// failed attempt you retry, and a mistyped password on the way out is a backup nobody can
    /// ever open again — including the person who made it, and by design, since the archive is
    /// AES-GCM and there is no recovery path. This dialog had one box for both, and the sentence
    /// beside it already warned that losing the password loses the backup.
    /// </param>
    public static async Task<string?> AskPasswordAsync(
        Window? owner, string title, string message, string okText = "Tamam", bool confirm = false)
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

        var again = new Wpf.Ui.Controls.PasswordBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            MinWidth = 280,
            PlaceholderText = "Parolayı tekrar yaz",
            Visibility = confirm ? Visibility.Visible : Visibility.Collapsed,
        };

        var complaint = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current
                .Resources["SystemFillColorCautionBrush"],
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel();
        panel.Children.Add(Wrapped(message));
        panel.Children.Add(box);
        panel.Children.Add(shown);
        panel.Children.Add(again);
        panel.Children.Add(complaint);
        panel.Children.Add(reveal);

        // Revealing replaces the box the second one is checked against, so the confirmation goes
        // with it: two boxes where one of them is plain text compares nothing useful.
        reveal.Checked += (_, _) => again.Visibility = Visibility.Collapsed;
        reveal.Unchecked += (_, _) => again.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;

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

        // Held open on a mismatch rather than accepting it. There is nothing later that could
        // catch this: the file encrypts fine with the typo, and the mistake surfaces months
        // afterwards as a backup that will not open.
        dialog.Closing += (_, e) =>
        {
            if (e.Result != ContentDialogResult.Primary) return;
            if (!confirm || reveal.IsChecked == true) return;

            // Empty means "do not encrypt", which the caller's own message explains. Nothing to
            // confirm, so nothing to compare.
            if (box.Password.Length == 0) return;

            if (box.Password == again.Password) return;

            complaint.Text = "İki parola aynı değil.";
            complaint.Visibility = Visibility.Visible;
            again.Password = "";
            again.Focus();
            e.Cancel = true;
        };

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
