"""
Sending a hosted model only the parts where somebody is speaking.

The local engine runs with ``vad_filter=True``, which has faster-whisper drop the non-speech before
the model ever decodes it. No hosted API does the equivalent: OpenAI's has no such parameter at
all, and our own server ships with it off. That one difference is enough, because of what this
application records — the *whole call*, on two separate channels, so while one person is talking the
other channel is minutes of nothing.

Whisper does not return nothing for silence. It was trained on thirty-second windows that always
contained speech, so a window with none produces whatever the training data has most of: in Turkish
that is "abone ol", over and over. Our own server's hallucination filter exists to catch exactly
those loops, which is a measurement of how often they happen.

So the silence is removed here, before the upload, where it works for every provider rather than
only the ones that will take a flag. The upload carries only the spans where somebody is speaking,
and every time the model reports is mapped back onto the real recording afterwards.

(An earlier version of this file blamed ``condition_on_previous_text``, on the grounds that the
library defaults it to on. It does — but the server's operator captured the live call and showed
they pass ``False``, the same as we do locally. Reading a library's default is not reading what the
caller passes, and the two were confused here. The remaining difference is VAD, and the vocabulary:
locally 209 terms bias every decoding window as hotwords, while a hosted API takes only an initial
prompt that Whisper reads once. That second gap cannot be closed — mlx-whisper has no hotwords —
which is one more reason to close the first one properly.)

**The mapping is the part that has to be exact.** Every line in this product carries a timestamp you
can click to hear the moment it was said; an offset that is wrong by a second turns every quote into
a claim about audio that does not contain it, which is worse than having no timestamps at all. So
the spans are kept as an explicit list, times are mapped through it rather than by arithmetic on a
running total, and anything the model reports past the end of the last span is clamped rather than
extrapolated.

Two safeguards on top:

  **Generous padding, and merging.** A span cut tight against speech clips the first consonant, and
  the model completes the fragment into a word nobody said. Each span is widened, and spans close
  enough together are merged rather than butted against each other.

  **Refuses to be clever.** If the recording is mostly speech there is nothing to gain and the file
  goes up untouched; if no speech is found at all the file goes up untouched too, because a silent
  channel is a fact the transcript should show rather than an empty upload.
"""

from __future__ import annotations

import wave
from contextlib import closing
from dataclasses import dataclass

from vt_worker.chunking import FRAME_MS, _frame_levels

# How loud a frame has to be, against the recording's own speech level, to count as speech.
#
# Measured against the median of the frames that are already above the mean, which is a decent
# stand-in for "how loud this person is" without assuming a recording level. A tenth of that is
# well below a quiet word and well above line noise.
SPEECH_RATIO = 0.10

# Padding either side of a detected span, in seconds.
#
# A span cut tight against speech loses the attack of the first consonant, and Whisper does not
# report a clipped word as missing — it completes the fragment into a plausible whole one. Quarter
# of a second is longer than any consonant and short enough that it does not put the silence back.
PAD_SECONDS = 0.25

# Spans closer together than this are merged.
#
# Ordinary speech is full of gaps this size — between words, between clauses — and cutting them out
# would splice syllables together into something the model reads as one mangled word. Only the
# silences long enough to be a *pause* are worth removing.
MERGE_GAP_SECONDS = 0.60

# Below this, a span is not speech. A single loud frame is a door, a keyboard, a breath.
MIN_SPAN_SECONDS = 0.30

# How much has to be saved before the audio is rewritten at all.
#
# Rewriting is not free and the mapping is one more thing that can be wrong, so it has to buy
# something. A recording that is a fifth silence is not the case this exists for; one that is half
# silence is exactly it.
WORTH_IT_RATIO = 0.25


@dataclass(frozen=True)
class SpeechSpan:
    """One stretch of speech: where it is in the recording, and where it lands in the upload."""

    start: float
    end: float
    offset: float

    @property
    def length(self) -> float:
        return max(0.0, self.end - self.start)


def find_speech(path: str) -> list[SpeechSpan]:
    """
    The spans of a recording that carry speech, in order, with their place in the upload.

    Anything that cannot be read as a WAV yields nothing, which sends the file up untouched. This
    is a quality improvement, not a requirement: refusing to upload a recording because its header
    could not be scanned would trade a better transcript for no transcript.
    """
    try:
        levels, _rate, duration = _frame_levels(path)
    except (OSError, wave.Error):
        return []

    if not levels or duration <= 0:
        return []

    frame = FRAME_MS / 1000.0
    threshold = _speech_threshold(levels)

    if threshold <= 0:
        return []

    # Raw runs of loud frames.
    runs: list[tuple[float, float]] = []
    start: float | None = None

    for index, level in enumerate(levels):
        if level >= threshold:
            if start is None:
                start = index * frame
        elif start is not None:
            runs.append((start, index * frame))
            start = None

    if start is not None:
        runs.append((start, len(levels) * frame))

    return _lay_out(_merge(_pad(runs, duration)), duration)


def write_speech_only(source: str, target: str) -> list[SpeechSpan]:
    """
    Writes the speech of one recording into a new WAV, and returns where each piece came from.

    An empty list means the file was left alone — either there is nothing to save or there is no
    speech to keep — and the caller should upload the original.
    """
    spans = find_speech(source)

    if not spans:
        return []

    try:
        _levels, _rate, duration = _frame_levels(source)
    except (OSError, wave.Error):
        return []

    kept = sum(span.length for span in spans)

    if duration <= 0 or kept >= duration * (1 - WORTH_IT_RATIO):
        return []

    try:
        _copy_spans(source, target, spans)
    except (OSError, wave.Error):
        return []

    return spans


def _copy_spans(source: str, target: str, spans: list[SpeechSpan]) -> None:
    with closing(wave.open(source, "rb")) as reader:
        rate = reader.getframerate()
        total = reader.getnframes()

        with closing(wave.open(target, "wb")) as writer:
            writer.setnchannels(reader.getnchannels())
            writer.setsampwidth(reader.getsampwidth())
            writer.setframerate(rate)

            for span in spans:
                first = min(max(0, int(span.start * rate)), total)
                last = min(max(first, int(span.end * rate)), total)

                reader.setpos(first)

                remaining = last - first
                block = rate * 30

                while remaining > 0:
                    take = min(block, remaining)
                    data = reader.readframes(take)
                    if not data:
                        break

                    writer.writeframes(data)
                    remaining -= take


def to_original(seconds: float, spans: list[SpeechSpan]) -> float:
    """
    A moment in the upload, put back where it belongs in the recording.

    Mapped through the spans rather than by arithmetic on a running total, because the two drift
    apart the moment a span is clipped by the end of the file — and a quote a second out of place
    points at audio that does not contain it, which is worse than no timestamp at all.

    A time past the end of the last span is clamped to it. The model does sometimes report an end
    slightly beyond what it was given; extrapolating that into the silence we removed would put a
    word somewhere it was provably not said.
    """
    if not spans:
        return seconds

    if seconds <= spans[0].offset:
        return spans[0].start

    for span in spans:
        if seconds <= span.offset + span.length:
            return span.start + (seconds - span.offset)

    last = spans[-1]
    return last.end


# ---- putting the spans together ---------------------------------------------


def _speech_threshold(levels: list[float]) -> float:
    """
    How loud counts as speech, taken from the recording rather than assumed.

    The median of the frames that are already above the mean stands in for "how loud this person
    is": it ignores the silence, which is most of the file and would drag any plain average down,
    and it ignores the single loudest moments, which are a door or a cough.
    """
    mean = sum(levels) / len(levels)
    loud = sorted(level for level in levels if level > mean)

    if not loud:
        return 0.0

    return loud[len(loud) // 2] * SPEECH_RATIO


def _pad(runs: list[tuple[float, float]], duration: float) -> list[tuple[float, float]]:
    return [
        (max(0.0, start - PAD_SECONDS), min(duration, end + PAD_SECONDS))
        for start, end in runs
    ]


def _merge(runs: list[tuple[float, float]]) -> list[tuple[float, float]]:
    merged: list[tuple[float, float]] = []

    for start, end in runs:
        if merged and start - merged[-1][1] <= MERGE_GAP_SECONDS:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))

    return merged


def _lay_out(runs: list[tuple[float, float]], duration: float) -> list[SpeechSpan]:
    """Drops what is too short to be speech and records where each survivor lands in the upload."""
    spans: list[SpeechSpan] = []
    offset = 0.0

    for start, end in runs:
        start, end = max(0.0, start), min(duration, end)

        if end - start < MIN_SPAN_SECONDS:
            continue

        spans.append(SpeechSpan(start=start, end=end, offset=offset))
        offset += end - start

    return spans
