"""Engine abstraction.

Several transcription backends are supported on purpose. They differ in Turkish accuracy, in
speed, and in how much of the CUDA toolchain they drag along, and none of them is best in every
situation. Keeping them behind one interface lets the same recording be run through two engines
and compared, which is the only honest way to pick one.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Callable, Sequence
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

    # The user's own vocabulary: product names, people, jargon. Biases every decoding window,
    # so "Sumsub" wins against "sum sub" where the audio is ambiguous. May be None.
    #
    # There was an initial_prompt beside this, carrying the same terms, and it is gone. The two
    # are not one feature spelled twice: hotwords is a weighting, and a wrong term simply never
    # wins; a prompt is text the decoder is told it has already written, so it continues the
    # style of it. The terms were a comma-separated list of capitalised words, and the model went
    # on writing that list instead of the conversation — measured against one real recording, the
    # same 180 seconds with and without, on the hosted service and on the local engine both.
    hotwords: str | None = None

    # Detect the language per window instead of once per file, for calls that switch between
    # Turkish and English mid-sentence. Slower, and only the large models can do it.
    multilingual: bool = False

    # Whether the hosted service should apply its loudness normalisation to this channel.
    #
    # Per channel and not per call, because the two sides of a conversation are different kinds
    # of signal: the loopback stream is written by the audio stack and is digitally silent
    # between words, while a microphone is a live input that always hears the room.
    #
    # None means "do not send the field", leaving the service on its own default. Engines that
    # have no such control ignore it. See chunking.prefers_gain for how the value is arrived at
    # and what it was measured against.
    normalize: bool | None = None


@dataclass(slots=True)
class EngineInfo:
    name: str
    available: bool
    version: str | None = None
    detail: str = ""


class AsrEngine(ABC):
    """One transcription backend. Instances are single-use: load, transcribe, exit."""

    name: str = "abstract"

    #: Whether this engine can act on :attr:`EngineOptions.normalize`.
    #:
    #: False everywhere but the ex5 engine, which is the only one with a service-side loudness
    #: step to ask about. It exists so the caller can tell "the gain was wrong" from "there is no
    #: gain to be wrong": a poor result is worth a second attempt only if the second attempt would
    #: differ. Without this the retry re-sent a byte-identical request and doubled the work of
    #: every local transcription that happened to score low.
    honours_normalize: bool = False

    #: Non-speech the last :meth:`transcribe` heard — laughter, applause — on the call timeline,
    #: each ``{"start_ms", "end_ms", "kind"}`` with the kind in lower-case ASCII.
    #:
    #: Empty unless the service tags them, which today only ElevenLabs does. The local engines
    #: never assign it, so the shared default is an immutable empty tuple on purpose; an engine
    #: that fills it replaces it per transcribe() rather than appending to this one.
    audio_events: Sequence[dict] = ()

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
