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

So: short pieces go through the synchronous endpoint, which is one round trip and supports the
model and format fields; everything else is submitted as a job and polled. A synchronous request
that is cut off anyway falls back to the job API rather than failing, because by then the audio has
been uploaded and the only thing left to lose is the conversation.

The job endpoint takes a different, smaller set of fields — ``file``, ``language``, ``prompt`` and
``word_timestamps`` — with no model and no response format. That is not a limitation worth working
around: it is a single-model server, ``word_timestamps`` already defaults to true, and asking for a
field the endpoint does not declare is what this whole file is a warning about.
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

# The service refuses uploads over 95 MB with a 413. Held a little under that so the multipart
# envelope and any header the proxy adds cannot push a body that just fits over the edge.
MAX_UPLOAD_BYTES = 90 * 1024 * 1024

# How much audio may go through the synchronous endpoint.
#
# Three minutes needs about fifty seconds of transcription at the machine's measured 3.8x, which
# leaves half of Cloudflare's hundred for the upload, the model being cold, and a short wait behind
# another job. Anything longer is not worth the gamble when the job API costs one extra round trip.
SYNC_MAX_SECONDS = 180.0

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
    """Whisper large-v3 on our own hardware: OpenAI's shape for short pieces, jobs for the rest."""

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
        """The queued endpoint. Four fields, and no model — the server hosts exactly one."""
        fields: dict[str, str] = {"word_timestamps": "true"}

        if options.language and not options.multilingual:
            fields["language"] = options.language

        if options.initial_prompt:
            fields["prompt"] = options.initial_prompt

        body, content_type = _multipart(fields, Path(path))

        return f"{self._base_url}/jobs", self._headers(content_type), body

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

        for _ in range(int(POLL_CEILING_SECONDS / POLL_SECONDS)):
            cloud_engine._sleep(POLL_SECONDS)

            state = self._poll(url)
            if state is None:
                continue  # a blip on the way to the server; the job is still running

            # How far into the audio the server has got. Without it a twenty-minute chunk is five
            # minutes of a bar that does not move, which reads as a hang rather than as work.
            done = state.get("progress_percent")

            if self._progress is not None and isinstance(done, (int, float)):
                self._progress(
                    self._progress_base + self._progress_span * min(1.0, max(0.0, float(done)) / 100),
                    f"{self._progress_label} · %{float(done):.0f}",
                )

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

            if status and status not in PENDING_STATUSES:
                # An unknown word is not a reason to abandon a job that may still finish, but it
                # is a reason to say what was seen when the wait eventually runs out.
                continue

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
