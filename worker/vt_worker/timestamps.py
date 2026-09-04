"""Repair word timestamps that claim a word lasted longer than a word can last.

A hosted engine returned the word "ne" and stamped it 557.34 - 594.75: thirty-seven seconds
for two letters. That single stamp is enough to destroy the conversation view. Turn boundaries
are found from the gaps between words (see :mod:`vt_worker.segmentation`), and a word that
spans thirty-seven seconds leaves no gap inside them — the line cannot be split, so everything
the other person said during those thirty-seven seconds sorts *after* it. The reply appears
below the sentence that provoked it and the conversation reads backwards.

Which end of such a word is real is not a matter of taste; it was measured. Across six calls,
the thirty-eight over-long words that both engines transcribed identically were compared against
the local engine's timing of the same word:

    the START agreed to within a median of 1.86 s
    the END   agreed to within a median of 0.05 s

So the engine is stretching the word *backwards*: the end marks where the word was actually
spoken, and the start has been dragged back to wherever the previous word finished, swallowing
the silence in between. The repair follows from that. Keep the end, which is right; move the
start forward to where a word of that length would have begun. The silence then reappears in
front of the word — exactly where it was — and the line splits there.

Measured on the same six calls, against the local engine as reference: turn-order agreement
rose from 85% to 91%, and the number of lines swallowing one of the other party's turns fell
from 44 to 26. Two calls that were already correct stayed correct.

This is deliberately not a general "smooth the timestamps" pass. Only impossible durations are
touched, and only in one direction. Everything else is left exactly as the engine reported it,
because a timestamp we invented is worse than one we merely distrust.
"""

from __future__ import annotations

from vt_worker.merge import Segment

# Longer than this and the duration is not a duration, it is a swallowed silence.
#
# Deliberately the same figure as ``segmentation.DEFAULT_MAX_GAP``, and for the same reason: a
# word that outlasts the gap which would have separated two turns is precisely a word able to
# hide a turn boundary inside itself. Measured at 1.2, 1.5 and 2.0 — 1.5 was best; 1.2 gained
# nothing further and started splitting lines that were already right.
MAX_WORD_SECONDS = 1.5

# How long a character of speech takes. The median over every local word in the reference
# archive, so it is this speaker in this language, not a figure from a paper.
SECONDS_PER_CHARACTER = 0.06

# No word is shorter than this, however few letters it has.
MINIMUM_WORD_SECONDS = 0.15


def repair_stretched_words(segments: list[Segment]) -> list[Segment]:
    """Pull the start of every impossibly long word forward to meet its end.

    Runs before re-segmentation, so the gaps it uncovers are the gaps that decide where the
    lines break. Segments are modified in place and returned for convenience; a segment whose
    words are untouched is returned unchanged.
    """
    for segment in segments:
        if not segment.words:
            continue

        touched = False

        for word in segment.words:
            if word.end - word.start <= MAX_WORD_SECONDS:
                continue

            word.start = max(0.0, word.end - _plausible_length(word.text))
            touched = True

        if not touched:
            continue

        # The segment's own span has to follow its words, or the line would still be reported as
        # covering the silence its first word no longer covers.
        segment.start = segment.words[0].start
        segment.end = max(segment.end, segment.words[-1].end)

    return segments


def _plausible_length(text: str) -> float:
    """How long the word would have taken to say."""
    return max(MINIMUM_WORD_SECONDS, len(text.strip()) * SECONDS_PER_CHARACTER)
