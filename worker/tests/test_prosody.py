"""
The prosody front end: level and pitch as numbers, on audio whose numbers are known in advance.

Everything here is synthetic on purpose. A sine has one pitch and one level and both can be
written down before the file exists, so a wrong answer is a wrong answer and not a matter of
taste; silence has neither, and a channel measured as having them is inventing. What these do not
settle — whether the thresholds hold on real calls decoded from the Opus archive — is the question
of docs/PLAN-SOSYALZEKA.md §6.3, and it is answered by listening, not by a test.
"""

from __future__ import annotations

import json
import math
import time
import wave

import pytest

from vt_worker import prosody, speaker

RATE = 16_000

# A tone whose RMS sits at -12 dBFS: peak = full scale × 10^(-12/20) × √2. Named by its RMS rather
# than its peak because RMS is what the module measures.
AMPLITUDE = round(32768 * 10 ** (-12 / 20) * math.sqrt(2))


def _tone(seconds: float, hz: float = 220.0, amplitude: int = AMPLITUDE):
    import numpy as np

    count = int(RATE * seconds)
    return (amplitude * np.sin(2 * math.pi * hz * np.arange(count) / RATE)).astype(np.int16)


def _silence(seconds: float):
    import numpy as np

    return np.zeros(int(RATE * seconds), dtype=np.int16)


def _write(path, samples, channels: int = 1) -> None:
    """A WAV in the shape this application records: 16 kHz, 16-bit, mono unless told otherwise."""
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(samples.tobytes())


def _dbfs(samples) -> float:
    """The RMS level of these exact samples, so the expectation is computed rather than assumed."""
    import numpy as np

    x = samples.astype(np.float64)
    return 20 * math.log10(math.sqrt(float(np.mean(x * x))) / 32768)


# ---- a tone ------------------------------------------------------------------


def test_a_pure_tone_is_measured_at_its_pitch(tmp_path):
    """
    220 Hz in, 220 Hz out, in every half second. If this fails the pitch tracker is wrong on the
    simplest signal there is, and nothing it says about a voice can be trusted.
    """
    path = tmp_path / "tone.wav"
    _write(path, _tone(4.0, hz=220.0))

    result = prosody.analyse(str(path))

    assert len(result.bins) == 8
    for b in result.bins:
        assert b.f0 is not None
        assert abs(b.f0 - 220.0) < 2.0, b


def test_a_pure_tone_is_measured_at_its_level(tmp_path):
    """
    The level reported is the RMS of the samples, in dBFS, and nothing else — no window, no
    weighting, no gain. The expected figure is computed from the samples that were written, not
    typed in, so the test cannot agree with the code by sharing its mistake.
    """
    import numpy as np

    samples = _tone(4.0)
    path = tmp_path / "tone.wav"
    _write(path, samples)

    expected = _dbfs(samples)
    assert abs(expected - (-12.0)) < 0.05   # the fixture is what its name says

    result = prosody.analyse(str(path))

    assert abs(float(np.mean([b.dbfs for b in result.bins])) - expected) < 0.5
    for b in result.bins:
        assert abs(b.dbfs - expected) < 0.5, b


def test_a_tone_is_speech_and_voiced_throughout(tmp_path):
    """
    A tone at -12 dBFS is above the speech gate in every frame and has a period in every frame,
    so the voiced share is one and the speech time is the file's length. A bin that comes back
    partly unvoiced means the gate or the tracker is dropping frames it has no reason to drop.
    """
    path = tmp_path / "tone.wav"
    _write(path, _tone(4.0))

    result = prosody.analyse(str(path))

    assert abs(result.speech_seconds - 4.0) < 0.05
    for b in result.bins:
        assert b.voiced >= 0.95, b
        assert b.f0_iqr is not None and b.f0_iqr < 1.0, b


def test_bins_are_half_a_second_apart_from_zero(tmp_path):
    """The timestamps are what the caller aligns to the transcript; a drift here misplaces every
    number on the timeline."""
    path = tmp_path / "tone.wav"
    _write(path, _tone(4.3))

    result = prosody.analyse(str(path))

    assert [b.start for b in result.bins] == [i * 0.5 for i in range(9)]


# ---- silence -----------------------------------------------------------------


def test_silence_has_no_speech_no_pitch_and_no_voicing(tmp_path):
    """
    Nothing in, nothing out. A channel of digital silence that comes back with a pitch is the
    tracker finding a period in zeros — the 0/0 of the normalisation read as a dip — and a
    voiced share above zero would put a voice on a timeline where nobody spoke.
    """
    path = tmp_path / "quiet.wav"
    _write(path, _silence(3.0))

    result = prosody.analyse(str(path))

    assert result.speech_seconds == 0.0
    assert result.floor_dbfs == pytest.approx(prosody.LEVEL_FLOOR_DBFS, abs=0.01)
    assert len(result.bins) == 6
    for b in result.bins:
        assert b.f0 is None
        assert b.f0_iqr is None
        assert b.voiced == 0.0
        assert b.dbfs == pytest.approx(prosody.LEVEL_FLOOR_DBFS, abs=0.01)


def test_speech_is_told_from_silence_by_the_gate_speaker_uses(tmp_path):
    """
    Two seconds of tone inside six of silence: two seconds of speech, a floor at the level of the
    silence, voiced bins only where the tone is. The gate is speaker.py's, by identity — a second
    threshold here would be a fourth opinion on what speech is, in a code base that has measured
    exactly one.
    """
    assert prosody.SPEECH_FLOOR_DBFS is speaker.SPEECH_FLOOR_DBFS

    import numpy as np

    path = tmp_path / "gap.wav"
    _write(path, np.concatenate([_silence(3.0), _tone(2.0), _silence(3.0)]))

    result = prosody.analyse(str(path))

    assert abs(result.speech_seconds - 2.0) < 0.1
    assert result.floor_dbfs == pytest.approx(prosody.LEVEL_FLOOR_DBFS, abs=0.01)

    by_start = {b.start: b for b in result.bins}
    for start in (3.0, 3.5, 4.0, 4.5):
        assert by_start[start].voiced >= 0.9, by_start[start]
        assert abs(by_start[start].f0 - 220.0) < 2.0
    for start in (0.0, 0.5, 1.0, 1.5, 2.0, 5.5, 6.0, 6.5, 7.0, 7.5):
        assert by_start[start].voiced == 0.0, by_start[start]
        assert by_start[start].f0 is None


# ---- the pitch tracker -------------------------------------------------------


def test_pitch_follows_a_change(tmp_path):
    """
    150 Hz for two seconds, then 300 Hz. The second half is the octave of the first, which is the
    error YIN makes when the threshold step is wrong: a tracker that reports 150 throughout has
    taken the longer period, and one that reports 300 throughout the shorter.
    """
    import numpy as np

    path = tmp_path / "step.wav"
    _write(path, np.concatenate([_tone(2.0, hz=150.0), _tone(2.0, hz=300.0)]))

    result = prosody.analyse(str(path))

    for b in result.bins:
        want = 150.0 if b.start < 2.0 else 300.0
        assert abs(b.f0 - want) < 2.0, b


@pytest.mark.parametrize("hz", [65.0, 380.0])
def test_the_edges_of_the_search_range_are_reachable(tmp_path, hz):
    """
    A voice near either end of the range is still measured to within two hertz. Failing at the
    low end means the window does not hold enough periods; at the high end, that the parabolic
    step is missing, since a 42.1-sample period read as 42 is nearly a hertz out at 380.
    """
    path = tmp_path / "edge.wav"
    _write(path, _tone(2.0, hz=hz))

    result = prosody.analyse(str(path))

    assert result.bins
    for b in result.bins:
        assert b.f0 is not None
        assert abs(b.f0 - hz) < 2.0, b


def test_a_file_shorter_than_a_frame_yields_nothing(tmp_path):
    """A fragment gets an empty answer, not an exception and not a bin invented from padding."""
    path = tmp_path / "blip.wav"
    _write(path, _tone(0.01))

    result = prosody.analyse(str(path))

    assert result.bins == []
    assert result.speech_seconds == 0.0


# ---- the format contract -----------------------------------------------------


def test_stereo_is_refused_by_name(tmp_path):
    """
    The recorder writes mono and so does every path that reaches here. A stereo file measured as
    mono interleaves two channels into one signal: the level is nobody's and the "period" is the
    interleaving. The refusal is speaker.read_wav's, reused rather than rewritten.
    """
    import numpy as np

    path = tmp_path / "stereo.wav"
    _write(path, np.zeros(RATE * 2, dtype=np.int16), channels=2)

    with pytest.raises(ValueError, match="mono"):
        prosody.analyse(str(path))


def test_the_wire_shape_is_four_columns_per_bin(tmp_path):
    """
    The C# side reads [t, dbfs, f0 | null, voiced] and nothing else, and a null pitch is a fact
    about the bin, not a missing column. A fifth column or a missing null breaks the parser.
    """
    import numpy as np

    path = tmp_path / "gap.wav"
    _write(path, np.concatenate([_silence(1.0), _tone(1.0)]))

    payload = prosody.analyse(str(path)).to_json()

    assert set(payload) == {"floor_dbfs", "speech_seconds", "bins"}
    assert all(len(row) == 4 for row in payload["bins"])
    assert payload["bins"][0][2] is None
    assert abs(payload["bins"][-1][2] - 220.0) < 2.0
    json.dumps(payload)   # nothing numpy-typed leaks into the event


# ---- time and memory ---------------------------------------------------------


def test_three_minutes_take_seconds_not_minutes(tmp_path):
    """
    Three minutes, half tone and half silence in ten-second blocks, crossing several batch
    boundaries. The plan's budget is twenty minutes of two channels in fifteen seconds on a
    processor; three minutes in twenty is a bound loose enough for any machine that runs the
    suite and tight enough to catch a per-frame loop having crept back in. The numbers are checked
    across the boundaries too: a batch stitched wrong shows up as a bin with the wrong pitch.
    """
    import numpy as np

    blocks = []
    for index in range(18):
        blocks.append(_tone(10.0) if index % 2 == 0 else _silence(10.0))
    samples = np.concatenate(blocks)

    path = tmp_path / "long.wav"
    _write(path, samples)

    started = time.monotonic()
    result = prosody.analyse(str(path))
    elapsed = time.monotonic() - started

    assert elapsed < 20.0, f"{elapsed:.1f} s for three minutes"
    assert len(result.bins) == 360
    assert abs(result.speech_seconds - 90.0) < 0.5

    voiced = [b for b in result.bins if b.voiced >= 0.9]
    assert 170 <= len(voiced) <= 180
    for b in voiced:
        assert abs(b.f0 - 220.0) < 2.0, b


# ---- the command -------------------------------------------------------------


def test_the_command_reports_each_side_under_its_name_and_nulls_the_missing_one(tmp_path, capsys):
    """
    One event, both channels named, the absent one null rather than missing — so the C# side
    reads the same shape whether or not the far side was recorded. A far channel that is
    silently dropped from the event looks, on the other side of the pipe, like a call with one
    participant.
    """
    from vt_worker.__main__ import cmd_prosody

    path = tmp_path / "mic.wav"
    _write(path, _tone(2.0))

    assert cmd_prosody({"id": "p1", "mic_path": str(path)}) == 0

    events = [json.loads(line) for line in capsys.readouterr().out.splitlines() if line.strip()]
    results = [e for e in events if e["type"] == "prosody"]

    assert len(results) == 1
    event = results[0]
    assert event["id"] == "p1"
    assert event["bin_seconds"] == prosody.BIN_SECONDS
    assert set(event["channels"]) == {"mic", "far"}
    assert event["channels"]["far"] is None

    mic = event["channels"]["mic"]
    assert set(mic) == {"floor_dbfs", "speech_seconds", "bins"}
    assert len(mic["bins"]) == 4
    assert abs(mic["bins"][0][2] - 220.0) < 2.0


def test_the_command_refuses_a_request_with_no_audio():
    """A request naming neither channel is a caller's bug, and it is answered with the protocol's
    bad_request rather than a traceback about None."""
    from vt_worker.__main__ import cmd_prosody
    from vt_worker.engines import EngineError

    with pytest.raises(EngineError) as caught:
        cmd_prosody({"id": "p2"})

    assert caught.value.code == "bad_request"
