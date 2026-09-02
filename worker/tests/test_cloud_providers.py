"""
ElevenLabs and Deepgram in their own words.

Both used to be sent OpenAI's request and died on the first upload while the connection test
showed green. These pin the endpoint, the header, the field names and the response mapping.
"""

from __future__ import annotations

import json

from vt_worker.engines import create
from vt_worker.engines.base import EngineOptions
from vt_worker.engines.cloud_providers import DeepgramEngine, ElevenLabsEngine


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
