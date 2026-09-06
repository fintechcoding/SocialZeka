using System.Threading;
using System.Windows;
using VoiceTranscript.App;
using VoiceTranscript.App.Views;

namespace VoiceTranscript.Tests;

/// <summary>
/// Actually builds every screen.
///
/// This exists because of a whole class of failure the compiler does not see. A
/// <c>{StaticResource Foo}</c> whose key was renamed, a <c>Symbol="Foo24"</c> that is not a real
/// icon, a pack URI that only resolves when the application is the entry assembly, a style
/// applied to the wrong control type — all of these compile cleanly and then throw the moment the
/// window is loaded. The application starts, the user clicks something, and it dies. That has
/// happened in this project more than once, each time found by a person rather than by a test.
///
/// So the markup is really parsed and the controls are really constructed, on a real STA thread
/// with the real application resources. Nothing is shown.
/// </summary>
public class WindowSmokeTests
{
    /// <summary>
    /// Every screen, built on one thread.
    ///
    /// One test rather than four, and that is not laziness. WPF resources belong to the thread
    /// that created them and the brushes in a theme dictionary are not frozen, so a second STA
    /// thread touching the same <see cref="Application"/> fails with a cross-thread error that
    /// says nothing about the markup. One thread, one application, every screen.
    /// </summary>
    [Fact]
    public void EveryScreenBuildsWithoutThrowing()
    {
        var failures = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                // InitializeComponent is what loads App.xaml into Application.Resources, and it
                // is generated rather than called from the constructor. Loading App.xaml as a
                // plain ResourceDictionary does not work: WPF tries to build a second
                // Application from it and throws.
                if (Application.Current is null) new App.App().InitializeComponent();

                Build("Ana pencere", () => new App.MainWindow(), failures);
                Build("Genel bakış", () => new OverviewPage(), failures);
                Build("Defter", () => new LedgerPage(), failures);
                Build("Takvim", () => new CalendarPage(), failures);
                Build("Sözler", () => new PromisesPage(), failures);
                Build("Yapılacaklar", () => new TodoPage(), failures);
                Build("Kişiler", () => new ContactsPage(), failures);
                Build("Arama", () => new SearchPage(), failures);
                Build("Sor", () => new AskPage(), failures);
                Build("Durum", () => new HealthPage(), failures);
                Build("İşlem durumu", () => new ProcessingPage(), failures);
                Build("Yapay zekâ durumu", () => new AiStatusPage(), failures);

                // The windows, not just the pages.
                //
                // These four hold the densest markup in the product — Settings alone is over
                // eight hundred lines — and none of them was covered until every literal string
                // in the application was moved behind a lookup, which rewrote all of their
                // markup at once. A page that no test builds is a page whose resource keys are
                // verified by the user opening it.
                var paths = new VoiceTranscript.Core.Configuration.AppPaths(
                    Path.Combine(Path.GetTempPath(), $"vt-smoke-{Guid.NewGuid():N}"));

                paths.EnsureCreated();

                var setup = new VoiceTranscript.App.Services.EnvironmentSetup(paths);
                var settings = new VoiceTranscript.Core.Configuration.AppSettings();

                using var http = new System.Net.Http.HttpClient();

                Build("Kayıt şeridi", () => new RecordingOverlay(), failures);

                Build("Arayan katmanı", () => new CallerOverlay(), failures);

                Build("Ayarlar", () => new SettingsWindow(
                    new VoiceTranscript.App.ViewModels.SettingsViewModel(settings, paths, http)), failures);

                Build("Kurulum", () => new SetupWindow(new VoiceTranscript.App.ViewModels.SetupViewModel(
                    setup,
                    new VoiceTranscript.App.Services.HardwareProbe(paths, setup, paths.Root),
                    () => settings,
                    paths.Root)), failures);

                // The two windows that appear because of a call, rather than because somebody
                // opened a menu.
                //
                // These were the last ones nobody built. That mattered more than it sounds: the
                // labelling window is shown after every call with a new contact, so a renamed
                // resource key or an icon that is not a real symbol would surface as the recording
                // finishing and no question ever being asked — which is indistinguishable from the
                // recorder failing, and is exactly what was reported.
                var database = new VoiceTranscript.Core.Storage.Database(
                    Path.Combine(paths.Root, "smoke.db"));

                database.Migrate();
                var repository = new VoiceTranscript.Core.Storage.Repository(database);

                Build("Görüşmeyi isimlendir", () => new LabelCallWindow(
                    repository,
                    callId: 1,
                    duration: TimeSpan.FromMinutes(3),
                    observedTitle: "Serdal",
                    app: VoiceTranscript.Core.Domain.CallApp.WhatsApp,
                    audioSummary: "mic: tamam; far: tamam",
                    hasSilentStream: false), failures);

                Build("Görüşmeyi taşı", () => new MoveCallWindow(
                    repository,
                    currentContactName: "Uliana",
                    observedTitle: "(3) WhatsApp",
                    app: VoiceTranscript.Core.Domain.CallApp.WhatsApp,
                    startedAt: DateTimeOffset.Now,
                    duration: TimeSpan.FromMinutes(3),
                    ledgerEntries: 2), failures);

                Build("Kişiyi yeniden adlandır", () => new RenameContactWindow("Serdal"), failures);

                // Every transcript a call has had. Built here because it is opened from inside
                // another window, which is the surest way for a page to ship unbuilt.
                Build("Dökümler", () => new TranscriptVersionsWindow(repository, callId: 1), failures);

                // The densest new markup in the product: four tabs, a chat layout, two
                // converters and a player, all of which compile cleanly and would throw on
                // first open if a resource key were wrong.
                Build("Görüşme penceresi", () => new CallWindow(
                    new VoiceTranscript.App.ViewModels.CallWindowViewModel(
                        repository, () => settings, http, callId: 1)), failures);

                // Four tabs, a search list, a ledger and a notes editor — and it reaches the
                // database on construction, so this also proves the queries behind it run.
                Build("Kişi penceresi", () => new ContactWindow(
                    new VoiceTranscript.App.ViewModels.ContactWindowViewModel(
                        repository, contactId: 1)), failures);

                // Builds both of its lists from the catalogue and from measured usage, so this
                // covers the queries behind the speed column as well as the markup.
                Build("Yeniden işle", () => new ReprocessWindow(
                    repository, settings, "Serdal", count: 1), failures);

                // Reads the call's existing card on construction, so the prefill query runs too —
                // against a call that genuinely HAS a card with a reminder. Built once against an
                // empty table, this passed while the shipped dialog threw in its constructor on
                // any call the user had put on the board: the materialisation of the dates only
                // happens when a row comes back.
                var reminded = repository.InsertCall(new VoiceTranscript.Core.Domain.Call
                {
                    App = VoiceTranscript.Core.Domain.CallApp.WhatsApp,
                    StartedAt = DateTimeOffset.Now,
                    State = VoiceTranscript.Core.Domain.ProcessingState.Analysed,
                });
                repository.PutOnBoard(
                    reminded, VoiceTranscript.Core.Domain.BoardLane.ToLookAt, title: "Evrak sözü");
                repository.RemindOn(reminded, DateOnly.FromDateTime(DateTime.Today.AddDays(3)));

                Build("Hatırlat", () => new RemindWindow(
                    repository, callId: reminded, subject: "Serdal · 12 Mart"), failures);

                // The user's wording and date beside the spoken ones. Built against a row that
                // already carries a correction, so the prefill reads every column the dialog
                // shows and the "Düzeltmeyi kaldır" branch is drawn.
                var promised = repository.InsertCommitment(new VoiceTranscript.Core.Domain.Commitment
                {
                    CallId = reminded,
                    Quote = "cumaya sana yollarım",
                    QuoteStartMs = 4200,
                    Obligation = "Sözleşmeyi göndermek",
                    DeadlineRaw = "cuma",
                    DeadlineDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2)),
                });
                repository.SetUserObligation(promised, "Sözleşme taslağını göndermek");

                Build("Sözü düzenle", () => new EditPromiseWindow(
                    repository,
                    repository.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promised).Commitment), failures);

                // Loads the definitions and renders every offered icon name through the
                // converter, so an invalid SymbolRegular in the choices would fail here.
                Build("Etiketler", () => new TagManagerWindow(repository), failures);

                // Seeded first, so the three lists are really populated and every row template is
                // really rendered — an empty ItemsControl would build cleanly and prove nothing.
                VoiceTranscript.Core.Analysis.HabitLexicon.Seed(repository);
                Build("Sözlükler", () => new SozlukWindow(repository), failures);

                // Against a call that already carries an intent, so the prefill and the "Kaldır"
                // branch are both drawn rather than only the empty form.
                repository.SaveCallIntent(reminded, "rakamı ben söylemeyeceğim");
                Build("Niyet", () => new NiyetWindow(
                    repository, callId: reminded, subject: "Serdal · 12 Mart"), failures);

                Build("Kişileri birleştir", () => new MergeContactWindow(
                    repository,
                    new VoiceTranscript.Core.Domain.Contact { Id = 1, Name = "Serdal" }), failures);

                // Constructed only, never shown, so the fetch its Loaded handler starts never
                // runs — which is what makes this safe to build against a real HttpClient with
                // no network.
                Build("Model seçici", () => new ModelPickerWindow(
                    http,
                    VoiceTranscript.Core.Llm.LlmProviderKind.OpenRouter,
                    "OpenRouter (bulut)",
                    "https://openrouter.ai/api/v1",
                    apiKey: null,
                    currentModel: "anthropic/claude-haiku-4.5"), failures);

                Build("Güncelleme", () => new UpdateWindow(
                    new VoiceTranscript.App.Services.UpdateService(http, paths),
                    new VoiceTranscript.Core.Update.Release(
                        VoiceTranscript.Core.Update.AppVersion.Parse("1.2.0"),
                        "Yenilikler burada",
                        "SocialZeka-Setup-1.2.0-win-x64.exe",
                        "https://example/setup.exe",
                        "https://example/SHA256SUMS",
                        68_000_000),
                    new VoiceTranscript.Core.Update.UpdateGuard
                    {
                        IsRecording = false,
                        IsProcessing = false,
                        QueueDepth = 0,
                        DataDirectoryOverridden = false,
                        InstalledNormally = true,
                        FreeDiskBytes = 10L * 1024 * 1024 * 1024,
                        InstallerBytes = 68_000_000,
                        RestorePending = false,
                    }), failures);

                // The theme entry point, against a window that was constructed but never shown —
                // exactly the state it meets on every start and from the tray's settings entry.
                // v2.1.0 shipped a crash here: UnWatch refuses a window that has not loaded, and
                // only a real unloaded window catches that class of precondition. Every choice,
                // twice each, because the second call meets an already-applied state; "light"
                // last so the shared Application resources end where the other builds began.
                Build("Tema (yüklenmemiş pencereyle)", () =>
                {
                    var themed = new App.MainWindow();
                    foreach (var choice in new[] { "system", "dark", null, "light" })
                    {
                        App.App.ApplyTheme(choice, themed);
                        App.App.ApplyTheme(choice, themed);
                    }

                    return themed;
                }, failures);

                database.ClearPool();

                try
                {
                    Directory.Delete(paths.Root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
            catch (Exception e)
            {
                failures.Add($"Uygulama başlatılamadı: {Describe(e)}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Generous: the first construction has to spin up the WPF stack and load the Fluent theme
        // dictionaries. The screens themselves take milliseconds.
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "Pencere kurulumu zaman aşımına uğradı.");

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    private static void Build(string name, Func<object> construct, List<string> failures)
    {
        try
        {
            _ = construct();
        }
        catch (Exception e)
        {
            failures.Add($"{name}:\n{Describe(e)}");
        }
    }

    /// <summary>
    /// Unwraps the inner exceptions.
    ///
    /// A XamlParseException only ever says that something threw. The inner exception is the one
    /// naming the property, resource or icon that is actually wrong, and without it this test
    /// reports a failure nobody can act on.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var detail = new System.Text.StringBuilder();

        for (var e = exception; e is not null; e = e.InnerException)
            detail.AppendLine($"  {e.GetType().Name}: {e.Message}");

        return detail.ToString().TrimEnd();
    }
}
