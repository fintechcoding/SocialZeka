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






# ---- the job request ---------------------------------------------------------


def test_the_job_request_sends_only_the_four_fields_that_endpoint_declares(engine, tmp_path):
    """
    A different, smaller shape — not the transcription request pointed at another path.

    The job endpoint declares file, language, prompt, word_timestamps and a handful of decoder
    flags, and nothing else. Sending model or response_format here would be the same mistake as
    the bracketed granularity field, just in the other direction.
    """
    url, headers, body = engine._job_request(_upload(tmp_path), _options())
    text = body.decode("utf-8", errors="replace")

    assert url == "https://stt.ex5.ai/v1/jobs"
    assert headers["Authorization"] == "Bearer KEY"

    assert 'name="word_timestamps"\r\n\r\ntrue' in text

    # On, not off. Off would hand us the model's repetition loops with no filter of our own;
    # on, we get the clean transcript plus a labelled list of what was taken out.
    assert 'name="filter_noise"\r\n\r\ntrue' in text

    # The same thing the local engine does, in the decoder rather than by cutting the file:
    # local gets this audio right with vad_filter=True, on the processor as well as the card.
    assert 'name="vad"\r\n\r\ntrue' in text
    assert 'name="language"\r\n\r\ntr' in text
    # And no prompt, ever. The endpoint declares one and it is the wrong field for a vocabulary:
    # a prompt is text the decoder is told it has already written, so it continues the style of
    # it, and a comma-separated list of capitalised terms is a style. Measured on one real
    # recording, the same 180 seconds with and without — with it the transcript came back as
    # "Yani, Uzun, Bir, Süre, Tabii, İşin..." and without it as the conversation.
    assert 'name="prompt"' not in text

    assert 'name="model"' not in text
    assert 'name="response_format"' not in text
    assert 'name="timestamp_granularities"' not in text


def test_the_server_is_told_not_to_normalise_a_channel_that_is_mostly_silent(engine, tmp_path):
    """
    Normalising one side of a call is gain applied to whatever is in the window, and for most of
    a conversation what is in this window is room tone.

    Measured against the live service on 2026-09-03 with sixty seconds of synthetic room tone —
    no speech, -55 dBFS. With the server's default (normalize=true) it came back with two
    hallucinated lines; with normalize=false, one, and the service's own filter caught that one
    as ``konusma_degil(no_speech=0.89)``. Nothing is given up: the recorder captures at a fixed
    gain and these files already peak at full scale.
    """
    _url, _headers, body = engine._job_request(_upload(tmp_path), _options())

    assert 'name="normalize"\r\n\r\nfalse' in body.decode("utf-8", errors="replace")


def test_the_cached_answer_belongs_to_the_request_that_produced_it(engine, monkeypatch, tmp_path):
    """
    Change a flag, press "yeniden yazıya dök", and the old answer must not come back.

    The workspace beside a recording exists so a rate limit does not cost a forty-minute upload
    twice, and it survives exactly the failures people retry after. But the key was the model and
    the chunk number and nothing else, so every fix to the request replayed the answer from before
    the fix, byte for byte — which reads as "the fix did nothing" and sends somebody looking for a
    different one.
    """
    chunk = cloud_engine.plan_chunks.__globals__["Chunk"](0, 0.0, 47.0)
    wav = tmp_path / "call.wav"
    wav.write_bytes(b"RIFF" + bytes(2048))

    answers = iter([{"text": "önce"}, {"text": "sonra"}])
    monkeypatch.setattr(engine, "_send", lambda url, headers, body=None: {"id": "j"})
    monkeypatch.setattr(
        engine, "_poll", lambda url: {"status": "completed", "result": next(answers)})

    first = engine._chunk_segments(str(wav), chunk, _options(), str(tmp_path), 1)
    again = engine._chunk_segments(str(wav), chunk, _options(), str(tmp_path), 1)

    # The same request is still answered from disk — that is what the workspace is for.
    assert [s.text for s in first] == [s.text for s in again] == ["önce"]

    # A different language is a different request, so it is asked again rather than replayed.
    changed = engine._chunk_segments(
        str(wav), chunk, EngineOptions(model_ref="x", language="en"), str(tmp_path), 1)

    assert [s.text for s in changed] == ["sonra"]


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


# ---- who the worker says it is ----------------------------------------------


def test_every_request_carries_a_name_because_the_default_one_is_banned(engine, monkeypatch, tmp_path):
    """
    The 403 that cost a real conversation, and had nothing to do with the key.

    urllib announces "Python-urllib/3.12" unless told otherwise. Cloudflare refuses that outright
    with HTTP 403 and a body of "error code: 1010" — verified against stt.ex5.ai, where the same
    request returns 200 under any real name. It reached the user as "API anahtarı kabul edilmedi
    (403). Ayarlardan anahtarı denetle." and sent them to check a key that was correct.
    """
    sent: dict[str, str] = {}

    class _Response:
        def read(self):
            return b'{"id":"j-ua","status":"queued"}'

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def urlopen(request, timeout=None):
        sent.update(request.headers)
        return _Response()

    monkeypatch.setattr(cloud_engine.urllib.request, "urlopen", urlopen)
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "completed", "result": {"text": "ok"}})

    engine._chunk_seconds = 60.0
    engine._post(_upload(tmp_path), _options())

    # urllib title-cases header names on the way in.
    assert sent["User-agent"].startswith("VoiceTranscript/")
    assert "Python-urllib" not in sent["User-agent"]
    assert sent["Authorization"] == "Bearer KEY"


def test_the_polling_request_carries_it_too(engine, monkeypatch):
    """The job is polled through a separate urllib call, and a ban there loses the same call."""
    sent: dict[str, str] = {}

    class _Response:
        def read(self):
            return b'{"status":"completed","result":{"text":"ok"}}'

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    monkeypatch.setattr(
        cloud_engine.urllib.request, "urlopen",
        lambda request, timeout=None: (sent.update(request.headers), _Response())[1])

    engine._poll("https://stt.ex5.ai/v1/jobs/j-1")

    assert sent["User-agent"].startswith("VoiceTranscript/")


def test_a_proxy_refusing_the_client_is_not_reported_as_a_bad_key(engine):
    """
    403 means two different things and only one of them is the user's to fix.

    Cloudflare's numbered refusals — 1010 for a banned client signature — carry no opinion about
    the key. Telling somebody to check a working key is worse than showing them the raw status,
    because they will do it, and then they will re-enter it, and it will fail again.
    """
    blocked = engine._fatal(403, "https://stt.ex5.ai/v1/jobs", "error code: 1010")

    assert blocked.code == "blocked"
    assert "Anahtarla ilgili değil" in str(blocked)

    # A service that really does refuse the key still says so.
    refused = engine._fatal(403, "https://stt.ex5.ai/v1/jobs", '{"detail":"Invalid API key"}')
    assert refused.code == "auth"


# ---- what actually gets uploaded ---------------------------------------------


def test_a_chunk_that_fits_goes_up_as_recorded(engine, tmp_path):
    """Against our own 95 MB there is nothing to get under, so the encoder never runs."""
    wav = tmp_path / "part0.wav"
    wav.write_bytes(b"RIFF" + bytes(38 * 1024 * 1024))  # twenty minutes of 16 kHz mono PCM

    assert engine._compress(str(wav), str(tmp_path)) == str(wav)


def test_the_upload_says_its_size_and_format(engine, monkeypatch, tmp_path):
    """
    Nobody should have to reverse-engineer a byte count to know what was sent.

    The service operator saw seven uploads, assumed the old 24 kbps Opus, divided words by
    megabytes and concluded transcription had become twenty times worse. It was the arithmetic:
    the same audio uncompressed is thirteen times the bytes, so every ratio against size moves by
    that much and nothing was actually wrong.
    """
    said: list[str] = []
    monkeypatch.setattr(engine, "_send", lambda url, headers, body=None: {"id": "j-size"})
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "completed", "result": {"text": "x"}})

    wav = tmp_path / "call.wav"
    wav.write_bytes(b"RIFF" + bytes(1_500_000))

    engine._chunk_segments(
        str(wav), cloud_engine.plan_chunks.__globals__["Chunk"](0, 0.0, 47.0),
        _options(), str(tmp_path), 1, lambda pct, text: said.append(text))

    assert any("MB" in line and "kayıpsız" in line for line in said), said


def test_everything_goes_through_the_job_queue_whatever_its_length(engine, monkeypatch, tmp_path):
    """
    There is no length at which the synchronous endpoint is safe.

    It was used for anything under three minutes, on the reasoning that a hundred seconds is
    generous for a short piece. The server transcribes one job at a time, so a fifteen-second clip
    submitted while somebody else's hour is running waits behind it — inside our own request. The
    queue decides the timeout, not the length of what we sent.
    """
    seen: list[str] = []
    monkeypatch.setattr(engine, "_send",
                        lambda url, headers, body=None: (seen.append(url), {"id": "j"})[1])
    monkeypatch.setattr(engine, "_poll", lambda url: {"status": "completed", "result": {"text": "x"}})

    for seconds in (5.0, 90.0, 1200.0, 0.0):
        engine._chunk_seconds = seconds
        engine._post(_upload(tmp_path), _options())

    assert seen == ["https://stt.ex5.ai/v1/jobs"] * 4


def test_a_long_job_reports_how_far_it_has_got(engine, monkeypatch):
    """Five minutes of a bar that does not move reads as a hang, not as work."""
    said: list[tuple[float, str]] = []
    engine._progress = lambda pct, note: said.append((pct, note))
    engine._progress_base, engine._progress_span = 0.02, 0.94
    engine._progress_label = "1/1 yazıya dökülüyor"

    states = [{"status": "processing", "progress_percent": 40},
              {"status": "processing", "progress_percent": 80},
              {"status": "completed", "result": {"text": "bitti"}}]
    monkeypatch.setattr(engine, "_poll", lambda url: states.pop(0))

    engine._await_job({"id": "j-1"})

    assert [note for _, note in said] == ["1/1 yazıya dökülüyor · %40", "1/1 yazıya dökülüyor · %80"]
    assert said[0][0] < said[1][0]


def test_a_wait_with_no_percentage_still_says_what_it_is(engine, monkeypatch):
    """
    The state that most needs naming, because it is the one where stopping costs the most.

    The server holds one job at a time, so waiting behind somebody else's hour is normal and can be
    long. Reporting only progress_percent left the bar frozen through all of it, and a bar that has
    not moved in four minutes is indistinguishable from a hung application — somebody watching it
    presses Durdur and throws away a place in a queue rather than a stuck job.
    """
    said: list[str] = []
    engine._progress = lambda pct, note: said.append(note)
    engine._progress_base, engine._progress_span = 0.0, 1.0
    engine._progress_label = "1/1"

    states = [
        {"status": "queued"},
        {"status": "processing"},
        {"status": "processing", "progress_percent": 60, "eta_seconds": 180},
        {"status": "completed", "result": {"text": "bitti"}},
    ]
    monkeypatch.setattr(engine, "_poll", lambda url: states.pop(0))

    engine._await_job({"id": "j-w"})

    assert said[0] == "1/1 · sunucuda sırada · 5 sn"
    assert said[1] == "1/1 · sunucuda işleniyor · 10 sn"
    assert said[2] == "1/1 · %60, ~3 dk kaldı"


def test_a_blip_does_not_reset_how_long_it_has_been_waiting(engine, monkeypatch):
    """A dropped poll is not progress. The clock the user reads has to keep running."""
    said: list[str] = []
    engine._progress = lambda pct, note: said.append(note)
    engine._progress_label = "1/1"

    states = [{"status": "queued"}, None, {"status": "queued"},
              {"status": "completed", "result": {"text": "x"}}]
    monkeypatch.setattr(engine, "_poll", lambda url: states.pop(0))

    engine._await_job({"id": "j-b"})

    assert said == ["1/1 · sunucuda sırada · 5 sn", "1/1 · sunucuda sırada · 15 sn"]


# ---- what the server took out ------------------------------------------------


def test_short_answers_the_server_filtered_out_come_back_marked_uncertain(engine):
    """
    A dropped segment is a dropped quote, and we cannot turn the filter off.

    filter_noise is declared on the synchronous request and not on /v1/jobs — the endpoint the
    service recommends and we use. It does report what it removed, and the segments most at risk
    are the ones it is least sure about: a quiet "hı" or "tamam" scored as probably-not-speech.
    Those come back carrying that score, and our own rule marks them uncertain rather than deleting
    them.
    """
    segments = engine._to_segments({
        "segments": [{"start": 0.0, "end": 2.0, "text": "Peki ne zaman?"}],
        "filtered_out": [
            {"start": 2.5, "end": 2.9, "text": "hı", "reason": "konusma_degil(no_speech=0.93)"},
            {"start": 3.0, "end": 3.2, "text": "", "reason": "bos"},
            {"start": 4.0, "end": 9.0, "text": "abone ol abone ol abone ol",
             "reason": "tekrar_dongusu"},
        ],
    }, offset=60.0)

    assert [s.text for s in segments] == ["Peki ne zaman?", "hı"]

    reinstated = segments[1]
    assert reinstated.start == 62.5                 # the call's timeline, not the chunk's
    assert reinstated.no_speech_prob == 0.93
    assert reinstated.is_low_confidence             # marked, and kept out of the number checks


def test_an_empty_or_hallucinated_segment_is_left_where_it_is(engine):
    """A repetition loop is a known artefact of the model, not something a person said."""
    segments = engine._to_segments({
        "text": "bir şey",
        "filtered_out": [
            {"start": 0.0, "end": 8.0, "text": "abone ol " * 12, "reason": "tekrar_dongusu"},
        ],
    }, offset=0.0)

    assert all("abone ol" not in s.text for s in segments)


def test_a_reason_we_cannot_read_still_counts_as_doubt(engine):
    """The server already decided it was probably not speech; recording certainty would be worse."""
    from vt_worker.engines.ex5_engine import _no_speech_in

    assert _no_speech_in("konusma_degil(no_speech=0.71)") == 0.71
    assert _no_speech_in("konusma_degil") > 0.6


def test_a_response_without_the_field_is_unchanged(engine):
    """Older servers, and every run where the filter took nothing out."""
    assert [s.text for s in engine._to_segments({"text": "sadece metin"}, 0.0)] == ["sadece metin"]
