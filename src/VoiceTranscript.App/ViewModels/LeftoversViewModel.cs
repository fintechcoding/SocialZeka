using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// One decision an import could not carry, as a person reads it.
///
/// Three regions and a visible line between them, because they are three different kinds of
/// claim and the product's whole argument rests on not mixing them: what is written HERE and what
/// is written OVER THERE are both evidence, neither is a recommendation, and the answer is the
/// user's. A row that quietly presented one side as the right one would be this screen making the
/// decision it exists to hand over.
/// </summary>
public sealed partial class LeftoverRow : ObservableObject
{
    public required ImportLeftover Model { get; init; }

    /// <summary>What kind of decision this is: a promise ruling, a note, a tag, an ear verdict.</summary>
    public required string Kind { get; init; }

    /// <summary>Which conversation or which person it hangs off.</summary>
    public required string Where { get; init; }

    /// <summary>The words it was anchored to, when it was anchored to words.</summary>
    public string? Quote { get; init; }

    public required string Mine { get; init; }
    public required string Theirs { get; init; }

    /// <summary>False when nothing was written here — the decision had nowhere to land.</summary>
    public bool HasMine => !Model.HadNowhereToLand;

    public bool CanTakeTheirs { get; init; }
    public bool CanKeepBoth { get; init; }

    /// <summary>Why one of the three is missing, said out loud. Null when all three are offered.</summary>
    public string? Refusal { get; init; }

    public bool HasRefusal => Refusal is not null;

    /// <summary>What the user answered, for the rows they have already closed.</summary>
    public string? Answer { get; init; }

    public bool IsAnswered => Answer is not null;
    public bool IsOpen => Answer is null;
}

/// <summary>
/// "Getirilmeyenler" — the questions an import left behind.
///
/// The merge carries a decision that arrives into an empty place, because writing into an empty
/// place is a move rather than a merge. What it will not do is pick a winner when both machines
/// ruled and the rulings differ: that is the silent loss the whole two-machine package exists to
/// end, and quietly discarding the loser is the same loss wearing a different hat. So the local
/// value stands, the incoming one becomes a row here, and the user answers it once.
///
/// EVERY ROW IS AN ANSWERABLE QUESTION OR SAYS WHY IT IS NOT. Some answers cannot be carried out
/// — a promise ruling whose words match two promises in one conversation, a value written by a
/// build that knows more than this one — and where that is so the row says which, in a sentence,
/// instead of showing a button that would do nothing. <see cref="LeftoverResolution"/> owns that
/// judgement; this screen owns the words for it.
/// </summary>
public sealed partial class LeftoversViewModel : ObservableObject
{
    private readonly Repository _repository;

    public LeftoversViewModel(Repository repository)
    {
        _repository = repository;

        // The shared strip, so a resolution can be taken back for as long as its sentence is on
        // screen. It matters more here than on most screens: "Ötekini al" writes over something
        // the user typed on this machine, and the row it came from is closed by the same click.
        Undo.Undone += (_, _) => Load();

        Load();
    }

    public UndoSlot Undo { get; } = new();

    public ObservableCollection<LeftoverRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// Whether the answered rows are shown beside the open ones.
    ///
    /// Off by default: the list with a question in it is the one somebody opened this for. The
    /// answered rows are never deleted — an answer is what stops the same question being asked on
    /// the next round trip — so they are here to be looked at rather than to be got through.
    /// </summary>
    [ObservableProperty] private bool _showAnswered;

    partial void OnShowAnsweredChanged(bool value) => Load();

    [RelayCommand]
    private void Refresh() => Load();

    private void Load()
    {
        Rows.Clear();

        foreach (var row in _repository.Leftovers(includeResolved: ShowAnswered))
            Rows.Add(Describe(row));

        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---- the three answers ---------------------------------------------------

    [RelayCommand]
    private void TakeTheirs(LeftoverRow row) => Answer(row, LeftoverResolutions.Theirs);

    [RelayCommand]
    private void KeepMine(LeftoverRow row) => Answer(row, LeftoverResolutions.Mine);

    [RelayCommand]
    private void KeepBoth(LeftoverRow row) => Answer(row, LeftoverResolutions.Both);

    /// <summary>
    /// Carries out one answer, says what happened, and keeps the way back.
    ///
    /// The undo puts back what was here AND asks the question again, because an answer taken back
    /// is not an answer — leaving the row closed would mean the user had silently agreed to the
    /// thing they had just undone.
    /// </summary>
    private void Answer(LeftoverRow row, string resolution)
    {
        if (!LeftoverResolution.Apply(_repository, row.Model, resolution))
        {
            // Refused rather than half done: nothing was written and the row is still open. Said
            // in the same strip, without a button, because there is nothing to take back.
            Undo.Say(Localisation.T("leftoverswindow.uygulanamadi"));
            return;
        }

        Undo.Offer(new Services.PendingUndo(
            Services.LedgerVerb.Settle,
            Localisation.T(resolution switch
            {
                LeftoverResolutions.Theirs => "leftoverswindow.otekini-aldim",
                LeftoverResolutions.Both => "leftoverswindow.ikisi-de-tutuldu",
                _ => "leftoverswindow.burada-kaldi",
            }),
            () =>
            {
                // "Burada kalsın" wrote nothing, so taking it back only reopens the question.
                if (resolution == LeftoverResolutions.Mine) _repository.ReopenLeftover(row.Model.Id);
                else LeftoverResolution.Undo(_repository, row.Model);
            }));

        Load();
    }

    // ---- turning a stored row into a sentence --------------------------------

    private LeftoverRow Describe(ImportLeftover row)
    {
        var choices = LeftoverResolution.Offer(_repository, row);

        return new LeftoverRow
        {
            Model = row,
            Kind = KindName(row),
            Where = WhereFrom(row),
            Quote = row.Quote,
            Mine = row.Mine is null
                ? Localisation.T("leftoverswindow.burada-bir-sey-yazmamis")
                : Read(row, row.Mine),
            Theirs = Read(row, row.Theirs),
            CanTakeTheirs = choices.CanTakeTheirs,
            CanKeepBoth = choices.CanKeepBoth,
            Refusal = Refusal(choices),
            Answer = row.Resolution is null ? null : string.Format(
                CultureInfo.CurrentCulture,
                Localisation.T("leftoverswindow.cevaplandi"),
                Localisation.T(row.Resolution switch
                {
                    LeftoverResolutions.Theirs => "leftoverswindow.otekini-al",
                    LeftoverResolutions.Both => "leftoverswindow.ikisi-de-tut",
                    _ => "leftoverswindow.burada-kalsin",
                })),
        };
    }

    /// <summary>
    /// The sentence under the buttons, when one of them is not there.
    ///
    /// "Take theirs" is named first because its absence is the one that changes what the user can
    /// do about the row; a missing "keep both" leaves both real answers standing.
    /// </summary>
    private static string? Refusal(LeftoverChoices choices) =>
        choices.Theirs is not LeftoverRefusal.None ? Say(choices.Theirs)
        : choices.Both is not LeftoverRefusal.None ? Say(choices.Both)
        : null;

    private static string Say(LeftoverRefusal refusal) => Localisation.T(refusal switch
    {
        LeftoverRefusal.NothingHere => "leftoverswindow.neden-burada-yok",
        LeftoverRefusal.NotUnique => "leftoverswindow.neden-tek-degil",
        LeftoverRefusal.Unreadable => "leftoverswindow.neden-okunamiyor",
        LeftoverRefusal.OneOrTheOther => "leftoverswindow.neden-biri-ya-da-oteki",
        _ => "leftoverswindow.neden-tek-deger",
    });

    private static string KindName(ImportLeftover row) => row.Kind switch
    {
        LeftoverKinds.Promise => Localisation.T("leftoverswindow.tur-soz"),
        LeftoverKinds.Note => Localisation.T("leftoverswindow.tur-not"),
        LeftoverKinds.Tag => Localisation.T("leftoverswindow.tur-etiket"),
        LeftoverKinds.Verdict => Localisation.T("leftoverswindow.tur-kulak"),
        LeftoverKinds.Suggestion => Localisation.T("leftoverswindow.tur-oneri"),
        LeftoverKinds.Board => Localisation.T("leftoverswindow.tur-pano"),
        _ => PersonField(row.Field),
    };

    private static string PersonField(string field) => Localisation.T(field switch
    {
        LeftoverFields.Photo => "leftoverswindow.alan-fotograf",
        LeftoverFields.BirthDate => "leftoverswindow.alan-dogum-gunu",
        LeftoverFields.PersonNote => "leftoverswindow.alan-not",
        _ => "leftoverswindow.tur-kisi",
    });

    /// <summary>Which conversation, or which person, this decision was about.</summary>
    private string WhereFrom(ImportLeftover row)
    {
        if (row.ContactId is { } person)
            return _repository.GetContact(person)?.Name ?? Localisation.T("leftoverswindow.silinmis");

        if (row.CallId is not { } call || _repository.GetCall(call) is not { } spoken)
            return Localisation.T("leftoverswindow.silinmis");

        var name = spoken.ContactId is { } who ? _repository.GetContact(who)?.Name : null;

        return string.Format(
            CultureInfo.CurrentCulture,
            Localisation.T("leftoverswindow.gorusme"),
            name ?? Localisation.T("leftoverswindow.isimsiz"),
            Dates.Moment(spoken.StartedAt));
    }

    /// <summary>
    /// One side of the row, in words rather than in columns.
    ///
    /// A note and a tag are already their own text. A promise ruling, a suggestion ruling and a
    /// board card are stored as one short machine line — "durum=1 · susturuldu=0 · tarih=- · söz=-"
    /// — because there is one column to put them in, and putting that on screen would be asking
    /// the user to arbitrate between two things neither of which they can read.
    /// <see cref="LeftoverValue"/> takes them apart; the words for the parts are here.
    /// </summary>
    private static string Read(ImportLeftover row, string? stored) => row.Kind switch
    {
        LeftoverKinds.Promise => LeftoverValue.ReadPromise(stored) is { } ruling
            ? Promise(ruling)
            : Verbatim(stored),

        LeftoverKinds.Suggestion => LeftoverValue.ReadSuggestion(stored) is { } ruled
            ? Suggestion(ruled)
            : Verbatim(stored),

        LeftoverKinds.Board => LeftoverValue.ReadBoard(stored) is { } card
            ? Board(card)
            : Verbatim(stored),

        LeftoverKinds.Verdict => Heard(stored),

        LeftoverKinds.Person when row.Field == LeftoverFields.BirthDate =>
            DateOnly.TryParse(stored, CultureInfo.InvariantCulture, out var day)
                ? Dates.DayAndYear(day)
                : Verbatim(stored),

        _ => Verbatim(stored),
    };

    private static string Verbatim(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? Localisation.T("leftoverswindow.bos") : stored;

    private static string Promise(LeftoverValue.PromiseRuling ruling)
    {
        var parts = new List<string>
        {
            Localisation.T(ruling.Status switch
            {
                CommitmentStatus.Fulfilled => "leftoverswindow.soz-tutuldu",
                CommitmentStatus.Renegotiated => "leftoverswindow.soz-yeniden-konusuldu",
                CommitmentStatus.Abandoned => "leftoverswindow.soz-tutulmadi",
                _ => "leftoverswindow.soz-acik",
            }),
        };

        if (ruling.Dismissed) parts.Add(Localisation.T("leftoverswindow.soz-susturuldu"));

        if (ruling.Deadline is { } day)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localisation.T("leftoverswindow.soz-vade"), Dates.DayAndYear(day)));

        if (ruling.Obligation is { } words)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localisation.T("leftoverswindow.soz-sozu"), words));

        return string.Join(" · ", parts);
    }

    private static string Suggestion(LeftoverValue.SuggestionRuling ruled)
    {
        var said = Localisation.T(ruled.Status switch
        {
            ActionStatus.Done => "leftoverswindow.oneri-yapildi",
            ActionStatus.Hidden => "leftoverswindow.oneri-reddedildi",
            ActionStatus.Routed => "leftoverswindow.oneri-yonlendirildi",
            _ => "leftoverswindow.oneri-acik",
        });

        return ruled.RoutedNote is null ? said : said + " · " + ruled.RoutedNote;
    }

    private static string Board(LeftoverValue.BoardCardValue card)
    {
        // The lane's own name, from the board's own vocabulary. A second spelling of "Bakılacak"
        // living in this file would be the fifth screen to name the four lanes for itself.
        var parts = new List<string> { BoardLane.NameOf(card.Lane) };

        if (card.Title is { } title) parts.Add(title);

        if (card.RemindOn is { } day)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localisation.T("leftoverswindow.pano-hatirlat"), Dates.Day(day)));

        return string.Join(" · ", parts);
    }

    private static string Heard(string? stored) =>
        int.TryParse(stored, CultureInfo.InvariantCulture, out var value)
            ? Localisation.T((VerdictValue)value switch
            {
                VerdictValue.Correct => "leftoverswindow.kulak-dogru",
                VerdictValue.NotThat => "leftoverswindow.kulak-bu-o-degil",
                VerdictValue.WantedAlarm => "leftoverswindow.kulak-uyari-isterdim",
                VerdictValue.Unneeded => "leftoverswindow.kulak-gereksiz",
                _ => "leftoverswindow.kulak-yanlis-duyulmus",
            })
            : Verbatim(stored);
}
