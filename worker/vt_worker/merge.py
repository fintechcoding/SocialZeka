"""Merge the two independently transcribed streams into one conversation.

The microphone stream is the user, the loopback stream is the other party. Because they were
captured separately, speaker attribution is a fact rather than a prediction, and no diarization
model is needed. All this module has to do is interleave them correctly and notice the two
situations where the separation is not as clean as it looks.
"""

from __future__ import annotations

import difflib
import re
import unicodedata
import zlib
from dataclasses import dataclass, field
from enum import Enum

# Above this, a segment's text is repeating itself rather than saying something.
#
# Whisper's own number, from the reference implementation, where exceeding it makes the decoder
# re-run the window at a higher temperature. We cannot re-run anything — the audio is on somebody
# else's machine by then — but the same measure still tells us the line is not evidence.
COMPRESSION_RATIO_LIMIT = 2.4

# Below this many characters the ratio measures zlib's header rather than the text, and ordinary
# short answers ("Tamam.", "Evet evet.") would score as loops.
_COMPRESSION_MIN_CHARS = 60


class Speaker(str, Enum):
    ME = "me"
    THEM = "them"


@dataclass(slots=True)
class Word:
    """One word and where it sits on the call timeline.

    ``probability`` is how sure the engine was of this word, on one scale for every engine:
    0..1, or None when the engine said nothing. faster-whisper and Deepgram already speak
    that scale; ElevenLabs reports a log-probability (0 or below) and is converted with exp on
    the way in; the OpenAI shape and ex5 carry no per-word figure and give None; whisper.cpp
    gives no words at all. None stays None — a made-up 1.0 would read as certainty, and the
    doubtful words are the ones the coaching layer needs to find. The scales agree on their
    ends, not on where "doubtful" begins, so a threshold on this is still measured per engine.
    """

    start: float
    end: float
    text: str
    probability: float | None = None


@dataclass(slots=True)
class Segment:
    speaker: Speaker
    start: float  # seconds from the start of the call
    end: float
    text: str
    avg_logprob: float | None = None
    no_speech_prob: float | None = None
    words: list[Word] = field(default_factory=list)

    # Set during merging.
    overlaps_other_speaker: bool = False
    suspected_echo: bool = False

    @property
    def duration(self) -> float:
        return max(0.0, self.end - self.start)

    @property
    def is_low_confidence(self) -> bool:
        """Whether numbers and dates in this segment should be kept out of automatic
        contradiction detection.

        A misheard "on sekiz bin" -> "on sekiz yuz" turns into a fabricated price conflict
        attributed to a real person, so uncertain audio must not feed the deterministic checks.

        Two of the three tests below need the decoder to have told us how sure it was, and a
        hosted one does not have to. Counted over this archive: 1321 of 2241 segments carry
        neither ``avg_logprob`` nor ``no_speech_prob``, and the split is exactly by engine — every
        segment from the local engine has both, every segment from the service has neither. Since
        both tests are None-guarded, a missing score read as *confident*, so the segments least
        worth trusting were the ones entering the ledger unmarked.

        The third test needs nothing from anybody. Whisper's own measure of an output that has
        come off the rails is how well its text compresses, and it is computed from the text.
        """
        if self.no_speech_prob is not None and self.no_speech_prob > 0.6:
            return True
        if self.avg_logprob is not None and self.avg_logprob < -1.0:
            return True
        return self.compression_ratio > COMPRESSION_RATIO_LIMIT

    @property
    def compression_ratio(self) -> float:
        """How much this segment's text repeats itself, by how far it compresses.

        Whisper's own fallback uses exactly this, and the threshold below is the one from its
        reference implementation. Text that says something new compresses two-ish times; text
        that has fallen into a loop compresses far harder, because the whole segment is one
        string repeated.

        Recomputing it here rather than reading it off the response is the point: the responses
        that need this test hardest are the ones that carry no scores at all. On this archive it
        is what "Mack Kiiiiiiii..." over 28 seconds and "Grafolfolfolfol..." have in common, and
        neither of them was marked as anything.
        """
        text = self.text.strip()
        if len(text) < _COMPRESSION_MIN_CHARS:
            # Short text compresses badly whatever it says — the header outweighs the content —
            # so the ratio is meaningless and a threshold on it would flag ordinary answers.
            return 0.0

        raw = text.encode("utf-8")
        return len(raw) / max(1, len(zlib.compress(raw, 6)))


@dataclass(slots=True)
class MergeStats:
    mic_segments: int = 0
    far_segments: int = 0
    overlap_segments: int = 0
    suspected_echo_segments: int = 0
    low_confidence_segments: int = 0

    @property
    def echo_ratio(self) -> float:
        total = self.mic_segments + self.far_segments
        return self.suspected_echo_segments / total if total else 0.0

    @property
    def likely_no_headphones(self) -> bool:
        """Loudspeaker use makes the far end bleed into the microphone.

        Windows does not echo-cancel a second, independent capture client, so both streams end
        up containing the same voice and attribution silently degrades. A high ratio of near
        duplicate text across the streams is the cheapest reliable signal of that.
        """
        return self.suspected_echo_segments >= 3 and self.echo_ratio > 0.15


@dataclass(slots=True)
class MergedTranscript:
    segments: list[Segment]
    stats: MergeStats

    @property
    def duration(self) -> float:
        return max((s.end for s in self.segments), default=0.0)

    def text(self) -> str:
        """Plain readable transcript, one labelled line per segment."""
        label = {Speaker.ME: "BEN", Speaker.THEM: "KARSI"}
        return "\n".join(
            f"[{_mmss(s.start)}] {label[s.speaker]}: {s.text.strip()}"
            for s in self.segments
            if s.text.strip()
        )


def _mmss(seconds: float) -> str:
    total = int(seconds)
    return f"{total // 60:02d}:{total % 60:02d}"


_PUNCT = re.compile(r"[^\w\s]", flags=re.UNICODE)


def _normalise(text: str) -> str:
    """Fold text for similarity comparison only.

    Turkish dotted and dotless i are collapsed together with the other diacritics, so two
    transcriptions of the same audio compare equal even when Whisper spells them differently.
    """
    folded = unicodedata.normalize("NFKC", text).casefold()
    table = str.maketrans("ıİğĞşŞçÇöÖüÜâÂîÎûÛ", "iiggssccoouuaaiiuu")
    folded = folded.translate(table)
    folded = _PUNCT.sub(" ", folded)
    return " ".join(folded.split())


def _similarity(a: str, b: str) -> float:
    na, nb = _normalise(a), _normalise(b)
    if not na or not nb:
        return 0.0
    return difflib.SequenceMatcher(None, na, nb).ratio()


def _overlaps(a: Segment, b: Segment, tolerance: float = 0.0) -> bool:
    return a.start < b.end - tolerance and b.start < a.end - tolerance


def merge_streams(
    mic_segments: list[Segment],
    far_segments: list[Segment],
    *,
    echo_similarity_threshold: float = 0.8,
) -> MergedTranscript:
    """Interleave both streams chronologically and annotate overlap and echo.

    Segments are ordered by start time. Where two segments genuinely overlap, both are kept:
    people talk over each other, and discarding either side would lose real content. This is
    exactly the case a diarization model handles worst, and separate capture handles for free.
    """
    for s in mic_segments:
        s.speaker = Speaker.ME
    for s in far_segments:
        s.speaker = Speaker.THEM

    stats = MergeStats(mic_segments=len(mic_segments), far_segments=len(far_segments))

    for mic in mic_segments:
        for far in far_segments:
            if far.start > mic.end:
                break  # far_segments is sorted, nothing later can overlap
            if not _overlaps(mic, far):
                continue

            mic.overlaps_other_speaker = True
            far.overlaps_other_speaker = True

            # The same words appearing on both streams at the same moment is not two people
            # agreeing verbatim; it is one voice reaching the microphone through the speakers.
            if _similarity(mic.text, far.text) >= echo_similarity_threshold:
                mic.suspected_echo = True
                far.suspected_echo = True

    merged = sorted([*mic_segments, *far_segments], key=lambda s: (s.start, s.speaker.value))

    stats.overlap_segments = sum(1 for s in merged if s.overlaps_other_speaker)
    stats.suspected_echo_segments = sum(1 for s in merged if s.suspected_echo)
    stats.low_confidence_segments = sum(1 for s in merged if s.is_low_confidence)

    return MergedTranscript(segments=merged, stats=stats)
