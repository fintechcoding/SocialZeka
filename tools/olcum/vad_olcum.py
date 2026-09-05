"""VAD sınırları eklenince ne değişiyor — altı görüşme, iki motor."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r'C:\Voice\VoiceTranscript\worker')

from prototip import load, to_segments, swallowed, split_sentences, CALLS
from vt_worker import turns

OUT = os.path.dirname(os.path.abspath(__file__))

print(f'{"görüşme":<22} {"motor":<7} {"durum":<9} {"satır":>6} {"yutulan":>8} {"bölge":>7}')
print('-' * 66)

for call, label in CALLS.items():
    regions = {}
    for ch in ('mic', 'far'):
        p = os.path.join(OUT, f'call-{call}-{ch}.wav')
        regions[ch] = turns.speech_regions(p) if os.path.exists(p) else []

    if not regions['mic']:
        continue

    for engine in ('yerel', 'bulut'):
        data = load(call, engine)
        if not data:
            continue

        mic, far = to_segments(data, True), to_segments(data, False)
        before = sorted(mic + far, key=lambda s: s.start)

        mic2 = turns.split_after_silence(mic, regions['mic'])
        far2 = turns.split_after_silence(far, regions['far'])
        after = sorted(mic2 + far2, key=lambda s: s.start)

        for state, lines in (('bugün', before), ('vad', after)):
            n = len(regions['mic']) + len(regions['far']) if state == 'vad' else 0
            print(f'{("#" + str(call) + " " + label):<22} {engine:<7} {state:<9} '
                  f'{len(lines):>6} {swallowed(lines):>8} {n if n else "":>7}')
