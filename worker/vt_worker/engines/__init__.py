"""Engine registry.

Engines are looked up by name so the C# side can offer a choice and the same recording can be
run through more than one for comparison.
"""

from __future__ import annotations

from vt_worker.engines.base import AsrEngine, EngineError, EngineInfo, EngineOptions
from vt_worker.engines.cloud_engine import CloudWhisperEngine
from vt_worker.engines.cloud_providers import DeepgramEngine, ElevenLabsEngine
from vt_worker.engines.ex5_engine import Ex5WhisperEngine
from vt_worker.engines.faster_whisper_engine import FasterWhisperEngine
from vt_worker.engines.whispercpp_engine import WhisperCppEngine

_ENGINES: dict[str, type[AsrEngine]] = {
    FasterWhisperEngine.name: FasterWhisperEngine,
    WhisperCppEngine.name: WhisperCppEngine,
    CloudWhisperEngine.name: CloudWhisperEngine,
    ElevenLabsEngine.name: ElevenLabsEngine,
    DeepgramEngine.name: DeepgramEngine,
    Ex5WhisperEngine.name: Ex5WhisperEngine,
}

DEFAULT_ENGINE = FasterWhisperEngine.name


def create(name: str) -> AsrEngine:
    try:
        return _ENGINES[name]()
    except KeyError:
        known = ", ".join(sorted(_ENGINES))
        raise EngineError("unknown_engine", f"Unknown engine '{name}'. Known: {known}") from None


def probe_all() -> list[EngineInfo]:
    """Report which engines can actually run here. Never raises."""
    infos: list[EngineInfo] = []
    for name, cls in _ENGINES.items():
        try:
            infos.append(cls.probe())
        except Exception as exc:  # pragma: no cover - a probe must never take the worker down
            infos.append(EngineInfo(name, available=False, detail=f"probe failed: {exc}"))
    return infos


__all__ = [
    "DEFAULT_ENGINE",
    "AsrEngine",
    "EngineError",
    "EngineInfo",
    "EngineOptions",
    "create",
    "probe_all",
]
