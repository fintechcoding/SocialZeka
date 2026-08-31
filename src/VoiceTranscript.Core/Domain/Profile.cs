namespace VoiceTranscript.Core.Domain;

/// <summary>
/// What the user knows about a person, in their own words.
///
/// Everything here was typed by the user. The pipeline never writes it, and it survives every
/// reprocess untouched — the same standing as a note. The ledger is where the machine's findings
/// live, each with its quote; a profile needs no quotes because the user is its source.
/// </summary>
public sealed record ContactProfile
{
    public required long ContactId { get; init; }

    /// <summary>Filename under the data directory's photos folder — never an outside path.</summary>
    public string? PhotoFile { get; init; }

    public DateOnly? BirthDate { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One labelled fact: "Meslek: Mimar", "Şehir: İzmir" — whatever the user wants kept.</summary>
public sealed record ContactField
{
    public long Id { get; init; }
    public required long ContactId { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public int Position { get; init; }
}
