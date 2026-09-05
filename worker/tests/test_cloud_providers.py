"""
ElevenLabs and Deepgram in their own words.

Both used to be sent OpenAI's request and died on the first upload while the connection test
showed green. These pin the endpoint, the header, the field names and the response mapping.
"""

from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from vt_worker import __main__ as worker_main
from vt_worker.chunking import Chunk
from vt_worker.engines import cloud_engine, create
from vt_worker.engines.base import EngineOptions
from vt_worker.engines.cloud_providers import DeepgramEngine, ElevenLabsEngine, _event_kind
from vt_worker.merge import merge_streams


def _upload(tmp_path):
    path = tmp_path / "upload-0.ogg"
    path.write_bytes(b"OggS-not-really")
    return str(path)


def test_registry_knows_both():
    assert isinstance(create("cloud-elevenlabs"), ElevenLabsEngine)
    assert isinstance(create("cloud-deepgram"), DeepgramEngine)


def test_elevenlabs_request_uses_its_own_endpoint_header_and_fields(tmp_path):
    engine = ElevenLabsEngine()
    engine.load(EngineOptions(model_ref="https://api.elevenlabs.io/v1|KEY|scribe_v2"))

    url, headers, body = engine._build_request(
        _upload(tmp_path), EngineOptions(model_ref="x", language="tr", hotwords="Sumsub, KYC"))

    assert url == "https://api.elevenlabs.io/v1/speech-to-text"
    assert headers["xi-api-key"] == "KEY"
    assert "Authorization" not in headers
    assert headers["Content-Type"].startswith("multipart/form-data")

    text = body.decode("utf-8", errors="replace")
    assert 'name="model_id"\r\n\r\nscribe_v2' in text
    assert 'name="language_code"\r\n\r\ntr' in text
    assert 'name="timestamps_granularity"\r\n\r\nword' in text
    assert text.count('name="keyterms"') == 2
    assert 'name="file"' in text
    assert 'name="model"\r\n' not in text


def test_elevenlabs_mixed_language_leaves_the_language_to_the_service(tmp_path):
    engine = ElevenLabsEngine()
    engine.load(EngineOptions(model_ref="https://api.elevenlabs.io/v1|KEY|scribe_v2"))

    _, _, body = engine._build_request(_upload(tmp_path), EngineOptions(model_ref="x", multilingual=True))

    assert 'name="language_code"' not in body.decode("utf-8", errors="replace")


def test_elevenlabs_words_become_one_segment_with_timed_words():
    engine = ElevenLabsEngine()
    payload = {
        "language_code": "tur",
        "text": "Sumsub onboarding yarın.",
        "words": [
            {"text": "Sumsub", "start": 0.5, "end": 0.9, "type": "word", "logprob": -0.1},
            {"text": " ", "start": 0.9, "end": 1.0, "type": "spacing"},
            {"text": "onboarding", "start": 1.0, "end": 1.6, "type": "word"},
            {"text": "(laughter)", "start": 1.6, "end": 2.0, "type": "audio_event"},
            {"text": "yarın.", "start": 2.1, "end": 2.5, "type": "word"},
        ],
    }

    [segment] = engine._to_segments(payload, offset=100.0)

    assert segment.text == "Sumsub onboarding yarın."
    assert [w.text.strip() for w in segment.words] == ["Sumsub", "onboarding", "yarın."]
    assert segment.words[0].start == 100.5
    assert segment.words[-1].end == 102.5
    assert segment.start == 100.5 and segment.end == 102.5


def test_elevenlabs_logprob_becomes_a_probability_on_the_shared_scale():
    """
    The service reports a log-probability (0 or below), faster-whisper and Deepgram a probability
    (0 to 1), and all of them land in the same Word.probability. If this fails the scales are
    mixed again: one threshold reads every ElevenLabs word as doubtful, or a word the service
    never scored is given a number it did not say.
    """
    payload = {
        "text": "Sumsub yarın",
        "words": [
            {"text": "Sumsub", "start": 0.0, "end": 0.4, "type": "word", "logprob": -0.1},
            {"text": "yarın", "start": 0.5, "end": 0.9, "type": "word"},
        ],
    }

    [segment] = ElevenLabsEngine()._to_segments(payload, offset=0.0)

    assert segment.words[0].probability == pytest.approx(0.905, abs=1e-3)
    assert segment.words[0].probability == pytest.approx(math.exp(-0.1), abs=1e-6)
    assert segment.words[1].probability is None


def test_elevenlabs_asks_the_service_to_tag_audio_events(tmp_path):
    """
    Without the flag the service never sends an event and the parser below is exercised by
    nothing; without it in the cache key a chunk cached before the flag is replayed without them.
    """
    engine = ElevenLabsEngine()
    engine.load(EngineOptions(model_ref="https://api.elevenlabs.io/v1|KEY|scribe_v2"))

    _, _, body = engine._build_request(_upload(tmp_path), EngineOptions(model_ref="x", language="tr"))

    assert 'name="tag_audio_events"\r\n\r\ntrue' in body.decode("utf-8", errors="replace")
    assert engine._request_signature(EngineOptions(model_ref="x"))["tag_audio_events"] is True


def test_elevenlabs_audio_events_are_listed_beside_the_words_not_inside_them():
    """
    A laugh between two words comes back as one event and the same two words. If this fails,
    either "(laughter)" is being quoted as something somebody said, or the event has moved the
    words around it — and a quote that plays the wrong audio is worse than no quote.
    """
    engine = ElevenLabsEngine()
    laughter = {"text": "(laughter)", "start": 1.6, "end": 2.0, "type": "audio_event"}
    words = [
        {"text": "onboarding", "start": 1.0, "end": 1.6, "type": "word", "logprob": -0.2},
        {"text": " ", "start": 1.6, "end": 2.1, "type": "spacing"},
        {"text": "yarın.", "start": 2.1, "end": 2.5, "type": "word"},
    ]
    with_event = {"text": "onboarding yarın.", "words": [words[0], laughter, *words[1:]]}
    without = {"text": "onboarding yarın.", "words": words}

    segments = engine._to_segments(with_event, offset=100.0)
    events = engine._to_events(with_event, offset=100.0)

    assert segments == engine._to_segments(without, offset=100.0)
    assert [w.text.strip() for w in segments[0].words] == ["onboarding", "yarın."]
    assert [(w.start, w.end) for w in segments[0].words] == [(101.0, 101.6), (102.1, 102.5)]
    assert "laughter" not in segments[0].text
    assert events == [{"start_ms": 101600, "end_ms": 102000, "kind": "laughter"}]
    assert engine._to_events(without, offset=100.0) == []


def test_audio_event_kinds_are_one_lower_case_ascii_token():
    """The C# side switches on the kind; a stray bracket or capital would make "laughter" two kinds."""
    assert _event_kind("(laughter)") == "laughter"
    assert _event_kind("[Door Slam]") == "door_slam"
    assert _event_kind(" (Gülme) ") == "gulme"
    assert _event_kind("()") == "unknown"


def test_only_the_engine_that_tags_events_reports_any():
    """
    An empty list rather than a missing attribute: the worker reads it off every engine alike. If
    this fails, a provider that does not tag events is inventing some, or the worker falls over
    reading a field an engine never heard of.
    """
    payload = {"text": "x", "words": [{"text": "(laughter)", "start": 0, "end": 1, "type": "audio_event"}]}

    assert DeepgramEngine()._to_events(payload, 0.0) == []
    assert cloud_engine.CloudWhisperEngine()._to_events(payload, 0.0) == []
    assert not cloud_engine.CloudWhisperEngine().audio_events


def test_events_ride_the_chunk_offset_and_are_reset_per_recording(tmp_path, monkeypatch):
    """
    Each piece is transcribed on its own with timings from zero, and one instance does both
    channels in turn. If this fails, a laugh in the second chunk is reported twenty minutes early,
    or the far channel's answer still carries the microphone's laughs.
    """
    engine = ElevenLabsEngine()
    engine.load(EngineOptions(model_ref="https://api.elevenlabs.io/v1|KEY|scribe_v2"))

    wav = tmp_path / "mic.wav"
    wav.write_bytes(b"RIFF-not-really")

    answers = [
        {"text": "alo", "words": [{"text": "alo", "start": 0.2, "end": 0.5, "type": "word"}]},
        {"text": "", "words": [{"text": "(laughter)", "start": 1.0, "end": 1.5, "type": "audio_event"}]},
    ]
    monkeypatch.setattr(
        cloud_engine, "plan_chunks", lambda path, max_seconds: [Chunk(0, 0.0, 1200.0), Chunk(1, 1200.0, 1500.0)])
    monkeypatch.setattr(
        cloud_engine, "slice_wav", lambda src, dst, start, end: Path(dst).write_bytes(b"part"))
    monkeypatch.setattr(engine, "_compress", lambda path, workspace, suffix="": path)
    monkeypatch.setattr(
        engine, "_post_with_retry", lambda upload, options: answers.pop(0) if answers else {"text": ""})

    segments = engine.transcribe(str(wav), EngineOptions(model_ref="x", language="tr"))

    assert [s.text for s in segments] == ["alo"]
    assert engine.audio_events == [{"start_ms": 1201000, "end_ms": 1201500, "kind": "laughter"}]

    assert engine.transcribe(str(wav), EngineOptions(model_ref="x", language="tr")) == []
    assert engine.audio_events == []


def test_the_result_carries_audio_events_beside_the_segments():
    """
    The C# side reads them off the result line by name. If this fails the field is missing or
    misnamed for the engine that tags events, absent instead of empty for the ones that do not,
    or the two channels' laughs are not in the order they happened.
    """
    merged = merge_streams([], [])

    quiet = worker_main._transcript_to_json("job", merged, {"engine": "faster-whisper"})

    assert quiet["audio_events"] == []
    assert quiet["engine"] == "faster-whisper"

    events = [
        {"channel": "far", "start_ms": 5000, "end_ms": 5400, "kind": "laughter"},
        {"channel": "mic", "start_ms": 1000, "end_ms": 1300, "kind": "laughter"},
    ]
    loud = worker_main._transcript_to_json("job", merged, {}, audio_events=events)

    assert loud["audio_events"] == [events[1], events[0]]
    assert loud["segments"] == quiet["segments"]


def test_deepgram_request_is_a_raw_body_with_query_options(tmp_path):
    engine = DeepgramEngine()
    engine.load(EngineOptions(model_ref="https://api.deepgram.com/v1|KEY|nova-2"))

    url, headers, body = engine._build_request(
        _upload(tmp_path), EngineOptions(model_ref="x", language="tr", hotwords="Sumsub"))

    assert url.startswith("https://api.deepgram.com/v1/listen?")
    assert "model=nova-2" in url and "language=tr" in url and "smart_format=true" in url
    assert "keywords=Sumsub%3A2" in url
    assert headers["Authorization"] == "Token KEY"
    assert headers["Content-Type"] == "audio/ogg"
    assert body == b"OggS-not-really"


def test_deepgram_nova3_uses_keyterm_and_detects_language_when_mixed(tmp_path):
    engine = DeepgramEngine()
    engine.load(EngineOptions(model_ref="https://api.deepgram.com/v1|KEY|nova-3"))

    url, _, _ = engine._build_request(
        _upload(tmp_path), EngineOptions(model_ref="x", language="tr", multilingual=True, hotwords="Sumsub"))

    from urllib.parse import parse_qs, urlsplit
    query = parse_qs(urlsplit(url).query)

    assert query["keyterm"] == ["Sumsub"]
    assert query["detect_language"] == ["true"]
    assert "language" not in query


def test_deepgram_response_maps_to_timed_words():
    engine = DeepgramEngine()
    payload = {
        "results": {"channels": [{"alternatives": [{
            "transcript": "Sumsub yarın.",
            "words": [
                {"word": "sumsub", "punctuated_word": "Sumsub", "start": 0.2, "end": 0.7, "confidence": 0.98},
                {"word": "yarın", "punctuated_word": "yarın.", "start": 0.8, "end": 1.1, "confidence": 0.9},
            ],
        }]}]},
    }

    [segment] = engine._to_segments(payload, offset=10.0)

    assert segment.text == "Sumsub yarın."
    assert [w.text.strip() for w in segment.words] == ["Sumsub", "yarın."]
    assert segment.words[0].start == 10.2
    assert segment.end == 11.1


def test_an_empty_deepgram_response_is_no_segments():
    assert DeepgramEngine()._to_segments({"results": {"channels": []}}, 0.0) == []
    assert DeepgramEngine()._to_segments(json.loads("{}"), 0.0) == []
