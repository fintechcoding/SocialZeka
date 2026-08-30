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
