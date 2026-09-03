"""Worker entry point.

Protocol: one job per process. The C# side starts this, writes a single JSON request on stdin,
reads newline-delimited JSON events from stdout, and waits for the process to exit.

One job per process is deliberate. Process exit is the only mechanism that reliably returns
every byte of VRAM to the driver, and it also makes the recorder immune to CUDA contexts being
invalidated across sleep, which is otherwise unrecoverable. Model load costs a few seconds
against a job measured in minutes.

Every line written to stdout is one JSON object:

    {"type": "hello",    "engines": [...], "cuda": {...}}
    {"type": "progress", "id": "...", "stage": "mic", "percent": 42.0}
    {"type": "result",   "id": "...", "segments": [...], "stats": {...}}
    {"type": "error",    "id": "...", "code": "...", "message": "..."}

Diagnostics go to stderr, never stdout, so the stream stays parseable.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import sys
import time
import traceback
from typing import Any

from vt_worker import chunking, dll_paths, gpu
from vt_worker import models
from vt_worker.engines import DEFAULT_ENGINE, EngineError, EngineOptions, create, probe_all
from vt_worker.merge import MergedTranscript, Segment, merge_streams
from vt_worker.segmentation import DEFAULT_MAX_GAP, resegment_on_gaps

# Below this share of the audible speech, a transcript is worth complaining about rather than
# presenting. Set from what a working engine does on a real call: on one measured stretch the
# local engine reached 0.96 and the service 0.96 with its VAD on, 0.69 with it off. Anything
# under four fifths is a transcript with conversation missing from it, not a quiet call.
LOW_COVERAGE = 0.8


def _configure_streams() -> None:
    """Force UTF-8.

    Without this, Turkish characters are mangled: a Windows console defaults to cp1254 or
    cp857 and the dotted and dotless i in particular do not survive the round trip. The C# side
    sets PYTHONIOENCODING as well; doing it here too means the worker is also correct when run
    by hand.
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")


def emit(payload: dict[str, Any]) -> None:
    """Write one protocol line and flush.

    Flushing matters: Python block-buffers stdout when it is not a terminal, so without this
    the parent sees no progress at all until the process exits.
    """
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log(message: str) -> None:
    sys.stderr.write(message + "\n")
    sys.stderr.flush()


def _cuda_report() -> dict[str, Any]:
    dll_paths.register_nvidia_dll_directories()
    report: dict[str, Any] = {"available": False, "device_count": 0}

    try:
        import ctranslate2

        count = ctranslate2.get_cuda_device_count()
        report["available"] = count > 0
        report["device_count"] = count
        report["ctranslate2_version"] = ctranslate2.__version__
    except ImportError as exc:
        report["error"] = f"ctranslate2 not installed: {exc}"
        return report
    except Exception as exc:  # pragma: no cover - depends on the local driver
        report["error"] = str(exc)

    # Named so the interface can show which card is doing the work rather than the word "CUDA".
    # On a laptop with switchable graphics that is the difference between the user believing the
    # discrete card is in use and knowing it.
    devices = gpu.enumerate_devices()
    if devices:
        report["devices"] = [
            {"index": d.index, "name": d.name, "total_memory_mb": d.total_memory_mb}
            for d in devices
        ]
        chosen = gpu.select_device(devices)
        if chosen is not None:
            report["selected_index"] = chosen.index
            report["selected_name"] = chosen.label

    missing = dll_paths.missing_cuda_dlls()
    if missing:
        report["missing_dlls"] = missing
        report["hint"] = (
            "pip install nvidia-cublas-cu12 and make sure the worker imports vt_worker.dll_paths "
            "before ctranslate2. cuDNN is NOT required on ctranslate2 4.6.3 and later."
        )

        # A card the driver reports while the maths library will not load is the failure this
        # whole report exists to catch: the count is not zero, so every naive check passes, and
        # the job dies partway through the first encode instead.
        report["usable"] = False
    else:
        report["usable"] = bool(report.get("available"))

    return report


def cmd_probe(cache_dir: str | None = None) -> int:
    """Report capabilities without loading a model, so the UI can offer real choices."""
    known = ["large-v3-turbo", "large-v3", "medium", "small", "base", "tiny"]

    emit(
        {
            "type": "hello",
            "python": sys.version.split()[0],
            "engines": [dataclasses.asdict(info) for info in probe_all()],
            "cuda": _cuda_report(),
            # So the UI can show which weights are already present rather than making the user
            # discover mid-call that a two-gigabyte download is about to start.
            "downloaded_models": [m for m in known if models.is_downloaded(m, cache_dir)],
        }
    )
    return 0


def _segment_to_json(segment: Segment) -> dict[str, Any]:
    return {
        "speaker": segment.speaker.value,
        "start": round(segment.start, 3),
        "end": round(segment.end, 3),
        "text": segment.text,
        "avg_logprob": segment.avg_logprob,
        "no_speech_prob": segment.no_speech_prob,
        "low_confidence": segment.is_low_confidence,
        "overlaps_other_speaker": segment.overlaps_other_speaker,
        "suspected_echo": segment.suspected_echo,
        "words": [
            {"start": round(w.start, 3), "end": round(w.end, 3), "text": w.text, "p": w.probability}
            for w in segment.words
        ],
    }


def _transcript_to_json(job_id: str, merged: MergedTranscript, meta: dict[str, Any]) -> dict[str, Any]:
    stats = merged.stats
    return {
        "type": "result",
        "id": job_id,
        "segments": [_segment_to_json(s) for s in merged.segments],
        "duration": round(merged.duration, 3),
        "stats": {
            "mic_segments": stats.mic_segments,
            "far_segments": stats.far_segments,
            "overlap_segments": stats.overlap_segments,
            "suspected_echo_segments": stats.suspected_echo_segments,
            "low_confidence_segments": stats.low_confidence_segments,
            "likely_no_headphones": stats.likely_no_headphones,
        },
        **meta,
    }


def cmd_transcribe(request: dict[str, Any]) -> int:
    job_id = str(request.get("id") or "job")
    engine_name = request.get("engine") or DEFAULT_ENGINE
    mic_path = request.get("mic_path")
    far_path = request.get("far_path")

    if not mic_path and not far_path:
        emit({"type": "error", "id": job_id, "code": "bad_request",
              "message": "at least one of mic_path or far_path is required"})
        return 2

    options = EngineOptions(
        model_ref=request.get("model_ref") or "small",
        device=request.get("device") or "auto",
        compute_type=request.get("compute_type") or "auto",
        language=request.get("language") or "tr",
        beam_size=int(request.get("beam_size") or 5),
        word_timestamps=bool(request.get("word_timestamps", True)),
        vad_filter=bool(request.get("vad_filter", True)),
        hotwords=request.get("hotwords") or None,
        multilingual=bool(request.get("multilingual", False)),
    )

    max_gap = float(request.get("resegment_max_gap", DEFAULT_MAX_GAP))

    started = time.monotonic()
    engine = create(engine_name)

    emit({"type": "progress", "id": job_id, "stage": "loading", "percent": 0.0})
    engine.load(options)

    def transcribe_stream(path: str | None, stage: str, weight_from: float, weight_to: float) -> list[Segment]:
        if not path:
            return []

        def on_progress(fraction: float, _stage: str) -> None:
            percent = weight_from + (weight_to - weight_from) * fraction
            emit({"type": "progress", "id": job_id, "stage": stage, "percent": round(percent, 1)})

        raw = engine.transcribe(path, options, on_progress)

        # Whisper merges utterances across silence when the VAD filter removes it, producing
        # segments whose timestamps are minutes away from where the words were actually said.
        # The word timestamps stay accurate, so the turns are cut back apart from those.
        return resegment_on_gaps(raw, max_gap=max_gap)

    # The two streams are transcribed independently and only then merged. Attribution comes
    # from which file a segment was in, so it is a fact rather than a model prediction.
    mic_segments = transcribe_stream(mic_path, "mic", 5.0, 50.0)
    far_segments = transcribe_stream(far_path, "far", 50.0, 95.0)

    emit({"type": "progress", "id": job_id, "stage": "merge", "percent": 97.0})
    merged = merge_streams(mic_segments, far_segments)

    engine.unload()

    # How much of the speech that is audibly there came back with words on it.
    #
    # An engine that invents is obvious; one that goes quiet is not, and for a record of what
    # somebody said the quiet failure is the worse of the two. Measured on one real stretch, the
    # hosted service returned words for 108 of 157 seconds of speech against the local engine's
    # 150 — and the missing 49 seconds ran at the same level as the rest, so nothing in the
    # transcript said they were missing. It is a number now, and a low one is worth acting on.
    coverage = {
        stage: chunking.speech_coverage(path, segments)
        for stage, path, segments in (("mic", mic_path, mic_segments), ("far", far_path, far_segments))
        if path
    }

    for stage, value in coverage.items():
        if value is not None and value < LOW_COVERAGE:
            log(f"{stage}: konuşmanın yalnızca %{value * 100:.0f}'i yazıya döküldü")

    emit(
        _transcript_to_json(
            job_id,
            merged,
            {
                "engine": engine_name,
                "model_ref": options.model_ref,
                "language": options.language,
                "resegment_max_gap": max_gap,
                "elapsed_s": round(time.monotonic() - started, 2),
                "speech_coverage": {
                    stage: round(value, 3) for stage, value in coverage.items() if value is not None
                },
            },
        )
    )
    return 0


def cmd_download(request: dict[str, Any]) -> int:
    """Fetch model weights, reporting progress so a multi-gigabyte pull is not a silent hang."""
    job_id = str(request.get("id") or "download")

    def on_progress(fraction: float, stage: str) -> None:
        emit({"type": "progress", "id": job_id, "stage": stage, "percent": round(fraction * 100, 1)})

    result = models.download(
        request.get("engine") or DEFAULT_ENGINE,
        request.get("model_ref") or "",
        request.get("cache_dir"),
        on_progress,
    )

    emit({"type": "downloaded", "id": job_id, **result})
    return 0


def cmd_selftest(request: dict[str, Any]) -> int:
    """Load a model and run it once, to prove the chain works before a real call depends on it."""
    job_id = str(request.get("id") or "selftest")

    def on_progress(fraction: float, stage: str) -> None:
        emit({"type": "progress", "id": job_id, "stage": stage, "percent": round(fraction * 100, 1)})

    result = models.self_test(
        request.get("engine") or DEFAULT_ENGINE,
        request.get("model_ref") or "",
        request.get("device") or "auto",
        request.get("compute_type") or "auto",
        request.get("language") or "tr",
        on_progress,
    )

    emit({"type": "selftest", "id": job_id, **result})
    return 0


def _read_request() -> dict[str, Any]:
    raw = sys.stdin.read()
    if not raw.strip():
        raise EngineError("bad_request", "no JSON request was supplied on stdin")
    try:
        request = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise EngineError("bad_request", f"request is not valid JSON: {exc}") from exc
    if not isinstance(request, dict):
        raise EngineError("bad_request", "request must be a JSON object")
    return request


def main(argv: list[str] | None = None) -> int:
    _configure_streams()

    parser = argparse.ArgumentParser(prog="vt_worker", description="VoiceTranscript ASR worker")
    parser.add_argument("command", choices=["probe", "transcribe", "download", "selftest"])
    args = parser.parse_args(argv)

    if args.command == "probe":
        # No request body, so the cache comes from the environment — the same environment the
        # download command ends up writing to. These two used to disagree, and the result was a
        # model that downloaded perfectly and was then reported as missing every single time.
        return cmd_probe(models.resolve_cache_dir())

    job_id = "job"
    try:
        request = _read_request()
        job_id = str(request.get("id") or "job")

        if args.command == "download":
            return cmd_download(request)
        if args.command == "selftest":
            return cmd_selftest(request)

        return cmd_transcribe(request)
    except EngineError as exc:
        emit({"type": "error", "id": job_id, "code": exc.code, "message": str(exc)})
        return 1
    except KeyboardInterrupt:
        emit({"type": "error", "id": job_id, "code": "cancelled", "message": "cancelled"})
        return 130
    except Exception as exc:  # pragma: no cover - last resort
        log(traceback.format_exc())
        emit({"type": "error", "id": job_id, "code": "unexpected", "message": str(exc)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
