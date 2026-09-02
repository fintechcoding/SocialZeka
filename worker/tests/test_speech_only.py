"""
Removing the silence before a hosted model sees it, and putting the clock back afterwards.

The local engine runs faster-whisper with ``vad_filter=True``; no hosted API does the equivalent.
This application records the whole call on two separate channels, so one channel is minutes of
nothing while the other person talks — and Whisper given silence does not return nothing, it
returns whatever its training data has most of.

The mapping back is what these tests are really for. Every line in this product carries a moment you
can click to hear; a time that is out by a second is a quote pointing at audio that does not contain
it, which is worse than no timestamp at all.
"""

from __future__ import annotations

import math
import struct
import wave

from vt_worker.speech_only import (
    MIN_SPAN_SECONDS,
    SpeechSpan,
    find_speech,
    to_original,
    write_speech_only,
)

RATE = 16_000


def _write(path, parts: list[tuple[float, bool]]) -> None:
    """A WAV built from (seconds, is_speech) pieces. Speech is a tone, silence is silence."""
    frames = bytearray()

    for seconds, loud in parts:
        for n in range(int(seconds * RATE)):
            value = int(9000 * math.sin(2 * math.pi * 220 * n / RATE)) if loud else 0
            frames += struct.pack("<h", value)

    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(bytes(frames))


def test_speech_is_found_and_the_silence_between_is_not(tmp_path):
    path = tmp_path / "call.wav"
    _write(path, [(2.0, True), (10.0, False), (2.0, True)])

    spans = find_speech(str(path))

    assert len(spans) == 2
    assert spans[0].start < 0.5 and spans[0].end < 3.0
    assert spans[1].start > 11.0

    # The second lands directly after the first in the upload — that is the whole point.
    assert spans[1].offset == spans[0].length


def test_a_time_in_the_upload_maps_back_to_where_it_was_said(tmp_path):
    """
    The assertion the ledger rests on.

    The second span starts at 12 s in the recording and at 2 s in the upload, so a word the model
    reports at 2.5 s was said at 12.5 s. Getting this wrong is not a cosmetic fault: it is a quote
    attached to audio that does not contain it.
    """
    spans = [
        SpeechSpan(start=0.0, end=2.0, offset=0.0),
        SpeechSpan(start=12.0, end=14.0, offset=2.0),
    ]

    assert to_original(0.0, spans) == 0.0
    assert to_original(1.5, spans) == 1.5      # inside the first
    assert to_original(2.0, spans) == 2.0      # the boundary belongs to the first
    assert to_original(2.5, spans) == 12.5     # inside the second, ten seconds later
    assert to_original(4.0, spans) == 14.0     # the very end


def test_a_time_past_the_end_is_clamped_rather_than_extrapolated(tmp_path):
    """
    The model does report an end slightly past what it was given.

    Carrying that overrun forward would place a word inside the silence we removed — somewhere it
    provably was not said. Clamping says "at the end of this span", which is true.
    """
    spans = [SpeechSpan(start=30.0, end=32.0, offset=0.0)]

    assert to_original(2.5, spans) == 32.0
    assert to_original(900.0, spans) == 32.0


def test_without_spans_a_time_is_left_alone(tmp_path):
    """No rewrite happened, so no mapping should either."""
    assert to_original(7.25, []) == 7.25


def test_a_recording_that_is_mostly_speech_is_left_alone(tmp_path):
    """
    Rewriting has to buy something. The mapping is one more thing that can be wrong, and a
    recording with little silence in it gains nothing to justify that.
    """
    path = tmp_path / "busy.wav"
    _write(path, [(10.0, True), (0.5, False), (10.0, True)])

    assert write_speech_only(str(path), str(tmp_path / "out.wav")) == []


def test_a_silent_channel_is_uploaded_as_it_is(tmp_path):
    """
    A channel with nothing on it is a fact the transcript should show — "karşı taraftan ses
    gelmedi" — not an empty file that fails in a different way.
    """
    path = tmp_path / "silent.wav"
    _write(path, [(20.0, False)])

    assert write_speech_only(str(path), str(tmp_path / "out.wav")) == []


def test_the_written_file_holds_the_speech_and_none_of_the_gap(tmp_path):
    path = tmp_path / "call.wav"
    _write(path, [(2.0, True), (20.0, False), (2.0, True)])

    out = tmp_path / "speech.wav"
    spans = write_speech_only(str(path), str(out))

    assert spans

    with wave.open(str(out), "rb") as wav:
        written = wav.getnframes() / wav.getframerate()

    # Both stretches of speech, their padding, and nothing like the twenty-second gap.
    assert 4.0 <= written <= 6.0
    assert abs(written - sum(s.length for s in spans)) < 0.05


def test_a_file_that_cannot_be_read_is_sent_untouched(tmp_path):
    """
    A quality improvement, not a requirement. Refusing to upload a recording because its header
    could not be scanned would trade a better transcript for no transcript.
    """
    broken = tmp_path / "broken.wav"
    broken.write_bytes(b"RIFF" + bytes(4096))

    assert find_speech(str(broken)) == []
    assert write_speech_only(str(broken), str(tmp_path / "out.wav")) == []


def test_a_single_loud_frame_is_not_speech(tmp_path):
    """A door, a keyboard, a breath. Keeping it would put a fragment in front of the model."""
    path = tmp_path / "tick.wav"
    _write(path, [(0.05, True), (20.0, False)])

    assert all(span.length >= MIN_SPAN_SECONDS for span in find_speech(str(path)))
