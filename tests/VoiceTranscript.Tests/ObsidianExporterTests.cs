using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Export;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

public sealed class ObsidianExporterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vt-ob-{Guid.NewGuid():N}.db");
    private readonly string _vault = Path.Combine(Path.GetTempPath(), $"vt-vault-{Guid.NewGuid():N}");
    private readonly Repository _repo;
    private readonly ObsidianExporter _exporter;

    public ObsidianExporterTests()
    {
        var database = new Database(_dbPath);
        database.Migrate();
        _repo = new Repository(database);

        Directory.CreateDirectory(_vault);
        _exporter = new ObsidianExporter(_repo, new ObsidianOptions { VaultPath = _vault });
    }

    public void Dispose()
    {
        // Scoped to this test’s own database. ClearAllPools would dispose pooled handles
        // belonging to every other test class running in parallel, which is a real and
        // measured source of ObjectDisposedException in unrelated tests.
        new Database(_dbPath).ClearPool();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _dbPath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }

        if (Directory.Exists(_vault)) Directory.Delete(_vault, recursive: true);
    }

    private (long callId, long contactId) Seed(
        string name = "Ahmet Yılmaz",
        CallKind kind = CallKind.OneToOne,
        params (bool me, int ms, string text)[] lines)
    {
        var contact = _repo.UpsertContact(name, CallApp.Telegram);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.Telegram,
            Kind = kind,
            StartedAt = new DateTimeOffset(2026, 8, 26, 9, 15, 0, TimeSpan.Zero),
            Duration = TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12),
            MicPath = @"D:\ses\call-mic.wav",
            FarPath = @"D:\ses\call-far.wav",
            State = ProcessingState.Analysed,
        });
        _repo.AssignContact(call, contact);

        if (lines.Length > 0)
        {
            _repo.ReplaceSegments(call, lines.Select(l => new Segment
            {
                CallId = call, IsMe = l.me, StartMs = l.ms, EndMs = l.ms + 3000, Text = l.text,
            }));
        }

        return (call, contact);
    }

    [Fact]
    public void WritesACallNoteAndAContactPage()
    {
        var (call, _) = Seed(lines: [(true, 0, "Merhaba"), (false, 4000, "Buyrun")]);

        var path = _exporter.ExportCall(call);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_exporter.ContactsDirectory, "Ahmet Yılmaz.md")));
    }

    [Fact]
    public void TheCallNoteCarriesFrontmatterObsidianCanQuery()
    {
        var (call, _) = Seed(lines: [(true, 0, "Merhaba")]);

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.StartsWith("---", text);
        Assert.Contains("tarih: 2026-08-26", text);
        Assert.Contains("kisi: \"Ahmet Yılmaz\"", text);
        Assert.Contains("uygulama: Telegram", text);
        Assert.Contains("tur: birebir", text);
    }

    [Fact]
    public void TheCallNoteLinksToTheContactPage()
    {
        var (call, _) = Seed(lines: [(true, 0, "Merhaba")]);

        Assert.Contains("[[Ahmet Yılmaz]]", File.ReadAllText(_exporter.ExportCall(call)));
    }

    [Fact]
    public void TheTranscriptIsWrittenWithSpeakersAndTimestamps()
    {
        var (call, _) = Seed(lines:
        [
            (true, 0, "On iki bin diye konuşmuştuk."),
            (false, 65_000, "Maliyetler arttı."),
        ]);

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("**Ben**: On iki bin diye konuşmuştuk.", text);
        Assert.Contains("**Ahmet Yılmaz**: Maliyetler arttı.", text);
        Assert.Contains("`01:05`", text);
    }

    [Fact]
    public void TurkishCharactersSurviveTheRoundTrip()
    {
        var (call, _) = Seed("Şükrü Gökçe", lines: [(false, 0, "Ödemeyi cuma günü yapacağım, ışıklar açık.")]);

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("Şükrü Gökçe", text);
        Assert.Contains("Ödemeyi cuma günü yapacağım, ışıklar açık.", text);
    }

    [Fact]
    public void FlagsAreRenderedWithTheirQuoteAndTimestamp()
    {
        var (call, contact) = Seed(lines: [(false, 45_000, "on sekiz bin olur ancak")]);

        _repo.InsertFlag(new Flag
        {
            CallId = call,
            ContactId = contact,
            Kind = FlagKind.ChangedAmount,
            Summary = "Fiyat 12.000 TL'den 18.000 TL'ye çıktı",
            Quote = "on sekiz bin olur ancak",
            QuoteStartMs = 45_000,
            CounterQuote = "on iki bin diye konuşmuştuk",
            CounterQuoteStartMs = 12_000,
        });

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("Fiyat 12.000 TL'den 18.000 TL'ye çıktı", text);
        Assert.Contains("`00:45`", text);
        Assert.Contains("on sekiz bin olur ancak", text);
        Assert.Contains("Önceki:", text);
    }

    /// <summary>
    /// A keyword match must never read as something the model concluded about a person.
    /// </summary>
    [Fact]
    public void HeuristicFlagsSayThatTheyAreHeuristics()
    {
        var (call, contact) = Seed(lines: [(false, 0, "hesabınız güvende değil")]);

        _repo.InsertFlag(new Flag
        {
            CallId = call, ContactId = contact, Kind = FlagKind.ScamPattern,
            Summary = "Sahte banka araması", Quote = "hesabınız güvende değil",
            IsHeuristic = true,
        });

        Assert.Contains("anahtar kelime eşleşmesi", File.ReadAllText(_exporter.ExportCall(call)));
    }

    [Fact]
    public void UncertainAudioIsMarkedInTheTranscript()
    {
        var (call, _) = Seed();

        _repo.ReplaceSegments(call, [new Segment
        {
            CallId = call, IsMe = false, StartMs = 0, EndMs = 2000,
            Text = "on sekiz bin", LowConfidence = true,
        }]);

        Assert.Contains("⚠️", File.ReadAllText(_exporter.ExportCall(call)));
    }

    [Fact]
    public void GroupCallsExplainWhyThereIsNoTranscript()
    {
        var (call, _) = Seed(kind: CallKind.Group);

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("tur: grup", text);
        Assert.Contains("Grup araması", text);
    }

    [Fact]
    public void ARecordingMadeOnLoudspeakersCarriesAWarning()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact, App = CallApp.Telegram, StartedAt = DateTimeOffset.UtcNow,
            State = ProcessingState.Analysed, LikelyNoHeadphones = true,
        });
        _repo.AssignContact(call, contact);

        Assert.Contains("Kulaklık kullanılmamış", File.ReadAllText(_exporter.ExportCall(call)));
    }

    [Fact]
    public void TheContactPageListsOpenPromisesAndMarksOverdueOnes()
    {
        var (call, contact) = Seed();

        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact,
            Quote = "Evrakları cuma günü yollarım", QuoteStartMs = 24_000,
            Obligation = "evrak gönderimi",
            DeadlineDate = new DateOnly(2020, 1, 1), // long past
        });

        _exporter.ExportCall(call);

        var text = File.ReadAllText(Path.Combine(_exporter.ContactsDirectory, "Ahmet Yılmaz.md"));

        Assert.Contains("Açık sözler", text);
        Assert.Contains("evrak gönderimi", text);
        Assert.Contains("süresi geçti", text);
    }

    /// <summary>
    /// A conditional promise is not broken by its date arriving, and the page must not imply
    /// otherwise.
    /// </summary>
    [Fact]
    public void ConditionalPromisesAreLabelled()
    {
        var (call, contact) = Seed();

        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, Quote = "Parayı yollarsan cuma günü gönderirim",
            Obligation = "gönderim", DeadlineDate = new DateOnly(2020, 1, 1), IsConditional = true,
        });

        _exporter.ExportCall(call);

        Assert.Contains("(koşullu)", File.ReadAllText(Path.Combine(_exporter.ContactsDirectory, "Ahmet Yılmaz.md")));
    }

    /// <summary>
    /// The page is regenerated rather than appended to, so a dismissed flag disappears instead
    /// of lingering as a stale accusation.
    /// </summary>
    [Fact]
    public void DismissedFlagsVanishWhenTheContactPageIsRegenerated()
    {
        var (call, contact) = Seed(lines: [(false, 0, "hesabınız güvende değil")]);

        var flag = _repo.InsertFlag(new Flag
        {
            CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic,
            Summary = "Aciliyet vurgusu", Quote = "hesabınız güvende değil",
        });

        _exporter.ExportContact(contact);
        var page = Path.Combine(_exporter.ContactsDirectory, "Ahmet Yılmaz.md");
        Assert.Contains("Aciliyet vurgusu", File.ReadAllText(page));

        _repo.DismissFlag(flag);
        _exporter.ExportContact(contact);

        Assert.DoesNotContain("Aciliyet vurgusu", File.ReadAllText(page));
    }

    /// <summary>The vault is very likely open in Obsidian while this runs.</summary>
    [Fact]
    public void RewritingAnExistingNoteLeavesNoTemporaryFilesBehind()
    {
        var (call, _) = Seed(lines: [(true, 0, "Merhaba")]);

        _exporter.ExportCall(call);
        _exporter.ExportCall(call);

        Assert.Empty(Directory.GetFiles(_exporter.CallsDirectory, "*.tmp"));
        Assert.Single(Directory.GetFiles(_exporter.CallsDirectory, "*.md"));
    }

    [Fact]
    public void NamesThatCannotBeFilenamesAreSanitised()
    {
        var (call, _) = Seed("Ahmet / Mehmet: ortak\\hat");

        var path = _exporter.ExportCall(call);

        Assert.True(File.Exists(path));
        Assert.DoesNotContain('/', Path.GetFileName(path));
        Assert.DoesNotContain('\\', Path.GetFileName(path));
    }

    [Fact]
    public void AnUnlabelledCallStillExports()
    {
        var call = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Transcribed,
        });

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("Bilinmeyen", text);
    }

    [Fact]
    public void AudioPathsAreIncludedSoTheRecordingCanBeFound()
    {
        var (call, _) = Seed(lines: [(true, 0, "Merhaba")]);

        var text = File.ReadAllText(_exporter.ExportCall(call));

        Assert.Contains("call-mic.wav", text);
        Assert.Contains("call-far.wav", text);
    }
}
