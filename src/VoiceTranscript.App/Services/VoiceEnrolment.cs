using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Voice;
using VoiceTranscript.Worker;

namespace VoiceTranscript.App.Services;

/// <summary>What learning one person's voice produced, including what it refused to use.</summary>
public sealed record EnrolmentResult(
    long ContactId,
    bool Learned,
    IReadOnlyList<long> Used,
    IReadOnlyList<long> Rejected);

/// <summary>
/// Learning what the people in the archive sound like, from the calls already filed under them.
///
/// The material is free: this application has been recording both sides of conversations and the
/// user has been labelling them for weeks. Nothing new has to be collected and nobody has to read
/// a sentence into a microphone.
///
/// <b>Only calls the user filed themselves are used.</b> A call that the voice recogniser itself
/// filed must never go on to teach the recogniser, or one wrong match becomes the evidence for the
/// next one and the error compounds silently. This project has already run exactly that loop once:
/// the vocabulary miner read its own bad transcripts back in as proper nouns, fed them to the
/// recogniser as a prompt, and got worse every round until somebody measured it. The rule that
/// came out of that is the rule here — a machine's own output is not evidence about the machine.
///
/// <b>And the labels themselves are not trusted.</b> They are hand-typed and the archive contains
/// demonstrable mistakes: two recordings filed under different names score 0.910 against each
/// other, higher than any pair that is genuinely the same person. A single call filed under the
/// wrong name would drag that person's average towards a stranger and quietly poison every later
/// match, so the recordings are checked against each other first and the odd one out is left out
/// — and named, because a recording that does not sound like the person it is filed under is
/// usually filed under the wrong person.
/// </summary>
public sealed class VoiceEnrolment(
    Repository repository,
    Func<PythonWorkerHost> worker,
    string modelDirectory,
    Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Learns one person's voice, or says why it could not.</summary>
    public async Task<EnrolmentResult> LearnAsync(long contactId, CancellationToken cancellationToken = default)
    {
        var calls = repository.VoiceEnrolmentCalls(contactId);
        var embeddings = new List<(long CallId, float[] Vector)>();
        var model = "";
        double speech = 0;

        foreach (var (callId, farPath) in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The archive is Opus; the embedder wants PCM. EnsurePcm is the same expansion the
            // transcriber uses, and it caches, so re-learning a person costs nothing the second
            // time.
            var pcm = AudioMaterialiser.EnsurePcm(farPath);
            if (pcm is null) continue;

            try
            {
                var voiceprint = await worker().EmbedSpeakerAsync(
                    new SpeakerRequest { Id = $"enrol-{callId}", WavPath = pcm, CacheDir = modelDirectory },
                    cancellationToken);

                if (!voiceprint.Usable) continue;

                embeddings.Add((callId, voiceprint.Vector!));
                model = voiceprint.Model;
                speech += voiceprint.SpeechSeconds;
            }
            catch (Exception e)
            {
                // One unreadable recording must not cost the person their voice.
                _log($"ses izi: görüşme #{callId} okunamadı ({e.Message})");
            }
        }

        if (embeddings.Count == 0) return new EnrolmentResult(contactId, false, [], []);

        var (vector, used, rejected) = VoiceMatcher.Enrol(embeddings);

        if (vector.Length == 0)
        {
            // Nothing agreed with anything. A voiceprint built from recordings that contradict
            // each other would file future calls under whichever of them happened to win, so the
            // old one is removed rather than replaced with a worse one.
            repository.DeleteVoiceprint(contactId);
            _log($"ses izi: kişi #{contactId} · kayıtlar birbirini tutmuyor, iz kurulmadı");

            return new EnrolmentResult(contactId, false, [], rejected);
        }

        repository.SaveVoiceprint(new Voiceprint
        {
            ContactId = contactId,
            Vector = vector,
            Model = model,
            CallsUsed = used.Count,
            SpeechSeconds = speech,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // The name is deliberately absent: this log is written to be shared and promises to carry
        // nobody's name. The contact number is enough to find the row.
        _log(rejected.Count == 0
            ? $"ses izi: kişi #{contactId} · {used.Count} görüşmeden kuruldu"
            : $"ses izi: kişi #{contactId} · {used.Count} görüşmeden kuruldu, "
              + $"etiketiyle uyuşmayan {rejected.Count} görüşme dışarıda bırakıldı: "
              + $"#{string.Join(", #", rejected)}");

        return new EnrolmentResult(contactId, true, used, rejected);
    }

    /// <summary>
    /// Learns every voice the archive can teach, one person at a time.
    ///
    /// Sequential on purpose. Each embedding is its own Python process — the worker runs one job
    /// per process — and running several at once would compete for the same cores the user is
    /// working on. This is a background chore, not something anybody is waiting for.
    /// </summary>
    public async Task<IReadOnlyList<EnrolmentResult>> LearnEverybodyAsync(
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var contacts = repository.ContactsWorthEnrolling();
        var results = new List<EnrolmentResult>();

        _log($"ses izi: {contacts.Count} kişi taranıyor");

        foreach (var contactId in contacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await LearnAsync(contactId, cancellationToken));
            progress?.Report((results.Count, contacts.Count));
        }

        var learned = results.Count(r => r.Learned);
        var suspect = results.Sum(r => r.Rejected.Count);

        _log($"ses izi: {learned}/{contacts.Count} kişi öğrenildi"
             + (suspect > 0 ? $" · {suspect} görüşme etiketiyle uyuşmuyor" : ""));

        return results;
    }
}
