using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// A worked example, loaded on demand.
///
/// This exists because of a gap between installing the application and understanding it. The
/// value here is not the recording, it is what shows up three calls later: a price that moved
/// twice, a promise that came due, a question that was asked twice and answered neither time.
/// Nobody can see that on their first day, and asking somebody to use a call recorder for a
/// month on faith is asking a lot.
///
/// So the example is a real one, written into the same tables as a real conversation, and it
/// removes cleanly. Everything about it is marked as a sample: the contact is named as one, the
/// audio is a synthesised envelope rather than a voice, and deleting the contact takes all of it
/// with it — the same delete a real person gets.
/// </summary>
public static class SampleData
{
    /// <summary>The contact name. Marked so nobody mistakes it for somebody they know.</summary>
    public const string ContactName = "Örnek Görüşme (demo)";

    /// <summary>One line of the worked example.</summary>
    private sealed record Line(bool IsMe, int StartMs, int EndMs, string Text, bool LowConfidence = false);

    /// <summary>Whether the sample is already in the archive.</summary>
    public static bool IsLoaded(Repository repository) =>
        repository.FindContacts(ContactName).Any(c => c.Name == ContactName);

    /// <summary>Removes the sample and everything belonging to it.</summary>
    public static void Remove(Repository repository)
    {
        foreach (var contact in repository.FindContacts(ContactName).Where(c => c.Name == ContactName))
            repository.DeleteContactCompletely(contact.Id);
    }

    /// <summary>
    /// Writes three conversations six weeks apart, with the ledger they produce.
    ///
    /// The figures are the point. Twelve thousand becomes fourteen and a half, then eighteen; a
    /// Friday deadline is given and passes; the same question about the invoice is asked twice
    /// and dodged both times. That is exactly the pattern this product exists to make visible,
    /// and it is invisible inside any single one of the three calls.
    /// </summary>
    public static void Load(Repository repository, AppPaths paths)
    {
        if (IsLoaded(repository)) return;

        var contactId = repository.UpsertContact(ContactName, CallApp.WhatsApp);
        var now = DateTimeOffset.Now;

        var first = WriteCall(repository, paths, contactId, now.AddDays(-48), FirstCall());
        var second = WriteCall(repository, paths, contactId, now.AddDays(-24), SecondCall());
        var third = WriteCall(repository, paths, contactId, now.AddDays(-9), ThirdCall());

        WriteLedger(repository, contactId, first, second, third);
    }

    // ---- the conversations ---------------------------------------------------

    private static Line[] FirstCall() =>
    [
        new(false, 0, 3200, "Merhaba, teklifi inceledim. Fiyat konusunda anlaşabilir miyiz?"),
        new(true, 3400, 6100, "Tabii, dinliyorum. Aklınızdaki rakam nedir?"),
        new(false, 6300, 11800, "On iki bin lira diye konuşmuştuk, ben de ona göre bütçe ayırdım."),
        new(true, 12000, 15400, "Evet, on iki bin. Bu rakam bu ay için geçerli."),
        new(false, 15600, 21200, "Anlaştık o zaman. Sözleşmeyi cuma günü size yollarım."),
        new(true, 21400, 24000, "Peki, cuma bekliyorum."),
        new(false, 24200, 28900, "Bir de faturayı hangi şirkete keseceğiz, onu netleştirelim."),
        new(true, 29100, 31500, "Merkez şirket üzerinden olacak."),
    ];

    private static Line[] SecondCall() =>
    [
        new(true, 0, 4100, "Merhaba. Sözleşme gelmedi, bir sıkıntı mı var?"),
        new(false, 4300, 10600, "Kusura bakmayın, araya işler girdi. Bu hafta içinde hallederim."),
        new(true, 10800, 14200, "Peki. Fiyatta bir değişiklik yok değil mi?"),
        new(false, 14400, 22800, "Aslında maliyetler arttı, on dört buçuk olarak güncellememiz gerekiyor."),
        new(true, 23000, 26400, "On iki bin konuşmuştuk ama."),
        new(false, 26600, 33100, "Biliyorum ama malzeme fiyatları çok oynadı, elimizde değil."),
        new(true, 33300, 37800, "Faturayı hangi şirkete keseceğiz, geçen sefer de sormuştum."),
        new(false, 38000, 42500, "Onu muhasebeyle konuşup size döneceğim."),
    ];

    private static Line[] ThirdCall() =>
    [
        new(false, 0, 5400, "Merhaba, işi başlatmak için bugün karar vermeniz lazım."),
        new(true, 5600, 8300, "Neden bugün? Acelesi ne?"),
        new(false, 8500, 16200, "Bu fiyat sadece bugün geçerli, yarın on sekiz bin olur ancak."),
        new(true, 16400, 20100, "On dört buçuk demiştiniz geçen görüşmede."),
        new(false, 20300, 27600, "Evet ama o zaman farklıydı, şimdi on sekiz bin olur ancak."),
        new(true, 27800, 32400, "Faturanın hangi şirkete kesileceğini hâlâ söylemediniz."),
        new(false, 32600, 37900, "Onu sonra konuşuruz, önce şu kararı verelim."),
        new(true, 38100, 42000, "Düşünüp size döneyim."),
    ];

    // ---- writing -------------------------------------------------------------

    private static long WriteCall(
        Repository repository,
        AppPaths paths,
        long contactId,
        DateTimeOffset when,
        Line[] lines)
    {
        var duration = TimeSpan.FromMilliseconds(lines[^1].EndMs + 2000);
        var directory = paths.RecordingDirectoryFor(when);

        Directory.CreateDirectory(directory);

        // Named before the row is written, so the paths can go in with it. The alternative
        // would be an update-after-insert that exists only for this one caller.
        var stem = $"sample-{when:yyyyMMdd-HHmmss}";
        var micPath = Path.Combine(directory, $"{stem}-mic.wav");
        var farPath = Path.Combine(directory, $"{stem}-far.wav");

        WriteEnvelope(micPath, duration, lines.Where(l => l.IsMe));
        WriteEnvelope(farPath, duration, lines.Where(l => !l.IsMe));

        var callId = repository.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            Direction = CallDirection.Incoming,
            Kind = CallKind.OneToOne,
            StartedAt = when,
            EndedAt = when + duration,
            Duration = duration,
            MicPath = micPath,
            FarPath = farPath,
            State = ProcessingState.Analysed,
            ObservedTitle = null,
        });

        repository.ReplaceSegments(callId, lines.Select(l => new Segment
        {
            CallId = callId,
            IsMe = l.IsMe,
            StartMs = l.StartMs,
            EndMs = l.EndMs,
            Text = l.Text,
            LowConfidence = l.LowConfidence,
        }));

        return callId;
    }

    /// <summary>
    /// Writes a WAV whose shape matches the conversation.
    ///
    /// Not a voice, and deliberately not one: synthesising somebody speaking would be both hard
    /// and dishonest. What it does produce is a real waveform with speech where speech was and
    /// silence where silence was, so the mirrored drawing shows exactly what it shows on a real
    /// call. Kept quiet enough that pressing play is not a surprise.
    /// </summary>
    private static void WriteEnvelope(string path, TimeSpan duration, IEnumerable<Line> lines)
    {
        var format = AudioFormat.WhisperPcm;
        var total = (int)(duration.TotalSeconds * format.SampleRate);
        var samples = new short[total];

        // A steady low hiss, so the file reads as a live microphone rather than a broken one.
        var noise = new Random(Seed: path.Length * 31 + total);
        for (var i = 0; i < total; i++) samples[i] = (short)noise.Next(-60, 60);

        foreach (var line in lines)
        {
            var from = Math.Clamp(line.StartMs * format.SampleRate / 1000, 0, total);
            var to = Math.Clamp(line.EndMs * format.SampleRate / 1000, 0, total);

            for (var i = from; i < to; i++)
            {
                // Syllables at roughly four a second, with a little variation so the drawing has
                // the uneven texture of speech rather than a solid block.
                var t = (i - from) / (double)format.SampleRate;
                var syllable = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4.2 * t);
                var envelope = syllable * syllable * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 0.7 * t));

                samples[i] = (short)(envelope * 5200 * Math.Sin(2 * Math.PI * 165 * t));
            }
        }

        using var sink = new WavPcmSink(path, format);

        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        sink.Write(bytes);
    }

    private static void WriteLedger(
        Repository repository,
        long contactId,
        long first,
        long second,
        long third)
    {
        // The price, said three times. Stored as claims so the application derives the change
        // itself rather than being told about it — the same path a real conversation takes.
        repository.InsertClaim(new Claim
        {
            CallId = first,
            ContactId = contactId,
            Quote = "On iki bin lira diye konuşmuştuk, ben de ona göre bütçe ayırdım.",
            QuoteStartMs = 6300,
            Entity = "iş",
            Attribute = "fiyat",
            Value = "12.000 TL",
            NumericValue = 12000,
            Unit = "TL",
        });

        repository.InsertClaim(new Claim
        {
            CallId = second,
            ContactId = contactId,
            Quote = "Aslında maliyetler arttı, on dört buçuk olarak güncellememiz gerekiyor.",
            QuoteStartMs = 14400,
            Entity = "iş",
            Attribute = "fiyat",
            Value = "14.500 TL",
            NumericValue = 14500,
            Unit = "TL",
        });

        repository.InsertClaim(new Claim
        {
            CallId = third,
            ContactId = contactId,
            Quote = "Bu fiyat sadece bugün geçerli, yarın on sekiz bin olur ancak.",
            QuoteStartMs = 8500,
            Entity = "iş",
            Attribute = "fiyat",
            Value = "18.000 TL",
            NumericValue = 18000,
            Unit = "TL",
        });

        // A promise with a date that has since passed.
        repository.InsertCommitment(new Commitment
        {
            CallId = first,
            ContactId = contactId,
            ByMe = false,
            Quote = "Sözleşmeyi cuma günü size yollarım.",
            QuoteStartMs = 15600,
            Obligation = "Sözleşmeyi göndermek",
            DeadlineRaw = "cuma günü",
            DeadlineDate = DateOnly.FromDateTime(DateTime.Now.AddDays(-41)),
            Status = CommitmentStatus.Open,
        });

        repository.InsertCommitment(new Commitment
        {
            CallId = second,
            ContactId = contactId,
            ByMe = false,
            Quote = "Onu muhasebeyle konuşup size döneceğim.",
            QuoteStartMs = 38000,
            Obligation = "Faturanın hangi şirkete kesileceğini bildirmek",
            DeadlineRaw = null,
            Status = CommitmentStatus.Open,
        });

        // A question asked twice, answered neither time. Countable, and checkable by listening.
        repository.InsertFlag(new Flag
        {
            CallId = third,
            ContactId = contactId,
            Kind = FlagKind.EvadedQuestion,
            Summary = "Fatura hangi şirkete kesilecek sorusu iki görüşmedir cevapsız",
            Quote = "Onu sonra konuşuruz, önce şu kararı verelim.",
            QuoteStartMs = 32600,
            CounterQuote = "Faturayı hangi şirkete keseceğiz, geçen sefer de sormuştum.",
            CounterCallId = second,
            CounterQuoteStartMs = 33300,
        });

        repository.InsertFlag(new Flag
        {
            CallId = third,
            ContactId = contactId,
            Kind = FlagKind.PressureTactic,
            Summary = "Aciliyet vurgusu: bugün karar verilmesi isteniyor",
            Quote = "Bu fiyat sadece bugün geçerli, yarın on sekiz bin olur ancak.",
            QuoteStartMs = 8500,
            IsHeuristic = true,
        });

        repository.SaveSummary(new CallSummary
        {
            CallId = third,
            Summary =
                "Fiyat üçüncü kez değişti ve bugün karar verilmesi istendi. Faturanın hangi " +
                "şirkete kesileceği sorusu iki görüşmedir cevapsız. Sözleşme hâlâ gelmedi.",
            ActionItems = "Sözleşmeyi yazılı olarak iste. Fatura şirketini netleştirmeden ödeme yapma.",
            ModelUsed = "örnek",
            CreatedAt = DateTimeOffset.Now.AddDays(-9),
        });
    }
}
