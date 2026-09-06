using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Worker;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Measures one call's level and pitch, and files the numbers.
///
/// A class of its own for the same reason as <see cref="HabitCounter"/>: the orchestrator is not
/// constructible in a test — it opens capture devices — so a rule written inside it is a rule
/// nothing can check. Here the decision of when to measure, and what counts as already measured,
/// is one method with a test beside it.
///
/// Unlike the habit count this one is not free: it reads the audio and runs a worker process for
/// a few seconds per call. So it runs after the transcript is safe, off the GPU path (the
/// arithmetic is numpy on the CPU), and a failure is a missing measurement rather than a failed
/// conversation.
///
/// Nothing here interprets anything. The numbers are stored; whether a stretch "stands out" is
/// recomputed on the screen from a threshold that is still a guess (PLAN-SOSYALZEKA §6.3), and
/// the timeline band that would draw them stays off until sixty peaks have been listened to.
/// </summary>
public static class ProsodyMeasurer
{
    /// <summary>
    /// Measures unless the stored numbers were already made from this recording.
    ///
    /// "This recording" is a file name and a length per channel. A re-transcription does not
    /// change either, which is the point: prosody comes out of the audio, and re-running a minute
    /// of CPU to rediscover the same figures after a better engine has been tried would be work
    /// for nothing. Trimming the silence or re-encoding a file does change it, and then the old
    /// numbers describe audio that no longer exists.
    /// </summary>
    /// <returns>True when a measurement was written.</returns>
    public static async Task<bool> MeasureIfStaleAsync(
        Repository repository,
        Func<PythonWorkerHost> worker,
        long callId,
        CancellationToken cancellationToken = default)
    {
        var call = repository.GetCall(callId);
        if (call is null) return false;

        // The worker reads WAV. A compressed archive file is materialised the same way playback
        // and transcription materialise it, so the measurement never runs against a file the
        // worker would have to guess at.
        var mic = AudioMaterialiser.EnsurePcm(call.MicPath);
        var far = AudioMaterialiser.EnsurePcm(call.FarPath);

        if (mic is null && far is null) return false;

        var key = ProsodySeries.AudioKey(mic, far);

        if (repository.GetProsody(callId) is { } stored && stored.AudioKey == key) return false;

        var measured = await worker().AnalyseProsodyAsync(
            new ProsodyRequest { Id = $"prosody-{callId}", MicPath = mic, FarPath = far },
            cancellationToken: cancellationToken);

        var snapshot = new ProsodySnapshot(
            measured.BinSeconds,
            Channel(measured, "mic"),
            Channel(measured, "far"));

        repository.SaveProsody(callId, key, snapshot.ToJson());

        return true;
    }

    /// <summary>
    /// One channel as the worker sent it: four numbers per bin, the pitch null where the half
    /// second carried none. A row of the wrong shape is dropped rather than guessed at — a bin
    /// with a made-up level would be indistinguishable from a measured one.
    /// </summary>
    private static ProsodyChannel? Channel(WorkerProsody measured, string name)
    {
        if (!measured.Channels.TryGetValue(name, out var channel) || channel is null) return null;

        var bins = new List<ProsodyBin>(channel.Bins.Length);

        foreach (var row in channel.Bins)
        {
            if (row is not { Length: >= 4 }) continue;
            if (row[0] is not { } start || row[1] is not { } dbfs || row[3] is not { } voiced) continue;

            bins.Add(new ProsodyBin(start, dbfs, row[2], voiced));
        }

        return new ProsodyChannel(channel.FloorDbfs, channel.SpeechSeconds, bins);
    }

    /// <summary>
    /// The stretches a reading must leave out: where both people were talking at once, and where
    /// the far side came through the microphone.
    ///
    /// A level measured across another voice is that other voice. Left in, one person's raised
    /// voice would appear on the other's curve — which is the single way this measurement could
    /// say something false about somebody.
    /// </summary>
    public static IReadOnlyList<(int StartMs, int EndMs)> ExcludedRegions(Repository repository, long callId) =>
    [
        .. repository.GetSegments(callId)
            .Where(s => s.OverlapsOtherSpeaker || s.SuspectedEcho)
            .Select(s => (s.StartMs, s.EndMs)),
    ];
}
