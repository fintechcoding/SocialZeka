using System.Windows;
using System.Windows.Controls;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Controls;

/// <summary>
/// The one "… · Geri al · ✕" strip, wherever a ruling can be taken back.
///
/// The markup is the whole control; this exists for <see cref="SlotProperty"/>, which is what a
/// page hands it: <c>&lt;controls:UndoBar Slot="{Binding LedgerUndo}" /&gt;</c>. Nothing else is
/// configurable on purpose — a strip that each page could dress differently is the six copies
/// coming back one property at a time.
/// </summary>
public partial class UndoBar : UserControl
{
    /// <summary>The view model's slot: what was just done, and its inverse.</summary>
    public static readonly DependencyProperty SlotProperty =
        DependencyProperty.Register(nameof(Slot), typeof(UndoSlot), typeof(UndoBar), new PropertyMetadata(null));

    public UndoBar() => InitializeComponent();

    public UndoSlot? Slot
    {
        get => (UndoSlot?)GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }
}
