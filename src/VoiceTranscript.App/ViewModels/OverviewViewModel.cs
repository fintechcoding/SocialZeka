using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

public enum AttentionKind
{
    /// <summary>Blocks the application from working at all until it is dealt with.</summary>
    Blocking,

    /// <summary>Needs the user, but nothing is broken.</summary>
    Action,

    /// <summary>Worth knowing.</summary>
    Info,
}

public enum AttentionAction
{
    None,
    ShowUnlabelled,
    RetryFailed,
    OpenSettings,

    /// <summary>Open the processing list on the failures, so each can be read and dealt with.</summary>
    ShowProcessing,
}

public sealed record AttentionItem(
    AttentionKind Kind,
    string Title,
    string Detail,
    string? ActionLabel = null,
    AttentionAction Action = AttentionAction.None)
{
    public Wpf.Ui.Controls.SymbolRegular Icon => Kind switch
    {
        AttentionKind.Blocking => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
        AttentionKind.Action => Wpf.Ui.Controls.SymbolRegular.Warning24,
        _ => Wpf.Ui.Controls.SymbolRegular.Info24,
    };

    /// <summary>
    /// Colour follows severity, never decoration.
    ///
    /// Three shades only, and each one means something: red is broken, amber wants a decision,
    /// blue is worth knowing. A palette that is prettier than this makes every notice look
    /// equally urgent, which is the same as none of them being urgent.
    /// </summary>
    public string BrushKey => Kind switch
    {
        AttentionKind.Blocking => "SystemFillColorCriticalBrush",
        AttentionKind.Action => "SystemFillColorCautionBrush",
        _ => "AccentTextFillColorPrimaryBrush",
    };

    public bool HasAction => ActionLabel is not null;
}

public sealed record RecentCall(
    Call Call, string ContactName, IReadOnlyList<string>? TagList = null, Circle? Circle = null)
{
    /// <summary>The circle this person is in, as an eight-pixel dot rather than another pill.</summary>
    public bool HasCircle => Circle is not null;

    /// <summary>The dot's colour. Empty when there is no circle, and then nothing is drawn.</summary>
    public string CircleColor => Circle?.Color ?? "";

    /// <summary>Named on hover: a colour alone is a code the user has to learn.</summary>
    public string CircleName => Circle?.Name ?? "";

    /// <summary>The user's labels, on the first list they look at — where "tehdit" must show.</summary>
    public IReadOnlyList<string> Tags => TagList ?? [];

    public bool HasTags => Tags.Count > 0;

    public string When => Dates.Moment(Call.StartedAt.ToLocalTime());

    /// <summary>Which application this came through, for the badge on the row.</summary>
    public string AppName => Call.App.ToString();

    public DateTimeOffset StartedAt => Call.StartedAt;

    public bool IsGroup => Call.Kind == CallKind.Group;
    public string Length => $"{(int)Call.Duration.TotalMinutes:00}:{Call.Duration.Seconds:00}";
    public bool NeedsLabel => Call.ContactId is null;

    /// <summary>
    /// What is happening to this recording, in plain language.
    ///
    /// States that are normal must not read like errors: a group call is deliberately not
    /// transcribed, and a queued call is waiting rather than stuck.
    /// </summary>
    /// <summary>One vocabulary, shared with every other screen. Null when there is nothing to say.</summary>
    public string? Status => CallStateText.Short(Call);

    public bool HasStatus => Status is not null;

    public string StatusBrushKey => CallStateText.BrushKey(Call.State);

    /// <summary>Why it failed, in the row — the reason used to be three screens away.</summary>
    public string? FailureLine => IsFailed ? Core.Asr.FailureText.Summarise(Call.FailureReason) : null;

    public bool HasFailureLine => FailureLine is not null;

    /// <summary>An unnamed call is not a person called "İsimsiz"; it is a question.</summary>
    public string DisplayName => NeedsLabel ? Localisation.T("overviewpage.isimsiz-gorusme") : ContactName;

    /// <summary>
    /// Whether something is actually happening to this recording right now.
    ///
    /// Narrower than it was, and the difference is visible on screen. Queued and Transcribed were
    /// both counted as working, so a list of recordings waiting their turn showed five spinners at
    /// once — which cannot be true, because processing is serialised behind a single semaphore so
    /// that Whisper and the analysis model never share the graphics card. Five things claiming to
    /// be in progress when at most one can be is not a cosmetic problem: it is the screen telling
    /// the user something they can reason their way to knowing is false.
    ///
    /// Queued now reads as waiting, which is what it is, and Transcribed as finished-for-now.
    /// </summary>
    public bool IsWorking => Call.State
        is ProcessingState.Transcribing or ProcessingState.Analysing;

    /// <summary>Waiting for its turn, with nothing happening to it yet.</summary>
    public bool IsWaiting => Call.State is ProcessingState.Recorded or ProcessingState.Queued;

    public bool IsFailed => Call.State == ProcessingState.Failed;

    /// <summary>The audio is still on disk. Without it there is nothing to re-transcribe or play.</summary>
    public bool HasAudio => Call.MicPath is not null || Call.FarPath is not null;

    /// <summary>A hand-started recording has no messenger; the badge would read "Unknown".</summary>
    public bool HasApp => Call.App != CallApp.Unknown;

    public bool CanRetranscribe => HasAudio && !IsGroup && !IsWorking;
    public bool CanReanalyse => !IsWorking && !IsWaiting;
    public bool CanDelete => !IsWorking;

    /// <summary>Said on the row once the sweep has taken the audio and only the text is left.</summary>
    public bool AudioGone => !HasAudio && Call.State is ProcessingState.Analysed or ProcessingState.Transcribed;
}

/// <summary>
/// Which slice of the archive one tab of the strip stands for.
///
/// An enum rather than the tab's label, because the label is a word in one language and the
/// selection is not: "Hepsi" and "Çevresiz" are the same two tabs in English, and a filter whose
/// identity is a sentence stops matching the moment the interface changes language. A named
/// circle carries its folded spelling beside this, which is the user's own data rather than the
/// interface's vocabulary.
/// </summary>
public enum CircleTabKind
{
    /// <summary>The whole archive. Where every launch starts, and never remembered otherwise.</summary>
    All,

    /// <summary>One circle the user defined.</summary>
    Circle,

    /// <summary>Everybody in no circle — the tab that cannot be removed.</summary>
    Uncircled,
}

/// <summary>
/// One entry of a circle dropdown: everything, one circle, or the people in none.
///
/// The same identity the strip carries, so the Görüşmeler page's dropdown and the first screen's
/// tabs are the same concept in two shapes rather than two half-alike features. The two screens
/// ask different questions — "bugün ne oldu" against "şunu bul" — and that is why one is tabs and
/// the other a dropdown; the registry records the difference.
/// </summary>
public sealed record CircleChoice(CircleTabKind Kind, string Name, string? Folded, string Color)
{
    public bool HasColor => Color.Length > 0;
}

/// <summary>
/// One tab of the strip over "Son görüşmeler".
///
/// The count is of the WHOLE archive, before any filtering — the same arithmetic the ledger and
/// the promises page have always used. A tab that counted what it is showing would say "Aile 12"
/// on a screen holding twelve rows, which tells the user nothing.
/// </summary>
public sealed partial class CircleTab(
    CircleTabKind kind, string name, string? folded, string color, int count) : ObservableObject
{
    public CircleTabKind Kind { get; } = kind;

    /// <summary>What the tab says. The user's own word for a circle; from the dictionary for the other two.</summary>
    public string Name { get; } = name;

    /// <summary>The folded identity of a named circle; null for "Hepsi" and "Çevresiz".</summary>
    public string? Folded { get; } = folded;

    public string Color { get; } = color;

    public bool HasColor => Color.Length > 0;

    public int Count { get; } = count;

    /// <summary>Selection lives here so no chip has to decide for itself whether it looks chosen.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>What this tab asks the database for.</summary>
    public CircleFilter Filter => Kind switch
    {
        CircleTabKind.Circle => CircleFilter.OfFolded(Folded ?? ""),
        CircleTabKind.Uncircled => CircleFilter.NoCircle,
        _ => CircleFilter.Everything,
    };
}

/// <summary>
/// The landing screen.
///
/// Deliberately not a list of contacts. Opening the application to a directory of names asks the
/// user to remember what they were looking for; opening it to what needs attention — recordings
/// nobody has named, promises that came due, anything that failed — answers that for them. The
/// archive is still one click away for when they do know.
/// </summary>
public sealed partial class OverviewViewModel(Repository repository, Func<AppSettings> settings, AppPaths paths) : ObservableObject
{
    /// <summary>Raised when a notice needs the shell to do something it cannot do itself.</summary>
    public event EventHandler<AttentionAction>? ActionRequested;

    /// <summary>Recordings nobody has named yet, for the labelling flow.</summary>
    public IReadOnlyList<Call> Unlabelled() => repository.UnlabelledCalls();

    /// <summary>
    /// Puts failed recordings back in the queue.
    ///
    /// Worth offering rather than making the user re-record: the audio is on disk and intact,
    /// and most failures here are a transient device or driver problem that a retry clears.
    /// </summary>
    public int RequeueFailed()
    {
        var failed = repository.FailedCalls(limit: 100);

        foreach (var call in failed)
            repository.SetCallState(call.Id, ProcessingState.Queued);

        Refresh();
        return failed.Count;
    }

    public ObservableCollection<AttentionItem> Attention { get; } = [];
    public ObservableCollection<RecentCall> Recent { get; } = [];
    public ObservableCollection<OverdueItem> Overdue { get; } = [];

    [ObservableProperty] private int _totalCalls;
    [ObservableProperty] private int _totalContacts;
    [ObservableProperty] private string _totalRecorded = "0 dk";
    [ObservableProperty] private int _pendingWork;

    /// <summary>Transcribed but never analysed. Not a backlog — see Repository.UnanalysedCount.</summary>
    [ObservableProperty] private int _unanalysed;

    /// <summary>
    /// The important-conversations panel: the one part of this archive a person arranges
    /// themselves, on the screen they see first.
    ///
    /// A flat, hand-ordered pile — drag in, drag around, take off. It used to be a summary line
    /// pointing at a separate four-lane board page; the user's own description of what they
    /// wanted was simpler and better: "önemli görüşmeler, oraya atabileyim, silebileyim,
    /// kaydırabileyim". The lanes went; the pile stayed.
    /// </summary>
    public ObservableCollection<PanelCard> Board { get; } = [];

    public bool HasBoard => Board.Count > 0;

    /// <summary>
    /// Cards whose reminder has come due.
    ///
    /// The only thing on this screen that asks for something back. Kept short and kept honest: a
    /// reminder somebody set themselves, on a conversation they chose, arriving on the day they
    /// asked for it. Nothing here is invented by the application, which is why it can be shown
    /// every day without becoming wallpaper.
    /// </summary>
    public ObservableCollection<DueCard> Due { get; } = [];

    public bool HasDue => Due.Count > 0;

    /// <summary>
    /// Birthdays within the next week, as "Uliana · 3 gün sonra (14 Mart)" lines.
    ///
    /// Every date came off a profile the user filled in; the application infers none of them.
    /// Empty almost always, and the section collapses to nothing then — a permanently empty
    /// "yaklaşan doğum günleri" box would be the application talking to hear itself.
    /// </summary>
    public ObservableCollection<string> Birthdays { get; } = [];

    public bool HasBirthdays => Birthdays.Count > 0;

    /// <summary>The Bugün tab with nothing to say — the only state that shows its empty text.</summary>
    public bool TodayIsEmpty => !HasDue && !HasBirthdays && !HasDayActions;
    [ObservableProperty] private bool _hasAnyData;

    public sealed record OverdueItem(Commitment Commitment, string ContactName)
    {
        public int DaysLate =>
            DateOnly.FromDateTime(DateTime.Now).DayNumber - (Commitment.EffectiveDeadline?.DayNumber ?? 0);

        public bool ByMe => Commitment.ByMe;

        /// <summary>Who owes whom, said plainly: "Sen → Uliana: evrak" is a different sentence
        /// from "Uliana: evrak", and the two used to be indistinguishable here.</summary>
        public string Line => ByMe
            ? $"Sen → {ContactName}: {Commitment.EffectiveObligation}"
            : $"{ContactName}: {Commitment.EffectiveObligation}";

        public string Quote => Commitment.Quote.Trim();
    }

    /// <summary>True when the worked example is in the archive.</summary>
    [ObservableProperty] private bool _hasSample;

    /// <summary>
    /// Loads a worked example so the product can be understood on the first day.
    ///
    /// The value of this application is not visible in any single conversation. It shows up
    /// three calls later, when a price has moved twice and a promise has come due — and asking
    /// somebody to record their calls for a month before they can see that is asking a lot. The
    /// example is written into the same tables as a real conversation and removes with the same
    /// delete, so it is a demonstration rather than a special mode.
    /// </summary>
    [RelayCommand]
    private void LoadSample()
    {
        Core.Storage.SampleData.Load(repository, paths);
        Refresh();
    }

    [RelayCommand]
    private void RemoveSample()
    {
        Core.Storage.SampleData.Remove(repository);
        Refresh();
    }

    [RelayCommand]
    private void RunAction(AttentionItem item) => ActionRequested?.Invoke(this, item.Action);

    [RelayCommand]
    public void Refresh()
    {
        HasSample = Core.Storage.SampleData.IsLoaded(repository);

        var (calls, contacts, recorded) = repository.Totals();

        TotalCalls = calls;
        TotalContacts = contacts;
        TotalRecorded = recorded.TotalHours >= 1
            ? $"{(int)recorded.TotalHours} sa {recorded.Minutes} dk"
            : $"{(int)recorded.TotalMinutes} dk";
        PendingWork = repository.PendingWorkCount();
        Unanalysed = repository.UnanalysedCount();

        LoadBoard();

        HasAnyData = calls > 0;

        LoadCircles();
        LoadRecent();

        Overdue.Clear();
        foreach (var (commitment, name) in repository.OverdueCommitments(DateOnly.FromDateTime(DateTime.Now)))
            Overdue.Add(new OverdueItem(commitment, name));

        OnPropertyChanged(nameof(OverdueLine));

        RebuildAttention();
    }

    // ---- the circle strip ---------------------------------------------------------------
    //
    // THE STRIP FILTERS ONE SECTION. The four figures at the top, the attention cards, the
    // overdue-promises line and the whole right-hand column are the same on every tab, and that
    // is not a layout preference. The card at the top is one sentence about the archive; a number
    // that shrinks because of a filter tells the user their archive shrank, which is a lie rather
    // than a display choice. The attention cards are "worth interrupting you for", and one of
    // their seven reasons — a recording nobody has named — belongs to no person and therefore to
    // no circle, so filtering them would hide it for ever. A family promise is still overdue on
    // the İş tab. And the right-hand column is the pile the user arranged with their own hands;
    // it does not answer to a filter over what the machine listed.

    /// <summary>The tabs, in the user's own order: Hepsi, their circles, then Çevresiz.</summary>
    public ObservableCollection<CircleTab> Circles { get; } = [];

    /// <summary>
    /// Whether the strip is worth drawing at all: two tabs mean nothing to choose between.
    /// </summary>
    public bool HasCircleStrip => Circles.Count > 2;

    /// <summary>
    /// Over four circles the strip stops reading as tabs.
    ///
    /// Written before it happens rather than after: seven pills wrap onto a second line, and a
    /// wrapped tab strip does not look like tabs any more. Past the threshold the same tabs are
    /// offered as one dropdown, which is what the Görüşmeler page uses at every size.
    /// </summary>
    public bool CirclesAreADropdown => Circles.Count > 6;

    public bool CirclesAreAStrip => HasCircleStrip && !CirclesAreADropdown;

    /// <summary>
    /// The chosen tab. Held for the session and NEVER PERSISTED: every launch opens on "Hepsi".
    ///
    /// A remembered selection would leave last night's family call behind a closed door — the
    /// user would open the application to a screen that looks complete and is not. The cost of
    /// forgetting is one click; the cost of remembering is a conversation nobody sees.
    /// </summary>
    [ObservableProperty] private CircleTab? _selectedCircle;

    /// <summary>True while the strip is being rebuilt; one load at the end rather than one each.</summary>
    private bool _buildingCircles;

    partial void OnSelectedCircleChanged(CircleTab? value)
    {
        foreach (var tab in Circles) tab.IsSelected = ReferenceEquals(tab, value);

        if (!_buildingCircles) LoadRecent();
    }

    /// <summary>Chosen from the strip. The dropdown form writes the same property directly.</summary>
    [RelayCommand]
    private void SelectCircle(CircleTab? tab)
    {
        if (tab is not null) SelectedCircle = tab;
    }

    /// <summary>
    /// Rebuilds the tabs from the whole archive, keeping the tab the user is looking at.
    ///
    /// One query for every count. The selection survives a refresh — a call arriving while the
    /// user is reading the Aile tab must not throw them back to Hepsi — but a circle that has
    /// been deleted since cannot be selected, and then the strip falls back to Hepsi rather than
    /// to an empty list with no explanation.
    /// </summary>
    private void LoadCircles()
    {
        var previousKind = SelectedCircle?.Kind ?? CircleTabKind.All;
        var previousFolded = SelectedCircle?.Folded;

        var (byCircle, uncircled) = repository.CallCountsByCircle();

        _buildingCircles = true;

        Circles.Clear();

        Circles.Add(new CircleTab(
            CircleTabKind.All, Localisation.T("overviewpage.cevre-hepsi"), null, "",
            byCircle.Values.Sum() + uncircled));

        foreach (var circle in repository.Circles())
        {
            var folded = Core.Text.TurkishText.NormalizeForSearch(circle.Name.Trim());

            Circles.Add(new CircleTab(
                CircleTabKind.Circle, circle.Name, folded, circle.Color,
                byCircle.GetValueOrDefault(folded)));
        }

        // Always last, always there. A person recorded five minutes ago is in no circle, and a
        // tab that could be taken away is a conversation that can vanish.
        Circles.Add(new CircleTab(
            CircleTabKind.Uncircled, Localisation.T("overviewpage.cevresiz"), null, "", uncircled));

        SelectedCircle =
            Circles.FirstOrDefault(t => t.Kind == previousKind
                                        && string.Equals(t.Folded, previousFolded, StringComparison.Ordinal))
            ?? Circles[0];

        _buildingCircles = false;

        OnPropertyChanged(nameof(HasCircleStrip));
        OnPropertyChanged(nameof(CirclesAreADropdown));
        OnPropertyChanged(nameof(CirclesAreAStrip));
    }

    /// <summary>
    /// The newest twelve of the chosen tab — asked of the database, not sieved out of a shared
    /// twelve.
    ///
    /// The cut comes before the filter on this screen: <c>ListCalls(limit: 12)</c> and then a
    /// filter in memory would show two rows on a tab whose circle holds forty-one conversations.
    /// So the circle travels into the query.
    ///
    /// Two queries for the names and the circles, never two per row. This screen used to ask the
    /// database for each row's contact by id — twelve rows, twelve extra queries — and the
    /// circles would have made it twenty-four. One list, one dictionary, the pattern the
    /// Görüşmeler page already uses.
    /// </summary>
    private void LoadRecent()
    {
        var recent = repository.ListCalls(limit: 12, circle: SelectedCircle?.Filter);
        var tags = repository.TagsOf(recent.Select(c => c.Id));
        var names = repository.ListContacts().ToDictionary(c => c.Id, c => c.Name);
        var circles = repository.CirclesByContact();

        Recent.Clear();

        foreach (var call in recent)
        {
            var name = call.ContactId is { } id ? names.GetValueOrDefault(id) : null;

            Recent.Add(new RecentCall(
                call,
                name ?? "İsimsiz",
                tags.GetValueOrDefault(call.Id, []),
                call.ContactId is { } contact ? circles.GetValueOrDefault(contact) : null));
        }

        OnPropertyChanged(nameof(RecentIsEmpty));
    }

    /// <summary>
    /// True when the chosen tab has nothing in it — said on screen, with the way back one click
    /// away, rather than an empty space under a strip.
    /// </summary>
    public bool RecentIsEmpty => Recent.Count == 0;

    /// <summary>"3 sözün vadesi geçti — 1 senin, 2 sana verilen": the one line the home screen keeps.</summary>
    public string OverdueLine => string.Format(
        Localisation.T("overviewpage.vadesi-gecen-ozet"),
        Overdue.Count,
        Overdue.Count(o => o.ByMe),
        Overdue.Count(o => !o.ByMe));

    /// <summary>
    /// Assembles the things worth interrupting the user about, most serious first.
    ///
    /// Kept short on purpose. A list of fifteen notices is a list nobody reads, and the whole
    /// point is that anything appearing here is worth acting on.
    /// </summary>
    private void RebuildAttention()
    {
        Attention.Clear();
        var current = settings();

        var unlabelled = repository.UnlabelledCalls();
        if (unlabelled.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                $"{unlabelled.Count} görüşme isimlendirilmemiş",
                "İsimsiz bir görüşme kişi geçmişinde görünmez. Kime ait olduğunu söylersen " +
                "defterine de işlenir.",
                "İsimlendir",
                AttentionAction.ShowUnlabelled));
        }

        var failed = repository.FailedCalls();
        if (failed.Count > 0)
        {
            // Grouped by reason, and the verb chosen by the reason. "Tekrar dene" on a card whose
            // failures all say "no service is configured" re-queued a hundred recordings into the
            // same wall; that card now goes to settings. Anything else opens the list, where each
            // row shows its own reason and its own retry.
            var reasons = failed
                .Select(f => Core.Asr.FailureText.Summarise(f.FailureReason))
                .GroupBy(r => r)
                .OrderByDescending(g => g.Count())
                .ToList();

            var allGuidance = failed.All(f => Core.Asr.FailureText.IsGuidance(f.FailureReason));

            var detail = reasons.Count == 1
                ? failed.Count == 1 ? reasons[0].Key : $"{failed.Count} görüşme aynı sebeple: {reasons[0].Key}"
                : $"{reasons.Count} farklı sebep; en sık: {reasons[0].Key}";

            Attention.Add(new AttentionItem(
                allGuidance ? AttentionKind.Action : AttentionKind.Blocking,
                $"{failed.Count} görüşme işlenemedi",
                detail,
                allGuidance ? "Ayarlar" : "Göster",
                allGuidance ? AttentionAction.OpenSettings : AttentionAction.ShowProcessing));
        }

        // Split by who made the promise. One mixed count taught nothing: "3 sözün tarihi
        // geçti" could be three things somebody owes you or three things you forgot you owe —
        // and the second kind is the one the user can fix this minute.
        var mine = Overdue.Where(o => o.ByMe).ToList();
        var theirs = Overdue.Where(o => !o.ByMe).ToList();

        if (mine.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                mine.Count == 1 ? "SENİN 1 sözünün tarihi geçti" : $"SENİN {mine.Count} sözünün tarihi geçti",
                string.Join(" · ", mine.Take(2).Select(o => o.Line))));
        }

        if (theirs.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                theirs.Count == 1 ? "1 sözün tarihi geçti" : $"{theirs.Count} sözün tarihi geçti",
                string.Join(" · ", theirs.Take(2).Select(o => o.Line))));
        }

        // Said on the main screen rather than buried in settings: in automatic mode a broken
        // driver silently turns into call audio being uploaded, and that should never be a
        // surprise.
        if (current.AudioMayLeaveTheMachine)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Info,
                current.AsrMode == TranscriptionMode.CloudOnly
                    ? "Yazıya dökme buluta gönderiliyor"
                    : "Yerel çalışmazsa buluta gönderilecek",
                // The endpoint that will actually be tried, not the model catalogue's label —
                // the same mismatch that had a real log announcing OpenAI two milliseconds
                // before it uploaded to somebody else's server.
                $"Ses {current.UsableSttEndpoints.FirstOrDefault()?.ResolvedName ?? current.CloudAsrModel.DisplayName} " +
                "servisine yükleniyor. Bu, görüşme sesinin makineden çıkması demek."));
        }

        if (current.ExportToObsidian && string.IsNullOrWhiteSpace(current.ObsidianVaultPath))
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Blocking,
                "Obsidian klasörü seçilmemiş",
                "Dışa aktarma açık ama nereye yazılacağı belli değil.",
                "Ayarlar",
                AttentionAction.OpenSettings));
        }

        // Reminders the user set for today: theirs, so safe to interrupt with — the same rule
        // as overdue promises, which already earned a place here.
        if (Due.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                Due.Count == 1 ? "1 hatırlatmanın günü geldi" : $"{Due.Count} hatırlatmanın günü geldi",
                string.Join(" · ", Due.Take(2).Select(d => d.Title))));
        }

        // Most serious first, applied where the list is BUILT. The sort used to live in
        // Refresh(), running on the stale pre-rebuild content — the code announced the fix and
        // then rebuilt the list in computation order, red faults below amber suggestions.
        // AttentionKind is declared in severity order, so ordering by it is enough.
        var ordered = Attention.OrderBy(i => i.Kind).ToList();
        Attention.Clear();
        foreach (var item in ordered) Attention.Add(item);
    }

    /// <summary>
    /// The board's state, and anything it says is due today.
    ///
    /// Both are cheap counts. Neither invents anything: the summary is what the user filed, and
    /// the due list is reminders they set themselves on conversations they chose. That is what
    /// makes this safe to show every single day — an application that starts with Windows and
    /// suggests things of its own accord becomes wallpaper within a week.
    /// </summary>
    private void LoadBoard()
    {
        Board.Clear();

        var cards = repository.OpenBoardCards();
        var tags = repository.TagsOf(cards.Select(c => c.CallId));

        // One list, one dictionary — the pattern the Görüşmeler page uses. Both loops below used
        // to ask the database for each card's contact by id, for names that come back in a single
        // query.
        var names = repository.ListContacts().ToDictionary(c => c.Id, c => c.Name);

        foreach (var card in cards)
        {
            var call = repository.GetCall(card.CallId);
            if (call is null) continue;

            var name = call.ContactId is { } cid ? names.GetValueOrDefault(cid) : null;

            // The first sentence of the machine's summary, when there is one. The card is a
            // pointer, not a claim: the quotes and their timestamps live in the window it opens.
            var summary = repository.GetSummary(card.CallId)?.Summary;
            var firstSentence = summary?.Split('.', 2)[0].Trim();

            var photo = call.ContactId is { } pid
                ? Services.ContactPhotoStore.PathFor(
                    repository.GetProfile(pid)?.PhotoFile, App.Paths?.Photos ?? "")
                : null;

            Board.Add(new PanelCard(
                card.CallId,
                name ?? "İsimsiz",
                call.StartedAt,
                $"{(int)call.Duration.TotalMinutes:00}:{call.Duration.Seconds:00}",
                string.IsNullOrWhiteSpace(firstSentence) ? null : firstSentence + ".",
                tags.GetValueOrDefault(card.CallId, []),
                card.RemindOn,
                photo));
        }

        Due.Clear();

        foreach (var card in repository.DueCards())
        {
            var call = repository.GetCall(card.CallId);
            if (call is null) continue;

            var name = call.ContactId is { } id ? names.GetValueOrDefault(id) : null;

            Due.Add(new DueCard(
                card.CallId,
                string.IsNullOrWhiteSpace(card.Title)
                    ? $"{name ?? "İsimsiz"} · {Dates.Day(call.StartedAt.ToLocalTime())}"
                    : card.Title!,
                card.RemindOn!.Value));
        }

        Birthdays.Clear();

        foreach (var (_, name, day, away) in repository.UpcomingBirthdays(
                     DateOnly.FromDateTime(DateTime.Today), withinDays: 7))
        {
            Birthdays.Add(away == 0
                ? $"{name} · bugün 🎂"
                : $"{name} · {away} gün sonra ({day:d MMMM})");
        }

        OnPropertyChanged(nameof(HasBoard));
        OnPropertyChanged(nameof(HasDue));
        OnPropertyChanged(nameof(HasBirthdays));
        OnPropertyChanged(nameof(TodayIsEmpty));

        BuildCalendar();
        LoadDayActions();
    }

    // ---- the panel's verbs --------------------------------------------------
    //
    // Each one writes and reloads. The panel is small by nature — a pile of conversations one
    // person is tracking by hand — so re-reading it wholesale after every change is simpler than
    // incremental bookkeeping and impossible to get out of sync.

    /// <summary>Puts a conversation on the panel. Dropping it there twice just moves it.</summary>
    public void AddToBoard(long callId)
    {
        repository.PutOnBoard(callId, BoardLane.ToLookAt);
        LoadBoard();
    }

    public void RemoveFromBoard(long callId)
    {
        repository.RemoveFromBoard(callId);
        LoadBoard();
    }

    /// <summary>
    /// Puts a conversation at the given panel index — the drop target of a drag, clamped, so a
    /// drop past the end simply means "last".
    /// </summary>
    public void MoveCardTo(long callId, int index)
    {
        var order = Board.Select(c => c.CallId).ToList();

        var from = order.IndexOf(callId);
        if (from >= 0) order.RemoveAt(from);

        order.Insert(Math.Clamp(index, 0, order.Count), callId);

        repository.ReorderBoard(order);
        LoadBoard();
    }

    public void MoveCardUp(long callId) => Nudge(callId, -1);
    public void MoveCardDown(long callId) => Nudge(callId, +1);

    /// <summary>Reminder in N days, or cleared with zero. Their card, their day, nothing invented.</summary>
    public void RemindCard(long callId, int days)
    {
        repository.RemindOn(callId, days <= 0
            ? null
            : DateOnly.FromDateTime(DateTime.Today).AddDays(days));

        LoadBoard();
    }

    private void Nudge(long callId, int delta)
    {
        var index = Board.Select(c => c.CallId).ToList().IndexOf(callId);
        if (index < 0) return;

        MoveCardTo(callId, index + delta);
    }

    // ---- the calendar -------------------------------------------------------
    //
    // Outlook's little month, wired to the reminder system: a day carrying a reminder is
    // marked, hovering says what and with whom, and clicking lists the reminders with the
    // conversation each one is tied to. It exists because a reminder you cannot see coming is
    // only an interruption on the day it fires — the calendar is where "yarın ne var?" lives.

    /// <summary>First day of the month the calendar is showing.</summary>
    [ObservableProperty] private DateOnly _calendarMonth =
        new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty] private string _calendarTitle = "";

    /// <summary>The 42 cells of a Monday-first six-week grid.</summary>
    public ObservableCollection<CalendarDay> CalendarDays { get; } = [];

    /// <summary>The day whose reminders are listed under the grid. Null when nothing is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCalendarDay))]
    [NotifyPropertyChangedFor(nameof(SelectedDayHeader))]
    private CalendarDay? _selectedCalendarDay;

    public bool HasSelectedCalendarDay => SelectedCalendarDay is not null;

    public string SelectedDayHeader => SelectedCalendarDay is { } day
        ? day.Date.ToDateTime(TimeOnly.MinValue).ToString("d MMMM dddd")
        : "";

    [RelayCommand]
    private void CalendarPrev()
    {
        CalendarMonth = CalendarMonth.AddMonths(-1);
        BuildCalendar();
    }

    [RelayCommand]
    private void CalendarNext()
    {
        CalendarMonth = CalendarMonth.AddMonths(1);
        BuildCalendar();
    }

    [RelayCommand]
    private void CalendarToday()
    {
        CalendarMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        BuildCalendar();
    }

    /// <summary>Cell click: pick the day when it holds anything, clear the pick when it is bare.</summary>
    public void SelectCalendarDay(CalendarDay day)
        => SelectedCalendarDay = day.HasAnything ? day : null;

    // ---- the day's suggested actions ---------------------------------------
    //
    // Machine suggestions, clearly captioned as such, in their own section UNDER the user's
    // own reminders — never mixed with them. Due ones plus the last few days' undated ones.

    public ObservableCollection<DayAction> DayActions { get; } = [];

    public bool HasDayActions => DayActions.Count > 0;

    private void LoadDayActions()
    {
        DayActions.Clear();

        foreach (var (action, contactName) in repository.OpenActions(DateOnly.FromDateTime(DateTime.Today)))
            DayActions.Add(new DayAction(action, contactName));

        OnPropertyChanged(nameof(HasDayActions));
        OnPropertyChanged(nameof(TodayIsEmpty));
    }

    /// <summary>The user's verdict on a suggestion from the home screen.</summary>
    public void SetDayActionStatus(DayAction row, ActionStatus status)
    {
        repository.SetActionStatus(row.Item.Id, status);
        DayActions.Remove(row);
        OnPropertyChanged(nameof(HasDayActions));

        // Told to every other list showing the same suggestion; see CallWindowViewModel.
        Services.CallActions.NotifyChanged();
    }

    private void BuildCalendar()
    {
        // Monday-first: the Turkish week starts there, and a calendar that disagrees with the
        // one on the user's wall reads as broken even when every date on it is right.
        var lead = ((int)CalendarMonth.DayOfWeek + 6) % 7;
        var start = CalendarMonth.AddDays(-lead);

        var reminders = repository.RemindersBetween(start, start.AddDays(41))
            .GroupBy(r => r.Day)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CalendarReminder>)
                    [.. g.Select(r => new CalendarReminder(r.CallId, r.ContactName, r.Title))]);

        var birthdays = repository.UpcomingBirthdays(start, withinDays: 41)
            .GroupBy(b => b.Day)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(b => b.Name)]);

        // The user's own promise deadlines — the marker this calendar exists to make
        // unforgettable: nobody sets a reminder for a promise they don't know they'll forget.
        var promises = repository.OwnCommitmentsBetween(start, start.AddDays(41))
            .GroupBy(p => p.Day)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CalendarPromise>)
                    [.. g.Select(p => new CalendarPromise(p.CallId, p.ContactName, p.Obligation))]);

        var today = DateOnly.FromDateTime(DateTime.Today);

        CalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);

            CalendarDays.Add(new CalendarDay(
                date,
                InMonth: date.Month == CalendarMonth.Month,
                IsToday: date == today,
                reminders.GetValueOrDefault(date, []),
                birthdays.GetValueOrDefault(date, []),
                promises.GetValueOrDefault(date, [])));
        }

        CalendarTitle = Dates.Month(CalendarMonth);

        // The pick survives a rebuild only while its day still has something to show.
        SelectedCalendarDay = SelectedCalendarDay is { } picked
            ? CalendarDays.FirstOrDefault(d => d.Date == picked.Date && d.HasAnything)
            : null;
    }
}

/// <summary>One suggested action on the home screen, with the person it concerns.</summary>
public sealed record DayAction(ActionItem Item, string ContactName)
{
    public long CallId => Item.CallId;

    public string Line => $"{Item.Action} — {ContactName}";

    public bool IsDue => Item.DeadlineDate is { } day && day <= DateOnly.FromDateTime(DateTime.Today);

    public string? DueText => Item.DeadlineDate is { } day
        ? Dates.Day(day)
        : null;
}

/// <summary>One reminder as the calendar tells it: who, why, and the conversation it hangs on.</summary>
public sealed record CalendarReminder(long CallId, string ContactName, string Title)
{
    public string Line => Title.Length > 0 ? $"{ContactName} — {Title}" : ContactName;
}

/// <summary>One of the user's own promise deadlines, anchored to the conversation it was made in.</summary>
public sealed record CalendarPromise(long CallId, string ContactName, string Obligation)
{
    public string Line => $"Sen: {Obligation} — {ContactName}";
}

/// <summary>One cell of the month grid.</summary>
public sealed record CalendarDay(
    DateOnly Date,
    bool InMonth,
    bool IsToday,
    IReadOnlyList<CalendarReminder> Reminders,
    IReadOnlyList<string> BirthdayNames,
    IReadOnlyList<CalendarPromise> Promises)
{
    public string Label => Date.Day.ToString();

    public bool HasReminders => Reminders.Count > 0;
    public bool HasBirthday => BirthdayNames.Count > 0;
    public bool HasPromises => Promises.Count > 0;
    public bool HasAnything => HasReminders || HasBirthday || HasPromises;

    /// <summary>What hovering says: reminders, own promise deadlines, birthdays — one per line.</summary>
    public string? Tooltip => !HasAnything
        ? null
        : string.Join(
            Environment.NewLine,
            Reminders.Select(r => $"🔔 {r.Line}")
                .Concat(Promises.Select(p => $"🤝 {p.Line} (söz)"))
                .Concat(BirthdayNames.Select(n => $"🎂 {n}")));
}

/// <summary>One conversation on the important pile, as the first screen shows it.</summary>
public sealed record PanelCard(
    long CallId,
    string ContactName,
    DateTimeOffset StartedAt,
    string Length,
    string? SummaryLine,
    IReadOnlyList<string> Tags,
    DateOnly? RemindOn,
    string? PhotoPath = null)
{
    public bool HasPhoto => PhotoPath is not null;

    public string When => Dates.Moment(StartedAt.ToLocalTime());

    public bool HasSummary => SummaryLine is not null;
    public bool HasTags => Tags.Count > 0;
    public bool HasReminder => RemindOn is not null;

    public string ReminderText => RemindOn is { } day ? $"Hatırlat: {Dates.Day(day)}" : "";
}

/// <summary>One reminder that has come due, as the first screen lists it.</summary>
public sealed record DueCard(long CallId, string Title, DateOnly Day)
{
    public string When => Day == DateOnly.FromDateTime(DateTime.Now)
        ? "bugün"
        : Day < DateOnly.FromDateTime(DateTime.Now)
            ? $"{(DateOnly.FromDateTime(DateTime.Now).DayNumber - Day.DayNumber)} gün geçti"
            : Dates.Day(Day);
}
