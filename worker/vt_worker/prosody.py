"""
Loudness and pitch as numbers over time — and only as numbers.

Two things can be measured about a voice without a model and without a word of the transcript:
how loud it is and how high it is. Both move over a call, and the movement is what the caller is
after — a level that climbs for four seconds, a pitch that rises a few semitones. What such a
movement *means* is a question this module does not answer, and that is a rule of the product
rather than a limit of the code (docs/PLAN-SOSYALZEKA.md §2, §7): the output is a series of
numbers with timestamps, named for what they are — decibels relative to full scale, hertz — and
nothing here attaches a word to any of them.

Two rules from the plan are built into the shape of the result rather than left to callers:

  **dBFS is never compared across calls.** The microphone's gain belongs to whatever headset was
  plugged in that day, and the far channel arrives through WhatsApp's own gain control; measured
  across the archive, far channels sit near -95 dBFS between words and microphones at -67 to -72
  (chunking.py, SILENT_FLOOR_DBFS). A level means something relative to the same channel of the
  same call, so every level is reported beside that channel's own noise floor and nothing else.

  **F0 across calls only once its stability has been measured** (§6.3). Pitch does not depend on
  the hardware the way level does, which makes it the one figure here that *could* be tracked
  over months — but that is a claim to measure on the archive, not to assume, and until it is
  measured the number is reported per call like everything else.

What is measured, and how:

  The file is cut into 25 ms frames every 10 ms — the framing speaker.py uses — and each frame's
  RMS is expressed in dBFS. Frames above SPEECH_FLOOR_DBFS are speech. That threshold is
  speaker.py's, imported rather than copied: it is the one gate that has been checked against
  this archive, and a second one chosen by eye would be worth less than it.

  Pitch is estimated on the speech frames with YIN (de Cheveigné & Kawahara, 2002): the
  difference between a window and itself shifted by a lag, normalised by its own running mean so
  that a true period reads near zero whatever the level, and the first dip below CMND_THRESHOLD
  inside the search range taken as the period, refined with a parabola through its neighbours.
  A frame with no such dip has no pitch. The difference function is an autocorrelation plus two
  energy terms, so it is computed for thousands of frames at once through the FFT — which is
  what makes twenty minutes of audio a few seconds of work on a processor, with no torch and no
  librosa (worker/requirements.txt says why neither is available).

  The frames are then gathered into half-second bins: the median pitch of the voiced frames, the
  spread of those pitches, the mean level, and the share of frames that were voiced. Half a
  second is short enough to see a phrase rise and long enough that a median over it is not one
  frame's mistake.

Memory is bounded by the batch, not by the file. A twenty-minute channel is 120,000 frames;
windowed for pitch that would be half a gigabyte of floats if it were built at once, so it is
built 4,096 frames at a time over a strided view that copies nothing until it is asked to.

None of the thresholds below has been measured against the archive yet. They are the plan's
(Paket G) and the literature's, and §6.3 is the gate that turns them into measured ones; they
are gathered at the top of this file so that gate has one place to write to.
"""

from __future__ import annotations

import math
import wave
from dataclasses import dataclass

# The same gate and the same reader as the voiceprint. The gate is the one number here that has
# been checked against real calls (speaker.py:72-75), and the reader carries the format contract
# — 16-bit mono, anything else refused by name — that every WAV reaching this worker obeys.
from vt_worker.speaker import SPEECH_FLOOR_DBFS, read_wav

# The framing speaker.py uses (25 ms every 10 ms, its FRAME_LENGTH and FRAME_SHIFT at 16 kHz),
# in milliseconds so a file at another rate is framed the same way in time. A frame this short
# follows syllables, which is the resolution the half-second bins average over.
FRAME_MS = 25
HOP_MS = 10

# The window pitch is estimated over: 1,024 samples at 16 kHz. YIN needs the window to hold its
# integration span plus the longest period searched for; half the window is the span and 60 Hz
# is 267 samples at 16 kHz, so there is room to spare, and at 64 ms the window still sits inside
# one syllable, so a pitch that moves within a word is not averaged flat. In milliseconds so a
# file at another rate keeps the same span in time; the arithmetic that depends on it is checked
# in pitch().
F0_WINDOW_MS = 64

# Where a speaking voice can be, from the plan (Paket G): 60 Hz is below the lowest adult
# phonation heard on a phone line and 400 Hz above ordinary speech. Wider would admit the octave
# errors YIN is known for at both ends; narrower would clip real voices.
F0_MIN_HZ = 60.0
F0_MAX_HZ = 400.0

# How deep a dip in the normalised difference function has to be before it counts as a period.
# The YIN paper's 0.1 is for clean recordings; the plan sets 0.15 for this audio, most of which is
# decoded from a 20-24 kbps Opus archive and is one side of a phone call, where the dips are
# shallower. Chosen, not measured — §6.3 is where it gets measured.
CMND_THRESHOLD = 0.15

# The bin the frames are gathered into: the plan's number. Fifty frames — short enough to show a
# phrase rising, long enough that a median over it is not one frame's mistake.
BIN_SECONDS = 0.5

# Frames handled in one pass. 4,096 windows of 1,024 float32 samples is 16 MB, and the FFT
# products beside it a few times that — a bound that holds whatever the length of the file.
BATCH_FRAMES = 4096

# Below this a level is not a measurement. One least-significant bit of 16-bit audio is -90.3
# dBFS RMS, so nothing between there and digital silence carries information; the clamp keeps a
# silent frame a finite figure rather than the -270 that a bare logarithm of an epsilon gives.
LEVEL_FLOOR_DBFS = -100.0


@dataclass(slots=True)
class Bin:
    """Half a second of one channel, in numbers."""

    start: float          # seconds from the start of the file
    dbfs: float           # mean level of the speech frames; of all frames when none is speech
    f0: float | None      # median pitch of the voiced frames, Hz; None when no frame was voiced
    f0_iqr: float | None  # interquartile range of those pitches, Hz; None likewise
    voiced: float         # share of the bin's frames that carry a pitch, 0..1


@dataclass(slots=True)
class Prosody:
    """One channel of one call, measured."""

    rate: int
    floor_dbfs: float      # median level of the frames below the speech gate; the quietest
                           # frame when there is no such frame
    speech_seconds: float  # frames above the gate, in seconds
    bins: list[Bin]

    def to_json(self) -> dict:
        """The wire shape from the plan: four columns per bin, the pitch null where absent."""
        return {
            "floor_dbfs": round(self.floor_dbfs, 1),
            "speech_seconds": round(self.speech_seconds, 1),
            "bins": [
                [
                    b.start,
                    round(b.dbfs, 1),
                    None if b.f0 is None else round(b.f0, 1),
                    round(b.voiced, 2),
                ]
                for b in self.bins
            ],
        }


def _sample_rate(path: str) -> int:
    with wave.open(path, "rb") as wav:
        return wav.getframerate()


def _windows(samples, length: int, hop: int, count: int):
    """Overlapping windows as a view — the framing in speaker.py:155-159, without the copy."""
    import numpy as np

    return np.lib.stride_tricks.as_strided(
        samples,
        shape=(count, length),
        strides=(samples.strides[0] * hop, samples.strides[0]),
        writeable=False,
    )


def levels(frames):
    """RMS of each frame in dBFS, as speaker.py:190-191 computes it, clamped at LEVEL_FLOOR_DBFS."""
    import numpy as np

    rms = np.sqrt(np.mean(frames * frames, axis=1))
    least = 32768 * 10 ** (LEVEL_FLOOR_DBFS / 20)

    return 20 * np.log10(np.maximum(rms, least) / 32768)


def pitch(frames, rate: int):
    """
    The fundamental frequency of each window in Hz, NaN where the window has no period.

    YIN, over every window at once. The difference function d(τ) = Σ (x[j] - x[j+τ])² for j over
    the first half of the window is an autocorrelation plus two energy terms: the spectrum of the
    whole window times the spectrum of that first half reversed gives the correlation at every
    lag in one inverse transform, and running sums of x² give the energies. Dividing d(τ) by its
    own mean over 1..τ then puts a true period near zero and every shorter lag near one, at any
    level, which is what lets one threshold serve a whisper and a shout.

    The period is the first lag inside [rate / F0_MAX_HZ, rate / F0_MIN_HZ] that is both below
    CMND_THRESHOLD and a local minimum — YIN's "absolute threshold" step, which is what keeps the
    estimate off the octave below — refined by the parabola through its two neighbours, because a
    period of 72.7 samples read as 73 is one hertz of error at 220 Hz and two at 400.
    """
    import numpy as np

    count, length = frames.shape
    span = length // 2
    lag_min = int(rate / F0_MAX_HZ)
    lag_max = int(math.ceil(rate / F0_MIN_HZ))
    lags = lag_max + 2   # 0..lag_max, plus one neighbour for the local-minimum test

    if lag_min < 2 or span + lags > length:
        raise ValueError(
            f"{rate} Hz: {F0_MIN_HZ:.0f}-{F0_MAX_HZ:.0f} Hz aralığı {length} örneklik pencereye sığmıyor")

    if count == 0:
        return np.full(0, np.nan, dtype=np.float32)

    frames = frames.astype(np.float32, copy=False)

    whole = np.fft.rfft(frames, length, axis=1)
    head = np.fft.rfft(frames[:, span:0:-1], length, axis=1)
    correlation = np.fft.irfft(whole * head, length, axis=1)[:, span:span + lags]

    energy = np.cumsum(frames * frames, axis=1)
    energy = energy[:, span:span + lags] - energy[:, :lags]

    difference = energy[:, :1] + energy - 2 * correlation

    # Cumulative mean normalisation, index k standing for lag k + 1. A window of digital silence
    # has a difference of zero at every lag, which is not a period; it reads as one, and one is
    # above any threshold.
    lag = np.arange(1, lags, dtype=np.float32)
    running = np.cumsum(difference[:, 1:], axis=1)
    with np.errstate(divide="ignore", invalid="ignore"):
        normalised = np.where(running > 0, difference[:, 1:] * lag / running, 1.0)

    low, high = lag_min - 1, lag_max
    middle = normalised[:, low:high]
    left = normalised[:, low - 1:high - 1]
    right = normalised[:, low + 1:high + 1]

    trough = (middle < CMND_THRESHOLD) & (middle < left) & (middle <= right)
    found = trough.any(axis=1)
    first = trough.argmax(axis=1)

    rows = np.arange(count)
    m, l, r = middle[rows, first], left[rows, first], right[rows, first]

    curvature = l - 2 * m + r
    with np.errstate(divide="ignore", invalid="ignore"):
        shift = np.where(curvature > 0, (l - r) / (2 * curvature), 0.0)
    shift = np.clip(shift, -1.0, 1.0)

    period = lag_min + first + shift

    return np.where(found, rate / period, np.nan).astype(np.float32)


def _quantile(sorted_rows, counts, q: float):
    """
    One quantile per row of a sorted array whose valid values come first and whose NaNs come
    last — which is where np.sort puts them — with linear interpolation, the same estimate
    np.percentile gives. Rows with no valid value get NaN, without a warning about it.
    """
    import numpy as np

    rows = np.arange(sorted_rows.shape[0])
    position = q * np.maximum(counts - 1, 0)
    below = np.floor(position).astype(int)
    above = np.minimum(below + 1, np.maximum(counts - 1, 0))
    weight = position - below

    return sorted_rows[rows, below] * (1 - weight) + sorted_rows[rows, above] * weight


def analyse_samples(samples, rate: int) -> Prosody:
    """One channel from samples already in memory. See analyse() for the file form."""
    import numpy as np

    frame = rate * FRAME_MS // 1000
    hop = rate * HOP_MS // 1000
    window = rate * F0_WINDOW_MS // 1000
    per_bin = round(BIN_SECONDS * 1000 / HOP_MS)

    total = len(samples)
    count = 1 + (total - frame) // hop if total >= frame else 0

    if count == 0:
        return Prosody(rate=rate, floor_dbfs=LEVEL_FLOOR_DBFS, speech_seconds=0.0, bins=[])

    # Each pitch window is centred on the level frame it belongs to, so the two describe the
    # same moment; zeros at both ends give the first and last frames a full window to sit in.
    lead = (window - frame) // 2
    padded = np.zeros(total + window - frame, dtype=np.int16)
    padded[lead:lead + total] = samples

    level_frames = _windows(padded[lead:], frame, hop, count)
    pitch_frames = _windows(padded, window, hop, count)

    level = np.empty(count, dtype=np.float32)
    f0 = np.full(count, np.nan, dtype=np.float32)

    for start in range(0, count, BATCH_FRAMES):
        stop = min(start + BATCH_FRAMES, count)

        batch = levels(level_frames[start:stop].astype(np.float32))
        level[start:stop] = batch

        # Pitch only where there is speech to have one: one side of a call is silent for most of
        # it, and a period found in room tone would be reported as a voice.
        speech = np.flatnonzero(batch > SPEECH_FLOOR_DBFS)
        if speech.size:
            f0[start + speech] = pitch(pitch_frames[start:stop][speech], rate)

    speaking = level > SPEECH_FLOOR_DBFS
    quiet = level[~speaking]
    floor = float(np.median(quiet)) if quiet.size else float(level.min())
    speech_seconds = float(speaking.sum()) * HOP_MS / 1000

    # Into bins: rows of per_bin frames, the last one padded with NaN so that every row is the
    # same width and the arithmetic below is one pass rather than a loop over the file.
    rows = -(-count // per_bin)
    level_rows = np.full(rows * per_bin, np.nan, dtype=np.float32)
    level_rows[:count] = level
    level_rows = level_rows.reshape(rows, per_bin)
    f0_rows = np.full(rows * per_bin, np.nan, dtype=np.float32)
    f0_rows[:count] = f0
    f0_rows = f0_rows.reshape(rows, per_bin)

    frames_in_row = (~np.isnan(level_rows)).sum(axis=1)
    speech_in_row = level_rows > SPEECH_FLOOR_DBFS   # NaN compares false, as it should here
    speech_count = speech_in_row.sum(axis=1)

    level_of_speech = np.where(speech_in_row, level_rows, 0).sum(axis=1) / np.maximum(speech_count, 1)
    level_of_all = np.nansum(level_rows, axis=1) / frames_in_row
    dbfs = np.where(speech_count > 0, level_of_speech, level_of_all)

    ordered = np.sort(f0_rows, axis=1)
    voiced_count = (~np.isnan(f0_rows)).sum(axis=1)
    median = _quantile(ordered, voiced_count, 0.5)
    spread = _quantile(ordered, voiced_count, 0.75) - _quantile(ordered, voiced_count, 0.25)

    bins = [
        Bin(
            start=index * BIN_SECONDS,
            dbfs=float(dbfs[index]),
            f0=float(median[index]) if voiced_count[index] else None,
            f0_iqr=float(spread[index]) if voiced_count[index] else None,
            voiced=float(voiced_count[index] / frames_in_row[index]),
        )
        for index in range(rows)
    ]

    return Prosody(rate=rate, floor_dbfs=floor, speech_seconds=speech_seconds, bins=bins)


def analyse(path: str) -> Prosody:
    """
    One channel of one call, from a WAV on disk.

    Reads with speaker.read_wav, so a stereo or non-16-bit file is refused by name rather than
    measured as if it were right: two interleaved channels read as one produce a level that
    belongs to nobody and a pitch that is an artefact of the interleaving.
    """
    return analyse_samples(read_wav(path), _sample_rate(path))
