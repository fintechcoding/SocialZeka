"""Splitting a long recording without cutting through a word."""

from __future__ import annotations

import array
import math
import os
import wave

from vt_worker.chunking import Chunk, plan_chunks, slice_wav, speech_coverage

RATE = 16_000


def _write(path: str, blocks: list[tuple[float, int]]) -> None:
    """
    Writes a WAV from (seconds, amplitude) blocks.

    Amplitude 0 is digital silence, which is what a pause between sentences looks like once the
    VoIP stack has finished with it.
    """
    samples = array.array("h")

    for seconds, amplitude in blocks:
        count = int(seconds * RATE)
        if amplitude == 0:
            samples.extend([0] * count)
        else:
            # A tone rather than a constant: a constant has no zero crossings and would make the
            # mean-absolute scan read it as louder than speech of the same peak.
            samples.extend(
                int(amplitude * math.sin(2 * math.pi * 200 * i / RATE)) for i in range(count)
            )

    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(samples.tobytes())


def test_short_recording_is_one_chunk(tmp_path):
    path = str(tmp_path / "short.wav")
    _write(path, [(30.0, 8000)])

    chunks = plan_chunks(path, max_seconds=1200)

    assert len(chunks) == 1
    assert chunks[0].start_seconds == 0.0
    assert chunks[0].end_seconds > 29


def test_long_recording_is_divided_evenly(tmp_path):
    path = str(tmp_path / "long.wav")
    _write(path, [(120.0, 8000)])

    chunks = plan_chunks(path, max_seconds=50)

    # Three pieces of forty seconds, not two of fifty and one of twenty: a very short tail
    # transcribes badly because the model has almost no context to work with.
    assert len(chunks) == 3

    lengths = [c.length_seconds for c in chunks]
    assert max(lengths) - min(lengths) < 25

    # No gaps and no overlaps: every second of the call belongs to exactly one piece.
    for previous, following in zip(chunks, chunks[1:]):
        assert previous.end_seconds == following.start_seconds

    assert chunks[0].start_seconds == 0.0
    assert abs(chunks[-1].end_seconds - 120.0) < 0.5


def test_the_cut_lands_in_the_silence_not_mid_word(tmp_path):
    """
    The behaviour this module exists for.

    Speech, then a gap, then speech. The arithmetic midpoint falls inside the second run of
    speech; the cut must move to the gap instead. A boundary through a word does not just lose
    that word, it leaves two half-words the model confidently completes into something nobody
    said — and this transcript gets quoted back as evidence about a real person.
    """
    path = str(tmp_path / "gap.wav")

    # 0-50 speech, 50-56 silence, 56-100 speech. Halfway is 50... move the gap off centre so the
    # test would fail if the code merely returned the midpoint.
    _write(path, [(38.0, 9000), (6.0, 0), (56.0, 9000)])

    chunks = plan_chunks(path, max_seconds=60)

    assert len(chunks) == 2
    boundary = chunks[0].end_seconds

    assert 38.0 <= boundary <= 44.0, f"cut at {boundary:.1f}s, which is inside speech"


def test_a_recording_with_no_silence_still_splits(tmp_path):
    """Continuous speech has no good cut, but the job must still proceed."""
    path = str(tmp_path / "solid.wav")
    _write(path, [(100.0, 9000)])

    chunks = plan_chunks(path, max_seconds=40)

    assert len(chunks) >= 2
    assert all(c.length_seconds > 0 for c in chunks)


def test_slice_extracts_exactly_the_requested_range(tmp_path):
    source = str(tmp_path / "source.wav")
    target = str(tmp_path / "slice.wav")

    _write(source, [(60.0, 8000)])
    slice_wav(source, target, 10.0, 25.0)

    with wave.open(target, "rb") as wav:
        seconds = wav.getnframes() / wav.getframerate()
        assert abs(seconds - 15.0) < 0.05
        assert wav.getframerate() == RATE
        assert wav.getnchannels() == 1


def test_slice_clamps_a_range_past_the_end(tmp_path):
    """A boundary computed from a rounded duration must not throw."""
    source = str(tmp_path / "source.wav")
    target = str(tmp_path / "slice.wav")

    _write(source, [(5.0, 8000)])
    slice_wav(source, target, 3.0, 99.0)

    with wave.open(target, "rb") as wav:
        assert abs(wav.getnframes() / wav.getframerate() - 2.0) < 0.05


def test_an_empty_file_does_not_crash_the_planner(tmp_path):
    path = str(tmp_path / "empty.wav")

    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)

    chunks = plan_chunks(path, max_seconds=600)

    assert chunks == [Chunk(0, 0.0, 0.0)]


def test_chunks_cover_the_whole_recording(tmp_path):
    """Nothing may be dropped: a missing minute is a missing minute of somebody's conversation."""
    path = str(tmp_path / "cover.wav")
    _write(path, [(20.0, 9000), (3.0, 0), (20.0, 9000), (3.0, 0), (20.0, 9000)])

    chunks = plan_chunks(path, max_seconds=25)
    covered = sum(c.length_seconds for c in chunks)

    assert abs(covered - 66.0) < 0.5


# ---- how much of the speech came back ----------------------------------------


class _Seg:
    """Only the two fields speech_coverage reads, so the test needs no engine."""

    def __init__(self, start: float, end: float):
        self.start, self.end = start, end


def test_coverage_counts_the_speech_that_came_back_with_words(tmp_path):
    """
    A transcript that goes quiet is the failure nothing was measuring.

    Measured on a real call: the hosted service returned words for 108 of 157 seconds of speech
    where the local engine returned 150, and the missing 49 seconds were at the same level as the
    rest — so the transcript alone could not say they were missing.
    """
    path = str(tmp_path / "call.wav")
    _write(path, [(10.0, 8000), (10.0, 0), (10.0, 8000)])  # speech, pause, speech

    assert plan_chunks(path, 60.0)  # the file is readable at all

    # Both stretches transcribed.
    assert speech_coverage(path, [_Seg(0.0, 10.0), _Seg(20.0, 30.0)]) > 0.95

    # Only the first: half the conversation is missing, and it says so.
    half = speech_coverage(path, [_Seg(0.0, 10.0)])
    assert 0.4 < half < 0.6

    # Nothing at all.
    assert speech_coverage(path, []) == 0.0


def test_coverage_does_not_accuse_a_silent_channel(tmp_path):
    """
    One channel is quiet for most of a call because the other person is talking, and a channel
    that is quiet throughout is a fact about the call rather than a failed transcription. Asking
    "what share of the speech came back" of a file with no speech in it has no answer, and
    inventing a low one would put a warning on every one-sided recording.
    """
    path = str(tmp_path / "quiet.wav")
    _write(path, [(20.0, 0)])

    assert speech_coverage(path, []) is None


def test_coverage_of_something_that_is_not_a_wav_is_unknown_rather_than_zero(tmp_path):
    path = str(tmp_path / "not.wav")
    with open(path, "wb") as handle:
        handle.write(b"OggS not a wav at all")

    assert speech_coverage(path, [_Seg(0.0, 5.0)]) is None
