"""Splitting a long recording into pieces a hosted API will accept.

Every hosted transcription service puts a ceiling on one request — 25 MB on OpenAI, and several
of them cap duration as well. A one-hour conversation is not an edge case for this application,
it is the ordinary case, so splitting has to be something the system does well rather than
something it falls back to.

The rule that matters: **never cut in the middle of a word.** A boundary through a word does not
merely lose that word, it produces two plausible half-words that the model will confidently
complete into something nobody said. In a transcript that gets quoted back as evidence about a
real person, an invented word is worse than a missing one.

So boundaries are placed at the quietest point in a window around each target, rather than at an
arithmetic offset. Conversations pause constantly; a window of half a minute practically always
contains a real silence.
"""

from __future__ import annotations

import array
import math
import wave
from contextlib import closing
from dataclasses import dataclass

# How far either side of a target boundary to look for a quiet moment. Wide enough to find a
# natural pause in ordinary conversation, narrow enough not to unbalance the chunks.
SEARCH_WINDOW_SECONDS = 20.0

# Resolution of the loudness scan. 50 ms is well under the length of a syllable, so a gap
# between words is still visible, and it keeps the scan cheap on an hour of audio.
FRAME_MS = 50

# How much quieter a moment has to be before the boundary is moved to it. A real pause between
# sentences is far below a third of speech level; anything closer than this is just the ordinary
# rise and fall of a voice, and chasing it only unbalances the chunks.
QUIET_ENOUGH_RATIO = 0.35


@dataclass(frozen=True)
class Chunk:
    """One piece of the recording, with the offset that puts it back on the call timeline."""

    index: int
    start_seconds: float
    end_seconds: float

    @property
    def length_seconds(self) -> float:
        return self.end_seconds - self.start_seconds


def _frame_levels(path: str) -> tuple[list[float], int, float]:
    """Mean absolute amplitude per frame, the frame rate, and the total duration."""
    with closing(wave.open(path, "rb")) as wav:
        rate = wav.getframerate()
        channels = wav.getnchannels()
        width = wav.getsampwidth()
        total_frames = wav.getnframes()

        if rate <= 0 or total_frames <= 0:
            return [], rate or 16_000, 0.0

        # 16-bit is what this application records and what the workers emit. Anything else is
        # not scanned rather than mis-scanned: a wrong reading would place boundaries mid-word,
        # which is precisely the failure this module exists to prevent.
        if width != 2:
            return [], rate, total_frames / rate

        block = max(1, int(rate * FRAME_MS / 1000))
        levels: list[float] = []

        while True:
            raw = wav.readframes(block)
            if not raw:
                break

            samples = array.array("h")
            samples.frombytes(raw[: len(raw) - (len(raw) % 2)])

            if not samples:
                continue

            if channels > 1:
                samples = samples[::channels]

            levels.append(sum(abs(s) for s in samples) / len(samples))

        return levels, rate, total_frames / rate


def _quietest_near(levels: list[float], target_frame: int, window_frames: int) -> int:
    """
    A good place to cut near a target, or the target itself when there is no good place.

    Two rules, and the second one is easy to miss until the chunks come out lopsided.

    Move the boundary only when the quietest moment nearby is *genuinely* quieter than the
    target. Taking the minimum unconditionally sounds right but is not: over continuous speech
    every frame is roughly as loud as every other, the minimum is then decided by noise, and it
    lands wherever the search happened to start — dragging every boundary to the same edge of its
    window and producing chunks of twenty, forty and sixty seconds where three of forty were
    intended.

    Among moments that are equally quiet, take the one nearest the target. A pause can be several
    seconds long; anywhere inside it is a safe cut, so the tie is broken on balance instead.
    """
    low = max(0, target_frame - window_frames)
    high = min(len(levels), target_frame + window_frames)

    if low >= high:
        return target_frame

    anchor = min(max(target_frame, low), high - 1)
    at_target = levels[anchor]

    window = levels[low:high]
    quietest = min(window)

    if at_target > 0 and quietest > QUIET_ENOUGH_RATIO * at_target:
        return anchor

    tolerance = quietest + max(1.0, quietest * 0.25)
    candidates = [i for i in range(low, high) if levels[i] <= tolerance]

    return min(candidates, key=lambda i: abs(i - anchor)) if candidates else anchor


def speech_coverage(path: str, segments) -> float | None:
    """
    How much of the audible speech in a recording came back with words on it, 0 to 1.

    A transcript that invents is obvious; a transcript that goes quiet is not, and it is the worse
    of the two in a record of what somebody said. Nothing anywhere was measuring it, so a service
    that returned two thirds of a conversation looked exactly like a conversation with pauses in
    it.

    Measured, on one 180-second stretch of a real call carrying 157 seconds of speech: the local
    engine covered 150 of them, and the hosted one 108 — and the missing 42 seconds were not
    quiet, they ran at the same -20 dBFS as the rest. There is no way to tell that from the
    transcript alone, which is why it is a number now.

    Returns None when the question does not apply: a file that cannot be scanned, or one with no
    speech in it at all — a channel that stays silent through a call is a fact about the call,
    not a failure to transcribe it.
    """
    try:
        levels, _rate, duration = _frame_levels(path)
    except (OSError, wave.Error):
        # A diagnostic may not be the thing that fails a job. Not knowing the coverage is a
        # missing number; raising here would throw away a transcript that is already in hand.
        return None

    if not levels or duration <= 0:
        return None

    # What counts as speech, without a fixed number that a different recording gain would break.
    #
    # The loud end of the file sets the scale: a tenth of the 90th percentile is comfortably below
    # speech and comfortably above room tone, on a recording whose peaks are speech. The absolute
    # floor stops a silent channel from finding "speech" in its own noise, since a tenth of
    # nothing is still nothing.
    ordered = sorted(levels)
    loud = ordered[int(len(ordered) * 0.9)]
    threshold = max(120.0, loud * 0.1)

    speech = {i for i, level in enumerate(levels) if level > threshold}
    if not speech:
        return None

    frames_per_second = 1000 / FRAME_MS
    covered = set()

    for segment in segments:
        first = int(segment.start * frames_per_second)
        last = int(segment.end * frames_per_second)
        covered.update(range(max(0, first), min(len(levels), last + 1)))

    return len(speech & covered) / len(speech)


#: Below this the quiet parts of a channel are digital silence rather than a room.
#:
#: The loopback stream is written by the audio stack and reads about -95 dBFS between words; a
#: live microphone never does, because a microphone is always hearing something. Measured across
#: this archive the two populations do not overlap or come close: far channels sat at -94 to -95,
#: microphone channels at -67 to -72. The line is drawn in the empty middle.
SILENT_FLOOR_DBFS = -85.0

#: Above this share of speech there is no window without a voice in it, and gain is safe.
#:
#: See :func:`prefers_gain` for what the number decides and what it was measured against.
DENSE_SPEECH_RATIO = 0.5


def noise_profile(path: str) -> tuple[float, float] | None:
    """
    A channel's noise floor in dBFS and the share of it that carries speech.

    Two numbers rather than one average, because the average of a sparse channel is its silence
    and the average of a busy one is its voice — the same figure meaning opposite things. Returns
    None when the file cannot be scanned or holds no speech at all.
    """
    try:
        levels, _rate, duration = _frame_levels(path)
    except (OSError, wave.Error):
        return None

    if not levels or duration <= 0:
        return None

    ordered = sorted(levels)
    loud = ordered[int(len(ordered) * 0.9)]
    threshold = max(120.0, loud * 0.1)

    quiet = [level for level in levels if level <= threshold]
    speech = [level for level in levels if level > threshold]

    if not speech:
        return None

    quiet.sort()
    floor = quiet[len(quiet) // 2] if quiet else 0.0

    return 20 * math.log10(max(floor, 1e-6) / 32768.0), len(speech) / len(levels)


def prefers_gain(path: str) -> bool:
    """
    Whether this channel should be handed to the service with its normalisation on.

    The service applies gain by default and it is not a neutral choice. On a channel with a real
    noise floor and long gaps, a window holding only room tone is lifted to where a decoder will
    write words into it, and the words are invented. On a channel that is mostly speech there is
    no such window, and the same gain makes quiet talking audible.

    Both halves are measured, on this archive, against the service:

        channel        floor   speech   normalize=on   normalize=off
        call-58-mic   -67 dB     21%       4 words        62 words
        call-57-mic   -72 dB     12%       0 words         9 words
        call-56-mic   -72 dB     29%      15 words        15 words
        call-58-far   -95 dB     22%      31 words        32 words
        a busy call        -      87%     151 sec         128 sec

    That last row is why this is a question and not a constant. Turning normalisation off outright
    was tried first, on the strength of a room-tone clip, and it cost 23 seconds of a dense call.
    Both results are real; they are about different recordings, and this function is the
    difference between them.
    """
    profile = noise_profile(path)
    if profile is None:
        return True  # nothing to go on, and the service's own default is gain on

    floor_dbfs, speech_ratio = profile

    # Digital silence takes no harm from gain: multiplying nothing leaves nothing, and the far
    # channel measured neutral on every file tried.
    if floor_dbfs <= SILENT_FLOOR_DBFS:
        return True

    return speech_ratio >= DENSE_SPEECH_RATIO


def plan_chunks(path: str, max_seconds: float) -> list[Chunk]:
    """
    Divides a recording into chunks no longer than max_seconds, cutting at quiet moments.

    Returns a single chunk covering the whole file when it already fits, so the common case
    costs one scan and nothing else.
    """
    levels, _rate, duration = _frame_levels(path)

    if duration <= 0:
        return [Chunk(0, 0.0, 0.0)]

    if duration <= max_seconds:
        return [Chunk(0, 0.0, duration)]

    # Spread the cuts evenly rather than filling each chunk to the brim: a final piece of eight
    # seconds transcribes poorly, because the model has almost no context to work with.
    count = int(duration // max_seconds) + 1
    nominal = duration / count

    frames_per_second = 1000 / FRAME_MS
    window_frames = int(SEARCH_WINDOW_SECONDS * frames_per_second)

    boundaries = [0.0]

    for i in range(1, count):
        target = nominal * i

        if levels:
            frame = _quietest_near(levels, int(target * frames_per_second), window_frames)
            candidate = frame / frames_per_second
        else:
            candidate = target

        # Never go backwards, and never produce an empty chunk, whatever the scan says.
        boundaries.append(max(candidate, boundaries[-1] + 1.0))

    boundaries.append(duration)

    return [
        Chunk(i, boundaries[i], boundaries[i + 1])
        for i in range(len(boundaries) - 1)
        if boundaries[i + 1] > boundaries[i]
    ]


def slice_wav(source: str, target: str, start_seconds: float, end_seconds: float) -> None:
    """
    Copies a time range into a new WAV, streaming rather than loading it all.

    Twenty-five minutes of 16 kHz mono is about 48 MB; reading that into memory to write it
    straight back out is avoidable, and this runs on a laptop that is also holding a model.
    """
    with closing(wave.open(source, "rb")) as reader:
        rate = reader.getframerate()
        total = reader.getnframes()

        start = min(max(0, int(start_seconds * rate)), total)
        end = min(max(start, int(end_seconds * rate)), total)

        reader.setpos(start)

        with closing(wave.open(target, "wb")) as writer:
            writer.setnchannels(reader.getnchannels())
            writer.setsampwidth(reader.getsampwidth())
            writer.setframerate(rate)

            remaining = end - start
            block = rate * 30  # thirty seconds at a time

            while remaining > 0:
                take = min(block, remaining)
                data = reader.readframes(take)
                if not data:
                    break

                writer.writeframes(data)
                remaining -= take
