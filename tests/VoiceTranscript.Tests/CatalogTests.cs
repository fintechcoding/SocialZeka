using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// The catalogs drive what the settings UI offers. These tests guard the invariants that make
/// that offer honest: the defaults must be sane, the numbers must be present, and anything
/// risky must carry its warning.
/// </summary>
public class CatalogTests
{
    [Fact]
    public void AsrModelIds_AreUnique()
    {
        var ids = AsrCatalog.All.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ExactlyOneAsrModel_IsMarkedRecommended()
        => Assert.Single(AsrCatalog.All, m => m.IsRecommended);

    [Fact]
    public void DefaultAsrModel_IsTheRecommendedOne()
    {
        Assert.True(AsrCatalog.Default.IsRecommended);
        Assert.Equal(AsrEngineKind.FasterWhisper, AsrCatalog.Default.Engine);
    }

    /// <summary>
    /// The default must fit the target card with room for the runtime, otherwise the very first
    /// real transcription silently spills to system memory and runs ten times slower.
    /// </summary>
    [Fact]
    public void DefaultAsrModel_FitsTheTargetGpu()
        => Assert.True(AsrCatalog.Default.VramGb < 3.0, $"{AsrCatalog.Default.VramGb} GB is too much for a 6 GB card alongside everything else");

    [Fact]
    public void DevelopmentModel_RunsWithoutAGpu()
    {
        var dev = AsrCatalog.Get(AsrCatalog.DevelopmentModelId);
        Assert.True(dev.RunsOnCpu);
        Assert.Equal(0, dev.VramGb);
    }

    /// <summary>
    /// Every model that is measurably worse than the default must say so where the user chooses,
    /// not somewhere else. Two entries exist purely so they can be compared and rejected.
    /// </summary>
    [Fact]
    public void ModelsWorseThanTheDefault_CarryAWarning()
    {
        var defaultWer = AsrCatalog.Default.Wer!.MediaSpeech!.Value;

        var worseWithoutWarning = AsrCatalog.All
            .Where(m => m.Wer?.MediaSpeech is { } wer && wer > defaultWer + 1.0)
            .Where(m => string.IsNullOrWhiteSpace(m.Warning))
            .Select(m => m.Id)
            .ToList();

        Assert.Empty(worseWithoutWarning);
    }

    [Fact]
    public void UnconfirmedRepositories_CarryAWarning()
    {
        var missing = AsrCatalog.All
            .Where(m => m.RepositoryUnconfirmed && string.IsNullOrWhiteSpace(m.Warning))
            .Select(m => m.Id);

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryAsrModel_HasSummaryAndModelRef()
    {
        Assert.All(AsrCatalog.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Summary), $"{m.Id} has no summary");
            Assert.False(string.IsNullOrWhiteSpace(m.ModelRef), $"{m.Id} has no model reference");
        });
    }

    [Fact]
    public void FittingIn_ExcludesModelsTooLargeForTheBudget()
    {
        var fits = AsrCatalog.FittingIn(2.0).ToList();
        Assert.DoesNotContain(fits, m => m.Id == "faster-whisper-large-v3");
        Assert.Contains(fits, m => m.Id == AsrCatalog.DefaultModelId);
    }

    [Fact]
    public void TurkishWer_AverageIgnoresMissingDatasets()
    {
        var wer = new TurkishWer(10.0, 20.0, null);
        Assert.Equal(15.0, wer.Average);

        Assert.Null(new TurkishWer(null, null, null).Average);
    }

    // ---- LLM ----------------------------------------------------------------

    [Fact]
    public void DefaultLlmModel_FitsTheUsableVramBudget()
    {
        var model = LocalLlmCatalog.Default;
        Assert.True(
            model.TotalGb <= LocalLlmCatalog.UsableVramGb,
            $"{model.DisplayName} needs {model.TotalGb} GB but only {LocalLlmCatalog.UsableVramGb} GB is usable");
    }

    [Fact]
    public void LlmModelsThatDoNotFit_CarryAWarning()
    {
        var silentlyTooBig = LocalLlmCatalog.All
            .Where(m => m.TotalGb > LocalLlmCatalog.UsableVramGb)
            .Where(m => string.IsNullOrWhiteSpace(m.Warning))
            .Select(m => m.Id);

        Assert.Empty(silentlyTooBig);
    }

    [Fact]
    public void FittingIn_ReturnsOnlyModelsWithinBudget()
        => Assert.All(LocalLlmCatalog.FittingIn(), m => Assert.True(m.TotalGb <= LocalLlmCatalog.UsableVramGb));

    /// <summary>
    /// The whole point of the project is that conversations stay on the machine. A provider that
    /// breaks that must be flagged, and the default must never be one of them.
    /// </summary>
    [Fact]
    public void DefaultLlmProvider_IsLocalAndAppManaged()
    {
        var provider = LlmProviders.Get(LlmProviderKind.LlamaServer);
        Assert.False(provider.SendsDataOffMachine);
        Assert.True(provider.IsSupervisedByApp);
        Assert.False(provider.RequiresApiKey);
    }

    [Fact]
    public void OpenRouter_IsMarkedAsLeavingTheMachine()
    {
        var provider = LlmProviders.Get(LlmProviderKind.OpenRouter);
        Assert.True(provider.SendsDataOffMachine);
        Assert.True(provider.RequiresApiKey);
    }

    [Fact]
    public void LocalProviders_DoNotClaimToNeedAnApiKey()
    {
        foreach (var kind in new[] { LlmProviderKind.LlamaServer, LlmProviderKind.Ollama, LlmProviderKind.LmStudio })
        {
            var provider = LlmProviders.Get(kind);
            Assert.False(provider.SendsDataOffMachine, $"{provider.DisplayName} should be local");
            Assert.False(provider.RequiresApiKey, $"{provider.DisplayName} should not need a key");
        }
    }

    /// <summary>
    /// Whisper and the analysis model cannot share 6 GB, so at least one local provider has to
    /// support being told to let go of the GPU.
    /// </summary>
    [Fact]
    public void AtLeastOneLocalProvider_CanReleaseTheGpu()
        => Assert.Contains(LlmProviders.All, p => !p.SendsDataOffMachine && p.SupportsExplicitUnload);

    [Fact]
    public void EveryProvider_HasASummary()
        => Assert.All(LlmProviders.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Summary)));

    // ---- credential hygiene ------------------------------------------------------------------
    //
    // The worker echoes the full "url|key|model" reference back in its result, and recording
    // that echo verbatim put a live API key into the database and onto the conversation
    // window's provenance line — seen on a real screenshot. These pin the scrub at every layer.

    [Fact]
    public void AThreePartEngineRefLosesItsMiddlePart()
        => Assert.Equal(
            "https://api.openai.com/v1|whisper-1",
            SttEndpoint.ScrubRef("https://api.openai.com/v1|sk-proj-SECRET|whisper-1"));

    [Fact]
    public void AnAlreadyCleanRefPassesThroughUnchanged()
    {
        Assert.Equal("https://api.openai.com/v1|whisper-1",
            SttEndpoint.ScrubRef("https://api.openai.com/v1|whisper-1"));
        Assert.Equal("faster-whisper", SttEndpoint.ScrubRef("faster-whisper"));
    }

    [Fact]
    public void DisplayForNeverShowsTheKeyOfALegacyThreePartRef()
    {
        var shown = AsrCatalog.DisplayFor("https://api.openai.com/v1|sk-proj-SECRET|whisper-1");

        Assert.DoesNotContain("SECRET", shown);
        Assert.Equal("api.openai.com · whisper-1", shown);
    }

    [Fact]
    public void DisplayForRendersACleanRefAsHostAndModel()
        => Assert.Equal(
            "api.groq.com · whisper-large-v3",
            AsrCatalog.DisplayFor("https://api.groq.com/openai/v1|whisper-large-v3"));
}
