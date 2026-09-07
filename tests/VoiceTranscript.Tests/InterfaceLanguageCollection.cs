namespace VoiceTranscript.Tests;

/// <summary>
/// The tests that read the interface's words, one at a time.
///
/// <c>Localisation.Use</c> swaps the whole process's dictionary. <see cref="LocalisationTests"/>
/// does that on purpose — proving a key resolves in both languages means switching to both — and
/// any class asserting on a sentence the interface produces is reading that same static while it
/// moves. The failure is the shape this repository has already been bitten by once: intermittent,
/// landing on a test that did nothing wrong, and reading as "the Turkish string came back in
/// English".
///
/// So the classes that touch it share a collection and never run beside each other. Parallelism
/// with everything else is kept: nothing outside this pair changes the language.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InterfaceLanguageCollection
{
    public const string Name = "Arayüz dili";
}
