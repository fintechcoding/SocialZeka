"""
Hosted transcription services with their own request dialect.

Both were listed in the catalogue as if they spoke OpenAI's /audio/transcriptions shape. ElevenLabs
does not: it serves POST /v1/speech-to-text, wants the key in an ``xi-api-key`` header, names its
fields ``model_id`` / ``language_code`` and answers with a flat word list. Every upload therefore
died with a 404 while the connection test — which only lists models — showed green. Deepgram is
further still: a raw audio body, ``Authorization: Token``, query-string options.

Each engine here describes only what differs — the request and the response shape — and inherits
chunking, resumable uploads, retry, and error classification from the OpenAI engine.
"""

from __future__ import annotations

import math
import re
import unicodedata
import urllib.parse
from pathlib import Path

from vt_worker.engines.base import EngineError, EngineOptions
from vt_worker.engines.cloud_engine import (
    CloudWhisperEngine,
    _as_float,
    _multipart,
    _with_leading_space,
)
from vt_worker.merge import Segment, Speaker, Word


def _terms(options: EngineOptions) -> list[str]:
    """The user's vocabulary as a list, from the comma line the request carries."""
    raw = options.hotwords or ""
    return [t.strip() for t in raw.split(",") if t.strip()]


def _single_segment(words: list[Word], text: str, offset: float) -> list[Segment]:
    """
    One segment per uploaded chunk, carrying every word.

    The providers return words, not sentences. The worker's own resegmentation splits on pauses
    afterwards, exactly as it does for the OpenAI shape when no segments come back — so quotes
    still land on the moment they were spoken.
    """
    text = text.strip()
    if not words and not text:
        return []

    return [
        Segment(
            speaker=Speaker.ME,  # overwritten by merge_streams
            start=words[0].start if words else offset,
            end=words[-1].end if words else offset,
            text=text or "".join(w.text for w in words).strip(),
            avg_logprob=None,
            no_speech_prob=None,
            words=words,
        )
    ]


class ElevenLabsEngine(CloudWhisperEngine):
    """ElevenLabs Scribe: POST /v1/speech-to-text, xi-api-key, model_id, word list back."""

    name = "cloud-elevenlabs"

    def _build_request(self, path: str, options: EngineOptions) -> tuple[str, dict[str, str], bytes]:
        fields: dict = {
            "model_id": self._model,
            "timestamps_granularity": "word",
            # Laughter and the like come back as items of their own in the word list. They are
            # read past by _to_segments and collected by _to_events, so the words and their
            # timings are the same with the flag as without it.
            "tag_audio_events": "true",
            "diarize": "false",
        }

        # Forced language unless the call switches languages; then the service decides per file.
        if options.language and not options.multilingual:
            fields["language_code"] = options.language

        terms = _terms(options)
        if terms:
            fields["keyterms"] = terms

        body, content_type = _multipart(fields, Path(path))
        url = f"{self._base_url}/speech-to-text"

        return url, {"xi-api-key": self._api_key, "Content-Type": content_type}, body

    def _request_signature(self, options: EngineOptions) -> dict:
        # The event flag changes what comes back, so a chunk cached before it was switched on
        # must not be replayed as the answer to a request that asks for events.
        return {**super()._request_signature(options), "tag_audio_events": True}

    def _heard_language(self, payload: dict, options: EngineOptions) -> str:
        return str(payload.get("language_code") or super()._heard_language(payload, options))

    def _to_segments(self, payload: dict, offset: float) -> list[Segment]:
        words = [
            Word(
                start=float(item.get("start", 0.0)) + offset,
                end=float(item.get("end", 0.0)) + offset,
                text=_with_leading_space(str(item.get("text", ""))),
                probability=_from_logprob(item.get("logprob")),
            )
            for item in payload.get("words") or []
            if item.get("type", "word") == "word" and str(item.get("text", "")).strip()
        ]

        return _single_segment(words, str(payload.get("text", "")), offset)

    def _to_events(self, payload: dict, offset: float) -> list[dict]:
        """
        What the service heard that was not a word — laughter, applause — beside the words.

        Kept out of the segments on purpose: "(laughter)" inside a line would be quoted as
        something somebody said. The items share the word list, so the times go through the
        same chunk offset as the words; in whole milliseconds, because that is the unit of
        every span the C# side stores.
        """
        return [
            {
                "start_ms": _ms(float(item.get("start", 0.0)) + offset),
                "end_ms": _ms(float(item.get("end", 0.0)) + offset),
                "kind": _event_kind(str(item.get("text", ""))),
            }
            for item in payload.get("words") or []
            if item.get("type") == "audio_event"
        ]


class DeepgramEngine(CloudWhisperEngine):
    """Deepgram: POST /v1/listen with the audio as the body and the options in the query string."""

    name = "cloud-deepgram"

    def _build_request(self, path: str, options: EngineOptions) -> tuple[str, dict[str, str], bytes]:
        query: list[tuple[str, str]] = [
            ("model", self._model),
            ("smart_format", "true"),
            ("punctuate", "true"),
        ]

        if options.language and not options.multilingual:
            query.append(("language", options.language))
        elif options.multilingual:
            query.append(("detect_language", "true"))

        # Vocabulary: nova-3 has keyterm, earlier models keywords with a boost.
        for term in _terms(options):
            if self._model.startswith("nova-3"):
                query.append(("keyterm", term))
            else:
                query.append(("keywords", f"{term}:2"))

        url = f"{self._base_url}/listen?{urllib.parse.urlencode(query)}"

        suffix = Path(path).suffix.lower()
        content_type = "audio/ogg" if suffix in (".ogg", ".opus") else "audio/wav"

        with open(path, "rb") as handle:
            body = handle.read()

        return url, {"Authorization": f"Token {self._api_key}", "Content-Type": content_type}, body

    def _to_segments(self, payload: dict, offset: float) -> list[Segment]:
        results = payload.get("results") if isinstance(payload, dict) else None

        # An answer carries "results", silence included: a channel whose one alternative has an
        # empty transcript and no words. A body without it is not an answer — an error a gateway
        # sent under a 200, a page of HTML that happened to parse, somebody else's service at the
        # address. It used to leave here as [] and be reported upstream as "konuşma bulunamadı",
        # a quiet recording, with nothing anywhere saying the service had transcribed nothing.
        if not isinstance(results, dict):
            raise EngineError("bad_response", _not_an_answer(payload))

        try:
            alternative = results["channels"][0]["alternatives"][0]
        except (KeyError, IndexError, TypeError):
            return []

        words = [
            Word(
                start=float(item.get("start", 0.0)) + offset,
                end=float(item.get("end", 0.0)) + offset,
                text=_with_leading_space(str(item.get("punctuated_word") or item.get("word") or "")),
                probability=_prob(item.get("confidence")),
            )
            for item in alternative.get("words") or []
            if str(item.get("punctuated_word") or item.get("word") or "").strip()
        ]

        return _single_segment(words, str(alternative.get("transcript", "")), offset)

    def _heard_language(self, payload: dict, options: EngineOptions) -> str:
        # Reported only when the service was asked to detect it. With the language forced there
        # is nothing for it to report, and the honest line names the language that was asked
        # for rather than printing "?" as though something had gone wrong.
        detected = _channel(payload).get("detected_language")
        if detected:
            return str(detected)

        if options.language and not options.multilingual:
            return f"{options.language} (istendi)"

        return "?"

    def _heard_seconds(self, payload: dict) -> float | None:
        metadata = payload.get("metadata")
        return _as_float(metadata.get("duration")) if isinstance(metadata, dict) else None


def _channel(payload: dict) -> dict:
    """The first channel of a Deepgram answer, or nothing when the answer has none."""
    try:
        channel = payload["results"]["channels"][0]
    except (KeyError, IndexError, TypeError):
        return {}

    return channel if isinstance(channel, dict) else {}


def _not_an_answer(payload: object) -> str:
    """What a body that is not a Deepgram answer looks like, for the error that names it."""
    if isinstance(payload, dict):
        said = payload.get("err_msg") or payload.get("message") or payload.get("error")
        if said:
            return f"Deepgram yanıt yerine bir hata döndürdü: {str(said)[:200]}"

        keys = ", ".join(sorted(str(key) for key in payload)[:8]) or "boş gövde"
        return f"Deepgram beklenmeyen bir yanıt döndürdü (alanlar: {keys})."

    return f"Deepgram beklenmeyen bir yanıt döndürdü ({type(payload).__name__})."


def _prob(value: object) -> float | None:
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _from_logprob(value: object) -> float | None:
    """
    ElevenLabs' word confidence on the scale every other engine uses.

    The service reports a log-probability, 0 or below; faster-whisper and Deepgram report a
    probability, 0 to 1; all of them land in the same ``Word.probability``. Stored as it came,
    the threshold that reads 0.6 as "sure" on the others read every ElevenLabs word as doubtful —
    -0.1 is a confident word. exp puts it on the contract's scale, and a figure the service did
    not give stays None rather than becoming a number.
    """
    logprob = _prob(value)
    if logprob is None or math.isnan(logprob):
        return None
    return math.exp(min(0.0, logprob))


def _ms(seconds: float) -> int:
    return max(0, int(round(seconds * 1000)))


def _event_kind(text: str) -> str:
    """Turns "(laughter)" into "laughter": one lower-case ASCII token the C# side can switch on."""
    folded = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]+", "_", folded).strip("_") or "unknown"
