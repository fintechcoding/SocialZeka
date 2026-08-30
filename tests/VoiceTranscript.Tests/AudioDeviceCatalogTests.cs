using VoiceTranscript.Capture;

namespace VoiceTranscript.Tests;

/// <summary>
/// Telling the two halves of a Bluetooth headset apart, and describing an endpoint well enough
/// to be chosen.
///
/// The distinction matters more than it looks. A headset presents itself to Windows twice: a
/// hands-free pair that carries the microphone at 16 kHz and drops the earphones out of stereo,
/// and a stereo output-only pair at full quality. Nothing in the endpoint properties says which
/// is which — only the name does — and picking the wrong one is how a recorded conversation ends
/// up sounding like a 1985 telephone.
/// </summary>
public class AudioDeviceCatalogTests
{
    [Theory]
    [InlineData("Headset (AirPods Pro Hands-Free AG Audio)")]
    [InlineData("Kulaklık Seti (AirPods Pro)")]
    [InlineData("Hands-Free AG Audio")]
    [InlineData("Hands Free AG Audio")]
    [InlineData("Eller Serbest AG Audio")]
    public void TheHandsFreeHalfOfAHeadsetIsRecognised(string name)
    {
        Assert.True(AudioDeviceCatalog.LooksHandsFree(name), name);
    }

    [Theory]
    [InlineData("Kulaklıklar (AirPods Pro Stereo)")]
    [InlineData("Speakers (Realtek(R) Audio)")]
    [InlineData("Mikrofon Dizisi (Intel Smart Sound)")]
    [InlineData("Hoparlör (2- USB Audio Device)")]
    public void TheStereoHalfAndOrdinaryDevicesAreNot(string name)
    {
        Assert.False(AudioDeviceCatalog.LooksHandsFree(name), name);
    }

    [Fact]
    public void AnEndpointDescribesItselfWellEnoughToBeChosen()
    {
        var device = new AudioDeviceInfo
        {
            Id = "{0.0.1.00000000}.{guid}",
            Name = "Kulaklıklar (AirPods Pro)",
            IsCommunicationsDefault = true,
            SampleRate = 48_000,
            Channels = 2,
        };

        Assert.Contains("aramalar için varsayılan", device.Description);
        Assert.Contains("48 kHz", device.Description);
        Assert.DoesNotContain("mono", device.Description);
    }

    [Fact]
    public void AHandsFreeEndpointSaysSoInItsDescription()
    {
        // Shown next to the name, because the two entries otherwise differ only by a word most
        // people have no reason to know the meaning of.
        var device = new AudioDeviceInfo
        {
            Id = "x",
            Name = "Headset (AirPods Pro Hands-Free)",
            IsHandsFree = true,
            SampleRate = 16_000,
            Channels = 1,
        };

        Assert.Contains("düşük kalite", device.Description);
        Assert.Contains("16 kHz mono", device.Description);
    }

    [Fact]
    public void AnEndpointThatRefusesToDescribeItselfIsStillListed()
    {
        // Some virtual devices refuse format negotiation. Not knowing the rate is acceptable;
        // dropping the device from the list is not, because it may be the one that works.
        var device = new AudioDeviceInfo { Id = "x", Name = "Sanal cihaz" };

        Assert.Equal("", device.Description);
    }

    [Fact]
    public void OnlyOneDefaultIsMentionedRatherThanBoth()
    {
        // A device that is both defaults would otherwise read "aramalar için varsayılan · müzik
        // ve video için varsayılan", which is longer and says nothing extra: the call default is
        // the one this application uses.
        var device = new AudioDeviceInfo
        {
            Id = "x",
            Name = "Hoparlör",
            IsCommunicationsDefault = true,
            IsMultimediaDefault = true,
        };

        Assert.Contains("aramalar için varsayılan", device.Description);
        Assert.DoesNotContain("müzik", device.Description);
    }
}
