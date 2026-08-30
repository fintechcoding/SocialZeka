"""Engine abstraction.

Several transcription backends are supported on purpose. They differ in Turkish accuracy, in
speed, and in how much of the CUDA toolchain they drag along, and none of them is best in every
situation. Keeping them behind one interface lets the same recording be run through two engines
and compared, which is the only honest way to pick one.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Callable
from dataclasses import dataclass

from vt_worker.merge import Segment

ProgressCallback = Callable[[float, str], None]


@dataclass(slots=True)
class EngineOptions:
    model_ref: str
    device: str = "auto"          # auto | cuda | cpu
    compute_type: str = "auto"    # auto | float16 | int8_float16 | int8
    language: str = "tr"
    beam_size: int = 5

    # Word timestamps are what make "click a line, hear that moment" possible, and they anchor
    # every quote the analysis layer is allowed to make.
    word_timestamps: bool = True

    # Whisper invents text when fed silence. Both of these suppress that, and the recorder
    # produces a lot of silence because it captures the whole call rather than just speech.
    vad_filter: bool = True
    condition_on_previous_text: bool = False
    no_speech_threshold: float = 0.6


@dataclass(slots=True)
class EngineInfo:
    name: str
    available: bool
    version: str | None = None
    detail: str = ""


class AsrEngine(ABC):
    """One transcription backend. Instances are single-use: load, transcribe, exit."""

    name: str = "abstract"

    @abstractmethod
    def load(self, options: EngineOptions) -> None:
        """Bring the model into memory. May take several seconds."""

    @abstractmethod
    def transcribe(
        self,
        wav_path: str,
        options: EngineOptions,
        progress: ProgressCallback | None = None,
    ) -> list[Segment]:
        """Transcribe one mono 16 kHz WAV file into time-stamped segments."""

    def unload(self) -> None:
        """Release the model.

        Deliberately best-effort. The reliable way to return every byte of VRAM to the driver
        is process exit, which is why the worker runs one job per process and then dies. In
        particular, torch.cuda.empty_cache() does nothing for CTranslate2, which does not use
        torch and keeps its own caching allocator.
        """

    @classmethod
    @abstractmethod
    def probe(cls) -> EngineInfo:
        """Report whether this engine can run here, without loading a model."""


class EngineError(RuntimeError):
    """Raised with a code the C# side can branch on."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
