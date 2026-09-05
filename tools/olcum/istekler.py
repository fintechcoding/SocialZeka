"""Ölçüm için yerel motora verilecek istekleri yazar."""
import json
import os

OUT = os.path.dirname(os.path.abspath(__file__))
CALLS = (24, 14, 38, 16, 17)

for call in CALLS:
    request = {
        "id": f"olcum-{call}",
        "engine": "faster-whisper",
        "mic_path": os.path.join(OUT, f'call-{call}-mic.wav'),
        "far_path": os.path.join(OUT, f'call-{call}-far.wav'),
        "model_ref": "large-v3",
        "device": "cuda",
        "language": "tr",
        "word_timestamps": True,
    }

    with open(os.path.join(OUT, f'istek-{call}.json'), 'w', encoding='utf-8') as f:
        json.dump(request, f)

print(len(CALLS), 'istek hazır')
