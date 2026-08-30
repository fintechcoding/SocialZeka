"""whisper.cpp backend.

Kept as a genuine second option rather than a formality. It matters in two situations the
default cannot cover: when the CTranslate2 CUDA stack will not install cleanly on a machine,
and when there is no NVIDIA GPU at all. It is slower than CTranslate2 on CUDA, so it is not the
default, but it runs the same weights and produces comparable Turkish accuracy.

The dependency is optional. If pywhispercpp is not installed the engine reports itself as
unavailable and the UI simply does not offer it.
"""

from __future__ import annotations

import wave
from contextlib import closing

from vt_worker.engines.base import (
    AsrEngine,
    EngineError,
    EngineInfo,
    EngineOptions,
    ProgressCallback,
)
from vt_worker.merge import Segment, Speaker


def _wav_duration_seconds(path: str) -> float:
    try:
        with closing(wave.open(path, "rb")) as wav:
            rate = wav.getframerate()
            return wav.getnframes() / rate if rate else 0.0
    except (OSError, wave.Error):
        return 0.0


class WhisperCppEngine(AsrEngine):
    name = "whisper.cpp"

    def __init__(self) -> None:
        self._model = None

    @classmethod
    def probe(cls) -> EngineInfo:
        try:
            import pywhispercpp  # noqa: F401
            from pywhispercpp.model import Model  # noqa: F401
        except ImportError as exc:
            return EngineInfo(
                cls.name,
                available=False,
                detail=f"not installed ({exc}). Optional: pip install pywhispercpp",
            )

        return EngineInfo(
            cls.name,
            available=True,
            version=getattr(pywhispercpp, "__version__", None),
            detail="ggml/gguf weights, runs on CPU as well as CUDA",
        )

    def load(self, options: EngineOptions) -> None:
        try:
            from pywhispercpp.model import Model
        except ImportError as exc:
            raise EngineError(
                "engine_missing",
                f"pywhispercpp is not installed: {exc}. Install it or choose the faster-whisper engine.",
            ) from exc

        try:
            self._model = Model(
                options.model_ref,
                language=options.language,
                print_realtime=False,
                print_progress=False,
            )
        except Exception as exc:
            raise EngineError("model_load_failed", str(exc)) from exc

    def transcribe(
        self,
        wav_path: str,
        options: EngineOptions,
        progress: ProgressCallback | None = None,
    ) -> list[Segment]:
        if self._model is None:
            raise EngineError("not_loaded", "load() must be called before transcribe()")

        total = _wav_duration_seconds(wav_path)

        try:
            raw_segments = self._model.transcribe(wav_path)
        except Exception as exc:
            raise EngineError("transcribe_failed", str(exc)) from exc

        results: list[Segment] = []
        for raw in raw_segments:
            text = (getattr(raw, "text", "") or "").strip()
            if not text:
                continue

            # pywhispercpp reports centiseconds, unlike faster-whisper which reports seconds.
            start = float(getattr(raw, "t0", 0)) / 100.0
            end = float(getattr(raw, "t1", 0)) / 100.0

            results.append(
                Segment(
                    speaker=Speaker.ME,  # overwritten by merge_streams
                    start=start,
                    end=end,
                    text=text,
                )
            )

            if progress and total > 0:
                progress(min(1.0, end / total), "transcribing")

        if progress:
            progress(1.0, "transcribing")

        return results

    def unload(self) -> None:
        self._model = None
