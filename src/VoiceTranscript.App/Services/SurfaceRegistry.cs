using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Which of the two things a surface does with a concept.
/// </summary>
public enum Completeness
{
    /// <summary>Every verb the concept names can be done here.</summary>
    Full,

    /// <summary>The concept is shown here and cannot be changed here. Not one verb, not "just this one".</summary>
    ReadOnly,
}

/// <summary>One act, and the member that carries it on a particular surface.</summary>
/// <param name="Verb">The act in the product's own words — "Tutuldu", "Reddet", "Sil".</param>
/// <param name="Member">
/// The command property or the click handler that performs it. Named rather than inferred,
/// because the same act is called <c>DismissCommand</c> on one screen and
/// <c>DismissPromiseCommand</c> on the next, and pretending otherwise would either force a
/// rename the user never asked for or make the rule unfalsifiable.
/// </param>
public sealed record VerbBinding(string Verb, string Member);

/// <summary>One place a concept is shown.</summary>
/// <param name="Name">What the user calls this screen.</param>
/// <param name="Owner">The view model or code-behind that holds the verbs.</param>
/// <param name="Markup">The markup file, relative to <c>Views</c>, that has to bind them.</param>
public sealed record ConceptSurface(
    string Name,
    Type Owner,
    string Markup,
    Completeness Completeness,
    params VerbBinding[] Bindings);

/// <summary>One row of the registry: a thing the user sees, everywhere they see it.</summary>
public sealed record ConceptRow(
    string Name,
    Ground Ground,
    IReadOnlyList<string> Verbs,
    IReadOnlyList<ConceptSurface> Surfaces);

/// <summary>
/// Every concept the user sees, with one row each.
///
/// The user asked for the interface to feel like one thing rather than a pile of screens. This
/// is the half of that answer a test can hold: a concept has an identity, a ground it stands on,
/// a set of verbs, and a list of the places it appears — and each of those places either does
/// all the verbs or none of them. "All or none" is the part that matters. A screen that offers
/// three of four verbs is worse than one that offers none, because the missing fourth is
/// invisible: the user does not learn that the act exists elsewhere, they learn that it does not
/// exist.
///
/// Nothing here is user-visible, so nothing here goes through the dictionary. It is the
/// interface's own description of itself, and the tests beside it are the only reader.
///
/// Adding a screen means adding a line here. That is the cost, and it is the point: a screen
/// nobody wrote a line for is a screen nobody decided the verbs for.
/// </summary>
public static class SurfaceRegistry
{
    /// <summary>The user's own promise, or the other side's: an obligation with a quote under it.</summary>
    public const string Promise = "Söz";

    /// <summary>A step the analysis proposed after a call, anchored to the sentence that prompted it.</summary>
    public const string Suggestion = "Öneri";

    /// <summary>Something the ledger noticed: a passed deadline, a moved date, a contradiction.</summary>
    public const string Finding = "Bulgu";

    /// <summary>A line the user wrote for themselves.</summary>
    public const string Todo = "Yapılacak";

    /// <summary>A day the user put on a conversation.</summary>
    public const string Reminder = "Hatırlatma";

    /// <summary>One counted occurrence of a speaking habit, with the second it happened at.</summary>
    public const string Moment = "An";

    /// <summary>Somebody the user talks to.</summary>
    public const string Person = "Kişi";

    /// <summary>The model's reading of a person or a call. Signed, dated, and switchable off.</summary>
    public const string Reading = "Okuma";

    public static IReadOnlyList<ConceptRow> All { get; } =
    [
        new(Promise, Ground.Evidence, ["Tutuldu", "Reddet"],
        [
            new("Sözler", typeof(PromisesViewModel), "PromisesPage.xaml", Completeness.Full,
                new VerbBinding("Tutuldu", "FulfilCommand"), new VerbBinding("Reddet", "DismissCommand")),

            new("Kişi kartı", typeof(ContactCardViewModel), "ContactCardView.xaml", Completeness.Full,
                new VerbBinding("Tutuldu", "FulfilCommand"), new VerbBinding("Reddet", "DismissPromiseCommand")),

            new("Görüşme penceresi", typeof(CallWindowViewModel), "CallWindow.xaml", Completeness.Full,
                new VerbBinding("Tutuldu", "FulfilCommitmentCommand"), new VerbBinding("Reddet", "DismissCommitmentCommand")),

            // The month shows deadlines so they cannot be forgotten; ruling on one belongs where
            // the quote is, which is two clicks away and never here.
            new("Takvim", typeof(CalendarViewModel), "CalendarPage.xaml", Completeness.ReadOnly),
            new("Genel bakış", typeof(OverviewViewModel), "OverviewPage.xaml", Completeness.ReadOnly),

            // Six seconds while the phone rings is not when anybody rules on a promise.
            new("Arayan katmanı", typeof(Views.CallerOverlay), "CallerOverlay.xaml", Completeness.ReadOnly),
        ]),

        new(Suggestion, Ground.Evidence, ["Yaptım", "Reddet"],
        [
            new("Yapılacaklar", typeof(TodoViewModel), "TodoPage.xaml", Completeness.Full,
                new VerbBinding("Yaptım", "ToggleCommand"), new VerbBinding("Reddet", "DismissCommand")),

            new("Görüşme penceresi", typeof(Views.CallWindow), "CallWindow.xaml", Completeness.Full,
                new VerbBinding("Yaptım", "ActionDone_Click"), new VerbBinding("Reddet", "ActionHide_Click")),

            new("Kişiler", typeof(Views.ContactsPage), "ContactsPage.xaml", Completeness.Full,
                new VerbBinding("Yaptım", "CallActionDone_Click"), new VerbBinding("Reddet", "CallActionHide_Click")),

            new("Genel bakış", typeof(Views.OverviewPage), "OverviewPage.xaml", Completeness.Full,
                new VerbBinding("Yaptım", "DayActionDone_Click"), new VerbBinding("Reddet", "DayActionHide_Click")),
        ]),

        new(Finding, Ground.Evidence, ["Reddet"],
        [
            new("Defter", typeof(LedgerViewModel), "LedgerPage.xaml", Completeness.Full,
                new VerbBinding("Reddet", "DismissCommand")),

            new("Kişiler", typeof(ContactsViewModel), "ContactsPage.xaml", Completeness.Full,
                new VerbBinding("Reddet", "DismissFlagCommand")),

            new("Görüşme penceresi", typeof(CallWindowViewModel), "CallWindow.xaml", Completeness.Full,
                new VerbBinding("Reddet", "DismissFlagCommand")),

            // The flow is a history of what happened, in order. Ruling on a line would edit the
            // history from inside it.
            new("Kişi penceresi", typeof(ContactWindowViewModel), "ContactWindow.xaml", Completeness.ReadOnly),
        ]),

        new(Todo, Ground.UserWriting, ["Ekle", "Yaptım", "Sil"],
        [
            new("Yapılacaklar", typeof(TodoViewModel), "TodoPage.xaml", Completeness.Full,
                new VerbBinding("Ekle", "AddCommand"), new VerbBinding("Yaptım", "ToggleCommand"), new VerbBinding("Sil", "DeleteCommand")),
        ]),

        // "Kur" is an act on a CONVERSATION — it is how a call gets a day — so it is not a verb
        // of the reminder row. What one can do to the row itself is take it off.
        new(Reminder, Ground.UserWriting, ["Kaldır"],
        [
            new("Yapılacaklar", typeof(TodoViewModel), "TodoPage.xaml", Completeness.Full,
                new VerbBinding("Kaldır", "ToggleCommand")),

            new("Takvim", typeof(CalendarViewModel), "CalendarPage.xaml", Completeness.ReadOnly),
        ]),

        new(Moment, Ground.Evidence, ["Doğru", "Yanlış duyulmuş", "Bu o değil"],
        [
            new("Aynam", typeof(MirrorViewModel), "MirrorPage.xaml", Completeness.Full,
                new VerbBinding("Doğru", "CorrectCommand"),
                new VerbBinding("Yanlış duyulmuş", "MisheardCommand"),
                new VerbBinding("Bu o değil", "NotThatCommand")),

            new("Görüşme penceresi", typeof(CallWindowViewModel), "CallWindow.xaml", Completeness.Full,
                new VerbBinding("Doğru", "HabitCorrectCommand"),
                new VerbBinding("Yanlış duyulmuş", "HabitMisheardCommand"),
                new VerbBinding("Bu o değil", "HabitNotThatCommand")),

            new("Kişi kartı", typeof(ContactCardViewModel), "ContactCardView.xaml", Completeness.Full,
                new VerbBinding("Doğru", "CorrectCommand"),
                new VerbBinding("Yanlış duyulmuş", "MisheardCommand"),
                new VerbBinding("Bu o değil", "NotThatCommand")),
        ]),

        new(Person, Ground.UserWriting, ["Yeniden adlandır", "Birleştir", "Sil"],
        [
            new("Kişiler", typeof(Views.ContactsPage), "ContactsPage.xaml", Completeness.Full,
                new VerbBinding("Yeniden adlandır", "RenameContact_Click"),
                new VerbBinding("Birleştir", "MergeContact_Click"),
                new VerbBinding("Sil", "DeleteContact_Click")),

            new("Kişi penceresi", typeof(Views.ContactWindow), "ContactWindow.xaml", Completeness.ReadOnly),
            new("Genel bakış", typeof(Views.OverviewPage), "OverviewPage.xaml", Completeness.ReadOnly),
        ]),

        new(Reading, Ground.ModelOpinion, ["Katılmıyorum"],
        [
            new("Kişi kartı", typeof(ContactCardViewModel), "ContactCardView.xaml", Completeness.Full,
                new VerbBinding("Katılmıyorum", "DisagreeWithOpinionCommand")),

            // The call's reading can be re-run or thrown away, which is housekeeping on a paid
            // request. Disagreeing with it — a judgement kept beside it — is the contact card's.
            new("Görüşme penceresi", typeof(CallWindowViewModel), "CallWindow.xaml", Completeness.ReadOnly),
        ]),
    ];

    /// <summary>The row for one concept, by the name it is registered under.</summary>
    public static ConceptRow Of(string concept) =>
        All.FirstOrDefault(row => row.Name == concept)
        ?? throw new InvalidOperationException($"Kayıtta böyle bir kavram yok: {concept}");
}
