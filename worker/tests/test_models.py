"""Tests for model download and verification.

The download itself needs the network, so what is covered here is the logic around it: which
repository an alias resolves to, and whether a cache entry counts as usable weights.
"""

from __future__ import annotations

import os
import sys
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vt_worker import models  # noqa: E402


def test_short_aliases_resolve_to_real_repositories():
    assert models._resolve_repository("large-v3-turbo") == "deepdml/faster-whisper-large-v3-turbo-ct2"
    assert models._resolve_repository("small") == "Systran/faster-whisper-small"


def test_a_full_repository_name_is_passed_through():
    assert models._resolve_repository("RsGoksel/ITU_Mainframe") == "RsGoksel/ITU_Mainframe"


def test_every_catalogued_alias_resolves_to_something_qualified():
    for alias in models._ALIASES:
        assert "/" in models._resolve_repository(alias)


def test_a_missing_model_is_not_reported_as_downloaded(tmp_path):
    assert not models.is_downloaded("definitely-not-a-real-model-xyz", str(tmp_path))


def test_the_probe_clip_is_the_format_whisper_expects(tmp_path):
    path = str(tmp_path / "probe.wav")
    models._write_probe_wav(path, seconds=2.0)

    with wave.open(path, "rb") as wav:
        assert wav.getframerate() == 16_000
        assert wav.getnchannels() == 1
        assert wav.getsampwidth() == 2
        assert abs(wav.getnframes() / 16_000 - 2.0) < 0.01


def test_the_probe_clip_starts_and_ends_in_silence(tmp_path):
    """Silence either side is what makes hallucination visible rather than hidden."""
    path = str(tmp_path / "probe.wav")
    models._write_probe_wav(path, seconds=3.0)

    with wave.open(path, "rb") as wav:
        frames = wav.readframes(wav.getnframes())

    quiet_bytes = int(16_000 * 0.4) * 2
    assert set(frames[:quiet_bytes]) == {0}
    assert set(frames[-quiet_bytes:]) == {0}

    # And the middle is not silent, or the test would prove nothing.
    assert set(frames[len(frames) // 2 : len(frames) // 2 + 200]) != {0}


def test_whispercpp_download_is_refused_with_an_explanation():
    """Its weights are a single file the user supplies; pretending otherwise would just fail."""
    try:
        models.download("whisper.cpp", "ggml-base.bin")
    except Exception as exc:
        assert "elle indirilmelidir" in str(exc)
    else:
        raise AssertionError("expected a refusal")


# ---- where the weights live, and what "downloaded" means -----------------------------------


def test_the_cache_directory_is_decided_in_one_place(tmp_path, monkeypatch):
    """
    Two halves of the application used to disagree about this and it made the product unusable.

    The download command was handed a directory explicitly; the probe command was called without
    one and fell back to the environment. The variable being set was HF_HOME, which is not the
    cache — huggingface_hub keeps its cache in $HF_HOME/hub. So a 1.6 GB download landed in one
    folder and the check that asks "is it downloaded" looked in another, and answered no every
    time, forever.
    """
    from vt_worker import models

    monkeypatch.delenv("HF_HUB_CACHE", raising=False)
    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path / "from-env"))

    # An explicit directory wins, and is published so any later caller agrees with it.
    assert models.resolve_cache_dir(str(tmp_path / "explicit")) == str(tmp_path / "explicit")
    assert os.environ["HF_HUB_CACHE"] == str(tmp_path / "explicit")

    # And a caller that passes nothing now gets the same answer.
    assert models.resolve_cache_dir() == str(tmp_path / "explicit")


def _fake_weights(root, repo: str, megabytes: float) -> None:
    """Writes a file where huggingface_hub would put the weights."""
    folder = root / ("models--" + repo.replace("/", "--")) / "snapshots" / "abc123"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "model.bin").write_bytes(b"\0" * int(megabytes * 1_000_000))


def test_real_weights_on_disk_are_recognised(tmp_path, monkeypatch):
    from vt_worker import models

    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path))

    assert models.is_downloaded("large-v3-turbo") is False

    _fake_weights(tmp_path, "deepdml/faster-whisper-large-v3-turbo-ct2", megabytes=1600)

    assert models.is_downloaded("large-v3-turbo") is True
    assert models.weights_on_disk("large-v3-turbo") > 1_000_000_000


def test_a_metadata_stub_is_not_mistaken_for_a_model(tmp_path, monkeypatch):
    # An interrupted transfer leaves a folder with small files in it. Treating that as present
    # is how somebody discovers mid-call that a two-gigabyte download is about to start.
    from vt_worker import models

    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path))
    _fake_weights(tmp_path, "deepdml/faster-whisper-large-v3-turbo-ct2", megabytes=0.5)

    assert models.is_downloaded("large-v3-turbo") is False


def test_the_check_does_not_demand_files_the_download_never_fetches(tmp_path, monkeypatch):
    """
    The bug that actually broke it.

    The download deliberately skips the README, the .gitattributes and the original PyTorch
    checkpoint, because CTranslate2 has no use for them. Asking huggingface_hub whether the
    *complete* snapshot is cached therefore always says no — it raises IncompleteSnapshotError
    naming exactly those files — and the model is reported as missing however many times it is
    successfully downloaded.
    """
    from vt_worker import models

    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path))

    # Weights and nothing else, which is precisely what a successful download leaves behind.
    _fake_weights(tmp_path, "deepdml/faster-whisper-large-v3-turbo-ct2", megabytes=1600)

    assert models.is_downloaded("large-v3-turbo") is True


def test_a_broken_copy_can_be_cleared(tmp_path, monkeypatch):
    from vt_worker import models

    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path))
    _fake_weights(tmp_path, "deepdml/faster-whisper-large-v3-turbo-ct2", megabytes=3)

    assert models.clear("large-v3-turbo") is True
    assert models.weights_on_disk("large-v3-turbo") == 0

    # Clearing something that is not there is not an error.
    assert models.clear("large-v3-turbo") is False
