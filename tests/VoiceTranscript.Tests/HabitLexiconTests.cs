using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The dictionary the habit counters read, and the one matching rule they all use.
///
/// The rule is token boundary + stem + a listed ending, because the two obvious rules are both
/// wrong: substring matching finds a stem inside an innocent word, whole-token matching misses
/// every inflected form. These tests pin the rule with the three words the plan names — one
/// that contains a stem, one that starts with a stem and continues wrongly, one that starts with
/// a stem and continues with a listed ending — and pin the seed's two mechanical properties: it
/// is actually embedded, and it is written once.
/// </summary>
public sealed class HabitLexiconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-lexicon-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;

    private static readonly HabitLexicon Seeded = HabitLexicon.From(HabitLexicon.EmbeddedSeed());

    public HabitLexiconTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repository = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Goes red when the seed is not in the assembly — which is exactly what happens when the
    /// csproj loses WithCulture="false": the ".tr." in the file name makes MSBuild compile it
    /// into a satellite, the build succeeds, and every counter comes up with nothing to count.
    /// </summary>
    [Fact]
    public void TheEmbeddedSeedLoadsAndIsNotEmpty()
    {
        var rows = HabitLexicon.EmbeddedSeed();

        Assert.True(rows.Count(r => r.Kind == HabitKind.Profanity) >= 20, "swear-word stems");
        Assert.True(rows.Count(r => r.Kind == HabitKind.Filler) >= 10, "fillers");
        Assert.DoesNotContain(rows, r => r.Kind == HabitKind.Dialect);
        Assert.All(rows, r => Assert.NotEmpty(r.LexemeFolded));

        // Folded stems are the keys, so they must already be ASCII-folded when they arrive.
        Assert.All(rows, r => Assert.Equal(Core.Text.TurkishText.NormalizeForSearch(r.Lexeme), r.LexemeFolded));
    }

    /// <summary>
    /// Goes red when a second start re-inserts the seed, or when it brings back a stem the user
    /// deleted. The rows are the user's from the moment they exist.
    /// </summary>
    [Fact]
    public void SeedingIsIdempotentAndHonoursADeletion()
    {
        var first = HabitLexicon.Seed(_repository);
        Assert.True(first > 0);
        Assert.Equal(first, _repository.Lexicon().Count);

        Assert.Equal(0, HabitLexicon.Seed(_repository));
        Assert.Equal(first, _repository.Lexicon().Count);

        _repository.DeleteLexeme(_repository.Lexicon()[0].Id);

        Assert.Equal(0, HabitLexicon.Seed(_repository));
        Assert.Equal(first - 1, _repository.Lexicon().Count);
    }

    /// <summary>A stem that sits INSIDE a word is not a hit. Goes red on substring matching.</summary>
    [Fact]
    public void AStemInsideAnotherWordIsNotAHit()
    {
        Assert.Empty(Seeded.Matches("klasik bir konu bu"));
    }

    /// <summary>A stem that starts a word and continues with an ending nobody listed is not a hit.</summary>
    [Fact]
    public void AStemWithAnUnlistedEndingIsNotAHit()
    {
        Assert.Empty(Seeded.Matches(Fold("şikayet ettim")));
        Assert.Empty(Seeded.Matches(Fold("sıcak bir gün")));
        Assert.Empty(Seeded.Matches(Fold("amca geldi, aman dedim")));
    }

    /// <summary>A stem with a listed ending is a hit, punctuation or not, and it carries its position.</summary>
    [Fact]
    public void AStemWithAListedEndingIsAHit()
    {
        var hits = Seeded.Matches(Fold("Hadi siktir, git."));

        var hit = Assert.Single(hits);
        Assert.Equal(HabitKind.Profanity, hit.Kind);
        Assert.Equal("siktir", hit.Token);
        Assert.Equal(1, hit.TokenIndex);
        Assert.Equal(5, hit.CharOffset);
    }

    /// <summary>The bare stem is always a hit; a second listed ending is, too.</summary>
    [Fact]
    public void TheBareStemAndEveryListedEndingAreHits()
    {
        Assert.Equal(2, Seeded.Matches(Fold("lan siktirdim")).Count);
    }

    /// <summary>
    /// "Bu küfür değil" lands in the dictionary as an exclusion, and the next recount must not
    /// resurrect the hit — the DismissedFlagKeys rule, for words.
    /// </summary>
    [Fact]
    public void AnExclusionRemovesTheHit()
    {
        var withExclusion = HabitLexicon.From(
        [
            .. HabitLexicon.EmbeddedSeed(),
            new HabitLexeme { Kind = HabitKind.Exclusion, Lexeme = "siktir" },
        ]);

        Assert.Empty(withExclusion.Matches(Fold("hadi siktir git")));

        // Only that token: the stem's other forms still count.
        Assert.Single(withExclusion.Matches(Fold("siktirdim")));
    }

    /// <summary>Fillers are whole tokens, found through Whisper's punctuation.</summary>
    [Fact]
    public void FillersAreFoundWholeThroughPunctuation()
    {
        var hits = Seeded.Matches(Fold("Yani, şey... işte öyle"));

        Assert.Equal(["yani", "sey", "iste"], hits.Select(h => h.Token));
        Assert.All(hits, h => Assert.Equal(HabitKind.Filler, h.Kind));
    }

    /// <summary>
    /// The version is what tells a stored report it was counted with an older dictionary, so it
    /// must move when the words move and stay when only their order does.
    /// </summary>
    [Fact]
    public void TheVersionFollowsTheRowsNotTheirOrder()
    {
        var rows = HabitLexicon.EmbeddedSeed();
        var version = HabitLexicon.Version(rows);

        Assert.Equal(version, HabitLexicon.Version(rows.Reverse()));
        Assert.Equal(version, HabitLexicon.Version(rows.Select(r => r with { Position = r.Position + 10 })));

        Assert.NotEqual(version, HabitLexicon.Version([.. rows, new HabitLexeme { Kind = HabitKind.Filler, Lexeme = "mesela" }]));
        Assert.NotEqual(version, HabitLexicon.Version(rows.Skip(1)));

        Assert.Equal(version, Seeded.LexiconVersion);
    }

    /// <summary>
    /// A piece that is only punctuation keeps its index, so a token's position still lines up
    /// with the engine's word list, where such a piece is a word too.
    /// </summary>
    [Fact]
    public void TokenisingKeepsPositionsForPunctuationOnlyPieces()
    {
        var tokens = HabitLexicon.Tokenize("ne — yani?");

        Assert.Equal(3, tokens.Count);
        Assert.Equal(("ne", 0, 0), (tokens[0].Text, tokens[0].Index, tokens[0].Offset));
        Assert.Equal(("", 1), (tokens[1].Text, tokens[1].Index));
        Assert.Equal(("yani", 2, 5), (tokens[2].Text, tokens[2].Index, tokens[2].Offset));
    }

    private static string Fold(string text) => Core.Text.TurkishText.NormalizeForSearch(text);
}
