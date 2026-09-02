"""
The ex5 server in its own words, and the two ways it differs from OpenAI's.

Both differences are silent when you get them wrong, which is the only reason these tests exist.
Sending ``timestamp_granularities[]`` returns 200 and a transcript with no word times — nothing
fails, and every quote in the ledger quietly loses the moment it was spoken. Sending a long
recording synchronously returns 524 from Cloudflare while the origin is still working, and the
piece is lost unless something submits it as a job instead.
"""

from __future__ import annotations

import io
import json
import urllib.error

import pytest

from vt_worker.engines import cloud_engine, create
from vt_worker.engines.base import EngineError, EngineOptions
from vt_worker.engines.ex5_engine import Ex5WhisperEngine


@pytest.fixture()
def engine(monkeypatch):
    # Five seconds a poll against a ceiling of an hour: without this the job tests would be the
    # slowest thing in the suite by three orders of magnitude.
    monkeypatch.setattr(cloud_engine, "_sleep", lambda seconds: None)

    made = Ex5WhisperEngine()
    made.load(EngineOptions(model_ref="https://stt.ex5.ai/v1|KEY|whisper-1"))
    return made


def _upload(tmp_path):
    path = tmp_path / "upload-0.ogg"
    path.write_bytes(b"OggS-not-really")
    return str(path)


def _options(**kwargs) -> EngineOptions:
    return EngineOptions(model_ref="x", language="tr", **kwargs)


def _http_error(code: int, body: bytes = b'{"detail":"nope"}') -> urllib.error.HTTPError:
    return urllib.error.HTTPError(
        url="https://stt.ex5.ai/v1/audio/transcriptions",
        code=code,
        msg="nope",
        hdrs={},  # type: ignore[arg-type]
        fp=io.BytesIO(body),
    )


def test_registry_knows_the_engine():
    assert isinstance(create("cloud-ex5"), Ex5WhisperEngine)


# ---- the synchronous request -------------------------------------------------


def test_word_timestamps_are_asked_for_the_way_this_server_declares_them(engine, tmp_path):
    """
    The whole reason this engine exists.

    The server is FastAPI and declares ``timestamp_granularities`` as a plain string. FastAPI
    drops form fields it has not declared, so OpenAI's ``timestamp_granularities[]`` is discarded
    in silence: 200, a good transcript, and no words at all.
    """
    url, headers, body = engine._sync_request(_upload(tmp_path), _options())
    text = body.decode("utf-8", errors="replace")

    assert url == "https://stt.ex5.ai/v1/audio/transcriptions"
    assert headers["Authorization"] == "Bearer KEY"

    assert 'name="timestamp_granularities"\r\n\r\nword' in text
    assert 'name="timestamp_granularities[]"' not in text
    assert 'name="response_format"\r\n\r\nverbose_json' in text
    assert 'name="model"\r\n\r\nwhisper-1' in text
    assert 'name="language"\r\n\r\ntr' in text


def test_a_mixed_language_call_leaves_the_language_to_the_service(engine, tmp_path):
    _, _, body = engine._sync_request(_upload(tmp_path), _options(multilingual=True))

    assert 'name="language"' not in body.decode("utf-8", errors="replace")


# ---- the job request ---------------------------------------------------------


def test_the_job_request_sends_only_the_four_fields_that_endpoint_declares(engine, tmp_path):
    """
    A different, smaller shape — not the transcription request pointed at another path.

    The job endpoint declares file, language, prompt and word_timestamps and nothing else. Sending
    model or response_format here would be the same mistake as the bracketed granularity field,
    just in the other direction.
    """
    url, headers, body = engine._job_request(
        _upload(tmp_path), _options(initial_prompt="Sumsub, KYC"))
    text = body.decode("utf-8", errors="replace")

    assert url == "https://stt.ex5.ai/v1/jobs"
    assert headers["Authorization"] == "Bearer KEY"

    assert 'name="word_timestamps"\r\n\r\ntrue' in text
    assert 'name="language"\r\n\r\ntr' in text
    assert 'name="prompt"\r\n\r\nSumsub, KYC' in text

    assert 'name="model"' not in text
    assert 'name="response_format"' not in text
    assert 'name="timestamp_granularities"' not in text


# ---- which door a piece goes through -----------------------------------------


def _record_urls(engine, monkeypatch, answers: dict[str, dict]):
    """Answer by URL and remember the order the engine asked in."""
    seen: list[str] = []

    def send(url, headers, body=None):
        seen.append(url)
        return answers[url]

    monkeypatch.setattr(engine, "_send", send)
    monkeypatch.setattr(engine, "_poll", lambda url: (seen.append(url), answers[url])[1])

    return seen


def test_a_short_piece_goes_through_the_synchronous_endpoint(engine, monkeypatch, tmp_path):
    seen = _record_urls(engine, monkeypatch, {
        "https://stt.ex5.ai/v1/audio/transcriptions": {"text": "kısa"},
    })

    engine._chunk_seconds = 90.0
    assert engine._post(_upload(tmp_path), _options()) == {"text": "kısa"}
    assert seen == ["https://stt.ex5.ai/v1/audio/transcriptions"]


def test_a_long_piece_is_submitted_as_a_job(engine, monkeypatch, tmp_path):
    """Twenty minutes is five minutes of work — three times Cloudflare's hundred seconds."""
    seen = _record_urls(engine, monkeypatch, {
        "https://stt.ex5.ai/v1/jobs": {"id": "j-1", "status": "queued"},
        "https://stt.ex5.ai/v1/jobs/j-1": {"status": "completed", "result": {"text": "uzun"}},
    })

    engine._chunk_seconds = 1200.0
    assert engine._post(_upload(tmp_path), _options()) == {"text": "uzun"}
    assert seen[0] == "https://stt.ex5.ai/v1/jobs"


def test_a_piece_of_unknown_length_is_submitted_as_a_job(engine, monkeypatch, tmp_path):
    """The safe default. Nothing sets _chunk_seconds outside transcribe(), and 524 costs a piece."""
    seen = _record_urls(engine, monkeypatch, {
        "https://stt.ex5.ai/v1/jobs": {"id": "j-2", "status": "queued"},
        "https://stt.ex5.ai/v1/jobs/j-2": {"status": "completed", "result": {"text": "bilinmeyen"}},
    })

    assert engine._post(_upload(tmp_path), _options()) == {"text": "bilinmeyen"}
    assert seen[0] == "https://stt.ex5.ai/v1/jobs"


def test_a_synchronous_request_cut_off_by_cloudflare_falls_back_to_the_job_api(
        engine, monkeypatch, tmp_path):
    """
    524 means the origin was still transcribing when the proxy gave up.

    The audio is uploaded and encoded by this point, the server may even have finished the work,
    and the recording exists once. Failing here would throw away a conversation over a proxy's
    patience, so the piece goes round again through the endpoint built for exactly this.
    """
    seen: list[str] = []

    def send(url, headers, body=None):
        seen.append(url)

        if url.endswith("/audio/transcriptions"):
            raise engine._fatal(524, url, "")

        return {"id": "j-3", "status": "queued"}

    monkeypatch.setattr(engine, "_send", send)
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "completed", "result": {"text": "kurtarıldı"}})

    engine._chunk_seconds = 120.0

    assert engine._post(_upload(tmp_path), _options()) == {"text": "kurtarıldı"}
    assert seen == [
        "https://stt.ex5.ai/v1/audio/transcriptions",
        "https://stt.ex5.ai/v1/jobs",
    ]


def test_a_refused_key_on_the_synchronous_path_is_not_retried_as_a_job(engine, monkeypatch, tmp_path):
    """A wrong key is wrong at both doors. Trying the second only delays saying so."""
    def send(url, headers, body=None):
        raise engine._fatal(401, url, "")

    monkeypatch.setattr(engine, "_send", send)
    engine._chunk_seconds = 60.0

    with pytest.raises(EngineError) as caught:
        engine._post(_upload(tmp_path), _options())

    assert caught.value.code == "auth"


# ---- waiting for a job -------------------------------------------------------


def test_a_job_is_polled_until_it_finishes(engine, monkeypatch):
    states = [
        {"status": "queued"},
        {"status": "processing"},
        {"status": "completed", "result": {"text": "bitti", "words": [{"word": "bitti"}]}},
    ]
    monkeypatch.setattr(engine, "_poll", lambda url: states.pop(0))

    assert engine._await_job({"id": "j-4"})["text"] == "bitti"
    assert states == []


def test_a_blip_while_polling_is_waited_out_rather_than_re_uploading(engine, monkeypatch):
    """
    The job is on the server and our connection has nothing to do with it.

    Letting a single 502 escape would send the piece back through _post_with_retry, which uploads
    the whole thing again and queues a second copy of work already being done — on a server that
    runs one job at a time, that is the slowest possible way to answer a transient error.
    """
    states = [None, None, {"status": "completed", "result": {"text": "sabır"}}]
    monkeypatch.setattr(engine, "_poll", lambda url: states.pop(0))

    assert engine._await_job({"id": "j-5"}) == {"text": "sabır"}


def test_a_failed_job_says_so_rather_than_waiting_out_the_hour(engine, monkeypatch):
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "failed", "error": "ses çözülemedi"})

    with pytest.raises(EngineError) as caught:
        engine._await_job({"id": "j-6"})

    assert caught.value.code == "api_error"
    assert "ses çözülemedi" in str(caught.value)


def test_a_job_that_never_finishes_ends_with_a_timeout_naming_the_job(engine, monkeypatch):
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "processing"})

    with pytest.raises(EngineError) as caught:
        engine._await_job({"id": "j-7"})

    assert caught.value.code == "timeout"
    assert "j-7" in str(caught.value)


def test_a_submission_without_a_job_number_is_an_error_not_an_endless_wait(engine):
    with pytest.raises(EngineError) as caught:
        engine._await_job({"status": "queued"})

    assert caught.value.code == "api_error"


def test_a_finished_job_carrying_the_transcript_at_the_top_level_is_still_read(engine, monkeypatch):
    """The result key is documented; a shape that is recognisably a transcript is not refused."""
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "done", "text": "düz"})

    assert engine._await_job({"id": "j-8"}) == {"status": "done", "text": "düz"}


def test_a_finished_job_with_nothing_in_it_is_an_error(engine, monkeypatch):
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "completed", "result": None})

    with pytest.raises(EngineError):
        engine._await_job({"id": "j-9"})


def test_a_job_the_server_has_forgotten_is_reported_rather_than_polled_forever(engine, monkeypatch):
    def urlopen(request, timeout=None):
        raise _http_error(404)

    monkeypatch.setattr(cloud_engine.urllib.request, "urlopen", urlopen)

    with pytest.raises(EngineError) as caught:
        engine._poll("https://stt.ex5.ai/v1/jobs/gone")

    assert "404" in str(caught.value)


# ---- limits and wording ------------------------------------------------------


def test_the_upload_ceiling_is_this_server_s_and_not_openai_s(engine, tmp_path, monkeypatch):
    """
    95 MB rather than 25.

    It matters on the machine where PyAV is missing: audio then goes up as raw 16 kHz WAV at
    1.92 MB a minute, so a twenty-minute piece is 38 MB. Under OpenAI's ceiling that is a refused
    conversation; under this server's it is merely a slow one.
    """
    assert Ex5WhisperEngine.max_upload_bytes > cloud_engine.MAX_UPLOAD_BYTES
    assert Ex5WhisperEngine.max_upload_bytes < 95 * 1024 * 1024

    big = tmp_path / "big.wav"
    big.write_bytes(b"\0" * 32 * 1024 * 1024)

    engine._check_size(str(big))  # 32 MB: refused by the shared engine, fine here


def test_the_documented_refusals_are_said_in_words_rather_than_status_codes(engine):
    too_large = engine._fatal(413, "https://stt.ex5.ai/v1/jobs", "")
    assert too_large.code == "too_large"
    assert "95 MB" in str(too_large)

    bad_audio = engine._fatal(400, "https://stt.ex5.ai/v1/jobs", "")
    assert bad_audio.code == "bad_audio"

    # Inherited from the shared engine, which is where the wording belongs: both are ordinary
    # HTTP and mean the same thing at every provider.
    assert engine._fatal(401, "u", "").code == "auth"
    assert engine._fatal(524, "u", "").code == "timeout"
