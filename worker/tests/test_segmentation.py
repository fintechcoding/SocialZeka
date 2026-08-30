"""Tests for pause-based re-segmentation.

The scenario in test_real_whisper_output_is_split_back_into_turns is taken verbatim from an
actual worker run, which is where the problem was found.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vt_worker.merge import Segment, Speaker, Word  # noqa: E402
from vt_worker.segmentation import resegment_on_gaps  # noqa: E402


def words(*spec: tuple[float, float, str]) -> list[Word]:
    return [Word(start=s, end=e, text=t) for s, e, t in spec]


def seg(start: float, end: float, text: str, w: list[Word] | None = None) -> Segment:
    return Segment(speaker=Speaker.ME, start=start, end=end, text=text, words=w or [])


def test_real_whisper_output_is_split_back_into_turns():
    """Real output: two utterances 6.4 s apart merged into one 13 s segment."""
    original = seg(
        0.0,
        13.04,
        " Hello, good morning. I am calling about the order we discussed last week. We agreed on 12,000",
        words(
            (0.00, 0.38, " Hello,"),
            (0.80, 1.08, " good"),
            (1.08, 1.42, " morning."),
            (2.26, 2.54, " I"),
            (2.54, 2.66, " am"),
            (2.66, 2.98, " calling"),
            (2.98, 3.30, " about"),
            (3.30, 3.50, " the"),
            (3.50, 3.72, " order"),
            (3.72, 3.94, " we"),
            (3.94, 4.26, " discussed"),
            (4.26, 5.02, " last week."),
            (11.44, 11.74, " We"),
            (11.74, 12.04, " agreed"),
            (12.04, 12.24, " on"),
            (12.24, 13.04, " 12,000"),
        ),
    )

    result = resegment_on_gaps([original])

    assert len(result) == 2

    first, second = result
    assert first.start == 0.0
    assert first.end == 5.02
    assert first.text.startswith("Hello, good morning.")

    # The price must now be anchored where it was actually said, not eleven seconds earlier.
    assert second.start == 11.44
    assert second.end == 13.04
    assert second.text == "We agreed on 12,000"


def test_pauses_inside_a_sentence_do_not_split_it():
    """The reference recording pauses 0.84 s between "morning." and "I"."""
    original = seg(
        0.0,
        3.0,
        " Hello, good morning. I am calling.",
        words(
            (0.00, 0.38, " Hello,"),
            (0.80, 1.42, " good morning."),
            (2.26, 2.54, " I"),
            (2.54, 3.00, " am calling."),
        ),
    )

    assert len(resegment_on_gaps(original and [original])) == 1


def test_split_threshold_is_configurable():
    original = seg(
        0.0,
        3.0,
        " bir iki",
        words((0.0, 0.5, " bir"), (2.5, 3.0, " iki")),
    )

    assert len(resegment_on_gaps([original], max_gap=1.5)) == 2
    assert len(resegment_on_gaps([original], max_gap=3.0)) == 1


def test_zero_threshold_disables_splitting():
    original = seg(0.0, 30.0, " a b", words((0.0, 0.5, " a"), (29.0, 30.0, " b")))

    assert len(resegment_on_gaps([original], max_gap=0)) == 1


def test_segments_without_word_timestamps_pass_through_untouched():
    """whisper.cpp does not always supply words; inventing boundaries would be worse."""
    original = seg(0.0, 10.0, "no words here")

    result = resegment_on_gaps([original])

    assert len(result) == 1
    assert result[0] is original


def test_speaker_and_confidence_are_preserved_across_a_split():
    original = Segment(
        speaker=Speaker.THEM,
        start=0.0,
        end=10.0,
        text=" a b",
        avg_logprob=-0.42,
        no_speech_prob=0.03,
        words=words((0.0, 0.5, " a"), (9.5, 10.0, " b")),
    )

    for part in resegment_on_gaps([original]):
        assert part.speaker is Speaker.THEM
        assert part.avg_logprob == -0.42
        assert part.no_speech_prob == 0.03


def test_multiple_pauses_produce_multiple_segments():
    original = seg(
        0.0,
        12.0,
        " a b c",
        words((0.0, 0.5, " a"), (5.0, 5.5, " b"), (11.5, 12.0, " c")),
    )

    result = resegment_on_gaps([original])

    assert [round(s.start, 2) for s in result] == [0.0, 5.0, 11.5]


def test_empty_input_is_handled():
    assert resegment_on_gaps([]) == []


# --- merging back into turns -------------------------------------------------


def test_sentence_split_across_two_whisper_segments_is_rejoined():
    """Real output: one spoken turn arrived as two segments broken mid-sentence."""
    a = seg(11.44, 13.04, "We agreed on 12,000",
            words((11.44, 11.74, " We"), (11.74, 12.04, " agreed"),
                  (12.04, 12.24, " on"), (12.24, 13.04, " 12,000")))
    b = seg(13.20, 16.85, "for the whole batch. Is that still correct?",
            words((13.20, 13.60, " for"), (13.60, 14.40, " the whole batch."),
                  (14.60, 16.85, " Is that still correct?")))

    result = resegment_on_gaps([a, b])

    assert len(result) == 1
    assert result[0].text == "We agreed on 12,000 for the whole batch. Is that still correct?"
    assert result[0].start == 11.44
    assert result[0].end == 16.85


def test_unsplit_segments_keep_their_original_text():
    """An engine whose word list does not cover the text must not lose part of the transcript."""
    sparse = seg(0.0, 3.0, "tam metin burada duruyor",
                 words((0.0, 0.5, " tam"), (2.5, 3.0, " duruyor")))

    result = resegment_on_gaps([sparse], max_gap=5.0)

    assert len(result) == 1
    assert result[0].text == "tam metin burada duruyor"


def test_different_speakers_are_never_merged():
    mine = Segment(speaker=Speaker.ME, start=0.0, end=1.0, text="evet",
                   words=words((0.0, 1.0, " evet")))
    theirs = Segment(speaker=Speaker.THEM, start=1.1, end=2.0, text="hayir",
                     words=words((1.1, 2.0, " hayir")))

    assert len(resegment_on_gaps([mine, theirs])) == 2


def test_turns_separated_by_a_real_pause_stay_separate():
    a = seg(0.0, 2.0, "birinci tur", words((0.0, 1.0, " birinci"), (1.5, 2.0, " tur")))
    b = seg(9.0, 10.0, "ikinci tur", words((9.0, 9.5, " ikinci"), (9.6, 10.0, " tur")))

    assert len(resegment_on_gaps([a, b])) == 2


def test_merge_keeps_the_less_confident_values():
    a = Segment(speaker=Speaker.ME, start=0.0, end=1.0, text="a", avg_logprob=-0.2,
                no_speech_prob=0.01, words=words((0.0, 1.0, " a")))
    b = Segment(speaker=Speaker.ME, start=1.2, end=2.0, text="b", avg_logprob=-1.3,
                no_speech_prob=0.44, words=words((1.2, 2.0, " b")))

    merged = resegment_on_gaps([a, b])[0]

    assert merged.avg_logprob == -1.3
    assert merged.no_speech_prob == 0.44
    assert merged.is_low_confidence


def test_split_then_merge_reconstructs_the_original_turns():
    """End to end: one over-long segment becomes exactly two turns, not four fragments."""
    original = seg(
        0.0, 13.04, " Hello there. We agreed on 12,000",
        words(
            (0.00, 0.40, " Hello"), (0.45, 1.00, " there."),
            (11.44, 11.74, " We"), (11.80, 12.10, " agreed"), (12.24, 13.04, " 12,000"),
        ),
    )

    result = resegment_on_gaps([original])

    assert [round(s.start, 2) for s in result] == [0.0, 11.44]
    assert result[0].text == "Hello there."
    assert result[1].text == "We agreed 12,000"
