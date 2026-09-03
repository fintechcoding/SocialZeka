using System.Windows;
using VoiceTranscript.App.Services;

namespace VoiceTranscript.Tests;

/// <summary>
/// Where the call overlays sit once they can be moved.
///
/// The strip is the only signal that says the microphone is open, so every way of losing it is a
/// way of leaving somebody recording without knowing. Once a position is remembered, the ways of
/// losing it are ordinary: the second monitor is unplugged, the laptop is docked at a different
/// resolution, the taskbar moves and the work area shrinks under a strip that was dropped near
/// the edge.
/// </summary>
public class OverlayPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void NeverMovedMeansCentredOnTheTopEdge()
    {
        var at = OverlayPlacement.Resolve(300, 34, Screen, null, null);

        Assert.Equal(810, at.X);
        Assert.Equal(0, at.Y);
    }

    [Fact]
    public void MovedMeansWhereItWasLeft()
    {
        var at = OverlayPlacement.Resolve(300, 34, Screen, 640, 500);

        Assert.Equal(640, at.X);
        Assert.Equal(500, at.Y);
    }

    /// <summary>
    /// A position saved on a monitor that is no longer attached. Honouring it literally puts the
    /// recording strip somewhere nobody can see it, on the one screen the user has left.
    /// </summary>
    [Fact]
    public void APositionOnAScreenThatIsGoneComesBack()
    {
        var at = OverlayPlacement.Resolve(300, 34, Screen, 3400, 1600);

        Assert.Equal(1620, at.X);
        Assert.Equal(1006, at.Y);
    }

    [Fact]
    public void ANegativePositionComesBackToo()
    {
        var at = OverlayPlacement.Resolve(300, 34, Screen, -500, -80);

        Assert.Equal(0, at.X);
        Assert.Equal(0, at.Y);
    }

    /// <summary>
    /// A panel wider than the screen still starts at the left edge. Clamping the other way round
    /// would produce a negative left and push its only close button off the display.
    /// </summary>
    [Fact]
    public void SomethingWiderThanTheScreenStartsAtItsEdge()
    {
        var at = OverlayPlacement.Resolve(2400, 1200, Screen, 900, 900);

        Assert.Equal(0, at.X);
        Assert.Equal(0, at.Y);
    }

    /// <summary>The work area, not the screen: a strip under the taskbar is a strip nobody sees.</summary>
    [Fact]
    public void ItStaysInsideTheWorkArea()
    {
        var docked = new Rect(0, 40, 1920, 960);

        var at = OverlayPlacement.Resolve(300, 34, docked, 100, 0);

        Assert.Equal(40, at.Y);
    }
}
