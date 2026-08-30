"""Transcription through a hosted API instead of the local GPU.

Speaks the OpenAI audio-transcriptions shape, so the same code reaches OpenAI, Groq and anything
else that copied that endpoint. Useful when the machine has no usable GPU, or when a stronger
model than a 6 GB card can hold is worth paying for.

Four things about this endpoint shape the caller cannot ignore, and all four are the difference
between "works on a two-minute test" and "works on a real conversation":

  **A hard file-size limit**, 25 MB on OpenAI. Our format is 16 kHz mono 16-bit WAV, which is
  1.92 MB per minute, so a call longer than about thirteen minutes simply will not upload. That
  is not an edge case for phone conversations, it is the normal case. Audio is re-encoded to
  Opus before upload, which brings an hour down to roughly ten megabytes.

  **Long requests fail in the middle.** Even under the size limit, a single request carrying an
  hour of audio is a long-lived connection against a rate-limited service, and when it fails it
  takes the whole hour with it. Uploads are therefore capped by duration as well as size, cut at
  quiet moments so no word is ever split, and each piece is retried on its own.

  **Rate limits are normal, not exceptional.** 429 is an ordinary response, and the service
  usually says how long to wait. Treating it as a failure loses conversations that would have
  succeeded twenty seconds later.

  **Word timestamps have to be asked for explicitly**, and only in verbose_json. Without them
  every quote in the ledger loses its position in the audio, which is the one thing that makes
  the analysis checkable.
"""

from __future__ import annotations

import json
import os
import shutil
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from vt_worker.chunking import plan_chunks, slice_wav
from vt_worker.engines.base import (
    AsrEngine,
    EngineError,
    EngineInfo,
    EngineOptions,
    ProgressCallback,
)
from vt_worker.merge import Segment, Speaker, Word

# OpenAI rejects anything larger. Others differ, but staying under the strictest limit means one
# code path rather than a per-provider table that goes stale.
MAX_UPLOAD_BYTES = 24 * 1024 * 1024

# Opus at 24 kbps mono is roughly 10 MB per hour and is designed for speech at exactly this
# bandwidth, so the accuracy cost is small compared to what the size saving buys.
OPUS_BITRATE = 24_000

# Ceiling on one upload, in seconds of audio.
#
# Twenty minutes rather than "as much as fits". Several providers cap duration separately from
# size; a shorter request is far less likely to be cut off mid-flight; a failure costs one piece
# instead of the whole call; and it is the only way to report honest progress on a long
# conversation instead of a bar that sits still for four minutes.
MAX_CHUNK_SECONDS = 1200.0

# Retry schedule. Five attempts spanning roughly a minute of waiting, which covers the ordinary
# rate-limit window without leaving somebody staring at a stuck job for an hour.
MAX_ATTEMPTS = 5
BASE_BACKOFF_SECONDS = 2.0
MAX_BACKOFF_SECONDS = 60.0

# Statuses worth trying again. Everything else — a bad key, a wrong model name, a malformed
# request — will fail identically forever, and retrying only delays telling the user why.
RETRYABLE_STATUS = frozenset({408, 409, 425, 429, 500, 502, 503, 504})


def _sleep(seconds: float) -> None:
    time.sleep(seconds)


class CloudWhisperEngine(AsrEngine):
    """Uploads audio to an OpenAI-compatible transcription endpoint."""

    name = "cloud-openai"

    def __init__(self) -> None:
        self._base_url = ""
        self._api_key = ""
        self._model = "whisper-1"
        self._timeout = 600

    @classmethod
    def probe(cls) -> EngineInfo:
        return EngineInfo(
            name=cls.name,
            available=True,
            version="http",
            detail="Ses bir API'ye yüklenir. Ekran kartı gerekmez.",
        )

    def load(self, options: EngineOptions) -> None:
        # model_ref carries everything the endpoint needs: "base_url|api_key|model".
        parts = (options.model_ref or "").split("|")
        if len(parts) < 3:
            raise EngineError(
                "bad_config",
                "Bulut motoru için adres, anahtar ve model adı gerekiyor.",
            )

        self._base_url = parts[0].rstrip("/")
        self._api_key = parts[1]
        self._model = parts[2]

    def transcribe(
        self,
        wav_path: str,
        options: EngineOptions,
        progress: ProgressCallback | None = None,
    ) -> list[Segment]:
        if not self._base_url:
            raise EngineError("not_loaded", "load() must be called before transcribe()")

        chunks = plan_chunks(wav_path, MAX_CHUNK_SECONDS)
        workspace = self._workspace(wav_path)

        try:
            segments: list[Segment] = []

            for chunk in chunks:
                if progress:
                    progress(
                        0.02 + 0.94 * chunk.index / len(chunks),
                        f"{chunk.index + 1}/{len(chunks)} yükleniyor",
                    )

                segments.extend(self._chunk_segments(wav_path, chunk, options, workspace, len(chunks)))

            if progress:
                progress(1.0, "tamamlandı")

            # Only once everything succeeded. A workspace left behind after a failure is what
            # makes the next attempt cheap: finished pieces are not uploaded twice.
            shutil.rmtree(workspace, ignore_errors=True)

            return segments
        except EngineError:
            raise
        except Exception as exc:  # pragma: no cover - defensive
            raise EngineError("cloud_failed", str(exc)) from exc

    # ---- per-chunk work ----------------------------------------------------

    @staticmethod
    def _workspace(wav_path: str) -> str:
        """
        A folder beside the recording holding finished pieces.

        Persistent rather than temporary so that a job interrupted by a rate limit, a dropped
        connection or a closed application resumes instead of restarting. Re-uploading forty
        minutes of audio that already transcribed cleanly costs real money and real time.
        """
        workspace = f"{wav_path}.cloudparts"
        os.makedirs(workspace, exist_ok=True)
        return workspace

    def _chunk_segments(
        self,
        wav_path: str,
        chunk,
        options: EngineOptions,
        workspace: str,
        total_chunks: int,
    ) -> list[Segment]:
        # The model is part of the key: changing model must not reuse the old model's answers.
        cache = os.path.join(workspace, f"{self._model}-{chunk.index}-{total_chunks}.json")

        if os.path.exists(cache):
            try:
                with open(cache, encoding="utf-8") as handle:
                    return _to_segments(json.load(handle), chunk.start_seconds)
            except (OSError, json.JSONDecodeError):
                os.unlink(cache)  # corrupt, fetch it again

        if total_chunks == 1:
            source = wav_path
        else:
            source = os.path.join(workspace, f"part{chunk.index}.wav")
            if not os.path.exists(source):
                slice_wav(wav_path, source, chunk.start_seconds, chunk.end_seconds)

        upload = self._compress(source, workspace, suffix=f"-{chunk.index}")
        payload = self._post_with_retry(upload, options)

        try:
            with open(cache, "w", encoding="utf-8") as handle:
                json.dump(payload, handle)
        except OSError:
            pass  # the cache is an optimisation, never a requirement

        # The slice and its compressed copy are large and no longer needed once the answer is
        # cached; the answer is what makes a resume cheap.
        for path in (source, upload):
            if path != wav_path and os.path.exists(path):
                try:
                    os.unlink(path)
                except OSError:
                    pass

        return _to_segments(payload, chunk.start_seconds)

    def _compress(self, wav_path: str, workspace: str, suffix: str = "") -> str:
        """Re-encode to Opus. Falls back to the original WAV if encoding is unavailable."""
        target = os.path.join(workspace, f"upload{suffix}.ogg")

        try:
            import av
        except ImportError:
            return wav_path

        try:
            with av.open(wav_path) as source, av.open(target, mode="w", format="ogg") as output:
                stream = output.add_stream("libopus", rate=16_000)
                stream.bit_rate = OPUS_BITRATE

                for frame in source.decode(audio=0):
                    frame.pts = None
                    for packet in stream.encode(frame):
                        output.mux(packet)

                for packet in stream.encode(None):
                    output.mux(packet)

            return target
        except Exception:
            # An encoder that is missing or unhappy is not worth failing the job over; the
            # uncompressed file may still be small enough, and the size check catches it if not.
            return wav_path

    def _post_with_retry(self, path: str, options: EngineOptions) -> dict:
        last: EngineError | None = None

        for attempt in range(MAX_ATTEMPTS):
            try:
                return self._post(path, options)
            except _Retryable as exc:
                last = EngineError(exc.code, exc.message)

                if attempt == MAX_ATTEMPTS - 1:
                    break

                # The service usually says how long to wait. Believing it is both faster and
                # politer than a fixed schedule that ignores what it told us.
                delay = exc.retry_after if exc.retry_after is not None else (
                    BASE_BACKOFF_SECONDS * (2**attempt)
                )
                _sleep(min(delay, MAX_BACKOFF_SECONDS))

        raise last or EngineError("api_error", "İstek tekrar tekrar başarısız oldu.")

    def _post(self, path: str, options: EngineOptions) -> dict:
        size = os.path.getsize(path)
        if size > MAX_UPLOAD_BYTES:
            raise EngineError(
                "too_large",
                f"Ses parçası sıkıştırıldıktan sonra bile çok büyük ({size // 1_000_000} MB). "
                "Opus kodlayıcı (PyAV) kurulu olmayabilir.",
            )

        fields = {
            "model": self._model,
            "language": options.language,
            # verbose_json is the only format that carries timing at all, and word granularity
            # has to be requested on top of it. Without both, every quote loses its position.
            "response_format": "verbose_json",
            "timestamp_granularities[]": "word",
        }

        body, content_type = _multipart(fields, Path(path))

        request = urllib.request.Request(
            f"{self._base_url}/audio/transcriptions",
            data=body,
            method="POST",
            headers={
                "Authorization": f"Bearer {self._api_key}",
                "Content-Type": content_type,
            },
        )

        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:400]

            if exc.code in RETRYABLE_STATUS:
                raise _Retryable(
                    "rate_limited" if exc.code == 429 else "api_error",
                    f"{exc.code}: {detail}",
                    _retry_after(exc),
                ) from exc

            if exc.code in (401, 403):
                raise EngineError(
                    "auth",
                    f"API anahtarı kabul edilmedi ({exc.code}). Ayarlardan anahtarı denetle.",
                ) from exc

            raise EngineError("api_error", f"{exc.code}: {detail}") from exc
        except urllib.error.URLError as exc:
            # A dropped connection mid-upload is ordinary on a laptop that moved between
            # networks, and is exactly the case retrying exists for.
            raise _Retryable("network", f"Sunucuya ulaşılamadı: {exc.reason}", None) from exc
        except TimeoutError as exc:
            raise _Retryable("timeout", "İstek zaman aşımına uğradı.", None) from exc

    def unload(self) -> None:
        self._api_key = ""


class _Retryable(Exception):
    """An failure that is worth another attempt, with the wait the server asked for."""

    def __init__(self, code: str, message: str, retry_after: float | None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retry_after = retry_after


def _retry_after(exc: urllib.error.HTTPError) -> float | None:
    raw = exc.headers.get("Retry-After") if exc.headers else None
    if not raw:
        return None

    try:
        # Seconds is the form every one of these services uses; the HTTP-date form is legal but
        # unheard of here, and guessing at it wrong would be worse than falling back.
        return max(0.0, float(raw))
    except (TypeError, ValueError):
        return None


def _to_segments(payload: dict, offset: float) -> list[Segment]:
    """
    Turns one API response into segments on the call timeline.

    The offset is what makes chunking safe. Each piece is transcribed on its own and comes back
    with timings that start at zero, so without this every quote after the first chunk would
    point at the wrong moment — and a quote that plays the wrong audio is worse than no quote.

    The speaker is set to ME here and overwritten by merge_streams, exactly as the local engines
    do it: which stream this was is the caller's knowledge, not the transcriber's.
    """
    words_payload = payload.get("words") or []

    words = [
        Word(
            start=float(word.get("start", 0.0)) + offset,
            end=float(word.get("end", 0.0)) + offset,
            text=str(word.get("word", "")),
            probability=None,
        )
        for word in words_payload
        if word.get("word")
    ]

    raw_segments = payload.get("segments") or []

    if raw_segments:
        segments = []

        for item in raw_segments:
            start = float(item.get("start", 0.0)) + offset
            end = float(item.get("end", start)) + offset

            segments.append(
                Segment(
                    speaker=Speaker.ME,  # overwritten by merge_streams
                    start=start,
                    end=end,
                    text=str(item.get("text", "")).strip(),
                    avg_logprob=_as_float(item.get("avg_logprob")),
                    no_speech_prob=_as_float(item.get("no_speech_prob")),
                    words=[w for w in words if start <= w.start < end],
                )
            )

        return segments

    # Some providers return only the flat text. One correctly placed segment beats losing the
    # call, and the offset still lines it up against the other stream.
    text = str(payload.get("text", "")).strip()
    if not text:
        return []

    return [
        Segment(
            speaker=Speaker.ME,  # overwritten by merge_streams
            start=offset,
            end=words[-1].end if words else offset,
            text=text,
            words=words,
        )
    ]


def _as_float(value: object) -> float | None:
    """
    Keeps the confidence figures the analysis depends on, without inventing any.

    Low-confidence lines are excluded from automatic contradiction detection, because an ASR
    error that turns one number into another would otherwise manufacture a false accusation
    against a real person. A provider that reports nothing must therefore stay None rather than
    be given a flattering default.
    """
    if value is None:
        return None

    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _multipart(fields: dict[str, str], file_path: Path) -> tuple[bytes, str]:
    """
    Builds a multipart body by hand.

    The worker deliberately has no HTTP client dependency: this environment is pinned around
    CTranslate2 and adding a package for one upload is how a working CUDA install gets broken.
    """
    boundary = f"----vt{uuid.uuid4().hex}"
    parts: list[bytes] = []

    for name, value in fields.items():
        parts.append(
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="{name}"\r\n\r\n'
            f"{value}\r\n".encode()
        )

    parts.append(
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{file_path.name}"\r\n'
        f"Content-Type: application/octet-stream\r\n\r\n".encode()
    )
    parts.append(file_path.read_bytes())
    parts.append(f"\r\n--{boundary}--\r\n".encode())

    return b"".join(parts), f"multipart/form-data; boundary={boundary}"
