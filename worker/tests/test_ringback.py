"""
Telling a ring-back from a conversation.

The thresholds in ringback.py were measured on a real archive (see its docstring); these build
the two shapes from scratch so the rule can be checked without that archive, and so a change to
either threshold has to be argued for rather than typed.
"""

from __future__ import annotations

import wave

import numpy as np
import pytest

from vt_worker import __main__ as worker_main
from vt_worker import ringback


RATE = 16_000


def _write(path, samples: np.ndarray) -> str:
    data = np.clip(samples, -1.0, 1.0)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(RATE)
        handle.writeframes((data * 32767).astype("<i2").tobytes())
    return str(path)


def _ringback(seconds: float = 60.0, on: float = 2.0, period: float = 6.0) -> np.ndarray:
    """One tone burst replayed on a clock, over digital silence — what a caller hears."""
    t = np.arange(int(RATE * seconds)) / RATE
    tone = 0.3 * np.sin(2 * np.pi * 450 * t)
    gate = (t % period) < on
    return tone * gate


def _speech(seconds: float = 60.0, seed: int = 7) -> np.ndarray:
    """Bursts that differ from each other and do not arrive on a clock."""
    rng = np.random.default_rng(seed)
    x = rng.normal(0, 0.0005, int(RATE * seconds)).astype(np.float32)

    at = 0.5
    while at < seconds - 2:
        length = float(rng.uniform(0.4, 1.6))
        start = int(at * RATE)
        end = min(len(x), start + int(length * RATE))

        # A different sound every time: a wandering pitch with its own envelope.
        t = np.arange(end - start) / RATE
        pitch = rng.uniform(90, 260) + 40 * np.sin(2 * np.pi * rng.uniform(0.5, 3) * t)
        x[start:end] += 0.25 * np.sin(2 * np.pi * np.cumsum(pitch) / RATE) * np.hanning(end - start)

        at += length + float(rng.uniform(0.3, 2.5))

    return x


def _room(seconds: float = 60.0) -> np.ndarray:
    """A microphone in a quiet room: never silent, never far from its own floor."""
    rng = np.random.default_rng(3)
    return rng.normal(0, 0.0004, int(RATE * seconds)).astype(np.float32)


# ---- the two shapes ---------------------------------------------------------


def test_a_ringback_is_a_clock_and_a_conversation_is_not(tmp_path):
    ring = ringback.shape_of(_write(tmp_path / "ring.wav", _ringback()))
    talk = ringback.shape_of(_write(tmp_path / "talk.wav", _speech()))

    assert ring is not None and talk is not None

    assert ring.rings, ring.as_dict()
    assert not talk.rings, talk.as_dict()

    # The two numbers the rule rests on, stated rather than left implicit.
    assert ring.jitter == pytest.approx(0.0, abs=0.05)
    assert ring.similarity > 0.9
    assert talk.similarity < 0.9


def test_a_quiet_room_is_silent_and_a_talking_channel_is_not(tmp_path):
    room = ringback.shape_of(_write(tmp_path / "room.wav", _room()))
    talk = ringback.shape_of(_write(tmp_path / "talk.wav", _speech()))

    assert room is not None and talk is not None
    assert room.silent, room.as_dict()
    assert not talk.silent, talk.as_dict()


def test_a_short_answer_is_speech_even_though_it_is_a_sliver_of_the_recording(tmp_path):
    """
    The hole the percentile test left, and the reason it is gone.

    "Silent" used to mean the 95th percentile level sat close to the 10th, which asks how much of
    a recording is loud rather than whether anybody said anything. Five per cent of a sixty-second
    recording is three seconds, so two words never moved it: a microphone somebody had spoken into
    measured about 1 dB of spread and read as silent — and a silent microphone is half of what
    deletes a recording.
    """
    for spoken in (1.5, 2.5, 4.0):
        channel = np.copy(_room(seconds=40.0))
        t = np.arange(int(RATE * spoken)) / RATE
        voice = 0.25 * np.sin(2 * np.pi * (150 + 30 * np.sin(2 * np.pi * 2 * t)) * t) * np.hanning(len(t))
        channel[RATE * 5: RATE * 5 + len(t)] += voice

        shape = ringback.shape_of(_write(tmp_path / f"said{spoken}.wav", channel))

        assert shape is not None
        assert not shape.silent, (spoken, shape.as_dict())


def test_a_knock_in_a_quiet_room_is_not_somebody_speaking(tmp_path):
    """
    The other direction: the microphone of the one real unanswered call in the archive holds a
    single loud transient — a door, a chair — and a rule that counted the loudest moment rather
    than the time spent speaking would have called it a conversation and kept a ring-out forever.
    """
    channel = np.copy(_room(seconds=40.0))
    channel[RATE * 7: RATE * 7 + RATE // 20] += 0.5  # 50 ms of loud nothing

    shape = ringback.shape_of(_write(tmp_path / "knock.wav", channel))

    assert shape is not None
    assert shape.silent, shape.as_dict()


# ---- the answer ------------------------------------------------------------


def test_ringing_over_a_silent_microphone_is_an_unanswered_call(tmp_path):
    reading = ringback.unanswered(
        _write(tmp_path / "mic.wav", _room()),
        _write(tmp_path / "far.wav", _ringback()),
    )

    assert reading is not None
    assert reading["unanswered"] is True
    assert "calma" in reading["why"] or "cal" in reading["why"]


def test_ringing_while_somebody_talks_into_the_microphone_is_not(tmp_path):
    """
    Both halves have to hold, and this is the case that proves the second one earns its keep.

    Call 88 on the archive this was measured against is a textbook ring-back over a microphone
    moving 54 dB, because the user was talking. Deleting that recording would delete somebody
    speaking.
    """
    reading = ringback.unanswered(
        _write(tmp_path / "mic.wav", _speech()),
        _write(tmp_path / "far.wav", _ringback()),
    )

    assert reading is not None
    assert reading["unanswered"] is False
    assert "mikrofon" in reading["why"]


@pytest.mark.parametrize("ringing, talking", [(60, 30), (60, 10), (60, 5), (90, 20), (30, 60), (45, 15)])
def test_a_call_answered_after_a_long_ring_is_kept_even_if_the_user_says_nothing(
    tmp_path, ringing, talking
):
    """
    The case that nearly slipped through, and the reason the rule asks about the worst burst
    rather than the average one.

    You call, it rings for a minute, they pick up and do the talking while you listen. The far
    channel is then ten identical ring bursts followed by a few of speech, and your microphone
    never moves — so the microphone condition cannot save it and the far channel has to. Asked as
    a mean, the ring outvoted the speech: 0.898 against a threshold of 0.90, two thousandths from
    deleting a recording of two people talking. Asked of the worst pair, the one that spans the
    moment somebody answered, it is 0.01.
    """
    far = np.concatenate([_ringback(seconds=ringing), _speech(seconds=talking, seed=ringing)])
    mic = _room(seconds=ringing + talking)

    reading = ringback.unanswered(
        _write(tmp_path / "mic.wav", mic), _write(tmp_path / "far.wav", far)
    )

    assert reading is not None
    assert reading["unanswered"] is False, reading["far"]

    # Not merely on the right side of the line: nowhere near it.
    assert reading["far"]["similarity"] < 0.5


def test_a_ring_that_was_never_answered_is_caught_at_every_length(tmp_path):
    """The other side of the same rule: a ring is a ring whether it rang for 25 seconds or four
    minutes, and lengthening it must not weaken the reading."""
    for seconds in (25, 60, 120, 240):
        reading = ringback.unanswered(
            _write(tmp_path / f"mic{seconds}.wav", _room(seconds=seconds)),
            _write(tmp_path / f"far{seconds}.wav", _ringback(seconds=seconds)),
        )

        assert reading is not None and reading["unanswered"] is True, (seconds, reading)
        assert reading["far"]["similarity"] >= ringback.MIN_SIMILARITY


def test_an_ordinary_conversation_is_never_called_unanswered(tmp_path):
    reading = ringback.unanswered(
        _write(tmp_path / "mic.wav", _speech(seed=1)),
        _write(tmp_path / "far.wav", _speech(seed=2)),
    )

    assert reading is not None
    assert reading["unanswered"] is False


# ---- when the question cannot be answered ----------------------------------


@pytest.mark.parametrize("mic, far", [(None, "far.wav"), ("mic.wav", None), (None, None)])
def test_one_channel_alone_answers_nothing(tmp_path, mic, far):
    """A missing side means "transcribe it the ordinary way", which loses nothing."""
    paths = {}
    for name, value in (("mic", mic), ("far", far)):
        paths[name] = _write(tmp_path / value, _room()) if value else None

    assert ringback.unanswered(paths["mic"], paths["far"]) is None


def test_a_file_that_will_not_read_answers_nothing(tmp_path):
    broken = tmp_path / "broken.wav"
    broken.write_bytes(b"not a wav at all")

    assert ringback.shape_of(str(broken)) is None
    assert ringback.unanswered(str(broken), _write(tmp_path / "far.wav", _ringback())) is None


def test_a_recording_too_short_to_hold_a_pattern_answers_nothing(tmp_path):
    tiny = _write(tmp_path / "tiny.wav", _ringback(seconds=0.02))

    assert ringback.shape_of(tiny) is None


def test_a_recording_too_long_to_be_a_ring_is_not_even_read(tmp_path):
    """
    Nobody lets a phone ring for five minutes, so above the cap the answer is known without
    measuring — and measuring anyway would read a hundred megabytes per channel to say no.

    The cap is on the whole recording, never on how much of it is read: measuring only the front
    of a call answered at 2:30 would find a ringing far channel over a silent microphone and
    delete a real conversation.
    """
    long_call = _write(
        tmp_path / "long.wav", _ringback(seconds=ringback.MAX_SECONDS + 5, period=6.0))

    assert ringback.shape_of(long_call) is None

    reading = ringback.unanswered(
        _write(tmp_path / "mic.wav", _room(seconds=ringback.MAX_SECONDS + 5)), long_call)

    assert reading is None


def test_the_levels_are_the_same_as_a_frame_by_frame_reading(tmp_path):
    """
    The running total replaced a frame-by-frame copy that did not fit in memory. It has to give
    the same numbers, or the thresholds measured against the old one mean nothing.
    """
    rng = np.random.default_rng(11)
    signal = rng.normal(0, 0.2, RATE * 3).astype(np.float32)
    path = _write(tmp_path / "noise.wav", signal)

    measured = ringback.shape_of(path)
    assert measured is not None

    # The same framing, computed the obvious way.
    x = np.frombuffer(
        wave.open(path, "rb").readframes(RATE * 3), dtype="<i2").astype(np.float64) / 32768.0
    size = int(RATE * ringback.FRAME_MS / 1000)
    hop = int(RATE * ringback.HOP_MS / 1000)
    count = 1 + (len(x) - size) // hop
    frames = x[np.arange(size)[None, :] + hop * np.arange(count)[:, None]]
    level = 20 * np.log10(np.maximum(np.sqrt(np.mean(frames ** 2, axis=1)), 1e-9))

    assert measured.floor_dbfs == pytest.approx(float(np.percentile(level, 10)), abs=0.01)
    assert measured.peak_dbfs == pytest.approx(float(np.percentile(level, 95)), abs=0.01)


def test_two_bursts_are_a_coincidence_not_a_clock(tmp_path):
    """Three is the smallest number of repeats that is evidence of a machine."""
    two = ringback.shape_of(_write(tmp_path / "two.wav", _ringback(seconds=11.0)))

    assert two is not None
    assert two.bursts < ringback.MIN_BURSTS
    assert not two.rings


# ---- what the job does with the answer --------------------------------------


def _run(request, monkeypatch):
    """Runs one transcribe job, collecting the protocol lines instead of printing them."""
    said = []
    monkeypatch.setattr(worker_main, "emit", said.append)
    monkeypatch.setattr(worker_main, "log", lambda message: None)

    def refuse(name):
        raise AssertionError(f"motor yuklenmemeliydi: {name}")

    monkeypatch.setattr(worker_main, "create", refuse)

    assert worker_main.cmd_transcribe(request) == 0
    return said


def test_an_unanswered_call_is_answered_before_an_engine_is_loaded(tmp_path, monkeypatch):
    """
    The whole point of the check is that nothing is uploaded, so nothing may be loaded either.

    create() raises here: if the job ever reaches the engine, the call was paid for.
    """
    lines = _run(
        {
            "id": "job-ring",
            "engine": "cloud-deepgram",
            "model_ref": "https://api.deepgram.com/v1|KEY|nova-3",
            "mic_path": _write(tmp_path / "mic.wav", _room()),
            "far_path": _write(tmp_path / "far.wav", _ringback()),
            "detect_unanswered": True,
        },
        monkeypatch,
    )

    result = [line for line in lines if line["type"] == "result"]
    assert len(result) == 1

    assert result[0]["segments"] == []
    assert result[0]["unanswered"]["unanswered"] is True
    assert result[0]["unanswered"]["far"]["bursts"] >= ringback.MIN_BURSTS


def test_the_check_does_nothing_unless_the_job_asked_for_it(tmp_path, monkeypatch):
    """An older caller, or one with the switch off, transcribes the ring like any other audio."""
    said = []
    monkeypatch.setattr(worker_main, "emit", said.append)
    monkeypatch.setattr(worker_main, "log", lambda message: None)

    reached = []
    monkeypatch.setattr(worker_main, "create", lambda name: reached.append(name) or (_ for _ in ()).throw(
        RuntimeError("stop here")))

    with pytest.raises(RuntimeError):
        worker_main.cmd_transcribe(
            {
                "id": "job-plain",
                "engine": "cloud-deepgram",
                "model_ref": "x|y|z",
                "mic_path": _write(tmp_path / "mic.wav", _room()),
                "far_path": _write(tmp_path / "far.wav", _ringback()),
            }
        )

    assert reached == ["cloud-deepgram"]
    assert not [line for line in said if line["type"] == "result"]
