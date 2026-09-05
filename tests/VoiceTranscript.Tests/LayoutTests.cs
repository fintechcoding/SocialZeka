using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.App.Views;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// How much of the conversation window the conversation gets.
///
/// Complaint 8 ("dikey alan dar"): at the window's own opening size the transcript had less than
/// half the height, because seven bands of chrome — title, labels, attention, tabs, quality,
/// talk share, player — stacked above and below it at around 366 px. The plan (§4.3) sets a
/// budget: everything above the transcript fits in 240 px, and the transcript keeps at least
/// 400 px of a 720 px window.
///
/// Measured rather than eyeballed, because a band grows back one margin at a time and nobody
/// notices until the window is opened on a laptop. The smoke test proves the markup builds; this
/// one lays it out at 880×720 with a seeded conversation — labels, an attention strip, a
/// quality line, talk share, two stored transcripts — so every band that can show is showing
/// when the ruler is held up.
///
/// <b>The ruler runs in a child process.</b> WPF allows one Application per process, it belongs
/// to the thread that created it, and the theme brushes cannot be frozen — so a second thread
/// building a window against it dies with "Cannot access Freezable across threads". The smoke
/// test already owns that one thread and that one Application. Whichever of the two classes
/// started first, the other failed, and in a parallel run that was a coin toss on every build.
/// A second process is the cheapest second Application there is: this class starts the test
/// module again on itself alone, the copy measures on its own STA thread and writes the numbers
/// to a file, and the assertions here read them. Costs two seconds; costs no flakiness.
/// </summary>
public class LayoutTests
{
    /// <summary>The window's own opening size (CallWindow.xaml Width/Height).</summary>
    private static readonly Size WindowSize = new(880, 720);

    /// <summary>PLAN-SOSYALZEKA §4.3: the bands above the transcript, at most.</summary>
    private const double ChromeBudget = 240;

    /// <summary>PLAN-SOSYALZEKA §4.11 row 8: the transcript, at least.</summary>
    private const double TranscriptMinimum = 400;

    /// <summary>Set in the child: measure here, on this process's own Application.</summary>
    private const string HostVariable = "VOICETRANSCRIPT_LAYOUT_HOST";

    /// <summary>Set in the child: where to write what was measured.</summary>
    private const string OutputVariable = "VOICETRANSCRIPT_LAYOUT_OUTPUT";

    /// <summary>
    /// Goes red when a band above the transcript grows — a strip that wraps to two lines, a
    /// margin that came back, a player that shows while there is no audio to play — or when the
    /// transcript is squeezed under 400 px in a 720 px window. Audio not loaded, so the player
    /// is folded away entirely.
    /// </summary>
    [Fact]
    public void TranscriptKeepsMostOfTheWindowWhenThereIsNoAudio()
    {
        var layout = Measured.Value.Folded;

        Assert.True(layout.ChromeRows <= ChromeBudget,
            $"Dökümün üstündeki bantlar {layout.ChromeRows:0} px, sınır {ChromeBudget} px.\n{layout.Breakdown}");
        Assert.True(layout.Transcript >= TranscriptMinimum,
            $"Döküm {layout.Transcript:0} px, en az {TranscriptMinimum} px olmalı.\n{layout.Breakdown}");
    }

    /// <summary>
    /// Goes red when the open player — waveform and transport — costs the transcript its 400 px,
    /// or when a band above the transcript grows while audio is loaded. The player is faked
    /// loaded without audio: its height is markup, not sound.
    /// </summary>
    [Fact]
    public void TranscriptKeepsMostOfTheWindowWithThePlayerOpen()
    {
        var layout = Measured.Value.Open;

        Assert.True(layout.ChromeRows <= ChromeBudget,
            $"Dökümün üstündeki bantlar {layout.ChromeRows:0} px, sınır {ChromeBudget} px.\n{layout.Breakdown}");
        Assert.True(layout.Transcript >= TranscriptMinimum,
            $"Döküm {layout.Transcript:0} px, en az {TranscriptMinimum} px olmalı.\n{layout.Breakdown}");
    }

    /// <summary>One pass of the ruler for both facts: in the child, done here; otherwise, asked of the child.</summary>
    private static readonly Lazy<Measurements> Measured = new(
        () => Environment.GetEnvironmentVariable(HostVariable) == "1" ? MeasureOnStaThread() : MeasureInChildProcess(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // ---- the parent: a second process for a second Application -------------

    private static Measurements MeasureInChildProcess()
    {
        var module = Path.ChangeExtension(typeof(LayoutTests).Assembly.Location, ".exe");
        Assert.True(File.Exists(module), $"Test modülü bulunamadı: {module}");

        var output = Path.Combine(Path.GetTempPath(), $"vt-layout-{Guid.NewGuid():N}.json");

        var start = new ProcessStartInfo(module)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("--filter-class");
        start.ArgumentList.Add(typeof(LayoutTests).FullName!);
        start.Environment[HostVariable] = "1";
        start.Environment[OutputVariable] = output;

        using var child = Process.Start(start)!;

        // Both streams drained while it runs, so a chatty child cannot block on a full pipe.
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();

        if (!child.WaitForExit(TimeSpan.FromSeconds(180)))
        {
            try { child.Kill(entireProcessTree: true); } catch (Exception) { }
            Assert.Fail("Ölçüm süreci zaman aşımına uğradı.");
        }

        try
        {
            Assert.True(File.Exists(output),
                $"Ölçüm süreci sonuç yazmadı (çıkış kodu {child.ExitCode}).\n{stdout.Result}\n{stderr.Result}");

            return JsonSerializer.Deserialize<Measurements>(File.ReadAllText(output))
                ?? throw new InvalidOperationException("Ölçüm dosyası boş.");
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    // ---- the child: one STA thread, one Application, both scenarios --------

    private static Measurements MeasureOnStaThread()
    {
        Measurements? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // The same entry the smoke test uses: InitializeComponent loads App.xaml into
                // Application.Resources, which is what every StaticResource in the window needs.
                if (Application.Current is null) new App.App().InitializeComponent();

                result = MeasureBothScenarios();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "Pencere ölçümü zaman aşımına uğradı.");

        if (failure is not null) Assert.Fail($"Görüşme penceresi ölçülemedi:\n{Describe(failure)}");

        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } output)
            File.WriteAllText(output, JsonSerializer.Serialize(result));

        return result!;
    }

    private static Measurements MeasureBothScenarios()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), $"vt-layout-{Guid.NewGuid():N}"));
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        var repository = new Repository(database);
        var callId = SeedConversation(repository);

        var settings = new AppSettings();
        using var http = new HttpClient();

        try
        {
            var model = new CallWindowViewModel(repository, () => settings, http, callId);
            var window = new CallWindow(model);

            try
            {
                // No audio behind the call, so the player has nothing to load and is folded.
                var folded = Measure(window, "oynatıcı katlı");

                // The player's height is markup, not sound: the flag is enough to unfold it.
                model.Playback.IsLoaded = true;
                var open = Measure(window, "oynatıcı açık");

                return new Measurements(folded, open);
            }
            finally
            {
                model.Dispose();
            }
        }
        finally
        {
            database.ClearPool();

            try
            {
                Directory.Delete(paths.Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A conversation that lights every band: a named contact, two labels, lines from both sides
    /// with one interruption, two stored transcripts with an engine and a coverage figure, and
    /// one flag so the attention strip shows.
    /// </summary>
    private static long SeedConversation(Repository repository)
    {
        var contactId = repository.UpsertContact("Gürhan Abi", CallApp.WhatsApp);

        var callId = repository.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            Direction = CallDirection.Outgoing,
            StartedAt = DateTimeOffset.Parse("2026-09-04T14:02:00+03:00"),
            Duration = TimeSpan.FromMinutes(18) + TimeSpan.FromSeconds(49),
            State = ProcessingState.Analysed,
        });

        (string Text, bool Me, int Start, int End)[] spoken =
        [
            ("Alo, Gürhan abi, nasılsın?", true, 0, 2_400),
            ("Buyur oğlum, iyiyim, sen nasılsın?", false, 2_600, 5_800),
            ("Şimdi şu kira meselesini konuşacaktık ya.", true, 6_000, 9_500),
            ("Evet, cuma günü ödeyeceğim demiştim.", false, 9_700, 12_900),
            ("Ama geçen ay da öyle demiştin abi.", true, 12_400, 15_200), // cuts in before the line ends
            ("Bu sefer kesin, hemen karar ver, bugün.", false, 15_500, 19_000),
            ("Tamam, cuma bekliyorum o zaman.", true, 19_200, 21_800),
        ];

        var lines = spoken.Select(s => new Segment
        {
            CallId = callId,
            IsMe = s.Me,
            StartMs = s.Start,
            EndMs = s.End,
            Text = s.Text,
        }).ToList();

        repository.ReplaceSegments(callId, lines);
        repository.SaveTranscriptVersion(callId, "large-v3", 0.83, lines);
        repository.SaveTranscriptVersion(callId, "deepgram/nova-3", 0.91, lines);

        repository.InsertFlag(new Flag
        {
            CallId = callId,
            ContactId = contactId,
            Kind = FlagKind.PressureTactic,
            Summary = "Bugün karar vermesi için baskı",
            Quote = "hemen karar ver, bugün",
            QuoteStartMs = 15_500,
        });

        repository.Tag(callId, "önemli");
        repository.Tag(callId, "kira");

        return callId;
    }

    /// <summary>
    /// Lays the window's content out at the opening size and reads the bands off it.
    ///
    /// The root grid is measured rather than the window: a window that was never shown has no
    /// handle, and its own measure pass is written for one that has. The grid is the whole
    /// client area, so nothing is lost but the frame.
    /// </summary>
    private static Layout Measure(Window window, string scenario)
    {
        var root = (Grid)window.Content;

        root.Measure(WindowSize);
        root.Arrange(new Rect(WindowSize));
        root.UpdateLayout();

        var scroller = (ScrollViewer)window.FindName("TranscriptScroller");
        Assert.True(scroller.ActualHeight > 0, "Döküm kaydırıcısı yerleşmedi.");

        // Every Auto row above the transcript, on the way up from it: the conversation tab's own
        // rows, the tab strip, the title band, the title bar.
        var rows = new List<string>();
        double chromeRows = 0;

        for (DependencyObject child = scroller; VisualTreeHelper.GetParent(child) is { } parent; child = parent)
        {
            if (parent is not Grid grid || child is not UIElement element || grid.RowDefinitions.Count == 0) continue;

            var row = Grid.GetRow(element);
            for (var i = 0; i < row; i++)
            {
                var height = grid.RowDefinitions[i].ActualHeight;
                if (height <= 0) continue;

                chromeRows += height;
                rows.Add($"    {Name(grid)} satır {i}: {height:0.#} px");
            }
        }

        var above = scroller.TransformToAncestor(root).Transform(new Point()).Y;
        var player = root.RowDefinitions[^1].ActualHeight;

        var breakdown =
            $"[{scenario}] üstteki satırlar {chromeRows:0} px (dökümün üst kenarı {above:0} px), " +
            $"döküm {scroller.ActualHeight:0} px, oynatıcı satırı {player:0} px, pencere içi {root.ActualHeight:0} px\n" +
            string.Join("\n", rows);

        return new Layout(chromeRows, above, scroller.ActualHeight, player, breakdown);
    }

    private static string Name(FrameworkElement element) =>
        string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;

    private static string Describe(Exception exception)
    {
        var detail = new System.Text.StringBuilder();

        for (var e = exception; e is not null; e = e.InnerException)
            detail.AppendLine($"  {e.GetType().Name}: {e.Message}");

        return detail.ToString().TrimEnd();
    }
}

/// <summary>What the ruler read off one scenario, in device-independent pixels.</summary>
internal sealed record Layout(double ChromeRows, double Above, double Transcript, double Player, string Breakdown);

/// <summary>Both scenarios from one pass — the player folded (no audio) and open.</summary>
internal sealed record Measurements(Layout Folded, Layout Open);
