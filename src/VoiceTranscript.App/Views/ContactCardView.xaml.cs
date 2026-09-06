namespace VoiceTranscript.App.Views;

/// <summary>
/// The contact card, hosted by the contact window and by the shell's contact page.
///
/// Nothing but markup and the generated constructor. The two things the card cannot do for
/// itself — opening a conversation at a moment, and going to the Sözler page — are events its
/// view model raises, because the two hosts answer them differently: a window opens a call
/// window, and a page inside the shell can also move the shell. A control that reached for
/// <c>Application.Current</c> to do either would work in one host and surprise in the other.
/// </summary>
public partial class ContactCardView
{
    public ContactCardView() => InitializeComponent();
}
