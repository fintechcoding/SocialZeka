"""
The self-hosted Whisper server at stt.ex5.ai, which has two doors and only one of them is safe.

It copies OpenAI's ``POST /v1/audio/transcriptions`` closely enough that the ordinary cloud engine
would appear to work, and that appearance is the trap. Two differences decide whether a
conversation survives, and both are invisible until a real call has already been recorded:

  **The field is ``timestamp_granularities``, not ``timestamp_granularities[]``, and it takes a
  plain string rather than a repeated field.** The service is FastAPI, and FastAPI silently drops
  form fields it does not declare. Sending OpenAI's spelling therefore returns 200 with a perfectly
  good transcript and no ``words`` array at all — every quote in the ledger loses the moment it was
  spoken, and nothing anywhere reports a problem. Verified against the server's own published
  schema (``GET /openapi.json``, no key required), not against the prose.

  **A long request is cut at 100 seconds.** Cloudflare sits in front of the origin, and the
  server's own description of ``POST /v1/jobs`` says in as many words that the job API exists to
  get past that timeout. The machine transcribes at roughly 3.8x real time, so 100 seconds buys
  about six minutes of audio *if the queue is empty* — and it holds one job at a time, so a chunk
  of any length can sit behind somebody else's hour. Duration alone cannot make a synchronous
  request safe.

So everything goes through ``POST /v1/jobs``. Short pieces were sent synchronously for a while, on
the reasoning that a hundred seconds is generous for a short piece; the server's operator supplied
the fact that ends that argument, which is that the machine transcribes **one job at a time**. A
fifteen-second clip submitted while somebody else's hour is running waits behind it, and the wait
is spent inside our own request — so the timeout is decided by the queue and not by what we sent.
No duration is safe, and the first difference above stops mattering as well: the job endpoint asks
for word timestamps by its own name and defaults them to true.

That endpoint takes a smaller set of fields — ``file``, ``language``, ``prompt``,
``word_timestamps``, ``filter_noise`` — with no model and no response format. Not a limitation
worth working around: it is a single-model server, and asking for a field the endpoint does not
declare is what this whole file is a warning about.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from vt_worker.engines import cloud_engine
from vt_worker.engines.base import EngineError, EngineOptions
from vt_worker.engines.cloud_engine import CloudWhisperEngine, _multipart
from vt_worker.merge import Segment, Speaker

# The service refuses uploads over 95 MB with a 413. Held a little under that so the multipart
# envelope and any header the proxy adds cannot push a body that just fits over the edge.
MAX_UPLOAD_BYTES = 90 * 1024 * 1024

# How often to ask whether a submitted job has finished, and how long to keep asking.
#
# Five seconds is the interval the service documents. The ceiling is an hour because the queue
# holds one job at a time: a twenty-minute chunk is about five minutes of work, so an hour is
# roughly ten conversations' worth of other people's audio ahead of ours — generous enough that
# hitting it means something is actually wrong, not that the service is busy.
POLL_SECONDS = 5.0
POLL_CEILING_SECONDS = 3600.0

# The names a job can go by while it is still worth waiting for, and the ones that end it.
PENDING_STATUSES = frozenset({"queued", "pending", "processing", "running", "started"})
DONE_STATUSES = frozenset({"completed", "complete", "succeeded", "success", "done", "finished"})
FAILED_STATUSES = frozenset({"failed", "error", "cancelled", "canceled", "expired"})


class Ex5WhisperEngine(CloudWhisperEngine):
    """Whisper large-v3 on our own hardware, always through the job queue."""

    name = "cloud-ex5"

    max_upload_bytes = MAX_UPLOAD_BYTES

    def _post(self, path: str, options: EngineOptions) -> dict:
        """
        Every piece goes through the job queue. There is no length at which the other door is safe.

        The synchronous endpoint was used for anything under three minutes, on the reasoning that
        Cloudflare's hundred seconds is generous for a short piece. The server's operator supplied
        the fact that breaks that reasoning: the machine transcribes one job at a time. A
        fifteen-second clip submitted while somebody else's hour is being transcribed waits behind
        it, and the wait is spent inside our own request — so the timeout is decided by the queue,
        not by the length of what we sent. Falling back on 524 recovered the conversation but paid
        for the upload twice.

        Nothing is given up by dropping it. The job endpoint takes the fields that matter, defaults
        word_timestamps to true, and costs one polling interval.
        """
        self._check_size(path)

        url, headers, body = self._job_request(path, options)

        return self._await_job(self._send(url, headers, body))

    # ---- the request --------------------------------------------------------

    def _job_request(self, path: str, options: EngineOptions) -> tuple[str, dict[str, str], bytes]:
        """
        The queued endpoint, with the hallucination filter deliberately left on.

        Turning it off is now possible and would be the wrong choice. Off, the response carries
        everything the model produced, including its repetition loops — "abone ol" twenty times
        over a silence — and we have no filter of our own to catch them; they would land in the
        ledger under the same rules as evidence. On, the transcript is clean *and* the response
        lists what was removed with a reason for each, which is strictly more than we get by
        refusing the filter: see _to_segments, where the ones that might be real speech come back
        marked uncertain and the known artefacts stay out.

        Sent explicitly rather than left to the default. It happens to default to true today; a
        server-side change to that default would quietly start feeding hallucinations into
        somebody's conversation, and a request that states what it wants cannot drift.
        """
        fields = self._job_fields(options)
        body, content_type = _multipart(fields, Path(path))

        return f"{self._base_url}/jobs", self._headers(content_type), body

    def _job_fields(self, options: EngineOptions) -> dict[str, str]:
        """
        The form fields of one job, apart from the file.

        Separate from the request so the cache key can be taken from exactly what was asked. A
        chunk's answer is kept on disk so a rate limit does not cost the upload again, and an
        answer produced under different flags is a different answer.
        """
        fields: dict[str, str] = {
            "word_timestamps": "true",
            "filter_noise": "true",
            # The same thing the local engine does, in the place it belongs.
            #
            # Local runs faster-whisper with vad_filter=True and gets this recording right — the
            # processor model gets it right too, so the difference was never the model. Whisper
            # given a long silence writes into it, and this application records the two sides of a
            # call separately, so one channel is quiet for most of a conversation.
            #
            # Cutting that silence out of the file ourselves was tried and made things worse: the
            # splices produced repetition loops, measured at a compression ratio of 8.35 against a
            # threshold of 2.4. A VAD inside the decoder is not the same operation — it skips
            # windows, it does not join unrelated audio together, and there is no seam to hallucinate
            # at. Which is why this belongs on the request and not in our own audio.
            #
            # And it is the single largest thing we control. Measured 2026-09-03 against 180
            # seconds of a real call carrying 157 seconds of speech, scored on how many of those
            # seconds came back with words on them:
            #
            #     server defaults (vad off)            108/157   20 lines   1 hallucinated
            #     vad=true                             151/157   43 lines   0 hallucinated
            #     the local engine, faster-whisper     150/157    8 lines   0 hallucinated
            #
            # With it the service is level with the local engine and finer-grained; without it,
            # a third of the conversation is missing — and missing quietly, which in a record of
            # what somebody said is worse than an invented line, because nothing looks wrong.
            #
            # A caution for whoever measures this next: on sixty seconds of synthetic room tone
            # the flag changes nothing at all, byte for byte, same timestamps. It was very nearly
            # written off on that basis. Silence is not the case it acts on.
            "vad": "true",
            # normalize is deliberately NOT sent, and the server's default (on) is what we want.
            #
            # It was sent as false for a few hours on the strength of the room-tone clip, where
            # turning it off halved the hallucinated lines — the reasoning being that gain applied
            # to a window with no speech in it only lifts the room tone to where a decoder will
            # transcribe it. That reasoning is sound and the conclusion was still wrong: on real
            # speech, normalize=false costs 23 of those 157 seconds (151 -> 128) and adds back a
            # hallucinated line. Whatever it does to silence, it helps the model hear.
        }

        if options.language and not options.multilingual:
            fields["language"] = options.language

        return fields

    def _request_signature(self, options: EngineOptions) -> dict:
        """The cache key sees every flag, so turning one on invalidates the answers from before."""
        return self._job_fields(options)

    def _headers(self, content_type: str) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._api_key}", "Content-Type": content_type}

    # ---- waiting for a job --------------------------------------------------

    def _await_job(self, submitted: dict) -> dict:
        """
        Poll a submitted job until it has an answer, and return that answer in the OpenAI shape.

        Transient failures while polling are waited out rather than raised. The job is already on
        the server and unaffected by our connection; letting a single 502 propagate would send the
        chunk back through _post_with_retry, which would upload the whole thing again and queue a
        second copy of work that is already being done.
        """
        job_id = str(submitted.get("id") or submitted.get("job_id") or "").strip()

        if not job_id:
            raise EngineError(
                "api_error",
                f"Servis işi kabul etti ama bir iş numarası vermedi: {json.dumps(submitted)[:200]}",
            )

        url = f"{self._base_url}/jobs/{urllib.parse.quote(job_id, safe='')}"
        waited = 0.0

        for _ in range(int(POLL_CEILING_SECONDS / POLL_SECONDS)):
            cloud_engine._sleep(POLL_SECONDS)

            # Counted here, not after a successful read: a dropped poll is not progress, but the
            # time still passed and the clock the user reads has to agree with their own.
            waited += POLL_SECONDS

            state = self._poll(url)
            if state is None:
                continue  # a blip on the way to the server; the job is still running

            status = str(state.get("status") or "").strip().lower()

            if status in DONE_STATUSES:
                return _result_of(state, job_id)

            if status in FAILED_STATUSES:
                detail = str(state.get("error") or state.get("detail") or "").strip()

                raise EngineError(
                    "api_error",
                    f"Servis bu parçayı yazıya dökemedi ({status})."
                    + (f" {detail}" if detail else ""),
                )

            # Still going. Say something, whatever the server chose to tell us.
            #
            # Reporting only progress_percent left the bar frozen for minutes whenever the server
            # did not send one — while queued behind another job, or while transcribing without
            # counting — and a bar that has not moved in four minutes is indistinguishable from a
            # hung application. Somebody watching one cannot tell whether to wait or to press
            # Durdur, and pressing it throws away a place in a queue rather than a stuck job.
            done = state.get("progress_percent")

            if self._progress is not None:
                fraction = (
                    min(1.0, max(0.0, float(done)) / 100)
                    if isinstance(done, (int, float))
                    else 0.0
                )

                self._progress(
                    self._progress_base + self._progress_span * fraction,
                    f"{self._progress_label} · {_waiting_text(status, done, state, waited)}",
                )

            # An unknown word is not a reason to abandon a job that may still finish, but it is a
            # reason to say what was seen when the wait eventually runs out.

        raise EngineError(
            "timeout",
            f"Servis {POLL_CEILING_SECONDS / 60:.0f} dakikada bu parçayı bitirmedi (iş {job_id}). "
            "Kuyrukta başka bir iş olabilir; kayıt duruyor, yeniden denenebilir.",
        )

    def _poll(self, url: str) -> dict | None:
        """One status read. None means "ask again"; an exception means the job is not coming back."""
        request = urllib.request.Request(url, method="GET", headers={
            "Authorization": f"Bearer {self._api_key}",
            # Same reason as the upload: the default urllib header is a 403 from Cloudflare.
            "User-Agent": cloud_engine.USER_AGENT,
        })

        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:400]

            if exc.code == 404:
                raise EngineError(
                    "api_error",
                    f"Servis bu işi tanımıyor (404, {url}). İş sonucu düşmüş olabilir.",
                ) from exc

            if exc.code in cloud_engine.RETRYABLE_STATUS:
                return None

            raise self._fatal(exc.code, url, detail) from exc
        except (urllib.error.URLError, TimeoutError):
            return None
        except json.JSONDecodeError:
            return None

    # ---- what the server took out -------------------------------------------

    def _to_segments(self, payload: dict, offset: float) -> list[Segment]:
        """
        The transcript, plus the short answers the server's own filter removed.

        The service runs a hallucination filter and, on the endpoint it recommends and we use,
        there is no way to turn it off: ``filter_noise`` is declared on the synchronous request and
        not on ``/v1/jobs``. It does now report what it dropped, which is the part we can act on.

        A dropped segment here is a dropped quote. Every line in the ledger is verbatim and carries
        a moment you can play, so a sentence removed upstream leaves a gap nobody can account for —
        and the segments most at risk are exactly the ones this filter is least sure about: "hı",
        "tamam", "aynen", a quiet agreement scored as probably-not-speech.

        So the ones it removed for not sounding like speech come back, carrying the confidence
        score that made it doubt them. Our own rule takes over from there: above 0.6 they are marked
        uncertain and kept out of the automatic contradiction checks, which is the treatment an
        unreliable line should get — marked, not deleted.

        The other two reasons are left where they are. An empty segment is nothing, and a repetition
        loop ("abone ol" twenty times over silence) is a known artefact of the model rather than
        something a person said; reinstating either would put noise in the ledger under the same
        rules as evidence.
        """
        segments = super()._to_segments(payload, offset)

        dropped = payload.get("filtered_out")
        if not isinstance(dropped, list):
            return segments

        for item in dropped:
            if not isinstance(item, dict):
                continue

            reason = str(item.get("reason") or "")
            text = str(item.get("text") or "").strip()

            if not text or not reason.startswith("konusma_degil"):
                continue

            segments.append(
                Segment(
                    speaker=Speaker.ME,  # overwritten by merge_streams
                    start=float(item.get("start", 0.0)) + offset,
                    end=float(item.get("end", 0.0)) + offset,
                    text=text,
                    no_speech_prob=_no_speech_in(reason),
                )
            )

        return sorted(segments, key=lambda seg: seg.start)

    # ---- error wording ------------------------------------------------------

    def _fatal(self, status: int, url: str, detail: str) -> EngineError:
        """The two statuses this service documents that the shared wording cannot know about."""
        if status == 413:
            return EngineError(
                "too_large",
                f"Servis 95 MB'ın üzerindeki yüklemeleri kabul etmiyor (413, {url}). "
                "Opus kodlayıcı (PyAV) kurulu değilse ses sıkıştırılmadan gönderiliyor olabilir.",
            )

        if status == 400:
            return EngineError(
                "bad_audio",
                f"Servis ses dosyasını çözemedi (400, {url}). Kayıt bozuk olabilir.",
            )

        return super()._fatal(status, url, detail)


def _waiting_text(status: str, done: object, state: dict, waited: float) -> str:
    """
    One line saying what the wait is, so it can be told apart from a hang.

    The queue holds one job at a time, so "nothing is happening" is a normal and often long state
    — and it is the one the user most needs named, because it is the one where pressing Durdur
    throws away a place in a queue rather than a stuck job.
    """
    minutes = f"{waited / 60:.0f} dk" if waited >= 60 else f"{waited:.0f} sn"

    if status in ("queued", "pending"):
        return f"sunucuda sırada · {minutes}"

    if isinstance(done, (int, float)):
        eta = state.get("eta_seconds")
        left = f", ~{float(eta) / 60:.0f} dk kaldı" if isinstance(eta, (int, float)) and eta > 60 else ""

        return f"%{float(done):.0f}{left}"

    return f"sunucuda işleniyor · {minutes}"


def _no_speech_in(reason: str) -> float:
    """
    The score the server doubted the segment on, out of "konusma_degil(no_speech=0.93)".

    Falls back to a value just over our own threshold when it cannot be read: the server already
    decided this was probably not speech, and recording that as certainty would be worse than
    recording it as doubt.
    """
    try:
        return float(reason.split("no_speech=")[1].rstrip(") "))
    except (IndexError, ValueError):
        return 0.9


def _result_of(state: dict, job_id: str) -> dict:
    """
    The transcription inside a finished job.

    Documented as ``result``, but a job that reports itself completed and carries the transcription
    at the top level is not a reason to lose the chunk — the shape is checked rather than trusted,
    and only a completed job with nothing recognisable in it is an error.
    """
    result = state.get("result")

    if isinstance(result, dict):
        return result

    if any(key in state for key in ("text", "segments", "words")):
        return state

    raise EngineError(
        "api_error",
        f"Servis işi bitti dedi ama sonuç göndermedi (iş {job_id}): {json.dumps(state)[:200]}",
    )
