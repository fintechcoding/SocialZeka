using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One conversation on the board, with enough of the call to be recognisable.</summary>
public sealed record BoardItem(BoardCard Card, Call Call, string ContactName)
{
    public long CallId => Card.CallId;

    /// <summary>What the user called it, or the conversation's own heading.</summary>
    public string Title => string.IsNullOrWhiteSpace(Card.Title)
        ? $"{ContactName} · {Call.StartedAt.ToLocalTime():d MMM}"
        : Card.Title!;

    public string When => Call.StartedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm");

    public string Length => $"{(int)Call.Duration.TotalMinutes:00}:{Call.Duration.Seconds:00}";

    public string AppName => Call.App.ToString();

    public bool HasReminder => Card.RemindOn is not null;
    public bool IsDue => Card.IsDue;

    public string ReminderText => Card.RemindOn is { } day
        ? day <= DateOnly.FromDateTime(DateTime.Now)
            ? "bugün"
            : day.ToString("d MMM")
        : "";
}

/// <summary>One column of the board.</summary>
public sealed partial class BoardLaneView(string lane) : ObservableObject
{
    public string Lane { get; } = lane;
    public string Name { get; } = BoardLane.NameOf(lane);
    public string EmptyText { get; } = BoardLane.EmptyText(lane);

    public ObservableCollection<BoardItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;
    public string Count => Items.Count == 0 ? "" : Items.Count.ToString();

    public void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>
/// Conversations set aside to come back to.
///
/// The archive answers "what was said"; this answers "what am I still carrying". They are
/// different questions and only the second one has a shape a person imposes — every other thing on
/// screen was produced by a machine from the audio, and this is the one place somebody says what
/// matters to them.
///
/// A card is always a conversation. Never a free-standing note: the rule the product rests on is
/// that every claim carries a verbatim quote and a timestamp you can play, and a bare "call Ahmet
/// back" card has neither. The moment the board accepts one, this stops being an archive of
/// evidence and becomes a to-do list that happens to sit beside one.
/// </summary>
public sealed partial class BoardViewModel(Repository repository) : ObservableObject
{
    public ObservableCollection<BoardLaneView> Lanes { get; } = [];

    [ObservableProperty] private string? _notice;

    public bool IsEmpty => Lanes.All(l => l.IsEmpty);

    /// <summary>One line for the strip on the first screen: "Bakılacak 3 · Bende 1".</summary>
    public string Summary
    {
        get
        {
            var parts = Lanes
                .Where(l => l.Items.Count > 0 && l.Lane != BoardLane.Done)
                .Select(l => $"{l.Name} {l.Items.Count}")
                .ToList();

            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        var cards = repository.BoardCards();

        // One query for the calls rather than one per card: a board with forty cards would
        // otherwise be forty round trips every time it is opened.
        var calls = repository.ListCalls(limit: 2000).ToDictionary(c => c.Id);

        Lanes.Clear();

        foreach (var lane in BoardLane.All)
        {
            var view = new BoardLaneView(lane);

            foreach (var card in cards.Where(c => c.Lane == lane).OrderBy(c => c.Position))
            {
                if (!calls.TryGetValue(card.CallId, out var call)) continue;

                var name = call.ContactId is { } id
                    ? repository.GetContact(id)?.Name ?? "İsimsiz"
                    : "İsimsiz";

                view.Items.Add(new BoardItem(card, call, name));
            }

            view.Changed();
            Lanes.Add(view);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Moves a card to another lane, or puts a conversation on the board for the first time.</summary>
    public void Move(long callId, string lane)
    {
        repository.PutOnBoard(callId, lane);
        Refresh();

        Notice = $"\"{BoardLane.NameOf(lane)}\" şeridine taşındı.";
    }

    [RelayCommand]
    private void Remove(BoardItem item)
    {
        repository.RemoveFromBoard(item.CallId);
        Refresh();

        // Said plainly, because taking something off a board looks like deleting it and this is
        // the one screen where nothing is ever deleted.
        Notice = "Panodan kaldırıldı. Görüşmenin kendisi duruyor.";
    }

    /// <summary>Sets a day to bring this card back, or clears it.</summary>
    public void Remind(long callId, DateOnly? day)
    {
        repository.RemindOn(callId, day);
        Refresh();

        Notice = day is null
            ? "Hatırlatma kaldırıldı."
            : $"{day:d MMMM} günü hatırlatılacak.";
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;
}
