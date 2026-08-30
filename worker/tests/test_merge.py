"""Tests for stream merging: the half of speaker attribution that lives in Python."""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vt_worker.merge import Segment, Speaker, merge_streams  # noqa: E402


def seg(start: float, end: float, text: str, **kw) -> Segment:
    return Segment(speaker=Speaker.ME, start=start, end=end, text=text, **kw)


def test_segments_are_ordered_chronologically():
    mic = [seg(0.0, 2.0, "alo merhaba"), seg(6.0, 7.5, "tamam anlastik")]
    far = [seg(2.5, 5.0, "merhaba, nasilsiniz")]

    merged = merge_streams(mic, far)

    assert [s.text for s in merged.segments] == [
        "alo merhaba",
        "merhaba, nasilsiniz",
        "tamam anlastik",
    ]


def test_speakers_are_assigned_from_the_stream_not_guessed():
    mic = [seg(0.0, 1.0, "ben konusuyorum")]
    far = [seg(1.0, 2.0, "ben karsi tarafim")]

    merged = merge_streams(mic, far)

    assert merged.segments[0].speaker is Speaker.ME
    assert merged.segments[1].speaker is Speaker.THEM


def test_simultaneous_speech_keeps_both_sides():
    """Talking over each other is where diarization models fail worst and separate capture wins."""
    mic = [seg(1.0, 4.0, "ama ben demistim ki")]
    far = [seg(2.0, 5.0, "hayir oyle konusmadik")]

    merged = merge_streams(mic, far)

    assert len(merged.segments) == 2
    assert all(s.overlaps_other_speaker for s in merged.segments)
    assert merged.stats.overlap_segments == 2


def test_non_overlapping_turns_are_not_flagged():
    mic = [seg(0.0, 2.0, "buyrun")]
    far = [seg(2.0, 4.0, "merhaba")]

    merged = merge_streams(mic, far)

    assert merged.stats.overlap_segments == 0
    assert not any(s.overlaps_other_speaker for s in merged.segments)


def test_identical_text_at_the_same_time_is_flagged_as_echo():
    """Loudspeaker use puts the far end into the microphone stream as well."""
    mic = [seg(1.0, 3.0, "yarin sabah gonderecegim")]
    far = [seg(1.0, 3.0, "yarin sabah gonderecegim")]

    merged = merge_streams(mic, far)

    assert all(s.suspected_echo for s in merged.segments)
    assert merged.stats.suspected_echo_segments == 2


def test_different_text_at_the_same_time_is_not_echo():
    mic = [seg(1.0, 3.0, "fiyat cok yuksek")]
    far = [seg(1.0, 3.0, "bu bizim son teklifimiz")]

    merged = merge_streams(mic, far)

    assert merged.stats.overlap_segments == 2
    assert merged.stats.suspected_echo_segments == 0


def test_echo_detection_survives_turkish_spelling_differences():
    """Whisper does not spell Turkish diacritics identically across two passes of the same audio."""
    mic = [seg(1.0, 3.0, "Odemeyi yarin yapacagim")]
    far = [seg(1.0, 3.0, "Ödemeyi yarın yapacağım")]

    merged = merge_streams(mic, far)

    assert merged.stats.suspected_echo_segments == 2


def test_headphone_warning_needs_a_sustained_pattern_not_one_coincidence():
    mic = [seg(float(i), i + 0.5, "evet") for i in range(20)]
    far = [seg(0.0, 0.5, "evet")]

    merged = merge_streams(mic, far)

    assert merged.stats.suspected_echo_segments == 2
    assert not merged.stats.likely_no_headphones


def test_headphone_warning_fires_when_echo_is_pervasive():
    mic = [seg(float(i) * 2, i * 2 + 1.5, f"cumle {i}") for i in range(6)]
    far = [seg(float(i) * 2, i * 2 + 1.5, f"cumle {i}") for i in range(6)]

    merged = merge_streams(mic, far)

    assert merged.stats.likely_no_headphones


def test_low_confidence_segments_are_identified():
    """Uncertain audio must be excluded from automatic contradiction detection."""
    clear = seg(0.0, 1.0, "on sekiz bin", avg_logprob=-0.2, no_speech_prob=0.01)
    muddy = seg(1.0, 2.0, "on sekiz yuz", avg_logprob=-1.4, no_speech_prob=0.05)
    silent = seg(2.0, 3.0, "hmm", avg_logprob=-0.3, no_speech_prob=0.9)

    assert not clear.is_low_confidence
    assert muddy.is_low_confidence
    assert silent.is_low_confidence

    merged = merge_streams([clear, muddy, silent], [])
    assert merged.stats.low_confidence_segments == 2


def test_readable_transcript_is_labelled_and_timestamped():
    mic = [seg(0.0, 2.0, "alo")]
    far = [seg(63.0, 65.0, "buyrun")]

    text = merge_streams(mic, far).text()

    assert text == "[00:00] BEN: alo\n[01:03] KARSI: buyrun"


def test_empty_streams_produce_an_empty_transcript():
    merged = merge_streams([], [])

    assert merged.segments == []
    assert merged.duration == 0.0
    assert merged.text() == ""


def test_one_sided_call_still_works():
    """A call where the far end never speaks must not be treated as an error."""
    merged = merge_streams([seg(0.0, 3.0, "kimse var mi")], [])

    assert len(merged.segments) == 1
    assert merged.stats.far_segments == 0
    assert merged.duration == 3.0
