namespace VoiceTranscript.Core.Domain;

/// <summary>
/// How a call came to be filed under a person.
///
/// Recorded because one of these is a machine decision and the others are not, and this
/// application has already paid for not telling them apart. A window title bound to a contact
/// filed every later call under that person; nothing said which rows had been decided that way,
/// so the repair had to be inferred from the damage rather than read off. With this, "show me
/// everything the voice decided" is one query, and undoing it is another.
/// </summary>
public enum ContactSource
{
    /// <summary>The user said so, in the labelling window. The only source that enrols a voice.</summary>
    User,

    /// <summary>A remembered window title. Kept for history; nothing writes it any more.</summary>
    Title,

    /// <summary>Matched against a stored voiceprint, above the confidence the settings allow.</summary>
    Voice,
}

public static class ContactSourceText
{
    /// <summary>The value stored in the database. Lower case so a hand-written query reads naturally.</summary>
    public static string Wire(this ContactSource source) => source switch
    {
        ContactSource.Title => "title",
        ContactSource.Voice => "voice",
        _ => "user",
    };

    /// <summary>Reads a stored value back, treating anything unrecognised as the user's own doing.</summary>
    public static ContactSource ToContactSource(string? stored) => stored switch
    {
        "voice" => ContactSource.Voice,
        "title" => ContactSource.Title,
        _ => ContactSource.User,
    };
}

/// <summary>
/// What one person sounds like, as the recogniser hears them.
///
/// The vector is a unit-length embedding; comparing two of them is a dot product, and the result
/// runs from about -0.1 for two strangers to about +0.9 for two recordings of one person. It
/// carries nothing that can be turned back into audio, and a vector made by a different model is
/// not comparable with one made by this one — which is why <see cref="Model"/> travels with it
/// rather than being assumed.
///
/// <see cref="CallsUsed"/> is not decoration. A print built from a single call has never had its
/// label checked against anything, so it may only suggest; automatic filing needs at least two
/// calls that agree with each other.
/// </summary>
public sealed record Voiceprint
{
    public long ContactId { get; init; }
    public float[] Vector { get; init; } = [];
    public string Model { get; init; } = "";
    public int CallsUsed { get; init; }
    public double SpeechSeconds { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// How alike two voices are, from -1 to 1. Both vectors are stored unit-length, so this is
    /// the cosine without the division.
    /// </summary>
    public static double Similarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0;

        double sum = 0;
        for (var i = 0; i < left.Count; i++) sum += left[i] * right[i];

        return sum;
    }

    public double Similarity(IReadOnlyList<float> other) => Similarity(Vector, other);
}
