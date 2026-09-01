"""Fetching model weights, and proving they actually work afterwards.

Two jobs that are easy to conflate and shouldn't be.

Downloading needs to be visible. Weights are one to three gigabytes and, left to the default
behaviour, they are fetched silently the first time a real call is transcribed — so the first
thing the user ever records appears to hang for several minutes with no explanation.

Verifying needs to be honest about what it proves. Loading a model and running it on a short
clip demonstrates that the weights are intact, that CUDA is reachable, and that the whole chain
executes. It says nothing about Turkish accuracy, which can only be measured against real calls
the user has corrected by hand. Reporting a speed figure as though it were a quality figure
would be worse than reporting nothing.
"""

from __future__ import annotations

import math
import os
import shutil
import struct
import tempfile
import time
import wave
from collections.abc import Callable
from pathlib import Path
from typing import Any

from vt_worker import dll_paths
from vt_worker.engines import EngineError, EngineOptions, create

ProgressCallback = Callable[[float, str], None]

SAMPLE_RATE = 16_000


def download(
    engine_name: str,
    model_ref: str,
    cache_dir: str | None = None,
    progress: ProgressCallback | None = None,
) -> dict[str, Any]:
    """Fetch the weights for a model, reporting progress as it goes.

    Downloading is separated from loading on purpose: pulling gigabytes over a slow link and
    initialising CUDA are different failures with different remedies, and a single opaque error
    covering both is much harder to act on.
    """
    dll_paths.register_nvidia_dll_directories()

    if engine_name == "whisper.cpp":
        raise EngineError(
            "not_supported",
            "whisper.cpp modelleri elle indirilmelidir; ggml/gguf dosyasının yolunu verin.",
        )

    try:
        from huggingface_hub import snapshot_download
    except ImportError as exc:  # pragma: no cover - part of the pinned dependency set
        raise EngineError("engine_missing", f"huggingface_hub not installed: {exc}") from exc

    repo = _resolve_repository(model_ref)
    cache_dir = resolve_cache_dir(cache_dir)

    if progress:
        progress(0.0, f"{repo} indiriliyor")

    # A previous attempt that stopped halfway leaves a folder huggingface_hub will happily
    # resume into, producing the same unusable result again. Anything present but too small is
    # therefore removed first rather than trusted.
    existing = weights_on_disk(model_ref, cache_dir)

    if 0 < existing < _MINIMUM_WEIGHTS_BYTES:
        if progress:
            progress(0.0, "yarım kalmış indirme temizleniyor")

        clear(model_ref, cache_dir)

    try:
        path = snapshot_download(
            repo_id=repo,
            cache_dir=cache_dir,
            # Only what CTranslate2 actually loads. A Whisper repository frequently also holds
            # the original PyTorch weights, which are large and completely unused here.
            allow_patterns=["*.bin", "*.json", "*.txt", "*.model"],
        )
    except Exception as exc:
        raise EngineError("download_failed", f"{repo}: {exc}") from exc

    size = sum(f.stat().st_size for f in Path(path).rglob("*") if f.is_file())

    # Verified rather than assumed. A transfer that ends early still returns a path, and
    # reporting that as a success is how a missing model turns into a failed call weeks later.
    weights = weights_on_disk(model_ref, cache_dir)

    if weights < _MINIMUM_WEIGHTS_BYTES:
        clear(model_ref, cache_dir)
        raise EngineError(
            "download_incomplete",
            f"{repo}: ağırlıklar eksik indi ({weights / 1_000_000:.0f} MB). "
            "Yarım kalan dosyalar silindi, tekrar denenebilir.",
        )

    if progress:
        progress(1.0, "indirildi")

    return {
        "repository": repo,
        "path": str(path),
        "size_mb": round(size / 1_000_000, 1),
    }


def resolve_cache_dir(cache_dir: str | None = None) -> str | None:
    """Where model weights live, decided in exactly one place.

    This function exists because of a bug that made the application unusable while looking
    perfectly healthy. The download command was given an explicit directory and wrote weights
    into it; the probe command was called without one and fell back to the environment. The
    environment variable in use was ``HF_HOME``, which is *not* the cache — huggingface_hub puts
    its cache in ``$HF_HOME/hub``. So a 1.6 GB download landed in one folder, the check that asks
    "is it downloaded" looked in another, and the answer was always no. The model downloaded
    successfully, every time, forever.

    Everything now goes through here, and the environment is set so that a caller who passes
    nothing gets exactly the same answer as one who passes the directory.
    """
    if cache_dir:
        # Applied to the environment as well, so any code path that reads it rather than taking
        # the argument — inside huggingface_hub, or a future command — agrees with this one.
        os.environ["HF_HUB_CACHE"] = cache_dir
        return cache_dir

    return os.environ.get("HF_HUB_CACHE") or None


# faster-whisper accepts short aliases and resolves them to the official conversions itself.
# Spelling the mapping out here means the download step and the load step agree on exactly
# which repository is involved, and the UI can show it.
_ALIASES = {
    "tiny": "Systran/faster-whisper-tiny",
    "base": "Systran/faster-whisper-base",
    "small": "Systran/faster-whisper-small",
    "medium": "Systran/faster-whisper-medium",
    "large-v2": "Systran/faster-whisper-large-v2",
    "large-v3": "Systran/faster-whisper-large-v3",
    "large-v3-turbo": "deepdml/faster-whisper-large-v3-turbo-ct2",
}


def _resolve_repository(model_ref: str) -> str:
    """Turn a short alias into the repository it refers to. Anything else is passed through."""
    return _ALIASES.get(model_ref, model_ref)


# A converted Whisper model is tens of megabytes at the very smallest. Anything under this is a
# metadata stub or an interrupted download, not usable weights.
_MINIMUM_WEIGHTS_BYTES = 20_000_000


def _repo_folder(cache_dir: str, repo: str) -> Path:
    """The folder huggingface_hub keeps one repository in.

    The layout is fixed and documented: ``models--<org>--<name>`` under the cache root. Building
    it here rather than asking the library is deliberate — see is_downloaded for why.
    """
    return Path(cache_dir) / ("models--" + repo.replace("/", "--"))


def weights_on_disk(model_ref: str, cache_dir: str | None = None) -> int:
    """Size in bytes of the largest weights file present, or 0 if there are none."""
    resolved = resolve_cache_dir(cache_dir)
    if not resolved:
        return 0

    folder = _repo_folder(resolved, _resolve_repository(model_ref))
    if not folder.is_dir():
        return 0

    largest = 0

    for candidate in folder.rglob("*.bin"):
        try:
            if not candidate.is_file():
                continue

            size = candidate.stat().st_size
            if size > largest:
                largest = size
        except OSError:
            # A file being written by a download in progress. Not knowing its size is fine.
            continue

    return largest


def is_downloaded(model_ref: str, cache_dir: str | None = None) -> bool:
    """Whether usable weights are already on disk, without touching the network.

    Looks at the filesystem rather than asking huggingface_hub, and that choice is the whole
    point of this function.

    The obvious implementation — ``snapshot_download(local_files_only=True)`` — asks whether the
    *complete* repository is cached. It never is, because the download deliberately fetches only
    the weights and skips the README, the .gitattributes and the original PyTorch checkpoint that
    CTranslate2 has no use for. So the library raises IncompleteSnapshotError, the caller reads
    that as "not present", and a model that downloaded perfectly is reported as missing every
    single time, forever. That is exactly what happened, and it made the application look broken
    while every individual piece of it worked.

    The question actually being asked is much smaller: are there usable weights on this disk. A
    converted Whisper model is a single .bin of at least tens of megabytes, so that is what is
    checked. Anything smaller is a metadata stub or an interrupted transfer.
    """
    return weights_on_disk(model_ref, cache_dir) >= _MINIMUM_WEIGHTS_BYTES


def clear(model_ref: str, cache_dir: str | None = None) -> bool:
    """Deletes a repository from the cache so the next download starts clean.

    Needed because a partial transfer leaves something that looks almost right: the folder is
    there, some files are there, and huggingface_hub will happily resume into it and produce the
    same broken result again. When the weights on disk are the wrong size the only reliable
    remedy is to remove the folder and start over.
    """
    resolved = resolve_cache_dir(cache_dir)
    if not resolved:
        return False

    folder = _repo_folder(resolved, _resolve_repository(model_ref))
    if not folder.is_dir():
        return False

    shutil.rmtree(folder, ignore_errors=True)
    return not folder.is_dir()


def _write_probe_wav(path: str, seconds: float = 3.0) -> None:
    """Write a short tone with silence either side.

    Speech would be better, but bundling a Turkish sample would only test the sample. What this
    has to establish is that the model loads, the device is reachable and the chain runs end to
    end — and a synthetic clip does that without shipping anything or pretending to measure
    accuracy it cannot measure.
    """
    frames = int(SAMPLE_RATE * seconds)
    quiet = int(SAMPLE_RATE * 0.5)
    samples = bytearray()

    for i in range(frames):
        if i < quiet or i > frames - quiet:
            value = 0
        else:
            # A quiet 220 Hz tone. Loud enough to be audio, not loud enough to invite the model
            # to hallucinate words onto it.
            value = int(3000 * math.sin(2 * math.pi * 220 * i / SAMPLE_RATE))
        samples += struct.pack("<h", value)

    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(bytes(samples))


def self_test(
    engine_name: str,
    model_ref: str,
    device: str = "auto",
    compute_type: str = "auto",
    language: str = "tr",
    progress: ProgressCallback | None = None,
) -> dict[str, Any]:
    """Load a model and run it once, reporting what that did and did not establish."""
    if progress:
        progress(0.1, "model yükleniyor")

    options = EngineOptions(
        model_ref=model_ref,
        device=device,
        compute_type=compute_type,
        language=language,
        # Off for the probe: the clip is deliberately near-silent, and the filter would remove it
        # entirely, leaving nothing to prove the decoder ran.
        vad_filter=False,
    )

    engine = create(engine_name)

    load_started = time.monotonic()
    engine.load(options)
    load_seconds = time.monotonic() - load_started

    if progress:
        progress(0.6, "deneme kaydı işleniyor")

    with tempfile.TemporaryDirectory() as directory:
        wav_path = str(Path(directory) / "probe.wav")
        _write_probe_wav(wav_path)

        transcribe_started = time.monotonic()
        segments = engine.transcribe(wav_path, options)
        transcribe_seconds = time.monotonic() - transcribe_started

    engine.unload()

    if progress:
        progress(1.0, "tamamlandı")

    audio_seconds = 3.0
    speed = audio_seconds / transcribe_seconds if transcribe_seconds > 0 else 0.0

    # Whisper invents words when handed something that is not speech. Seeing that here is not a
    # failure of the installation, but it is worth surfacing, because it is exactly why the
    # recorder runs with the VAD filter on and hallucination suppression enabled.
    hallucinated = [s.text for s in segments if s.text.strip()]

    return {
        "engine": engine_name,
        "model_ref": model_ref,
        "repository": _resolve_repository(model_ref),
        # What it RESOLVED to, not what was asked for. Reporting the request back ("auto") was
        # the answer to a question nobody had, and this is the field the "Sına" button shows.
        # int8_float16 cannot run on a processor, so the pair together is proof of where the
        # work happened.
        "device": getattr(engine, "device", device),
        "compute_type": getattr(engine, "compute_type", compute_type),
        "requested_device": device,
        "load_seconds": round(load_seconds, 2),
        "transcribe_seconds": round(transcribe_seconds, 2),
        "speed_factor": round(speed, 1),
        "hallucinated_on_silence": hallucinated,
        # Said plainly so the number is not mistaken for a quality measurement.
        "note": (
            "Bu sınama modelin yüklendiğini ve çalıştığını gösterir. Türkçe doğruluğu hakkında "
            "bir şey söylemez; onu ancak kendi kayıtlarınızı elle düzeltip ölçerek bilebilirsiniz."
        ),
    }
