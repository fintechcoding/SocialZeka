"""Tests for repairing words that claim an impossible duration.

The numbers in test_the_word_that_swallowed_a_turn are from a real cloud run on call #38:
the word "Evet" was stamped 3.35 - 11.39, and that one stamp put the other party's reply
above the sentence it answered.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vt_worker.merge import Segment, Speaker, Word  # noqa: E402
from vt_worker.segmentation import resegment_on_gaps  # noqa: E402
from vt_worker.timestamps import repair_stretched_words  # noqa: E402


def words(*spec: tuple[float, float, str]) -> list[Word]:
    return [Word(start=s, end=e, text=t) for s, e, t in spec]


def seg(start: float, end: float, text: str, w: list[Word] | None = None) -> Segment:
    return Segment(speaker=Speaker.ME, start=start, end=end, text=text, words=w or [])


def test_ordinary_words_are_left_exactly_as_the_engine_reported_them():
    """Nothing is smoothed. A stamp we did not have to touch is a stamp we do not touch."""
    original = words((0.0, 0.4, "İyi"), (0.4, 0.9, "abi"), (1.1, 1.6, "ne"))
    before = [(w.start, w.end) for w in original]

    repair_stretched_words([seg(0.0, 1.6, "İyi abi ne", original)])

    assert [(w.start, w.end) for w in original] == before


def test_an_impossible_word_keeps_its_end_and_gives_up_its_start():
    """The end is where the word was said; the start is where the silence began."""
    stretched = words((3.35, 11.39, "Evet"))

    repair_stretched_words([seg(3.35, 11.39, "Evet", stretched)])

    assert stretched[0].end == 11.39
    assert stretched[0].start > 11.0


def test_a_very_short_word_still_gets_a_real_duration():
    """Length comes from the letters, but never rounds down to nothing."""
    stretched = words((100.0, 130.0, "o"))

    repair_stretched_words([seg(100.0, 130.0, "o", stretched)])

    assert stretched[0].end - stretched[0].start >= 0.15


def test_the_segment_span_follows_its_repaired_words():
    """A line still claiming the old start would keep swallowing the silence."""
    segment = seg(3.35, 12.0, "Evet abi", words((3.35, 11.39, "Evet"), (11.5, 12.0, "abi")))

    repair_stretched_words([segment])

    assert segment.start == segment.words[0].start
    assert segment.start > 11.0


def test_the_word_that_swallowed_a_turn():
    """End to end: after the repair the line splits where the speaker actually stopped.

    Before, the whole thing is one line running 0.0 - 11.39, and the other party's reply at
    second 6 sorts underneath it. The gap the split needs is inside the word "Evet", which is
    exactly where no gap can be seen.
    """
    spoken = words(
        (0.0, 0.5, "İyi"), (0.5, 1.0, "abi"), (1.0, 1.43, "ne"), (1.43, 2.1, "yapayım"),
        (3.35, 11.39, "Evet"), (11.39, 11.8, "abi"))

    before = resegment_on_gaps([seg(0.0, 11.8, "İyi abi ne yapayım Evet abi", spoken)])
    assert len(before) == 1

    after = resegment_on_gaps(
        repair_stretched_words([seg(0.0, 11.8, "İyi abi ne yapayım Evet abi", spoken)]))

    assert len(after) == 2
    assert after[0].end < 3.0
    assert after[1].start > 10.0
