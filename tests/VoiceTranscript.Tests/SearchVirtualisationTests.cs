using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.App.Views;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The search results are built as they are looked at, not all at once.
///
/// The query side of this screen was always careful — FTS5, the filters in SQL, five hundred
/// rows at most — and the drawing side threw that away. The results were an
/// <c>ItemsControl</c> inside an <c>ItemsControl</c> inside a <c>ScrollViewer</c>, and an
/// <c>ItemsControl</c> has no scrolling panel of its own: it is measured with unlimited height,
/// so it builds a container, a card and a dozen elements for every row it holds, whether or not
/// any of them can be seen. On this machine the archive is small and nobody noticed. On the
/// user's other machine it is already larger and grows by about nine conversations a day, and a
/// word as ordinary as "tamam" reaches the five hundred the query is capped at — at which point
/// pressing Enter means waiting for five hundred cards before the first one appears.
///
/// So the ruler is held up: five hundred hits, one layout pass at a fixed size, and a count of
/// how many of them were actually built. Red means the results list has stopped virtualising —
/// a plain <c>ItemsControl</c> again, an <c>ItemsPanel</c> that is not a virtualising one, or a
/// <c>ScrollViewer</c> wrapped around the list so it is handed infinite height once more.
///
/// <b>The ruler runs in a child process</b>, for the reason <see cref="LayoutTests"/> gives at
/// length: WPF allows one <see cref="Application"/> per process and it belongs to the thread
/// that made it, so the smoke test's one thread and one application cannot be shared.
/// </summary>
public class SearchVirtualisationTests
{
    /// <summary>What the query is capped at, and what a common word really returns.</summary>
    private const int Hits = 500;

    /// <summary>A window this application is ordinarily used at.</summary>
    private static readonly Size PageSize = new(1100, 760);

    /// <summary>
    /// The bar. A virtualising panel builds what fits plus a little; the un-virtualised page
    /// built all five hundred. Anything under a fifth of the result set is unambiguously the
    /// first case and comfortably above what a tall window realises.
    /// </summary>
    private const int RealisedCeiling = Hits / 5;

    private const string HostVariable = "VOICETRANSCRIPT_SEARCH_HOST";
    private const string OutputVariable = "VOICETRANSCRIPT_SEARCH_OUTPUT";

    /// <summary>
    /// Goes red when the results list stops building only what is on screen: five hundred hits
    /// go in and five hundred rows come out of the visual tree, which is the freeze the user
    /// would meet on Enter.
    /// </summary>
    [Fact]
    public void FiveHundredHitsDoNotAllBecomeRows()
    {
        var measured = Measured.Value;

        Assert.Equal(Hits, measured.Results);

        Assert.True(measured.Realised <= RealisedCeiling,
            $"Sonuç satırlarının {measured.Realised} tanesi görsel ağaca kuruldu ({measured.Results} sonuçtan); "
            + $"sınır {RealisedCeiling}. Toplam öğe: {measured.Elements}, yerleşim: {measured.LayoutMs:0} ms.");
    }

    /// <summary>
    /// Goes red when the grouping is lost on the way to the flat list.
    ///
    /// Laying the two nested lists end to end is only safe while every person's heading is still
    /// a row of the sequence, immediately above their own lines. If the headings stop being
    /// emitted the results become one undivided column of sentences with no name against any of
    /// them — the same page the search screen replaced, where finding the person was the answer
    /// and the sentence was lost.
    /// </summary>
    [Fact]
    public void ThePeopleStillHeadTheirOwnResults()
    {
        var measured = Measured.Value;

        Assert.True(measured.Groups > 1, "Ölçüm tek kişiye düştü; gruplama sınanamıyor.");
        Assert.Equal(measured.Groups, measured.HeadingRows);
        Assert.Equal(measured.Results + measured.Groups, measured.Rows);

        // And the headings are virtualised with everything else: they are rows, not chrome.
        Assert.True(measured.RealisedHeadings <= measured.Groups,
            $"{measured.RealisedHeadings} başlık kuruldu, {measured.Groups} grup var.");
    }

    private static readonly Lazy<Measurement> Measured = new(
        () => Environment.GetEnvironmentVariable(HostVariable) == "1" ? MeasureOnStaThread() : MeasureInChildProcess(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // ---- the parent: a second process for a second Application -------------

    private static Measurement MeasureInChildProcess()
    {
        var module = Path.ChangeExtension(typeof(SearchVirtualisationTests).Assembly.Location, ".exe");
        Assert.True(File.Exists(module), $"Test modülü bulunamadı: {module}");

        var output = Path.Combine(Path.GetTempPath(), $"vt-search-virt-{Guid.NewGuid():N}.json");

        var start = new ProcessStartInfo(module)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("--filter-class");
        start.ArgumentList.Add(typeof(SearchVirtualisationTests).FullName!);
        start.Environment[HostVariable] = "1";
        start.Environment[OutputVariable] = output;

        using var child = Process.Start(start)!;

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

            return JsonSerializer.Deserialize<Measurement>(File.ReadAllText(output))
                   ?? throw new InvalidOperationException("Ölçüm dosyası boş.");
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    // ---- the child: one STA thread, one Application, one layout pass -------

    private static Measurement MeasureOnStaThread()
    {
        Measurement? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new VoiceTranscript.App.App().InitializeComponent();

                result = MeasureOnce();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "Arama sayfası ölçümü zaman aşımına uğradı.");
        if (failure is not null) Assert.Fail($"Arama sayfası ölçülemedi:\n{failure}");

        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } output)
            File.WriteAllText(output, JsonSerializer.Serialize(result));

        return result!;
    }

    private static Measurement MeasureOnce()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vt-search-virt-{Guid.NewGuid():N}.db");
        var database = new Database(file);

        database.Migrate();

        try
        {
            var repository = new Repository(database);
            var groups = Seed(repository);

            var model = new SearchViewModel(repository) { Query = "tamam" };
            var page = new SearchPage { DataContext = model };

            // A window so the page has the resources and the size it would really be given;
            // never shown, exactly as the layout ruler does it.
            var window = new Window { Content = page, Width = PageSize.Width, Height = PageSize.Height };

            model.SearchCommand.Execute(null);

            var clock = Stopwatch.StartNew();

            page.Measure(PageSize);
            page.Arrange(new Rect(PageSize));
            page.UpdateLayout();

            clock.Stop();

            var realised = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var headings = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var elements = 0;

            Walk(page, element =>
            {
                elements++;

                switch (element)
                {
                    case FrameworkElement { DataContext: SearchResult r }: realised.Add(r); break;
                    case FrameworkElement { DataContext: SearchGroup g }: headings.Add(g); break;
                }
            });

            GC.KeepAlive(window);

            return new Measurement(
                Results: model.ResultCount,
                Groups: groups,
                Rows: model.Rows.Count,
                HeadingRows: model.Rows.OfType<SearchGroup>().Count(),
                Realised: realised.Count,
                RealisedHeadings: headings.Count,
                Elements: elements,
                LayoutMs: clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            database.ClearPool();

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(file + suffix)) File.Delete(file + suffix);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Five hundred matching lines across ten people — the shape of "tamam" in a real archive,
    /// where the same ordinary word turns up in everybody's conversations.
    /// </summary>
    private static int Seed(Repository repository)
    {
        const int people = 10;
        const int callsEach = 5;
        const int linesEach = Hits / (people * callsEach);

        for (var p = 0; p < people; p++)
        {
            var contactId = repository.UpsertContact($"Kişi {p:00}", CallApp.WhatsApp);

            for (var c = 0; c < callsEach; c++)
            {
                var callId = repository.InsertCall(new Call
                {
                    ContactId = contactId,
                    App = CallApp.WhatsApp,
                    StartedAt = DateTimeOffset.Now.AddDays(-(p * callsEach + c)),
                    Duration = TimeSpan.FromMinutes(6),
                    State = ProcessingState.Analysed,
                });

                var lines = new List<Segment>();

                for (var i = 0; i < linesEach; i++)
                {
                    var text = $"tamam abi {p}-{c}-{i} numaralı satırda öyle konuşmuştuk";

                    lines.Add(new Segment
                    {
                        CallId = callId,
                        IsMe = i % 2 == 0,
                        StartMs = i * 4000,
                        EndMs = i * 4000 + 3000,
                        Text = text,
                        TextNormalised = VoiceTranscript.Core.Text.TurkishText.NormalizeForSearch(text),
                    });
                }

                repository.ReplaceSegments(callId, lines);
            }
        }

        return people;
    }

    private static void Walk(DependencyObject node, Action<DependencyObject> visit)
    {
        visit(node);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Walk(VisualTreeHelper.GetChild(node, i), visit);
    }

    /// <summary>What one layout pass over five hundred hits cost, in rows and in elements.</summary>
    public sealed record Measurement(
        int Results, int Groups, int Rows, int HeadingRows,
        int Realised, int RealisedHeadings, int Elements, double LayoutMs);
}
