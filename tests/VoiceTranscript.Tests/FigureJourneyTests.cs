using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// "Rakam yolculuğu": every subject this person has given more than one answer about.
///
/// The ledger already flags a price that moved; this is the same evidence laid out as a path, so
/// the user can see 15.000 → 18.000 → 20.000 with a date and a millisecond on each stop. What
/// these tests pin down is that it groups the way the ledger groups — the other party's lines,
/// by entity and attribute — and that it does not stop at numbers: a delivery date that went
/// from "cuma" to "gelecek hafta" is the same movement and the same evidence.
/// </summary>
public sealed class FigureJourneyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-fig-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public FigureJourneyTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Call(DateTimeOffset at)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = at,
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        return call;
    }

    private void Claim(
        long call, string entity, string attribute, string value,
        decimal? numeric, string quote, int ms, bool byMe = false, bool lowConfidence = false) =>
        _repo.InsertClaim(new Claim
        {
            CallId = call,
            ContactId = _contact,
            ByMe = byMe,
            Quote = quote,
            QuoteStartMs = ms,
            Entity = entity,
            Attribute = attribute,
            Value = value,
            NumericValue = numeric,
            Unit = numeric is null ? null : "TL",
            LowConfidence = lowConfidence,
        });

    /// <summary>
    /// Three answers about one figure, in the order they were given, each playable.
    ///
    /// Red means the journey is out of order, has lost a stop, or has lost the millisecond that
    /// makes a stop worth clicking — at which point the card is asserting a change the user
    /// cannot check.
    /// </summary>
    [Fact]
    public void EveryValueASubjectHasHeldIsAStopInTimeOrder()
    {
        var june = Call(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero));
        var july = Call(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
        var august = Call(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));

        Claim(june, "kira", "tutar", "15.000", 15_000, "Kira on beş bin diye konuşmuştuk", 4_000);
        Claim(july, "kira", "tutar", "18.000", 18_000, "Kira on sekiz bin oldu", 6_000);
        Claim(august, "kira", "tutar", "20.000", 20_000, "Yirmi binin altı olmaz", 8_000);

        var journey = Assert.Single(_repo.FigureJourney(_contact));

        Assert.Equal("kira", journey.Entity);
        Assert.Equal("tutar", journey.Attribute);
        Assert.Equal(3, journey.DistinctValues);
        Assert.Equal([15_000m, 18_000m, 20_000m], journey.Stops.Select(s => s.NumericValue));
        Assert.Equal([june, july, august], journey.Stops.Select(s => s.CallId));
        Assert.Equal(4_000, journey.Stops[0].StartMs);
        Assert.Equal("TL", journey.Stops[0].Unit);
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero), journey.Stops[0].CallStartedAt);
    }

    /// <summary>
    /// A date that moved is a journey too.
    ///
    /// Red means the card only tracks numbers, and "cuma" becoming "gelecek hafta" — the same
    /// evidence, in words — has quietly stopped being shown.
    /// </summary>
    [Fact]
    public void NonNumericValuesTravelAsWell()
    {
        var first = Call(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        var second = Call(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));

        Claim(first, "teslim", "tarih", "cuma", null, "Cuma günü teslim ederim", 2_000);
        Claim(second, "teslim", "tarih", "gelecek hafta", null, "Gelecek hafta olur artık", 5_000);

        var journey = Assert.Single(_repo.FigureJourney(_contact));
        Assert.Equal(2, journey.DistinctValues);
        Assert.Equal(["cuma", "gelecek hafta"], journey.Stops.Select(s => s.Value));
        Assert.All(journey.Stops, s => Assert.Null(s.NumericValue));
    }

    /// <summary>
    /// A subject that never changed is not a journey, and the user's own figures are not the
    /// other person's.
    ///
    /// Red means the card is showing movement where there was none, or attributing a number the
    /// user said themselves to the person opposite — the rule ChangedAmounts already follows.
    /// </summary>
    [Fact]
    public void OneAnswerIsNoJourneyAndTheUsersOwnFiguresAreNotCounted()
    {
        var first = Call(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero));
        var second = Call(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));

        // Said twice, the same both times.
        Claim(first, "depozito", "tutar", "5.000", 5_000, "Depozito beş bin", 1_000);
        Claim(second, "depozito", "tutar", "5.000", 5_000, "Depozito yine beş bin", 2_000);

        // The user's own two figures about a third thing.
        Claim(first, "aidat", "tutar", "500", 500, "Aidatı beş yüz ödüyorum", 3_000, byMe: true);
        Claim(second, "aidat", "tutar", "750", 750, "Aidat yedi yüz elli oldu", 4_000, byMe: true);

        Assert.Empty(_repo.FigureJourney(_contact));
    }

    /// <summary>
    /// Uncertain audio is listed and marked, not dropped.
    ///
    /// A flag is an accusation and is held to the stricter rule; a journey is a list of what was
    /// said, and hiding a stop would leave a gap in a path the user is being asked to read. Red
    /// means either the stop vanished or its uncertainty did.
    /// </summary>
    [Fact]
    public void AnUncertainStopIsShownCarryingItsMark()
    {
        var first = Call(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero));
        var second = Call(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));

        Claim(first, "kira", "tutar", "15.000", 15_000, "Kira on beş bin", 1_000);
        Claim(second, "kira", "tutar", "18.000", 18_000, "Kira on sekiz bin", 2_000, lowConfidence: true);

        var journey = Assert.Single(_repo.FigureJourney(_contact));
        Assert.Equal(2, journey.Stops.Count);
        Assert.False(journey.Stops[0].LowConfidence);
        Assert.True(journey.Stops[1].LowConfidence);
    }

    /// <summary>Different subjects stay different journeys, newest movement first.</summary>
    [Fact]
    public void SubjectsAreKeptApartAndTheMostRecentComesFirst()
    {
        var june = Call(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero));
        var july = Call(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
        var september = Call(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));

        Claim(june, "kira", "tutar", "15.000", 15_000, "Kira on beş bin", 1_000);
        Claim(july, "kira", "tutar", "18.000", 18_000, "Kira on sekiz bin", 2_000);

        Claim(june, "teslim", "tarih", "cuma", null, "Cuma günü olur", 3_000);
        Claim(september, "teslim", "tarih", "gelecek hafta", null, "Gelecek haftaya kaldı", 4_000);

        var journeys = _repo.FigureJourney(_contact);

        Assert.Equal(2, journeys.Count);
        Assert.Equal("teslim", journeys[0].Entity);
        Assert.Equal("kira", journeys[1].Entity);
    }
}
