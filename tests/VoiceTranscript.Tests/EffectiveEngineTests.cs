using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// What will actually transcribe the next call, said where the choice is made.
///
/// This screen let somebody believe the opposite of what it was set to, and the way it did that
/// is worth keeping in mind: the mode is a small combo at the top of a long page, and the local
/// model has its own large card far below with a model name on it. Choosing a model there is the
/// act a person remembers — so "yerel seçili" was a true memory of something that had no effect,
/// while every call went to a hosted service. The only thing that disagreed was a status line on
/// a different screen, which was then reported as a bug in the status line.
/// </summary>
public class EffectiveEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-engine-{Guid.NewGuid():N}");
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Nothing here holds a handle; the directory can still be busy on Windows.
        }

        GC.SuppressFinalize(this);
    }

    private SettingsViewModel Model(TranscriptionMode mode, bool withService)
    {
        var settings = new AppSettings
        {
            AsrMode = mode,
            AsrModelId = "faster-whisper-large-v3-turbo",
            SttEndpoints = withService
                ?
                [
                    new SttEndpoint
                    {
                        Id = "a", Kind = "ex5", Name = "ex5 Whisper (kendi sunucumuz)",
                        BaseUrl = "https://stt.ex5.ai/v1", ApiKey = "wsk_test", Model = "whisper-1",
                        Enabled = true,
                    },
                ]
                : [],
        };

        return new SettingsViewModel(settings, new AppPaths(_root), _http);
    }

    [Fact]
    public void LocalOnlyNamesTheLocalModelAndSaysNothingLeaves()
    {
        var line = Model(TranscriptionMode.LocalOnly, withService: true).EffectiveEngineLine;

        Assert.Contains("çıkmaz", line);
        Assert.DoesNotContain("ex5", line);
    }

    /// <summary>
    /// The case that produced the report. The sentence has to name the service AND say the local
    /// model is not used, because the local model card is the thing the person was looking at.
    /// </summary>
    [Fact]
    public void CloudOnlyNamesTheServiceAndSaysTheLocalChoiceIsIdle()
    {
        var line = Model(TranscriptionMode.CloudOnly, withService: true).EffectiveEngineLine;

        Assert.Contains("ex5 Whisper (kendi sunucumuz)", line);
        Assert.Contains("ÇIKAR", line);
        Assert.Contains("kullanılmaz", line);
    }

    [Fact]
    public void AutomaticSaysWhichOneWhen()
    {
        var line = Model(TranscriptionMode.Automatic, withService: true).EffectiveEngineLine;

        Assert.Contains("Ekran kartı çalışıyorsa", line);
        Assert.Contains("ex5 Whisper (kendi sunucumuz)", line);
    }

    /// <summary>
    /// Cloud-only with nothing configured is a setting that cannot transcribe anything, and
    /// saying so here is cheaper than a failed run an hour later.
    /// </summary>
    [Fact]
    public void CloudOnlyWithNoServiceSaysSo()
    {
        var line = Model(TranscriptionMode.CloudOnly, withService: false).EffectiveEngineLine;

        Assert.Contains("servis yok", line);
    }

    [Fact]
    public void TheSentenceFollowsTheMode()
    {
        var model = Model(TranscriptionMode.CloudOnly, withService: true);

        var changed = false;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.EffectiveEngineLine)) changed = true;
        };

        model.AsrMode = TranscriptionMode.LocalOnly;

        Assert.True(changed);
        Assert.Contains("çıkmaz", model.EffectiveEngineLine);
    }
}
