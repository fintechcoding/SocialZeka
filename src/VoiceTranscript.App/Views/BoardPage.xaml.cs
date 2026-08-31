using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Conversations set aside to come back to.
///
/// Cards are moved from a menu rather than dragged. Drag and drop across an ItemsControl in WPF is
/// real work — adorners, hit testing, insertion points, scroll-while-dragging — and it buys
/// nothing here that a named menu does not: there are four lanes and they have names, so
/// "Şeride taşı → Bende" is one gesture and unambiguous. Dragging can come later if the board
/// turns out to be used enough to want it.
/// </summary>
public partial class BoardPage
{
    public BoardPage() => InitializeComponent();

    private BoardViewModel? ViewModel => DataContext as BoardViewModel;

    private static BoardItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as BoardItem;

    private void Card_Click(object sender, MouseButtonEventArgs e) => Open(ItemOf(sender));

    private void Open_Click(object sender, RoutedEventArgs e) => Open(ItemOf(sender));

    private void Open(BoardItem? item)
    {
        if (item is null) return;

        var window = new CallWindow(new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, item.CallId))
        {
            Owner = Window.GetWindow(this),
        };

        window.Show();
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (sender is not FrameworkElement { Tag: string lane }) return;

        ViewModel?.Move(item.CallId, lane);
    }

    /// <summary>
    /// Sets a reminder a fixed number of days out.
    ///
    /// Offsets rather than a date picker. The reminders people actually set on a conversation are
    /// "tomorrow", "next week", "in a month" — a calendar makes the common case slower to serve a
    /// case that rarely arises.
    /// </summary>
    private void Remind_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var days)) return;

        ViewModel?.Remind(
            item.CallId,
            days <= 0 ? null : DateOnly.FromDateTime(DateTime.Now).AddDays(days));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;

        ViewModel?.RemoveCommand.Execute(item);
    }
}
