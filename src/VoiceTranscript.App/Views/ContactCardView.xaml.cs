using System.Windows;

namespace VoiceTranscript.App.Views;

/// <summary>
/// The contact card, hosted by the contact window and by the shell's contact page.
///
/// Almost nothing but markup. The three things the card cannot do for itself — opening a
/// conversation at a moment, going to the Sözler page, and scrolling to one of its own sections
/// — are events its view model raises. The first two are answered by the host, because the two
/// hosts answer them differently: a window opens a call window, and a page inside the shell can
/// also move the shell. A control that reached for <c>Application.Current</c> to do either would
/// work in one host and surprise in the other.
///
/// The third is answered here rather than by a host, because where a section sits is this
/// control's own knowledge and nobody else's.
/// </summary>
public partial class ContactCardView
{
    public ContactCardView()
    {
        InitializeComponent();
        DataContextChanged += WhenCardChanged;
    }

    /// <summary>The card currently bound, so its event can be let go of when it is replaced.</summary>
    private ViewModels.ContactCardViewModel? _bound;

    private void WhenCardChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The shell throws the view model away and builds a new one every time the selected
        // person changes, so without the unsubscribe this control would accumulate one handler
        // per contact ever looked at and hold every one of them alive.
        if (_bound is not null) _bound.JourneyRequested -= ShowJourney;

        _bound = e.NewValue as ViewModels.ContactCardViewModel;

        if (_bound is not null) _bound.JourneyRequested += ShowJourney;
    }

    /// <summary>
    /// Brings "Rakam yolculuğu" into view, after the layout that would place it has run.
    ///
    /// Deferred for the same reason the citation seek is deferred: the request arrives in the
    /// same breath as the person being selected, and scrolling a panel that has not measured its
    /// children yet is silently ignored — which looks exactly like a button that does nothing.
    /// </summary>
    private void ShowJourney(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(
            new Action(() => JourneySection.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Loaded);
}
