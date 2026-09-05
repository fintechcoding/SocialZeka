"""Yerel ve bulut, aynı seslerin üzerinde, altı görüşme.

Üç soruyu ayrı ayrı ölçer:
  ne kadarını duydu      — kelime, konuşma kapsaması, belirsiz satır
  damgalar hizalı mı     — sesin enerjisiyle örtüşme ve en iyi kayma
  sohbet okunuyor mu     — bölünmüş cümle, yutulan cevap, noktalama
"""
import io
import json
import os
import wave

import numpy as np

OUT = os.path.dirname(os.path.abspath(__file__))
BLOCK_MS = 50

CALLS = {
    24: 'Uliana · 1 Eyl 15:14 · 12:29',
    14: 'Serdal · 30 Ağu 17:54 · 08:39',
    38: 'Bozkurt · 2 Eyl 13:51 · 07:02',
    16: 'Sinan · 30 Ağu 19:01 · 05:18',
    60: 'Uliana · 3 Eyl 21:51 · 03:20',
    17: 'Avukat Polonya · 31 Ağu 12:52 · 02:44',
}

SHORT, NEAR = 1.5, 4.0


def last_result(path):
    if not os.path.exists(path):
        return None

    found = None
    for line in io.open(path, encoding='utf-8-sig'):
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        if isinstance(row, dict) and row.get('segments') is not None:
            found = row

    return found


def load(call, engine):
    name = f'cikti-{call}.jsonl' if engine == 'yerel' else f'bulut-cikti-{call}.jsonl'
    return last_result(os.path.join(OUT, name))


def speech_mask(path):
    with wave.open(path, 'rb') as w:
        rate = w.getframerate()
        raw = w.readframes(w.getnframes())

    a = np.frombuffer(raw, dtype=np.int16).astype(np.float64)
    block = int(rate * BLOCK_MS / 1000)
    n = len(a) // block
    if n == 0:
        return np.zeros(0, dtype=bool)

    rms = np.sqrt((a[:n * block].reshape(n, block) ** 2).mean(axis=1))
    db = 20 * np.log10(rms / 32768.0 + 1e-9)

    loud, floor = np.percentile(db, 90), np.median(db)
    return db > max(floor + 6.0, loud - 25.0)


def spoken_mask(rows, length, me):
    m = np.zeros(length, dtype=bool)
    for r in rows:
        if bool(r.get('is_me', r.get('speaker') == 'me')) != me:
            continue
        a = int(r['start'] * 1000 / BLOCK_MS)
        b = int(r['end'] * 1000 / BLOCK_MS)
        m[max(0, a):min(length, b + 1)] = True
    return m


def best_lag(audio, spoken):
    best, shift_at = -1.0, 0
    window = int(5000 / BLOCK_MS)

    for shift in range(-window, window + 1):
        moved = np.roll(spoken, shift)
        if shift > 0:
            moved[:shift] = False
        elif shift < 0:
            moved[shift:] = False

        union = (audio | moved).sum()
        if union == 0:
            continue

        iou = (audio & moved).sum() / union
        if iou > best:
            best, shift_at = iou, shift

    return shift_at * BLOCK_MS / 1000.0, best


def flow(rows):
    lines = sorted(rows, key=lambda r: r['start'])
    for r in lines:
        r['me'] = bool(r.get('is_me', r.get('speaker') == 'me'))

    split = 0
    for i in range(1, len(lines) - 1):
        a, b, c = lines[i - 1], lines[i], lines[i + 1]
        if a['me'] != c['me'] or a['me'] == b['me']:
            continue
        if b['end'] - b['start'] > SHORT or c['start'] - a['end'] > NEAR:
            continue
        split += 1

    swallowed = sum(
        1 for line in lines
        if any(o['me'] != line['me'] and line['start'] < o['start'] and o['end'] < line['end']
               for o in lines))

    unpunctuated = sum(1 for r in lines if not r['text'].strip().endswith(('.', '?', '!', '…')))
    out_of_order = sum(1 for i in range(1, len(lines)) if lines[i]['start'] < lines[i - 1]['start'])

    return split, swallowed, unpunctuated, out_of_order


print('NE KADARINI DUYDU')
print(f'{"görüşme":<32} {"motor":<7} {"satır":>6} {"kelime":>7} {"belirsiz":>10} '
      f'{"kapsama mic":>12} {"kapsama far":>12} {"geçen":>7}')
print('-' * 100)

totals = {'yerel': [0, 0, 0], 'bulut': [0, 0, 0]}

for call, label in CALLS.items():
    for engine in ('yerel', 'bulut'):
        data = load(call, engine)
        if not data:
            print(f'{("#" + str(call) + " " + label):<32} {engine:<7} (yok)')
            continue

        rows = data['segments']
        words = sum(len(r['text'].split()) for r in rows)
        unsure = sum(1 for r in rows if r['low_confidence'])
        cov = data.get('speech_coverage') or {}

        totals[engine][0] += len(rows)
        totals[engine][1] += words
        totals[engine][2] += unsure

        print(f'{("#" + str(call) + " " + label):<32} {engine:<7} {len(rows):>6} {words:>7} '
              f'{unsure:>6} (%{100 * unsure / max(1, len(rows)):>2.0f}) '
              f'{cov.get("mic", float("nan")):>12.3f} {cov.get("far", float("nan")):>12.3f} '
              f'{data["elapsed_s"]:>6.0f}s')

print()
for engine in ('yerel', 'bulut'):
    lines, words, unsure = totals[engine]
    if lines:
        print(f'TOPLAM {engine}: {lines} satır · {words} kelime · '
              f'{unsure} belirsiz (%{100 * unsure / lines:.0f})')

print()
print('DAMGALAR HİZALI MI (kayma sn / örtüşme)')
print(f'{"görüşme":<32} {"motor":<7} {"mic":>16} {"far":>16}')
print('-' * 76)

for call, label in CALLS.items():
    masks = {}
    for channel, me in (('mic', True), ('far', False)):
        path = os.path.join(OUT, f'call-{call}-{channel}.wav')
        if os.path.exists(path):
            masks[channel] = speech_mask(path)

    if not masks:
        continue

    for engine in ('yerel', 'bulut'):
        data = load(call, engine)
        if not data:
            continue

        cells = []
        for channel, me in (('mic', True), ('far', False)):
            if channel not in masks:
                cells.append('—')
                continue

            audio = masks[channel]
            lag, iou = best_lag(audio, spoken_mask(data['segments'], len(audio), me))
            cells.append(f'{lag:+.2f} / {iou:.2f}')

        print(f'{("#" + str(call)):<32} {engine:<7} {cells[0]:>16} {cells[1]:>16}')

print()
print('SOHBET OKUNUYOR MU')
print(f'{"görüşme":<32} {"motor":<7} {"bölünmüş":>9} {"yutulan":>8} {"noktasız":>9} {"sırasız":>8}')
print('-' * 78)

for call, label in CALLS.items():
    for engine in ('yerel', 'bulut'):
        data = load(call, engine)
        if not data:
            continue

        split, swallowed, unpunctuated, disorder = flow(data['segments'])
        print(f'{("#" + str(call)):<32} {engine:<7} {split:>9} {swallowed:>8} '
              f'{unpunctuated:>9} {disorder:>8}')
