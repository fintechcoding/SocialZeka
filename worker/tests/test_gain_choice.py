"""
Choosing whether the hosted service should apply its loudness normalisation.

The service runs a single-pass ffmpeg loudnorm, which is dynamic: quiet blocks are pushed towards
the target harder than loud ones, so a channel's noise floor rises further than its speech does.
On a live microphone with long gaps that narrows the contrast the decoder needs and the answer
collapses — 4 words where turning it off gave 62. On a channel that is mostly speech there is no
window of pure room tone to lift, and the same gain helps.

These are the two shapes, built rather than recorded so the boundary is exercised deliberately.
"""

import math
import struct
import wave

import pytest

from vt_worker import chunking


def _write(path, blocks, rate=16000):
    """A mono 16 kHz file from (seconds, amplitude) pairs."""
    frames = bytearray()
    phase = 0.0

    for seconds, amplitude in blocks:
        for _ in range(int(seconds * rate)):
            phase += 2 * math.pi * 180 / rate
            frames += struct.pack("<h", int(amplitude * math.sin(phase)))

    with wave.open(str(path), "w") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(bytes(frames))

    return str(path)


def test_a_live_microphone_with_long_gaps_is_sent_without_gain(tmp_path):
    """The shape that measured 4 words against 62: a real noise floor, and mostly gaps."""
    path = _write(tmp_path / "mic.wav", [(2.0, 8000), (16.0, 20), (2.0, 8000), (16.0, 20)])

    floor, ratio = chunking.noise_profile(path)

    assert floor > chunking.SILENT_FLOOR_DBFS, "room tone, not digital silence"
    assert ratio < chunking.DENSE_SPEECH_RATIO
    assert chunking.prefers_gain(path) is False


def test_a_loopback_channel_keeps_its_gain(tmp_path):
    """Silence written by the audio stack takes no harm: multiplying nothing leaves nothing."""
    path = _write(tmp_path / "far.wav", [(2.0, 8000), (16.0, 0), (2.0, 8000), (16.0, 0)])

    floor, _ratio = chunking.noise_profile(path)

    assert floor <= chunking.SILENT_FLOOR_DBFS
    assert chunking.prefers_gain(path) is True


def test_a_busy_channel_keeps_its_gain(tmp_path):
    """
    The row that stops this being a constant.

    Turning normalisation off outright was tried and cost 23 seconds of a recording that was 87%
    speech. A channel with no empty window in it has nothing for the gain to ruin.
    """
    path = _write(tmp_path / "busy.wav", [(8.0, 8000), (1.0, 20), (8.0, 8000), (1.0, 20)])

    _floor, ratio = chunking.noise_profile(path)

    assert ratio >= chunking.DENSE_SPEECH_RATIO
    assert chunking.prefers_gain(path) is True


def test_an_unreadable_file_leaves_the_service_to_its_own_default(tmp_path):
    """A missing measurement is not an argument for changing anything."""
    missing = tmp_path / "yok.wav"

    assert chunking.noise_profile(str(missing)) is None
    assert chunking.prefers_gain(str(missing)) is True


def test_the_choice_reaches_the_request(tmp_path):
    """The decision is worth nothing if it does not become a form field."""
    from vt_worker.engines import EngineOptions
    from vt_worker.engines.ex5_engine import Ex5WhisperEngine

    engine = Ex5WhisperEngine()
    engine.load(EngineOptions(model_ref="https://example/v1|k|whisper-1"))

    for asked, expected in ((True, "true"), (False, "false")):
        fields = engine._job_fields(EngineOptions(model_ref="x", normalize=asked))
        assert fields["normalize"] == expected

    # Nothing to say means nothing sent, so the service keeps its own default.
    assert "normalize" not in engine._job_fields(EngineOptions(model_ref="x", normalize=None))
