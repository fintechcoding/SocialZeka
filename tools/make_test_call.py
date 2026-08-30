"""Assemble synthesised utterances into a two-stream test call.

The development machine has no audio hardware, so real dual-stream capture cannot be exercised
there. This produces the same artefacts the capture layer would: two 16 kHz mono WAV files of
identical length, one per speaker, plus a ground-truth file describing exactly who said what and
when. That makes the rest of the pipeline testable without a microphone, a GPU, or a phone call.

Crucially the far-end file is padded with real silence where the far end is not speaking, which
is what a correct capture implementation produces. A capture bug that concatenates packets
instead would produce a shorter file, and comparing against the ground truth catches it.

Usage:
    powershell -File tools/synth_utterances.ps1 -ScriptPath tools/testcall.json -OutDir .work/utt
    python tools/make_test_call.py --utterances .work/utt --script tools/testcall.json --out .work/call
"""

from __future__ import annotations

import argparse
import json
import wave
from pathlib import Path

SAMPLE_RATE = 16_000
CHANNELS = 1
SAMPLE_WIDTH = 2  # 16-bit


def read_pcm(path: Path) -> bytes:
    with wave.open(str(path), "rb") as wav:
        if wav.getframerate() != SAMPLE_RATE or wav.getnchannels() != CHANNELS:
            raise SystemExit(
                f"{path.name}: expected {SAMPLE_RATE} Hz mono, got "
                f"{wav.getframerate()} Hz / {wav.getnchannels()} ch"
            )
        return wav.readframes(wav.getnframes())


def frames(pcm: bytes) -> int:
    return len(pcm) // (CHANNELS * SAMPLE_WIDTH)


def silence(frame_count: int) -> bytes:
    return b"\x00" * (frame_count * CHANNELS * SAMPLE_WIDTH)


def build_stream(lines: list[dict], utterances: dict[str, bytes], want: str, total_frames: int) -> bytes:
    """Lay this speaker's utterances onto an otherwise silent timeline."""
    buffer = bytearray()

    for line in lines:
        if line["voice"] != want:
            continue

        start_frame = int(round(float(line["start"]) * SAMPLE_RATE))
        if start_frame < frames(buffer):
            raise SystemExit(
                f"utterance {line['id']} starts at {line['start']}s but the stream is already "
                f"{frames(buffer) / SAMPLE_RATE:.2f}s long — the test script overlaps itself"
            )

        buffer += silence(start_frame - frames(buffer))
        buffer += utterances[line["id"]]

    if frames(buffer) < total_frames:
        buffer += silence(total_frames - frames(buffer))

    return bytes(buffer)


def write_wav(path: Path, pcm: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(CHANNELS)
        wav.setsampwidth(SAMPLE_WIDTH)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(pcm)


def main() -> int:
    parser = argparse.ArgumentParser(description="Assemble a synthetic two-stream call")
    parser.add_argument("--utterances", required=True, type=Path)
    parser.add_argument("--script", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    lines: list[dict] = json.loads(args.script.read_text(encoding="utf-8"))
    utterances = {line["id"]: read_pcm(args.utterances / f"{line['id']}.wav") for line in lines}

    # Give each utterance its real duration, then work out how long the call actually is.
    truth = []
    end_of_call = 0.0
    for line in lines:
        duration = frames(utterances[line["id"]]) / SAMPLE_RATE
        start = float(line["start"])
        end = start + duration
        end_of_call = max(end_of_call, end)
        truth.append(
            {
                "id": line["id"],
                "speaker": "me" if line["voice"] == "mic" else "them",
                "start": round(start, 3),
                "end": round(end, 3),
                "text": line["text"],
            }
        )

    total_frames = int(round(end_of_call * SAMPLE_RATE))

    mic = build_stream(lines, utterances, "mic", total_frames)
    far = build_stream(lines, utterances, "far", total_frames)

    # The property that matters: both streams cover the same wall-clock span.
    assert frames(mic) == frames(far) == total_frames, "streams must be the same length"

    write_wav(args.out / "mic.wav", mic)
    write_wav(args.out / "far.wav", far)
    (args.out / "truth.json").write_text(
        json.dumps({"duration": round(end_of_call, 3), "segments": truth}, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )

    print(f"call length : {end_of_call:.2f}s")
    print(f"mic.wav     : {frames(mic) / SAMPLE_RATE:.2f}s")
    print(f"far.wav     : {frames(far) / SAMPLE_RATE:.2f}s")
    print(f"utterances  : {len(truth)}")
    print(f"written to  : {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
