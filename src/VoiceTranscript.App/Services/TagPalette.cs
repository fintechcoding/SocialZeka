using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Services;

/// <summary>
/// The tag looks, loaded once and read everywhere a pill is drawn.
///
/// Converters run per pill per row per repaint; giving each of them a database query would put
/// disk reads inside list virtualisation. So the definitions are read here at startup and after
/// every save from the manager window, and the converters read this dictionary and nothing else.
/// </summary>
public static class TagPalette
{
    private static readonly Lock Gate = new();

    private static Dictionary<string, TagDef> _byFolded = new(StringComparer.Ordinal);

    /// <summary>Definitions in user order, for pickers and the manager list.</summary>
    public static IReadOnlyList<TagDef> All { get; private set; } = [];

    /// <summary>
    /// Reads the definitions, seeding the starter vocabulary on first run. Call at startup and
    /// after the manager window saves.
    /// </summary>
    public static void Load(Repository repository)
    {
        repository.SeedDefaultTagDefs();

        var defs = repository.TagDefs();

        lock (Gate)
        {
            All = defs;
            _byFolded = defs.ToDictionary(
                d => Core.Text.TurkishText.NormalizeForSearch(d.Tag),
                d => d,
                StringComparer.Ordinal);
        }
    }

    /// <summary>This tag's look, or null when the user never gave it one.</summary>
    public static TagDef? Find(string tag)
    {
        lock (Gate)
        {
            return _byFolded.TryGetValue(
                Core.Text.TurkishText.NormalizeForSearch(tag.Trim()), out var def)
                ? def
                : null;
        }
    }
}
