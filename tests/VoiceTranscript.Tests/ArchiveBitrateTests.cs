using System.Reflection;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// The archive bitrate is a transcription decision, not only a storage one.
///
/// The first transcription reads the PCM original. Every later one does not: "Yeniden yazıya dök"
/// decodes this archive back to PCM and transcribes that, so whatever the encoder throws away is
/// thrown away for every transcript after the first.
///
/// It was set to 24 kbps when the archive was only ever going to be listened to, and that is
/// measurably on the wrong side of a cliff — one recording at four bitrates gave 1624 words at
/// 21.5 kbps and 330 at 18.2, and Opus undershoots its target on speech with pauses in it. The
/// symptom was a mystery for most of a day: the good transcript people compared against was the
/// first run on the original recording, and every cloud re-run read audio that had been through
/// this. It looked like a cloud fault and was ours.
/// </summary>
public class ArchiveBitrateTests
{
    private static int Bitrate =>
        (int)typeof(OpusArchive)
            .GetField("Bitrate", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    /// <summary>
    /// Sixty-four is a floor, set by the person whose conversations these are: "asla 64 kbit
    /// altına inme". Written down here because a storage complaint is the obvious reason somebody
    /// would lower it later, and the cost of doing so is not paid in storage — it is paid, quietly,
    /// by every transcript made after the first.
    /// </summary>
    [Fact]
    public void TheArchiveNeverGoesBelowSixtyFour()
    {
        Assert.True(Bitrate >= 64_000, $"{Bitrate} bps is below the floor this archive is held to");
    }

    [Fact]
    public void AndDoesNotPayForQualityThatIsNotThere()
    {
        // The recording is 16 kHz mono 16-bit — 256 kbps. Opus is transparent for speech long
        // before that, so anything approaching it is bytes for nothing.
        Assert.True(Bitrate <= 96_000, $"{Bitrate} bps is past where a 16 kHz mono encoder can help");
    }

    /// <summary>
    /// An hour of conversation, so the storage side of the trade is written down rather than
    /// discovered when somebody's disk fills.
    /// </summary>
    [Fact]
    public void AnHourStillCostsAFractionOfTheRecording()
    {
        var archived = Bitrate * 3600.0 / 8;
        var original = 16_000 * 2 * 3600.0;

        // A fifth of the recording, per side. Not the twentieth it used to be, and that is the
        // point: the difference was being paid for in words.
        Assert.True(archived <= original / 3, $"{archived / 1e6:0.#} MB an hour is not a saving");
    }
}
