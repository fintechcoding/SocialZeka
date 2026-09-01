"""Surviving the things a hosted transcription API does on a bad day."""

from __future__ import annotations

import io
import json
import urllib.error

import pytest

from vt_worker.engines import cloud_engine
from vt_worker.engines.base import EngineError, EngineOptions


def _http_error(code: int, retry_after: str | None = None) -> urllib.error.HTTPError:
    headers = {"Retry-After": retry_after} if retry_after else {}
    return urllib.error.HTTPError(
        url="https://example.invalid/audio/transcriptions",
        code=code,
        msg="nope",
        hdrs=headers,  # type: ignore[arg-type]
        fp=io.BytesIO(b'{"error":"detail"}'),
    )


@pytest.fixture()
def engine(monkeypatch):
    # Never actually sleep: the schedule spans a minute and these tests must stay instant.
    monkeypatch.setattr(cloud_engine, "_sleep", lambda seconds: None)

    made = cloud_engine.CloudWhisperEngine()
    made.load(EngineOptions(model_ref="https://example.invalid/v1|key|whisper-1", language="tr"))
    return made


def _options() -> EngineOptions:
    return EngineOptions(model_ref="x|y|z", language="tr")


def test_http_statuses_are_classified_into_retry_or_give_up(engine, monkeypatch, tmp_path):
    """
    The classification is the whole point.

    429 and 503 mean "later"; 401 means "never, and here is why". Getting this backwards either
    loses conversations that would have worked, or hides a wrong API key behind a minute of
    pointless retrying.
    """
    upload = tmp_path / "chunk.ogg"
    upload.write_bytes(b"not really audio")

    def raise_status(code):
        def opener(request, timeout=None):
            raise _http_error(code, retry_after="3")

        return opener

    for code in (429, 500, 503):
        monkeypatch.setattr(cloud_engine.urllib.request, "urlopen", raise_status(code))

        with pytest.raises(cloud_engine._Retryable) as caught:
            engine._post(str(upload), _options())

        assert caught.value.retry_after == 3.0

    for code in (401, 403):
        monkeypatch.setattr(cloud_engine.urllib.request, "urlopen", raise_status(code))

        with pytest.raises(EngineError) as fatal:
            engine._post(str(upload), _options())

        assert fatal.value.code == "auth"

    # A malformed request is permanent too, but it is not an authentication problem and must not
    # be reported as one.
    monkeypatch.setattr(cloud_engine.urllib.request, "urlopen", raise_status(400))

    with pytest.raises(EngineError) as bad:
        engine._post(str(upload), _options())

    assert bad.value.code == "api_error"


def test_retryable_failures_are_retried_until_the_budget_runs_out(engine, monkeypatch):
    calls = {"n": 0}

    def always_busy(self, path, options):
        calls["n"] += 1
        raise cloud_engine._Retryable("rate_limited", "429", retry_after=1.0)

    monkeypatch.setattr(cloud_engine.CloudWhisperEngine, "_post", always_busy)

    with pytest.raises(EngineError) as caught:
        engine._post_with_retry("ignored", _options())

    assert calls["n"] == cloud_engine.MAX_ATTEMPTS
    assert caught.value.code == "rate_limited"


def test_a_transient_failure_that_clears_returns_the_answer(engine, monkeypatch):
    calls = {"n": 0}

    def clears(self, path, options):
        calls["n"] += 1
        if calls["n"] < 3:
            raise cloud_engine._Retryable("network", "connection reset", retry_after=None)
        return {"text": "tamam", "words": [], "segments": []}

    monkeypatch.setattr(cloud_engine.CloudWhisperEngine, "_post", clears)

    payload = engine._post_with_retry("ignored", _options())

    assert payload["text"] == "tamam"
    assert calls["n"] == 3


def test_a_bad_key_is_not_retried(engine, monkeypatch):
    """
    401 will answer identically forever. Retrying it five times with backoff only delays telling
    the user the one thing they need to know.
    """
    calls = {"n": 0}

    def unauthorised(self, path, options):
        calls["n"] += 1
        raise EngineError("auth", "API anahtarı kabul edilmedi (401).")

    monkeypatch.setattr(cloud_engine.CloudWhisperEngine, "_post", unauthorised)

    with pytest.raises(EngineError) as caught:
        engine._post_with_retry("ignored", _options())

    assert calls["n"] == 1
    assert caught.value.code == "auth"


def test_retry_after_header_is_believed():
    exc = _http_error(429, retry_after="17")
    assert cloud_engine._retry_after(exc) == 17.0


def test_a_missing_or_unparsable_retry_after_falls_back():
    assert cloud_engine._retry_after(_http_error(429)) is None
    assert cloud_engine._retry_after(_http_error(429, retry_after="Wed, 21 Oct 2026 07:28:00 GMT")) is None


def test_segments_are_offset_onto_the_call_timeline():
    """
    Each piece is transcribed on its own, so its timings start at zero. Without the offset every
    quote after the first chunk would point at the wrong moment in the recording — and a quote
    that plays the wrong audio is worse than no quote.
    """
    payload = {
        "segments": [
            {"start": 0.0, "end": 2.0, "text": "ilk"},
            {"start": 2.0, "end": 4.0, "text": "ikinci"},
        ],
        "words": [
            {"word": "ilk", "start": 0.1, "end": 0.5},
            {"word": "ikinci", "start": 2.1, "end": 2.6},
        ],
    }

    segments = cloud_engine._to_segments(payload, offset=1200.0)

    assert [s.start for s in segments] == [1200.0, 1202.0]
    assert segments[0].words[0].start == pytest.approx(1200.1)
    assert segments[1].words[0].start == pytest.approx(1202.1)


def test_bare_cloud_words_gain_the_leading_space_the_local_engines_carry():
    """
    OpenAI's verbose_json words are bare tokens — no whitespace, no parameter to change it —
    while faster-whisper's carry their leading space. Downstream text is rebuilt with
    ``"".join``, which on bare tokens glued whole sentences into single words: a real call
    rendered as "aloalonapıyonbirtanem". The cloud parser must impose the local convention.
    """
    payload = {
        "segments": [{"start": 0.0, "end": 3.0, "text": "az bekle canım"}],
        "words": [
            {"word": "az", "start": 0.0, "end": 0.4},
            {"word": "bekle", "start": 0.5, "end": 0.9},
            {"word": "canım", "start": 1.0, "end": 1.4},
        ],
    }

    words = cloud_engine._to_segments(payload, offset=0.0)[0].words

    assert "".join(w.text for w in words).strip() == "az bekle canım"

    # A provider that already sends the space is passed through, not double-spaced.
    assert cloud_engine._with_leading_space(" bekle") == " bekle"


def test_a_flat_text_response_still_produces_one_segment():
    """Not every provider returns segments. One correctly placed segment beats losing the call."""
    segments = cloud_engine._to_segments({"text": "sadece metin"}, offset=60.0)

    assert len(segments) == 1
    assert segments[0].text == "sadece metin"
    assert segments[0].start == 60.0


def test_an_empty_response_produces_nothing_rather_than_a_blank_segment():
    assert cloud_engine._to_segments({"text": "   "}, offset=0.0) == []
