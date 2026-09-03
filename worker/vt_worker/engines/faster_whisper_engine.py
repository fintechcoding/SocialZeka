"""faster-whisper / CTranslate2 backend. The default and fastest CUDA path."""

from __future__ import annotations

import itertools
import os
import sys
import wave
from contextlib import closing

from vt_worker import dll_paths, gpu
from vt_worker.engines.base import (
    AsrEngine,
    EngineError,
    EngineInfo,
    EngineOptions,
    ProgressCallback,
)
from vt_worker.merge import Segment, Speaker, Word


def _is_cuda_runtime_failure(message: str) -> bool:
    """Whether an error is the GPU stack giving out rather than the audio being bad.

    Matched on the message because CTranslate2 raises a plain RuntimeError for all of these and
    there is no type to catch. The strings are the ones it actually produces: a missing or
    unloadable library, an exhausted card, and a context invalidated by the machine sleeping.
    """
    lowered = message.lower()
    return any(
        marker in lowered
        for marker in ("cublas", "cudnn", "cuda", "out of memory", "no kernel image")
    )


def _wav_duration_seconds(path: str) -> float:
    try:
        with closing(wave.open(path, "rb")) as wav:
            rate = wav.getframerate()
            return wav.getnframes() / rate if rate else 0.0
    except (OSError, wave.Error):
        return 0.0


def transcribe_kwargs(options: EngineOptions) -> dict:
    """The keyword arguments one transcription gets, from the options the request carried.

    A function rather than a literal in the call so it can be tested without a model: what
    reaches faster-whisper from the user's vocabulary and language choice is exactly the kind
    of thing that silently stops working when a parameter is renamed.
    """
    kwargs = {
        "language": options.language,
        "beam_size": options.beam_size,
        "word_timestamps": options.word_timestamps,
        "vad_filter": options.vad_filter,
        "condition_on_previous_text": options.condition_on_previous_text,
        "no_speech_threshold": options.no_speech_threshold,
    }

    if options.hotwords:
        kwargs["hotwords"] = options.hotwords

    # Per-window language detection needs the language left open; forcing "tr" and asking for
    # code-switching at the same time would be a contradiction faster-whisper resolves by
    # forcing.
    if options.multilingual:
        kwargs["multilingual"] = True
        kwargs["language"] = None

    return kwargs


class FasterWhisperEngine(AsrEngine):
    name = "faster-whisper"

    def __init__(self) -> None:
        self._model = None
        self._device = "cpu"
        self._compute_type = "int8"
        self._device_label: str | None = None

    @classmethod
    def probe(cls) -> EngineInfo:
        dll_paths.register_nvidia_dll_directories()
        try:
            import ctranslate2
            import faster_whisper
        except ImportError as exc:
            return EngineInfo(cls.name, available=False, detail=f"not installed: {exc}")

        detail = ""
        try:
            devices = ctranslate2.get_cuda_device_count()
            detail = f"cuda devices: {devices}"
            if devices == 0:
                missing = dll_paths.missing_cuda_dlls()
                if missing:
                    detail += f"; missing DLLs: {', '.join(missing)}"
        except Exception as exc:  # pragma: no cover - depends on the local CUDA install
            detail = f"cuda probe failed: {exc}"

        return EngineInfo(
            cls.name,
            available=True,
            version=getattr(faster_whisper, "__version__", None) or ctranslate2.__version__,
            detail=detail,
        )

    def load(self, options: EngineOptions) -> None:
        dll_paths.register_nvidia_dll_directories()

        try:
            from faster_whisper import WhisperModel
        except ImportError as exc:
            raise EngineError("engine_missing", f"faster-whisper is not installed: {exc}") from exc

        device, compute_type = self._resolve_device(options)

        # Which card, when there is more than one. Intel and AMD integrated graphics never
        # appear here — CTranslate2 counts CUDA devices and they are not among them — but two
        # NVIDIA cards can be, and index 0 is PCI order rather than the better card.
        index = 0
        if device == "cuda":
            chosen = gpu.select_device()
            if chosen is not None:
                index = chosen.index
                self._device_label = chosen.label
                sys.stderr.write(f"gpu: {chosen.label} (device {chosen.index})\n")
                sys.stderr.flush()

        # On the processor, leave two cores to the person using the machine. CTranslate2's default
        # takes every core it can see, and a 40-minute call then makes the laptop unusable for the
        # twenty minutes it takes; two cores fewer costs about a tenth of the speed.
        cpu_threads = 0
        if device == "cpu":
            cpu_threads = max(1, (os.cpu_count() or 4) - 2)

        try:
            self._model = WhisperModel(
                options.model_ref,
                device=device,
                device_index=index,
                compute_type=compute_type,
                cpu_threads=cpu_threads,
            )
            self._device = device
            self._compute_type = compute_type

            # Said out loud, after the model is really built.
            #
            # The "gpu: ..." line above names the card that was *selected*, which is not the same
            # claim as "the work ran there", and nothing anywhere stated the device finally used
            # or its precision. Somebody watching a flat NVIDIA graph in Task Manager has no way
            # to check — Windows does not show CUDA compute in the default GPU panels at all —
            # and the honest answer to "did it use the graphics card?" should not require reading
            # the source. int8_float16 is itself the proof: CTranslate2 refuses it on a processor.
            sys.stderr.write(
                f"engine ready: device={device} index={index} compute_type={compute_type} "
                f"model={options.model_ref}\n")
            sys.stderr.flush()
        except Exception as exc:
            message = str(exc)
            if "cublas" in message.lower() or "cudnn" in message.lower():
                missing = dll_paths.missing_cuda_dlls()
                hint = (
                    f" Missing CUDA runtime DLLs: {', '.join(missing)}."
                    if missing
                    else " The CUDA runtime DLLs were found, so this is likely a version mismatch."
                )
                raise EngineError("cuda_runtime", message + hint) from exc
            raise EngineError("model_load_failed", message) from exc

    @property
    def device(self) -> str:
        """The device the model was really built on — not the one that was requested.

        These differ, and the difference is the whole question somebody asks when they watch a
        graphics card sit idle. "auto" was being reported back verbatim, which answered nothing.
        """
        return self._device

    @property
    def compute_type(self) -> str:
        """The precision in use. int8_float16 is GPU-only and is itself proof of where this ran."""
        return self._compute_type

    @staticmethod
    def _resolve_device(options: EngineOptions) -> tuple[str, str]:
        """Decides where the model runs, and refuses a GPU that cannot actually do the work.

        ``get_cuda_device_count()`` asks the *driver*. It answers 1 on any machine with a working
        NVIDIA card, whether or not cuBLAS — the library CTranslate2 does its matrix maths with —
        can be loaded. So a machine missing cublas64_12.dll reports CUDA as ready, loads the
        model onto the GPU without complaint, and then dies partway through the first encode with
        "Library cublas64_12.dll is not found or cannot be loaded".

        That failure arrives after the recording is over, which is the worst possible time: the
        conversation is gone and there is nothing left to transcribe again. So cuBLAS is checked
        here, before anything is committed to, and a machine that cannot load it transcribes on
        the processor instead. Slower is not a failure. Losing the conversation is.
        """
        device = options.device
        compute_type = options.compute_type

        if device == "auto":
            try:
                import ctranslate2

                device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
            except Exception:
                device = "cpu"

        if device == "cuda":
            missing = dll_paths.missing_cuda_dlls()
            if missing:
                sys.stderr.write(
                    "cuda unusable: " + ", ".join(missing) + " could not be loaded; "
                    "falling back to the processor. Install nvidia-cublas-cu12 into the worker "
                    "environment to get the GPU back.\n"
                )
                sys.stderr.flush()
                device = "cpu"

                # The chosen precision belongs to the device that was rejected. int8_float16 is
                # a GPU compute type and asking a CPU for it fails outright.
                if compute_type in ("int8_float16", "float16"):
                    compute_type = "auto"

        if compute_type == "auto":
            # int8_float16 roughly halves VRAM against float16 with no meaningful accuracy cost,
            # which is what makes a large model fit next to everything else on a 6 GB card.
            compute_type = "int8_float16" if device == "cuda" else "int8"

        return device, compute_type

    def _start(self, wav_path: str, options: EngineOptions):
        """Begins a transcription, moving to the processor if the GPU gives out.

        faster-whisper is lazy: ``transcribe()`` returns a generator and the model does not touch
        the GPU until the first segment is pulled. A CUDA runtime failure therefore surfaces from
        the *loop*, not from the call, which is why wrapping only the call caught nothing.

        One retry, on the processor. If that fails too the fault is not the device and the error
        should reach the user unchanged rather than being retried into a different message.
        """

        def begin():
            kwargs = transcribe_kwargs(options)
            try:
                return self._model.transcribe(wav_path, **kwargs)
            except TypeError as exc:
                # An older faster-whisper without hotwords/multilingual. The vocabulary is
                # dropped rather than the transcription; said in the log so it is not a mystery.
                dropped = {k: kwargs.pop(k) for k in ("hotwords", "multilingual") if k in kwargs}
                if not dropped:
                    raise
                sys.stderr.write(f"faster-whisper does not accept {sorted(dropped)} ({exc}); continuing without\n")
                sys.stderr.flush()
                if kwargs.get("language") is None:
                    kwargs["language"] = options.language
                return self._model.transcribe(wav_path, **kwargs)

        try:
            segments_iter, info = begin()
            first = next(segments_iter, None)
        except Exception as exc:
            message = str(exc)
            if not _is_cuda_runtime_failure(message) or self._device != "cuda":
                raise

            sys.stderr.write(
                f"gpu transcription failed ({message}); retrying on the processor so the "
                "recording is not lost\n"
            )
            sys.stderr.flush()

            from faster_whisper import WhisperModel

            self._model = WhisperModel(options.model_ref, device="cpu", compute_type="int8")
            self._device = "cpu"

            segments_iter, info = begin()
            first = next(segments_iter, None)

        return (itertools.chain([first], segments_iter) if first is not None else iter(())), info

    def transcribe(
        self,
        wav_path: str,
        options: EngineOptions,
        progress: ProgressCallback | None = None,
    ) -> list[Segment]:
        if self._model is None:
            raise EngineError("not_loaded", "load() must be called before transcribe()")

        total = _wav_duration_seconds(wav_path)

        segments_iter, _info = self._start(wav_path, options)

        results: list[Segment] = []
        for raw in segments_iter:
            text = (raw.text or "").strip()
            if text:
                results.append(
                    Segment(
                        speaker=Speaker.ME,  # overwritten by merge_streams
                        start=float(raw.start),
                        end=float(raw.end),
                        text=text,
                        avg_logprob=getattr(raw, "avg_logprob", None),
                        no_speech_prob=getattr(raw, "no_speech_prob", None),
                        words=[
                            Word(
                                start=float(w.start),
                                end=float(w.end),
                                text=w.word,
                                probability=getattr(w, "probability", None),
                            )
                            for w in (getattr(raw, "words", None) or [])
                        ],
                    )
                )

            if progress and total > 0:
                progress(min(1.0, float(raw.end) / total), "transcribing")

        if progress:
            progress(1.0, "transcribing")

        return results

    def unload(self) -> None:
        self._model = None
