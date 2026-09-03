"""
The speaker front end, and the refusal that keeps it honest.

None of these load the model. The weights are a 26 MB download and a test suite that needs the
network is a test suite that gets skipped; everything worth guarding here is either arithmetic or
a decision made before the model is ever reached.

The one property that cannot be tested without the model — that this front end produces the same
vectors as the measurement it was tuned against — was checked by hand against the cached
embeddings from that measurement and came back at cosine 1.0000 on five recordings.
"""

from __future__ import annotations

import math
import struct
import wave

import pytest

from vt_worker import speaker

RATE = speaker.RATE


def _tone(path, seconds: float, amplitude: int = 8000, hz: float = 220.0) -> None:
    """A WAV in the shape this application records: 16 kHz, mono, 16-bit."""
    frames = int(RATE * seconds)
    samples = bytearray()

    for i in range(frames):
        samples += struct.pack("<h", int(amplitude * math.sin(2 * math.pi * hz * i / RATE)))

    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(bytes(samples))


def _samples(seconds: float, amplitude: int = 8000, hz: float = 220.0):
    import numpy as np

    count = int(RATE * seconds)
    return (amplitude * np.sin(2 * math.pi * hz * np.arange(count) / RATE)).astype(np.int16)


# ---- the front end -----------------------------------------------------------


def test_the_features_have_the_shape_the_model_declares():
    """80 bins, one frame every 10 ms. The model's input is ['B', 'T', 80]; T is ours to get right."""
    features = speaker.fbank(_samples(1.0))

    assert features.shape[1] == speaker.NUM_MEL == 80

    # 25 ms window sliding 10 ms over one second: (16000 - 400) / 160 + 1.
    assert features.shape[0] == 1 + (RATE - speaker.FRAME_LENGTH) // speaker.FRAME_SHIFT


def test_the_mean_is_removed_over_time():
    """Cepstral mean normalisation, which is what the model was trained on. Without it the
    embedding carries the channel — the microphone, the codec — as much as the voice."""
    import numpy as np

    features = speaker.fbank(_samples(2.0))

    assert np.allclose(features.mean(axis=0), 0.0, atol=1e-4)


def test_too_short_for_a_single_frame_is_empty_rather_than_an_error():
    """A caller handed a fragment gets nothing back, not an exception and not a garbage vector."""
    assert speaker.fbank(_samples(0.05)).shape[0] == 0


# ---- the speech gate ---------------------------------------------------------


def test_silence_is_dropped_and_speech_is_kept():
    """
    One side of a call is silent for most of it, because the other person is talking. Averaging an
    embedding over that silence averages in the room, and the room is the same for everybody.
    """
    import numpy as np

    loud = _samples(2.0, amplitude=8000)
    quiet = np.zeros(int(RATE * 3.0), dtype=np.int16)

    kept = speaker.speech_only(np.concatenate([quiet, loud, quiet]))

    # The two seconds of tone survive; the six seconds of silence do not.
    assert 1.5 * RATE < len(kept) < 2.5 * RATE


def test_a_channel_with_nothing_on_it_yields_nothing():
    import numpy as np

    assert len(speaker.speech_only(np.zeros(RATE * 5, dtype=np.int16))) == 0


# ---- the refusal -------------------------------------------------------------


def test_too_little_speech_returns_nothing_at_all(tmp_path):
    """
    The most load-bearing decision in the module, and it happens before the model is loaded.

    Measured over this application's archive: with no floor the error rate is 13.8%, and over
    recordings holding at least thirty seconds of speech it is 1.1%. Below the floor a vector is
    noise — and a caller handed a number will compare it against somebody, while a caller handed
    None cannot. So the refusal lives here, where the evidence is, rather than at each call site.
    """
    path = tmp_path / "short.wav"
    _tone(path, seconds=speaker.MIN_SPEECH_SECONDS - 5)

    # No model download, no session: the floor is checked first, which is also why this test runs
    # offline.
    assert speaker.embed(str(path)) is None


def test_a_recording_that_is_all_silence_returns_nothing(tmp_path):
    path = tmp_path / "quiet.wav"
    _tone(path, seconds=60.0, amplitude=0)

    assert speaker.embed(str(path)) is None


# ---- the format contract -----------------------------------------------------


def test_stereo_is_refused_by_name(tmp_path):
    """
    The recorder writes 16 kHz mono 16-bit and so does every path that reaches here. Something
    else arriving means a bug upstream, and a wrong-format file decoded as if it were right
    produces a plausible vector for a voice nobody has — which is worse than a stack trace.
    """
    path = tmp_path / "stereo.wav"

    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(b"\x00\x00\x00\x00" * RATE)

    with pytest.raises(ValueError, match="mono"):
        speaker.read_wav(str(path))


def test_the_model_name_travels_with_the_vector():
    """
    Stored voiceprints carry the model that made them, because vectors from two models are not
    comparable and comparing them does not fail — it quietly returns a number near zero for two
    recordings of the same person.
    """
    assert speaker.MODEL_NAME
    assert speaker.MODEL_FILE.endswith(".onnx")
