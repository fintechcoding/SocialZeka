using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The worked example, end to end.
///
/// This is the only test in the suite that exercises the product's actual promise: three
/// conversations go in, and what comes out is a price that moved twice, a promise that came due,
/// and a question that went unanswered — none of which is visible inside any single call. If this
/// stops working, the application still records and transcribes perfectly and is worth nothing.
/// </summary>
public class SampleDataTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"vt-sample-{Guid.NewGuid():N}");

    private readonly AppPaths _paths;
    private readonly Repository _repository;

    public SampleDataTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file can still be held briefly. A temp folder is swept anyway.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadingProducesThreeCallsForOneContact()
    {
        SampleData.Load(_repository, _paths);

        var contact = Assert.Single(_repository.ListContacts());
        Assert.Equal(SampleData.ContactName, contact.Name);

        var calls = _repository.ListCalls(contact.Id);
        Assert.Equal(3, calls.Count);

        // Marked as a sample in its own name, so nobody mistakes it for somebody they know.
        Assert.Contains("demo", contact.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryCallHasBothStreamsOnDisk()
    {
        // The mirrored waveform is the clearest statement the product makes about itself, and it
        // needs two real files to draw.
        SampleData.Load(_repository, _paths);

        var contact = _repository.ListContacts()[0];

        foreach (var call in _repository.ListCalls(contact.Id))
        {
            Assert.NotNull(call.MicPath);
            Assert.NotNull(call.FarPath);
            Assert.True(File.Exists(call.MicPath), call.MicPath);
            Assert.True(File.Exists(call.FarPath), call.FarPath);
        }
    }

    [Fact]
    public void TheWaveformOfEachStreamShowsSpeechWhereThatPersonSpoke()
    {
        SampleData.Load(_repository, _paths);

        var contact = _repository.ListContacts()[0];
        var call = _repository.ListCalls(contact.Id).OrderBy(c => c.StartedAt).First();

        var mic = Core.Audio.WaveformPeaks.Read(call.MicPath!, buckets: 100);
        var far = Core.Audio.WaveformPeaks.Read(call.FarPath!, buckets: 100);

        Assert.True(mic.Max() > 0.05f, "mikrofon akışı boş çıktı");
        Assert.True(far.Max() > 0.05f, "karşı taraf akışı boş çıktı");

        // The first call opens with the other party talking, so the two are not identical —
        // which is the entire point of recording them separately.
        Assert.True(far[0] > mic[0], "iki akış ayırt edilemiyor");
    }

    [Fact]
    public void ThePriceMovedTwiceAndTheApplicationWorksThatOutItself()
    {
        // Stored as three separate claims, exactly as a real conversation would produce them.
        // Nothing tells the repository that this is a change; it derives it.
        SampleData.Load(_repository, _paths);

        var changes = _repository.ChangedAmounts();
        var series = Assert.Single(changes).Series;

        Assert.Equal([12000m, 14500m, 18000m], series.Select(c => c.NumericValue));

        // Each figure carries the words it came from, so it can be listened to rather than
        // taken on trust.
        Assert.All(series, claim => Assert.False(string.IsNullOrWhiteSpace(claim.Quote)));
    }

    [Fact]
    public void APromiseCameDueAndIsReportedAsOverdue()
    {
        SampleData.Load(_repository, _paths);

        var overdue = _repository.OverdueCommitments(DateOnly.FromDateTime(DateTime.Now));
        var (commitment, contactName) = Assert.Single(overdue);

        Assert.Equal(SampleData.ContactName, contactName);
        Assert.Contains("Sözleşme", commitment.Obligation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cuma", commitment.Quote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheUnansweredQuestionIsFlaggedWithBothHalvesOfIt()
    {
        SampleData.Load(_repository, _paths);

        var flags = _repository.RecentFlags();
        var evaded = flags.Single(f => f.Flag.Kind == Core.Domain.FlagKind.EvadedQuestion);

        Assert.Contains("Fatura", evaded.Flag.Summary, StringComparison.OrdinalIgnoreCase);

        // The dodge and the question it dodged, so the pair can be read together.
        Assert.False(string.IsNullOrWhiteSpace(evaded.Flag.Quote));
        Assert.False(string.IsNullOrWhiteSpace(evaded.Flag.CounterQuote));
    }

    [Fact]
    public void AHeuristicFlagIsLabelledAsOneRatherThanPassedOffAsInference()
    {
        // A curated keyword list must never be presented as something a model concluded. The
        // whole trust argument depends on the difference being visible.
        SampleData.Load(_repository, _paths);

        var pressure = _repository.RecentFlags()
            .Single(f => f.Flag.Kind == Core.Domain.FlagKind.PressureTactic);

        Assert.True(pressure.Flag.IsHeuristic);
    }

    [Fact]
    public void TheTranscriptIsSearchableInTurkish()
    {
        SampleData.Load(_repository, _paths);

        // Upper case, and with the letters the default Unicode rules get wrong.
        var hits = _repository.Search("FATURA");

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains("atura", h.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadingTwiceDoesNotDuplicateAnything()
    {
        SampleData.Load(_repository, _paths);
        SampleData.Load(_repository, _paths);

        Assert.Single(_repository.ListContacts());
        Assert.Equal(3, _repository.ListCalls().Count);
    }

    [Fact]
    public void RemovingTakesTheAudioWithIt()
    {
        // The same delete a real person gets. A "delete" that leaves recordings on disk would
        // not be deletion, and the sample must not be a special case.
        SampleData.Load(_repository, _paths);

        var call = _repository.ListCalls()[0];
        var micPath = call.MicPath!;

        Assert.True(File.Exists(micPath));

        SampleData.Remove(_repository);

        Assert.Empty(_repository.ListContacts());
        Assert.Empty(_repository.ListCalls());
        Assert.False(File.Exists(micPath), "ses dosyası diskte kaldı");
        Assert.False(SampleData.IsLoaded(_repository));
    }

    [Fact]
    public void RemovingWhenNothingIsLoadedIsHarmless()
    {
        SampleData.Remove(_repository);

        Assert.Empty(_repository.ListContacts());
    }
}
