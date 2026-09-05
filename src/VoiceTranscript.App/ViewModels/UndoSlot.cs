using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// One notice line with a "Geri al" beside it.
///
/// The ledger page, the call window and the contact windows all make rulings on the same rows,
/// and each needs the same small thing afterwards: a sentence saying what happened, a way to
/// take it back for as long as the sentence is on screen, and a way to close it. This is that
/// thing, held by each view model as a property so the markup is the same card everywhere.
/// </summary>
public sealed partial class UndoSlot : ObservableObject
{
    private PendingUndo? _pending;

    /// <summary>What was just done, or null when nothing is being said.</summary>
    [ObservableProperty] private string? _notice;

    /// <summary>True while the last ruling can still be taken back.</summary>
    public bool CanUndo => _pending is not null;

    /// <summary>Raised after an undo was applied, so the owner can re-read what it shows.</summary>
    public event EventHandler? Undone;

    /// <summary>Shows the ruling and keeps its inverse ready.</summary>
    public void Offer(PendingUndo undo)
    {
        _pending = undo;
        Notice = undo.Sentence;
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Says something that has no inverse — a sweep's tally, a refusal.</summary>
    public void Say(string sentence)
    {
        _pending = null;
        Notice = sentence;
        OnPropertyChanged(nameof(CanUndo));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_pending is not { } pending) return;

        _pending = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));

        pending.Undo();
        Undone?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Clear()
    {
        _pending = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));
    }
}
