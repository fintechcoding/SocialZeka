using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// One word per state, everywhere. A failed call was four different words on four screens.
/// </summary>
public sealed class CallStateTextTests
{
    private static Call With(ProcessingState state, string? reason = null, CallKind kind = CallKind.OneToOne) => new()
    {
        App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = state, FailureReason = reason, Kind = kind,
    };

    [Theory]
    [InlineData(ProcessingState.Recorded, "Sırada")]
    [InlineData(ProcessingState.Queued, "Sırada")]
    [InlineData(ProcessingState.Transcribing, "Yazıya dökülüyor")]
    [InlineData(ProcessingState.Transcribed, "Çözümlenmedi")]
    [InlineData(ProcessingState.Analysing, "Çözümleniyor")]
    [InlineData(ProcessingState.Failed, "İşlenemedi")]
    public void EveryStateHasOneWord(ProcessingState state, string expected)
    {
        Assert.Equal(expected, CallStateText.Short(With(state)));
        Assert.Equal(expected, CallStateText.Label(With(state)));
    }

    [Fact]
    public void TheOrdinaryFinishedStateIsSilentInRowsAndNamedInFilters()
    {
        Assert.Null(CallStateText.Short(With(ProcessingState.Analysed)));
        Assert.Equal("Hazır", CallStateText.Label(With(ProcessingState.Analysed)));
    }

    [Fact]
    public void AGroupCallSaysSoWhateverItsState()
    {
        Assert.Equal("Grup — sadece ses", CallStateText.Short(With(ProcessingState.Analysed, kind: CallKind.Group)));
    }

    [Theory]
    [InlineData("Çok kısa kayıt.", "Çok kısa — ses silindi")]
    [InlineData("Kullanıcı durdurdu; yeniden işlenebilir.", "Durduruldu — yeniden işlenebilir")]
    [InlineData("Grup araması", "Atlandı")]
    [InlineData(null, "Atlandı")]
    public void SkippedIsToldApartByItsReason(string? reason, string expected)
    {
        Assert.Equal(expected, CallStateText.Short(With(ProcessingState.Skipped, reason)));
    }

    [Fact]
    public void FailedIsRedWorkingIsAccentWaitingIsFaint()
    {
        Assert.Equal("SystemFillColorCriticalBrush", CallStateText.BrushKey(ProcessingState.Failed));
        Assert.Equal("AccentTextFillColorPrimaryBrush", CallStateText.BrushKey(ProcessingState.Transcribing));
        Assert.Equal("TextFillColorTertiaryBrush", CallStateText.BrushKey(ProcessingState.Queued));
    }

    [Fact]
    public void TheUserIsSenAndTheOtherPartyIsTheirNameWhenKnown()
    {
        Assert.Equal("Sen", SpeakerText.For(true, "Uliana"));
        Assert.Equal("Uliana", SpeakerText.For(false, "Uliana"));
        Assert.Equal("karşı taraf", SpeakerText.For(false, null));
        Assert.Equal("karşı taraf", SpeakerText.Other("  "));
    }
}
