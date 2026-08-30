using System.IO;
using System.Reflection;
using System.Text.Json;

namespace VoiceTranscript.Core.Text;

/// <summary>
/// The words the interface uses, in whichever language is chosen.
///
/// Lives here rather than in the application project for a mundane and expensive reason: WPF
/// compiles its final assembly through a generated temporary project that drops EmbeddedResource
/// items, so a dictionary embedded in the application project never reaches the output — and the
/// build succeeds, so the first sign of it is every label on every screen showing its own key.
///
/// Turkish is the base rather than English, and that is a deliberate inversion of the usual
/// arrangement. This application was written in Turkish for a Turkish user: its error messages,
/// its ledger, its empty states and its wording about what is and is not being recorded were all
/// written in that language first, and translating them into English to translate them back would
/// have lost the phrasing that took the longest to get right.
///
/// So the Turkish dictionary is generated from the markup itself and is exact by construction. A
/// missing key in any other language falls back to it — a screen in mixed languages is a poor
/// result, but a screen with blank labels where text should be is a broken one.
/// </summary>
public static class Localisation
{
    /// <summary>Languages on offer, in the order the settings list shows them.</summary>
    public static readonly (string Code, string Name)[] Available =
    [
        ("tr", "Türkçe"),
        ("en", "English"),
    ];

    private const string Fallback = "tr";

    private static readonly Lock Gate = new();

    private static Dictionary<string, string> _current = [];
    private static Dictionary<string, string> _base = [];

    /// <summary>The language in use. Changing it is <see cref="Use"/>.</summary>
    public static string Language { get; private set; } = Fallback;

    /// <summary>Raised after the language changes, so open windows can be rebuilt.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Loads a language, keeping Turkish underneath it.
    ///
    /// Unknown codes fall back rather than throwing: a settings file carried over from a later
    /// version naming a language this build does not have must not stop the application opening.
    /// </summary>
    public static void Use(string? code)
    {
        var wanted = Available.Any(l => l.Code == code) ? code! : Fallback;

        lock (Gate)
        {
            if (_base.Count == 0) _base = Load(Fallback);

            _current = wanted == Fallback ? _base : Load(wanted);
            Language = wanted;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The text for a key.
    ///
    /// Returns the key itself when nothing matches. That is deliberately ugly: a missing string
    /// shows up as <c>overview.title</c> on screen rather than as an empty label, which is the
    /// difference between a fault somebody reports and one nobody notices.
    /// </summary>
    public static string T(string key)
    {
        lock (Gate)
        {
            if (_current.Count == 0) Load();

            if (_current.TryGetValue(key, out var text)) return text;
            if (_base.TryGetValue(key, out var fallback)) return fallback;
        }

        return key;
    }

    private static void Load()
    {
        if (_base.Count == 0) _base = Load(Fallback);
        if (_current.Count == 0) _current = _base;
    }

    private static Dictionary<string, string> Load(string code)
    {
        // Embedded rather than loose files beside the executable: the application is installed
        // into a versioned directory that is replaced on every update, and a loose resource file
        // is exactly the kind of thing that survives one upgrade and not the next.
        var name = $"VoiceTranscript.Core.Resources.strings.{code}.json";

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return [];

            using var reader = new StreamReader(stream);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }
}
