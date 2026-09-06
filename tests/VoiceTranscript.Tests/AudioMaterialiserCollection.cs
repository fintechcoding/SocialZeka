namespace VoiceTranscript.Tests;

/// <summary>
/// The test classes that point <see cref="Core.Audio.AudioMaterialiser.CacheDirectory"/> at a
/// directory of their own, kept from running at the same time as each other.
///
/// That property is process-wide, and two classes were setting it in their constructor and
/// clearing it in Dispose. Run in parallel — which is xunit's default across classes — one
/// class's teardown wiped the other's cache directory in the middle of a test, and the failure
/// was an assertion about a path being null in a test that has nothing to do with paths.
///
/// It surfaced only when a class was added elsewhere in the suite and the scheduling shifted,
/// which is the worst way for a race to be found: green for months, then red in a change that
/// touched none of it. Naming the shared resource is what stops that happening again.
/// </summary>
public static class AudioMaterialiserCollection
{
    public const string Name = "AudioMaterialiser.CacheDirectory";
}
