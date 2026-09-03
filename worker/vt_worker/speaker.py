"""
A voice, as 256 numbers — so the far end of a call can be recognised as somebody already known.

This application never has to guess *which side* is speaking: the microphone is the user and the
loopback is the other party, and that is a fact about which file the audio came from. What it does
have to guess is *who the other party is*, and until now the only answer was the call window's
title. The archive records how badly that works — one generic "Voice call" title spread across
eight different contacts, and a migration whose job is to mark such titles unreliable after the
damage.

A speaker embedding answers the same question from the audio. Two recordings of one person land
close together; two people land far apart. Comparing them is a dot product.

**Measured on this application's own archive rather than on a benchmark**, because the published
numbers are about different audio: VoxCeleb is wideband English read speech, and this is 16 kHz
Turkish conversation, most of it decoded back from a 20-24 kbps Opus archive, one side of a phone
call. Three models, thirty-two labelled calls:

    WeSpeaker ResNet34-LM   26 MB    EER 1.1%   <- chosen
    CAM++ (3D-Speaker)      28 MB    EER 1.1%
    WeSpeaker ResNet293-LM 114 MB    EER 0.5%

Four things that measurement settled, and each one shapes the code below:

  **Thirty seconds of speech is a floor, not a preference.** Over the whole set the error rate is
  13.8%; over recordings holding at least thirty seconds of speech it is 1.1%. Below that an
  embedding is noise, and the honest response is to return nothing rather than a number somebody
  will act on.

  **Most of the remaining error is the archive's labels, not the model.** At a twenty-second floor
  a single mislabelled recording moves the error rate from 0.8% to 20.2%. The model is more
  reliable than what it is being scored against.

  **Model size does not help.** The 114 MB model is no better than the 26 MB one on this audio.
  The bottleneck is the recording, so the small one is chosen.

  **Only the far channel is usable.** The user's own microphone, checked against itself — the same
  person by construction, no labels involved — fails to match a third of the time, at every
  duration. The capture hardware changes between calls and channel mismatch is what wrecks speaker
  verification. Nothing here looks at the microphone.

The front end is Kaldi-style 80-bin log-mel, which is what the model was trained on. Getting it
wrong does not fail loudly, it just degrades the scores, so the details below are matched to
Kaldi's implementation deliberately: the Povey window rather than Hann, per-frame DC removal
before pre-emphasis, and the mean subtracted over time at the end.
"""

from __future__ import annotations

import math
import os
import wave
from dataclasses import dataclass

# What the model is, in one place. Written into every voiceprint we store, so that changing the
# model invalidates the stored ones rather than silently comparing vectors from two different
# spaces — which would not error, it would just quietly stop working.
MODEL_REPOSITORY = "Wespeaker/wespeaker-voxceleb-resnet34-LM"
MODEL_FILE = "voxceleb_resnet34_LM.onnx"
MODEL_NAME = "wespeaker-resnet34-LM"

RATE = 16_000
FRAME_LENGTH = 400   # 25 ms
FRAME_SHIFT = 160    # 10 ms
NUM_MEL = 80
FFT = 512

# Below this there is no point asking. Measured: over this archive the error rate is 13.8% with no
# floor and 1.1% with this one. It is the single most load-bearing number in the file.
MIN_SPEECH_SECONDS = 30.0

# What counts as somebody speaking. One side of a call is silent for most of it because the other
# person is talking, and averaging an embedding over that silence averages in the room — which is
# the same room for everybody.
SPEECH_FLOOR_DBFS = -40.0
GATE_BLOCK_MS = 100

# How much audio one pass sees. These models were trained on a few seconds at a time; handing one
# a ten-minute utterance is far outside what it has been shown. Several windows are scored and
# averaged instead.
WINDOW_SECONDS = 6.0
MAX_WINDOWS = 20


@dataclass(slots=True)
class Voiceprint:
    """One voice as a unit vector, with the evidence behind it."""

    vector: list[float]
    speech_seconds: float
    windows: int
    model: str


def _povey_window() -> list[float]:
    """Kaldi's window: Hann raised to 0.85, not Hamming. The difference is small and it matters."""
    return [
        (0.5 - 0.5 * math.cos(2 * math.pi * i / (FRAME_LENGTH - 1))) ** 0.85
        for i in range(FRAME_LENGTH)
    ]


def _mel_bank(low: float = 20.0, high: float = RATE / 2):
    """Triangular filters spaced evenly in mel, over the power spectrum, on HTK's mel scale."""
    import numpy as np

    def to_mel(hz):
        return 1127.0 * np.log(1.0 + hz / 700.0)

    def from_mel(mel):
        return 700.0 * (np.exp(mel / 1127.0) - 1.0)

    bins = FFT // 2 + 1
    freqs = np.linspace(0, RATE / 2, bins)
    points = from_mel(np.linspace(to_mel(low), to_mel(high), NUM_MEL + 2))

    bank = np.zeros((NUM_MEL, bins), dtype=np.float32)
    for i in range(NUM_MEL):
        left, centre, right = points[i], points[i + 1], points[i + 2]
        rising = (freqs - left) / max(centre - left, 1e-9)
        falling = (right - freqs) / max(right - centre, 1e-9)
        bank[i] = np.clip(np.minimum(rising, falling), 0, None)

    return bank


_WINDOW = None
_BANK = None


def _front_end():
    """The window and the filterbank, built once. numpy is imported lazily so that importing this
    module costs nothing on the many code paths that never touch a voice."""
    global _WINDOW, _BANK

    if _BANK is None:
        import numpy as np

        _WINDOW = np.array(_povey_window(), dtype=np.float32)
        _BANK = _mel_bank()

    return _WINDOW, _BANK


def fbank(samples):
    """80-bin log-mel with the mean removed over time, which is the shape the model expects."""
    import numpy as np

    window, bank = _front_end()

    count = 1 + (len(samples) - FRAME_LENGTH) // FRAME_SHIFT
    if count < 25:
        return np.zeros((0, NUM_MEL), dtype=np.float32)

    frames = np.lib.stride_tricks.as_strided(
        samples,
        shape=(count, FRAME_LENGTH),
        strides=(samples.strides[0] * FRAME_SHIFT, samples.strides[0]),
    ).astype(np.float32).copy()

    frames -= frames.mean(axis=1, keepdims=True)   # DC per frame, before pre-emphasis, as Kaldi does
    frames[:, 1:] -= 0.97 * frames[:, :-1]
    frames[:, 0] -= 0.97 * frames[:, 0]
    frames *= window

    power = np.abs(np.fft.rfft(frames, n=FFT)) ** 2
    mel = np.log(np.maximum(power @ bank.T, 1e-10))

    return (mel - mel.mean(axis=0, keepdims=True)).astype(np.float32)


def speech_only(samples):
    """The parts where somebody is speaking, concatenated.

    Cutting silence out of audio that is going to be *transcribed* was tried in this project and
    made things worse — the splices produced repetition loops. This is a different operation with
    no such risk: nothing here is transcribed and no timestamp survives, so joining two speech
    regions has no seam anybody can hear the consequences of. What it buys is an embedding of the
    voice rather than of the room.
    """
    import numpy as np

    block = RATE * GATE_BLOCK_MS // 1000
    if len(samples) < block:
        return samples[:0]

    usable = len(samples) - (len(samples) % block)
    blocks = samples[:usable].reshape(-1, block).astype(np.float32)

    rms = np.sqrt(np.mean(blocks * blocks, axis=1))
    level = 20 * np.log10(np.maximum(rms, 1e-9) / 32768)

    kept = blocks[level > SPEECH_FLOOR_DBFS]
    return kept.reshape(-1).astype(np.int16) if kept.size else samples[:0]


def read_wav(path: str):
    """16 kHz mono 16-bit, which is what this application records and what the model wants."""
    import numpy as np

    with wave.open(path, "rb") as wav:
        if wav.getnchannels() != 1 or wav.getsampwidth() != 2:
            raise ValueError(
                f"{os.path.basename(path)}: 16 bit mono bekleniyor, "
                f"{wav.getnchannels()} kanal / {wav.getsampwidth() * 8} bit geldi")

        return np.frombuffer(wav.readframes(wav.getnframes()), dtype=np.int16)


def model_path(cache_dir: str | None = None) -> str:
    """Where the weights are, fetching them once if they are not there yet.

    Kept out of models.py because the checks there are Whisper's: a repository is considered
    present when it holds a .bin of at least twenty megabytes, and this is a single .onnx. Sharing
    that code would mean teaching it about a second shape for no benefit.
    """
    from huggingface_hub import hf_hub_download

    from vt_worker.models import resolve_cache_dir

    return hf_hub_download(
        repo_id=MODEL_REPOSITORY,
        filename=MODEL_FILE,
        cache_dir=resolve_cache_dir(cache_dir),
    )


_SESSION = None


def _session(path: str):
    """One ONNX session per process. The worker runs one job per process, so this is once."""
    global _SESSION

    if _SESSION is None:
        import onnxruntime

        # CPU deliberately. The GPU build of onnxruntime is forbidden in this environment — see
        # worker/requirements.txt — and a 26 MB model over a handful of six-second windows is a
        # few hundred milliseconds on a processor anyway.
        _SESSION = onnxruntime.InferenceSession(path, providers=["CPUExecutionProvider"])

    return _SESSION


def embed(wav_path: str, cache_dir: str | None = None) -> Voiceprint | None:
    """
    One voice from one recording, or None when the recording cannot answer the question.

    None is returned rather than a weak vector on purpose. A caller that receives a number will
    compare it against somebody; a caller that receives nothing cannot. Below MIN_SPEECH_SECONDS
    the comparison is measurably not worth making, so the refusal happens here where the evidence
    is, not at each call site.
    """
    import numpy as np

    samples = read_wav(wav_path)
    speech = speech_only(samples)
    seconds = len(speech) / RATE

    if seconds < MIN_SPEECH_SECONDS:
        return None

    session = _session(model_path(cache_dir))
    name = session.get_inputs()[0].name
    step = int(RATE * WINDOW_SECONDS)

    vectors = []
    for start in range(0, len(speech) - step + 1, step):
        features = fbank(speech[start:start + step])
        if features.shape[0] < 25:
            continue

        out = session.run(None, {name: features[None, :, :]})[0][0]
        norm = float(np.linalg.norm(out))
        if norm > 0:
            vectors.append(out / norm)

        if len(vectors) >= MAX_WINDOWS:
            break

    if not vectors:
        return None

    mean = np.mean(vectors, axis=0)
    mean = mean / np.linalg.norm(mean)

    return Voiceprint(
        vector=[float(x) for x in mean],
        speech_seconds=round(seconds, 1),
        windows=len(vectors),
        model=MODEL_NAME,
    )
