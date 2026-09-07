namespace VoiceTranscript.Core.Domain;

/// <summary>
/// A group of people, in the user's own word: "Aile", "İş".
///
/// The user's word, never a judgement. Nothing here ranks anybody, scores anybody or sorts
/// people into better and worse; a circle is a name somebody chose for a set of people they
/// already know they belong together, and the application's only job is to remember it.
///
/// Shaped exactly like <see cref="TagDef"/>, and for the same reason: a definition is a name and
/// an appearance, the assignment lives elsewhere, and deleting a definition must never delete
/// what the user assigned. Written by the user alone — the analysis pipeline may never put
/// somebody in a circle.
///
/// The concept is called a CIRCLE and not a group because "grup" is already taken: CallKind.Group
/// means a three-way recording nobody transcribes, in fifteen places in the code. Two meanings
/// for one word in one product is how a reader learns to distrust both.
/// </summary>
/// <param name="Name">The spelling shown on screen — the user's own.</param>
/// <param name="Icon">A WPF-UI SymbolRegular name, e.g. "Home24". Unknown names fall back safely.</param>
/// <param name="Color">Hex colour like "#E81123".</param>
/// <param name="Position">Order in the tab strip, the dropdowns and the manager list.</param>
public sealed record Circle(string Name, string Icon, string Color, int Position = 0);

/// <summary>
/// Which slice of the archive a listing is asking for.
///
/// It exists because the cut comes BEFORE the filter on the first screen. The overview asks for
/// the newest twelve conversations; filtering those twelve in memory would show two rows on the
/// "Aile" tab of an archive holding forty-one family conversations, and the user would say their
/// calls had disappeared. So the circle travels into the query and each tab fetches its own
/// newest twelve.
///
/// Three states rather than a nullable string, because "no circle asked for" and "the people in
/// no circle" are different questions with different answers, and a null would have had to mean
/// both.
/// </summary>
public sealed record CircleFilter
{
    private CircleFilter(string? folded, bool without)
    {
        Folded = folded;
        Without = without;
    }

    /// <summary>The folded identity of the circle asked for, or null for the other two states.</summary>
    public string? Folded { get; }

    /// <summary>True when the question is "who is in no circle at all".</summary>
    public bool Without { get; }

    /// <summary>The whole archive, unfiltered — what every screen asked for before circles existed.</summary>
    public static CircleFilter Everything { get; } = new(null, false);

    /// <summary>
    /// The people in no circle. Never removable from the strip: a person recorded five minutes
    /// ago is here, and a tab that can be taken away is a conversation that can vanish.
    /// </summary>
    public static CircleFilter NoCircle { get; } = new(null, true);

    /// <summary>One circle, by the user's spelling. Folded the way the tag dictionary folds.</summary>
    public static CircleFilter Of(string circle) =>
        new(Text.TurkishText.NormalizeForSearch(circle.Trim()), false);

    /// <summary>One circle, by an identity that is already folded.</summary>
    public static CircleFilter OfFolded(string folded) => new(folded, false);
}
