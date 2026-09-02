"""Transcription through a hosted API instead of the local GPU.

Speaks the OpenAI audio-transcriptions shape, so the same code reaches OpenAI, Groq and anything
else that copied that endpoint. Useful when the machine has no usable GPU, or when a stronger
model than a 6 GB card can hold is worth paying for.

Four things about this endpoint shape the caller cannot ignore, and all four are the difference
between "works on a two-minute test" and "works on a real conversation":

  **A hard file-size limit**, 25 MB on OpenAI. Our format is 16 kHz mono 16-bit WAV, which is
  1.92 MB per minute, so a call longer than about thirteen minutes simply will not upload. That
  is not an edge case for phone conversations, it is the normal case. Audio is re-encoded to
  Opus before upload, which brings an hour down to roughly thirty megabytes — and, because the
  unit that has to fit is one twenty-minute chunk rather than the call, leaves the bitrate free to
  be chosen for accuracy rather than for size.

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
from vt_worker.speech_only import SpeechSpan, to_original, write_speech_only
from vt_worker.engines.base import (
    AsrEngine,
    EngineError,
    EngineInfo,
    EngineOptions,
    ProgressCallback,
)
from vt_worker.merge import Segment, Speaker, Word

# OpenAI rejects anything larger, and it is the strictest of the services in the catalogue, so it
# is what every engine gets unless it says otherwise. A service that accepts more raises the
# ceiling on its own class rather than here — one number per engine, next to the engine, instead
# of a table in this file that nobody remembers to update when a provider is added.
MAX_UPLOAD_BYTES = 24 * 1024 * 1024

# What the recording actually carries: 16 kHz mono 16-bit PCM.
#
# Nothing above this is quality, it is padding — the information in the file stops here, and an
# encoder asked for more simply writes bigger frames around the same sound.
SOURCE_BITRATE = 16_000 * 2 * 8

# The ceiling worth paying for, and the floor below which the model stops hearing.
#
# The floor is measured, not assumed. One real recording, transcribed four times: at 21.5 kbps it
# came back with 1624 words; at 18.2 kbps, the same audio, 330. Eighty per cent of the conversation
# gone — and not hallucinated, not garbled, simply not heard: no speech found in 520 of its 659
# seconds. Opus at that bitrate is still perfectly clear to a person and has already thrown away
# what the model listens for. The old target was 24 kbps, which Opus undershoots to 18-21 on speech
# with pauses in it, so every upload sat on the wrong side of that cliff by a kbps or two.
#
# The ceiling is where a 16 kHz mono encoder becomes transparent. Above it the bytes buy nothing.
MAX_OPUS_BITRATE = 128_000
MIN_OPUS_BITRATE = 32_000

# How much of a service's limit one upload may use.
#
# The rest is for the multipart envelope and for the next conversation being a little longer than
# this one. A chunk that only just fits is a 413 waiting to happen.
UPLOAD_MARGIN = 0.6

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

# Who we say we are.
#
# urllib announces "Python-urllib/3.12" unless told otherwise, and a service behind Cloudflare
# refuses that outright: HTTP 403 with a body of "error code: 1010", which is Cloudflare for "the
# owner has banned your browser". Verified against stt.ex5.ai — the same request that returns 403
# under the default header returns 200 under any real name. It arrived as "API anahtarı kabul
# edilmedi (403)" after a real call, sending the user to check a key that was never the problem.
USER_AGENT = "VoiceTranscript/1.0 (+https://github.com/fintechcoding/VoiceTranscript)"


def _sleep(seconds: float) -> None:
    time.sleep(seconds)


class CloudWhisperEngine(AsrEngine):
    """Uploads audio to an OpenAI-compatible transcription endpoint."""

    name = "cloud-openai"

    # What this engine will put in one request, and how much audio one request carries.
    #
    # Class attributes rather than the module constants used directly, because the limits are the
    # one thing that genuinely differs between services: a server that accepts 95 MB should not be
    # held to OpenAI's 25, and holding it there is what turns "PyAV is missing" into a failed
    # conversation on a machine that would otherwise have coped.
    max_upload_bytes = MAX_UPLOAD_BYTES
    max_chunk_seconds = MAX_CHUNK_SECONDS

    def __init__(self) -> None:
        self._base_url = ""
        self._api_key = ""
        self._model = "whisper-1"
        self._timeout = 600

        # How much audio the upload being built covers, in seconds.
        #
        # Passed on the instance rather than through _build_request, whose signature every
        # provider dialect implements. Safe because an engine is single-use and its chunks are
        # uploaded one after another; a provider that has to choose an endpoint by duration —
        # ex5 picks between the synchronous and the job API — reads it, and nothing else does.
        self._chunk_seconds = 0.0

        # Where this chunk sits on the overall bar, so an engine that learns mid-upload how far
        # the server has got can say so without knowing how many chunks there are.
        self._progress: ProgressCallback | None = None
        self._progress_base = 0.0
        self._progress_span = 0.0
        self._progress_label = ""

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

        chunks = plan_chunks(wav_path, self.max_chunk_seconds)
        workspace = self._workspace(wav_path)

        try:
            segments: list[Segment] = []

            for chunk in chunks:
                if progress:
                    progress(
                        0.02 + 0.94 * chunk.index / len(chunks),
                        f"{chunk.index + 1}/{len(chunks)} yükleniyor",
                    )

                segments.extend(
                    self._chunk_segments(wav_path, chunk, options, workspace, len(chunks), progress))

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
        progress: ProgressCallback | None = None,
    ) -> list[Segment]:
        # The model is part of the key: changing model must not reuse the old model's answers.
        cache = os.path.join(workspace, f"{self.name}-{self._model}-{chunk.index}-{total_chunks}.json")

        if os.path.exists(cache):
            try:
                with open(cache, encoding="utf-8") as handle:
                    return self._to_segments(json.load(handle), chunk.start_seconds)
            except (OSError, json.JSONDecodeError):
                os.unlink(cache)  # corrupt, fetch it again

        if total_chunks == 1:
            source = wav_path
        else:
            source = os.path.join(workspace, f"part{chunk.index}.wav")
            if not os.path.exists(source):
                slice_wav(wav_path, source, chunk.start_seconds, chunk.end_seconds)

        # Only the parts where somebody is speaking.
        #
        # The local engine runs with vad_filter=True and drops non-speech before decoding; no
        # hosted API does the equivalent. This application records the whole call on two separate
        # channels, so while one person talks the other channel is minutes of nothing, and Whisper
        # given silence does not return nothing — it returns whatever its training data has most
        # of. Doing it here rather than asking each provider for a flag means it works on all of
        # them, OpenAI included. See speech_only for the reasoning and for the mapping back.
        speech = os.path.join(workspace, f"speech-{chunk.index}.wav")
        spans = write_speech_only(source, speech)

        if not spans and os.path.exists(speech):
            try:
                os.unlink(speech)
            except OSError:
                pass

        upload = self._compress(speech if spans else source, workspace, suffix=f"-{chunk.index}")

        # Said out loud, because working it out afterwards is guesswork and somebody did.
        #
        # The service operator saw seven uploads, assumed the old 24 kbps Opus, divided words by
        # megabytes and concluded the transcription was twenty times worse than usual. It was the
        # arithmetic: the same audio uncompressed is thirteen times the bytes, so every ratio
        # against size moves by that much and nothing was wrong. One line naming the size and the
        # format settles it without anyone reverse-engineering a byte count.
        if progress:
            megabytes = os.path.getsize(upload) / 1_000_000
            how = "kayıpsız" if upload == source else "Opus"

            # Everything that decides the answer, in one line.
            #
            # Two rounds of this went on comparing a local transcript with a cloud one and guessing
            # at what differed — the flag, the bitrate, the silence — and one of those guesses was
            # wrong in a way nobody could check. What is actually sent is not a mystery; it just
            # was not written down. The language matters most: forced Turkish on a channel where
            # somebody speaks Russian comes back as Turkish syllables that mean nothing.
            said = "otomatik" if options.multilingual else (options.language or "otomatik")
            terms = len((options.initial_prompt or "").split(",")) if options.initial_prompt else 0
            cut = f" · {removed:.0f} sn sessizlik atıldı" if (removed := _silence_removed(chunk, spans)) else ""

            progress(
                0.02 + 0.94 * chunk.index / total_chunks,
                f"{chunk.index + 1}/{total_chunks} yükleniyor · {megabytes:.1f} MB · {how}"
                f" · dil {said} · sözlük {terms} terim (ipucu){cut}",
            )

        self._chunk_seconds = chunk.length_seconds

        self._progress = progress
        self._progress_base = 0.02 + 0.94 * chunk.index / total_chunks
        self._progress_span = 0.94 / total_chunks
        self._progress_label = f"{chunk.index + 1}/{total_chunks} yazıya dökülüyor"

        payload = self._post_with_retry(upload, options)

        try:
            with open(cache, "w", encoding="utf-8") as handle:
                json.dump(payload, handle)
        except OSError:
            pass  # the cache is an optimisation, never a requirement

        # The slice and its compressed copy are large and no longer needed once the answer is
        # cached; the answer is what makes a resume cheap.
        for path in (source, speech, upload):
            if path != wav_path and os.path.exists(path):
                try:
                    os.unlink(path)
                except OSError:
                    pass

        segments = self._restore(self._to_segments(payload, 0.0), spans, chunk.start_seconds)

        # What came back, beside what went out. The service reports the language it decided on, and
        # that is the one number that says whether forcing ours was the right call.
        if progress:
            heard = str(payload.get("language") or "?")
            words = sum(len(segment.words) for segment in segments)

            progress(
                0.02 + 0.94 * (chunk.index + 1) / total_chunks,
                f"{chunk.index + 1}/{total_chunks} geldi · dil {heard}"
                f" · {len(segments)} satır · {words} kelime",
            )

        return segments

    @staticmethod
    def _restore(segments: list[Segment], spans: list[SpeechSpan], chunk_start: float) -> list[Segment]:
        """
        Puts every time back on the recording's own clock.

        Two shifts, in order. First out of the upload and back onto the chunk, undoing the silence
        that was removed; then onto the whole call, undoing the chunk's own position. Doing it in
        one step is what would go wrong: the spans describe the chunk, not the call, so adding the
        chunk offset before the mapping would look up the wrong span and be wrong by minutes rather
        than by the silence.

        Every line in this product carries a moment you can click to hear, so a time that is out by
        a second is a quote pointing at audio that does not contain it.
        """
        for segment in segments:
            segment.start = to_original(segment.start, spans) + chunk_start
            segment.end = to_original(segment.end, spans) + chunk_start

            for word in segment.words:
                word.start = to_original(word.start, spans) + chunk_start
                word.end = to_original(word.end, spans) + chunk_start

        return segments

    def _to_segments(self, payload: dict, offset: float) -> list[Segment]:
        """The provider's response, as segments on the call timeline. OpenAI's shape by default."""
        return _to_segments(payload, offset)

    def _compress(self, wav_path: str, workspace: str, suffix: str = "") -> str:
        """
        Send the best thing that fits: the recording itself where it will go, Opus where it will not.

        Compression here has one purpose, which is to get under somebody else's ceiling. It was
        being applied as though it had a second — saving space that nobody needed saved — at a
        fixed 24 kbps, and that turned out to cost most of a conversation. See MIN_OPUS_BITRATE:
        the same audio gave 1624 words at 21.5 kbps and 330 at 18.2.

        So the question is asked the other way round now. A twenty-minute chunk is 38 MB as
        recorded; against our own server's 95 MB that goes up untouched, and the encoder never
        enters the picture. Against OpenAI's 25 MB it does not fit, and the bitrate is then whatever
        the remaining room allows — about 100 kbps, four times the old figure and still less than
        half of what that limit would bear.

        The original file is returned unchanged when it fits, which the caller already handles: it
        deletes nothing that is the recording itself.
        """
        size = os.path.getsize(wav_path)
        allowed = self.max_upload_bytes * UPLOAD_MARGIN

        if size <= allowed:
            return wav_path

        target = os.path.join(workspace, f"upload{suffix}.ogg")

        try:
            import av
        except ImportError:
            return wav_path

        try:
            with av.open(wav_path) as source, av.open(target, mode="w", format="ogg") as output:
                stream = output.add_stream("libopus", rate=16_000)
                stream.bit_rate = self._bitrate_for(size, allowed)

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

    @staticmethod
    def _bitrate_for(size: float, allowed: float) -> int:
        """
        The highest bitrate that fits, bounded at both ends.

        Scaled from what the source carries rather than picked from a table, so it follows the
        service's limit without anyone maintaining a per-provider number. Clamped at the top
        because a 16 kHz mono encoder has nothing left to say above MAX_OPUS_BITRATE, and at the
        bottom because a limit tight enough to demand less than MIN_OPUS_BITRATE should be refused
        by the size check with a sentence, not met quietly by an upload the model cannot hear.
        """
        return int(min(MAX_OPUS_BITRATE, max(MIN_OPUS_BITRATE, SOURCE_BITRATE * allowed / size)))

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
        self._check_size(path)

        url, headers, body = self._build_request(path, options)
        return self._send(url, headers, body)

    def _check_size(self, path: str) -> None:
        """
        Refuse locally what the service would refuse anyway, and say which limit was hit.

        Kept apart from _post so an engine that chooses between two endpoints still performs it
        once, before either. Naming the limit matters: "too big" against a 25 MB service and
        against a 95 MB one call for different answers.
        """
        size = os.path.getsize(path)

        if size > self.max_upload_bytes:
            raise EngineError(
                "too_large",
                f"Ses parçası sıkıştırıldıktan sonra bile çok büyük ({size // 1_000_000} MB, "
                f"sınır {self.max_upload_bytes // 1_000_000} MB). "
                "Opus kodlayıcı (PyAV) kurulu olmayabilir.",
            )

    def _build_request(self, path: str, options: EngineOptions) -> tuple[str, dict[str, str], bytes]:
        """
        One upload, in the OpenAI shape.

        A separate step from sending so a provider with its own dialect — ElevenLabs, Deepgram —
        only has to describe its request, and shares the retry, error and timeout handling.
        """
        fields = {
            "model": self._model,
            "language": options.language,
            # verbose_json is the only format that carries timing at all, and word granularity
            # has to be requested on top of it. Without both, every quote loses its position.
            "response_format": "verbose_json",
            "timestamp_granularities[]": "word",
        }

        # The user's vocabulary, in the field the OpenAI shape has for it. Product names and
        # people are what a hosted model gets wrong in exactly the same way the local one does.
        if options.initial_prompt:
            fields["prompt"] = options.initial_prompt

        # A mixed-language call: let the service detect rather than forcing Turkish on English words.
        if options.multilingual:
            fields.pop("language", None)

        body, content_type = _multipart(fields, Path(path))

        # Named in every error below. A real night of failures read "OpenAI: 404: Invalid URL" —
        # which is impossible against api.openai.com, so the request was going somewhere else
        # (a base URL left over from trying another provider), and nothing on screen said where.
        # An error that names the endpoint answers that question the moment it appears.
        url = f"{self._base_url}/audio/transcriptions"

        return url, {"Authorization": f"Bearer {self._api_key}", "Content-Type": content_type}, body

    def _send(self, url: str, headers: dict[str, str], body: bytes) -> dict:
        # Set here rather than in each _build_request: a dialect that forgets it does not fail
        # visibly, it fails as somebody else's 403 a fortnight later.
        headers = {"User-Agent": USER_AGENT, **headers}

        request = urllib.request.Request(url, data=body, method="POST", headers=headers)

        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:400]

            if exc.code in RETRYABLE_STATUS:
                raise _Retryable(
                    "rate_limited" if exc.code == 429 else "api_error",
                    f"{exc.code} ({url}): {detail}",
                    _retry_after(exc),
                ) from exc

            raise self._fatal(exc.code, url, detail) from exc
        except urllib.error.URLError as exc:
            # A dropped connection mid-upload is ordinary on a laptop that moved between
            # networks, and is exactly the case retrying exists for.
            raise _Retryable("network", _network_message(url, exc.reason), None) from exc
        except TimeoutError as exc:
            raise _Retryable("timeout", "İstek zaman aşımına uğradı.", None) from exc

    def _fatal(self, status: int, url: str, detail: str) -> EngineError:
        """
        A refusal that will be refused again, said in the words the user can act on.

        Three of these are ordinary and used to arrive as the same unreadable line. A 401 is a key
        to fix. A 413 is an upload the service will never take, which is a different problem from
        the local size check and needs the service's own ceiling named, not ours. A 524 is
        Cloudflare hanging up on an origin that was still working — the request may well have
        succeeded on the server, and telling somebody "api_error 524" invites them to blame the
        recording. Everything else keeps the status and the address, because the address is what a
        real night of "404: Invalid URL" turned out to hinge on.
        """
        if status == 403 and _is_cloudflare_block(detail):
            # Cloudflare's own numbered refusals: 1010 is a banned client signature, 1020 a
            # firewall rule. Neither says anything about the key, and both used to be reported as
            # "check your API key" — a wrong instruction is worse than a raw status code, because
            # the user follows it.
            return EngineError(
                "blocked",
                f"Servisin önündeki güvenlik katmanı isteği engelledi ({status}, {url}). "
                "Anahtarla ilgili değil; sunucunun bu istemciyi tanıması gerekiyor.",
            )

        if status in (401, 403):
            return EngineError(
                "auth",
                f"API anahtarı kabul edilmedi ({status}). Ayarlardan anahtarı denetle.",
            )

        if status == 413:
            return EngineError(
                "too_large",
                f"Servis bu yüklemeyi büyük buldu (413, {url}). "
                "Sesin daha küçük parçalara bölünmesi gerekiyor.",
            )

        if status == 524:
            return EngineError(
                "timeout",
                f"Servis yanıtı 100 saniyede yetiştiremedi (524, {url}). "
                "Uzun kayıtlar için işi kuyruğa veren bir uç nokta gerekiyor.",
            )

        return EngineError("api_error", f"{status} ({url}): {detail}")

    def unload(self) -> None:
        self._api_key = ""


class _Retryable(Exception):
    """An failure that is worth another attempt, with the wait the server asked for."""

    def __init__(self, code: str, message: str, retry_after: float | None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retry_after = retry_after


def _silence_removed(chunk, spans) -> float:
    """Seconds of the chunk that never reached the model, or zero when it went up whole."""
    if not spans:
        return 0.0

    return max(0.0, chunk.length_seconds - sum(span.length for span in spans))


def _network_message(url: str, reason: object) -> str:
    """
    A dropped connection, said in terms of what actually happened.

    "EOF occurred in violation of protocol (_ssl.c:2406)" is the C source file and line number of
    somebody else's TLS library, and it reached the conversation row verbatim. It is also not the
    generic network wobble it reads as: reproduced against api.elevenlabs.io, posting a megabyte to
    a route that does not exist gives exactly this, while posting a few bytes to the same route
    gives a clean 404. The gateway resets rather than reading a body it has nowhere to put.

    That is why the same misconfiguration produced two different errors on one evening — short
    calls 404, long calls this — and why the address is the first thing to name. It stays
    retryable, because a laptop changing networks mid-upload looks identical and does recover.
    """
    text = str(reason)

    if "EOF occurred" in text or "violation of protocol" in text:
        return (
            f"Sunucu yükleme sırasında bağlantıyı kapattı ({url}). "
            "Çoğunlukla adresin bu servise ait olmadığı anlamına gelir; ağ kesintisi de olabilir."
        )

    return f"Sunucuya ulaşılamadı ({url}): {text}"


def _is_cloudflare_block(detail: str) -> bool:
    """
    Whether a refusal body is Cloudflare's rather than the service's.

    Cloudflare answers with an HTML page whose only machine-readable part is the line
    "error code: 1010". A real API refusing a key answers with its own JSON. Matching on the
    phrase is crude, but the alternative — treating every 403 as a key problem — is what produced
    the instruction to check a key that was correct.
    """
    return "error code:" in detail.lower() or "cloudflare" in detail.lower()


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
            # faster-whisper's convention, imposed here: every word carries its leading space,
            # so downstream text is rebuilt with plain "".join. OpenAI's verbose_json words come
            # bare, and joining them bare glued whole sentences into single words —
            # "aloalonapıyonbirtanem" on a real call.
            text=_with_leading_space(str(word.get("word", ""))),
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


def _with_leading_space(token: str) -> str:
    """Give a bare word the leading space the local engines' words already carry.

    Downstream code rebuilds segment text with ``"".join(w.text ...)`` on the assumption —
    true for faster-whisper and whisper.cpp — that each word brings its own separator. A word
    that already starts with whitespace passes through untouched, so applying this to a
    well-behaved provider changes nothing.
    """
    if not token or token[0].isspace():
        return token
    return " " + token


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
        # A list is the same field repeated — how multipart carries "keyterms[]".
        for one in (value if isinstance(value, (list, tuple)) else [value]):
            parts.append(
                f"--{boundary}\r\n"
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'
                f"{one}\r\n".encode()
            )

    parts.append(
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{file_path.name}"\r\n'
        f"Content-Type: application/octet-stream\r\n\r\n".encode()
    )
    parts.append(file_path.read_bytes())
    parts.append(f"\r\n--{boundary}--\r\n".encode())

    return b"".join(parts), f"multipart/form-data; boundary={boundary}"
