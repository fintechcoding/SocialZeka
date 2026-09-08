"""
Telling a call that rang and was never answered from a call somebody picked up.

The recorder cannot ask the messenger whether a call was answered, and for an outgoing call it
cannot work it out from the audio devices either: the microphone opens the moment you dial, so a
phone ringing at the other end looks exactly like a conversation to the detector (mic open,
speaker playing). The call is recorded, uploaded to a paid transcriber, comes back with no words
in it, and lands in the archive as a failure somebody has to delete by hand. Four of them on this
machine in three days, and each one had cost an upload before anyone saw it.

The audio itself says which it was, and it says it loudly. A ring-back is one recorded sound
played on a clock; a conversation never repeats itself. Measured on this archive, over every
recording that could be read (33 channels, 20-400 s), the far channel of the one known unanswered
call against every answered one:

    kanal                    parça  dönem   düzensizlik  benzerlik
    97 far  (cevapsız)         10    6.0 s      0.00        1.00
    88 far  (kısa, 2 satır)     4    6.0 s      0.00        1.00
    72 far  (kısa, 1 satır)     3    4.5 s      0.00        1.00
    otuz gerçek görüşme        3-219  1.5-7.4 s  0.29-1.28  -0.02-0.61

Nothing sits between the two groups. The bursts of a ring-back are the same buffer replayed, so
consecutive ones correlate at 1.00 and their spacing has no jitter at all; the loudest a
conversation ever came to that was 0.61 and 0.29. That is not a threshold picked to fit, it is a
gap with nothing in it.

**Two conditions, not one, and the second is what makes this safe to act on.** The far channel
ringing says the other end had not picked up *yet*; it does not say the call was never answered,
and it does not say the recording is worthless. So the microphone has to agree: nobody said
anything on this side either. Measured, the microphone of the unanswered call moves 7.0 dB
between its quietest and loudest tenth — room tone and nothing else — while every answered call's
microphone moves 37-69 dB. Call 88 is exactly why this matters: its far channel is a textbook
ring-back, and its microphone moves 53.7 dB, because the user was talking. That call is a
recording of somebody speaking and must not be thrown away.

What this module does NOT do: decide anything. It reports what it measured, and the caller
decides. A recording is deleted on the strength of these numbers, so they are returned with the
evidence attached rather than as a bare yes.
"""

from __future__ import annotations

import wave
from contextlib import closing
from dataclasses import dataclass

# numpy is imported inside the functions that need it, not here, and that is a rule of this
# package rather than a preference. The worker's probe and download commands run BEFORE the
# setup wizard has installed anything, on a machine where numpy may not exist yet; __main__
# imports every module unconditionally, so one import at a module top turns "no numpy yet" into
# a worker that cannot start and a setup screen that cannot say why. prosody.py and speaker.py
# both do it this way for the same reason.

#: Framing, shared with prosody.py and speaker.py rather than chosen again here.
FRAME_MS = 25
HOP_MS = 10

#: Shortest run of loud frames that counts as a burst. Under 200 ms is a click, not a ring.
MIN_BURST_FRAMES = 20

#: A ring-back has to repeat before it is a pattern. Two bursts is a coincidence; three is a clock.
MIN_BURSTS = 3

#: How far the WORST gap between two bursts may sit from the median gap, as a share of it.
#:
#: The worst one, not the average, and that distinction is the difference between a rule that
#: holds and a rule that got lucky. See :data:`MIN_SIMILARITY`.
MAX_JITTER = 0.15

#: How alike the LEAST alike pair of consecutive bursts has to be, as a correlation of envelopes.
#:
#: **Measured on the average first, and the average was nearly wrong.** A call that rang for a
#: minute and was then answered carries ten identical ring bursts followed by a few of speech, so
#: the mean similarity is dragged almost all the way up by the ring: 0.898 against a threshold of
#: 0.90. It passed by two thousandths, which is luck rather than a rule, and one more ring burst
#: would have deleted a real conversation.
#:
#: Asked of the worst pair the numbers separate completely, because the one pair that spans the
#: moment somebody picked up looks nothing like a repeat:
#:
#:     en kotu benzerlik        en kotu gap sapmasi
#:     saf calma          0.974-0.982     0.000
#:     calip sonra acilan 0.006-0.015     0.003   (far kanal, sentetik)
#:     otuz gercek gorusme -0.738-0.075   0.55-10.0
#:
#: The two ring-backs already in the archive (#72, #88) sit at 1.000 and 0.000 with the rest of
#: the ring-outs, which is what they are; #88 is spared by the microphone condition instead.
MIN_SIMILARITY = 0.90

#: What counts as somebody speaking, in dBFS.
#:
#: speaker.py's own number, taken rather than chosen again: "the single most load-bearing number
#: in the file", measured on this archive at a 13.8% error rate without it and 1.1% with it.
SPEECH_FLOOR_DBFS = -40.0

#: Shortest stretch above that floor which is a word rather than a knock, in frames.
SPEECH_RUN_FRAMES = 20

#: How many seconds of speech a channel may hold and still count as one nobody spoke on.
#:
#: **The percentile test this replaced was wrong, and wrong in the direction that deletes.** It
#: asked whether the channel's 95th percentile sat far above its 10th, which is a question about
#: how much of the recording is loud, not about whether anybody said anything: five per cent of a
#: sixty-second recording is three seconds, so a short "alo?" never lifted the 95th at all. A
#: microphone somebody spoke two words into measured 1 dB of spread and read as silent.
#:
#: Seconds above the speech floor asks the question directly. Measured over every microphone
#: channel in this archive under five minutes:
#:
#:     cagri 97 (cevapsiz)          0.3 sn
#:     cagri 90, 56, 57 (konusmus)  2.5, 3.0, 3.5 sn
#:     cagri 88 (konusmus)          5.5 sn   <- far kanali ders kitabi calma sesi
#:     cagri 58, 89, 96, 65         12.3, 14.0, 35.2, 38.6 sn
#:
#: One second sits between 0.3 and 2.5 with room on both sides, and leaves call 88 — the one the
#: microphone condition exists for — five times clear of it.
#:
#: The known miss: call 87, ten seconds long on a microphone so quiet nothing crossed the floor,
#: reads as silent. It is spared anyway, because its far channel is not a ring; the far condition
#: is the strong one and this is the second lock, not the first.
QUIET_SPEECH_SECONDS = 1.0

#: Longest recording this question is asked of at all.
#:
#: Nobody lets a phone ring for five minutes, so above this the answer is known without measuring
#: anything — and asking anyway would be the expensive half of the job on the recordings where it
#: is certain to say no. A fifty-minute conversation is 48 MB per channel of 16 kHz PCM, and the
#: whole point of running before the upload is that it costs nothing.
#:
#: The cap is on the WHOLE recording rather than on how much of it is read, and that distinction
#: is load-bearing. Reading only the first two minutes of a call answered at 2:30 would find a
#: ringing far channel over a silent microphone and delete a real conversation. Either the whole
#: recording is measured or the question is not asked.
MAX_SECONDS = 300.0


@dataclass(frozen=True)
class ChannelShape:
    """What one channel looks like, in the numbers the decision is made from."""

    seconds: float
    floor_dbfs: float
    peak_dbfs: float
    speech_seconds: float
    bursts: int
    period_s: float | None
    jitter: float | None
    similarity: float | None

    @property
    def range_db(self) -> float:
        return self.peak_dbfs - self.floor_dbfs

    @property
    def rings(self) -> bool:
        """
        Whether this channel is a recorded sound replayed on a clock.

        Every burst has to be like the one before it and every gap the same, rather than most of
        them: a call answered after a long ring is exactly a channel where most bursts repeat and
        the last few do not, and it must not read as a ring.
        """
        return (
            self.bursts >= MIN_BURSTS
            and self.jitter is not None
            and self.jitter <= MAX_JITTER
            and self.similarity is not None
            and self.similarity >= MIN_SIMILARITY
        )

    @property
    def silent(self) -> bool:
        """Whether nothing was said on this channel: too little of it is speech to be anybody."""
        return self.speech_seconds < QUIET_SPEECH_SECONDS

    def as_dict(self) -> dict:
        return {
            "seconds": round(self.seconds, 2),
            "floor_dbfs": round(self.floor_dbfs, 1),
            "peak_dbfs": round(self.peak_dbfs, 1),
            "range_db": round(self.range_db, 1),
            "speech_seconds": round(self.speech_seconds, 2),
            "bursts": self.bursts,
            "period_s": None if self.period_s is None else round(self.period_s, 2),
            "jitter": None if self.jitter is None else round(self.jitter, 3),
            "similarity": None if self.similarity is None else round(self.similarity, 3),
        }


def _samples(path: str):
    """
    One channel of 16-bit PCM as floats in -1..1, with its sample rate.

    Empty for a recording longer than :data:`MAX_SECONDS`, which is how the caller learns the
    question does not apply — read before the frames are counted, so a long call costs a header
    read rather than a hundred megabytes.
    """
    import numpy as np

    with closing(wave.open(path, "rb")) as handle:
        rate = handle.getframerate()
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        total = handle.getnframes()

        if width != 2 or rate <= 0 or channels <= 0:
            return np.zeros(0, dtype=np.float32), rate or 16_000

        if total / rate > MAX_SECONDS:
            return np.zeros(0, dtype=np.float32), rate

        raw = handle.readframes(total)

    data = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0

    return (data[::channels] if channels > 1 else data), rate


def shape_of(path: str) -> ChannelShape | None:
    """
    Measures one channel. None when there is not enough audio to say anything about.

    Never raises for a file it cannot read: a missing measurement is a call transcribed the
    ordinary way, which is the safe outcome, while an exception here would fail a recording that
    is perfectly good.
    """
    import numpy as np

    try:
        x, rate = _samples(path)
    except (OSError, wave.Error, ValueError):
        return None

    size = int(rate * FRAME_MS / 1000)
    hop = int(rate * HOP_MS / 1000)

    if size <= 0 or hop <= 0 or len(x) < size * 4:
        return None

    count = 1 + (len(x) - size) // hop

    # Frame energies from one running total rather than from a frame-by-frame copy.
    #
    # The copy is the obvious way to write this and it is the one that does not fit: overlapping
    # 25 ms frames every 10 ms hold each sample two and a half times, so five minutes of audio
    # becomes 48 MB of view and a 96 MB float64 square on top of it, per channel, before a single
    # number is computed. A cumulative sum gives the same energies from one pass and one array.
    energy = np.concatenate(([0.0], np.cumsum(x.astype(np.float64) ** 2)))
    starts = hop * np.arange(count)
    rms = np.sqrt(np.maximum(energy[starts + size] - energy[starts], 0.0) / size)

    level = 20 * np.log10(np.maximum(rms, 1e-9))

    floor = float(np.percentile(level, 10))
    peak = float(np.percentile(level, 95))

    # Relative to this channel's own floor and peak, never to a fixed number: the far channel is
    # digital silence between words and a microphone never is (chunking.SILENT_FLOOR_DBFS).
    gate = floor + max(6.0, (peak - floor) * 0.35)
    loud = level > gate

    runs = _runs(loud)
    starts = [start for start, _ in runs]

    gaps = np.diff(starts) * (HOP_MS / 1000.0) if len(starts) >= 2 else np.zeros(0)

    # The median rather than the mean, and the worst gap's distance from it rather than the
    # spread of all of them: one speech burst after a minute of ringing moves a mean and a
    # standard deviation hardly at all, and moves this straight past the threshold.
    period = float(np.median(gaps)) if gaps.size and np.median(gaps) > 0 else None
    jitter = (
        float(np.max(np.abs(gaps - period)) / period)
        if period is not None and period > 0
        else None
    )

    return ChannelShape(
        seconds=len(x) / rate,
        floor_dbfs=floor,
        peak_dbfs=peak,
        speech_seconds=_speech_seconds(level),
        bursts=len(runs),
        period_s=period,
        jitter=jitter,
        similarity=_similarity(x, runs, hop),
    )


def _speech_seconds(level) -> float:
    """
    How much of this channel is somebody speaking, in seconds.

    An absolute floor rather than one derived from the channel's own percentiles, because the
    question is "did anybody speak" and a channel with nothing in it has no scale to measure
    against. Runs shorter than :data:`SPEECH_RUN_FRAMES` are dropped: a door, a knock and a
    keyboard all cross the floor for a moment, and the microphone of the one unanswered call in
    this archive holds exactly such a transient.
    """
    above = level > SPEECH_FLOOR_DBFS

    total = 0
    index = 0

    while index < len(above):
        if not above[index]:
            index += 1
            continue

        end = index
        while end < len(above) and above[end]:
            end += 1

        if end - index >= SPEECH_RUN_FRAMES:
            total += end - index

        index = end

    return total * (HOP_MS / 1000.0)


def _runs(loud) -> list[tuple[int, int]]:
    """Contiguous stretches of loud frames, as (first, last-exclusive) frame indices."""
    runs: list[tuple[int, int]] = []
    index = 0

    while index < len(loud):
        if not loud[index]:
            index += 1
            continue

        end = index
        while end < len(loud) and loud[end]:
            end += 1

        if end - index >= MIN_BURST_FRAMES:
            runs.append((index, end))

        index = end

    return runs


def _similarity(x, runs: list[tuple[int, int]], hop: int) -> float | None:
    """
    How alike the LEAST alike pair of consecutive bursts is, or None when there are not two.

    Compared on the envelope — the absolute sample values — rather than on the waveform, because
    two plays of the same tone need not start on the same phase and a waveform correlation would
    read that as a difference. The shortest burst sets the length compared, capped at two seconds:
    a ring is decided well inside that, and a longer window would drift out of alignment.
    """
    import numpy as np

    if len(runs) < 2:
        return None

    span = min(min(end - start for start, end in runs), 200) * hop
    if span <= 0:
        return None

    pieces = [
        np.abs(x[start * hop: start * hop + span])
        for start, _ in runs
        if start * hop + span <= len(x)
    ]

    scores = []

    for first, second in zip(pieces, pieces[1:]):
        a = first - first.mean()
        b = second - second.mean()
        size = np.linalg.norm(a) * np.linalg.norm(b)

        if size > 0:
            scores.append(float(np.dot(a, b) / size))

    # The worst pair decides. A mean lets a long ring outvote the moment somebody answered, which
    # on a sixty-second ring-out plus thirty seconds of conversation came to 0.898 against a
    # threshold of 0.90 — two thousandths from deleting a recording of two people talking.
    return float(np.min(scores)) if scores else None


def unanswered(mic_path: str | None, far_path: str | None) -> dict | None:
    """
    Whether this recording is a call that rang and was never answered, with the evidence.

    Both halves have to hold. The far channel has to be a sound replayed on a clock, and the
    microphone has to carry nothing but room tone: a ring-back alone only says the other end had
    not picked up yet, and call 88 on this archive is a textbook ring-back over a microphone that
    moves 53 dB because the user was talking into it.

    Returns None when the question cannot be answered — a channel missing, a file that will not
    read, audio too short to have a pattern in it. None means "transcribe it the ordinary way",
    which is the outcome that loses nothing.
    """
    if not far_path or not mic_path:
        return None

    far = shape_of(far_path)
    mic = shape_of(mic_path)

    if far is None or mic is None:
        return None

    verdict = far.rings and mic.silent

    return {
        "unanswered": verdict,
        "far": far.as_dict(),
        "mic": mic.as_dict(),
        "why": _why(far, mic, verdict),
    }


def _why(far: ChannelShape, mic: ChannelShape, verdict: bool) -> str:
    """One sentence naming the numbers the answer rests on, for the log."""
    if verdict:
        return (
            f"karsi kanal {far.bursts} kez ayni sesi calmis"
            f" (donem {far.period_s:.1f} sn, en kotu sapma {far.jitter:.2f},"
            f" benzerlik {far.similarity:.2f}); mikrofonda {mic.speech_seconds:.1f} sn konusma"
            " var, yani bu tarafta da kimse konusmamis"
        )

    if not far.rings:
        return (
            f"karsi kanal calma sesine benzemiyor ({far.bursts} parca,"
            f" en kotu sapma {far.jitter if far.jitter is not None else float('nan'):.2f},"
            f" en kotu benzerlik {far.similarity if far.similarity is not None else float('nan'):.2f})"
        )

    return (
        f"karsi kanal caliyor ama mikrofonda {mic.speech_seconds:.1f} sn konusma var:"
        " bu tarafta konusulmus"
    )
