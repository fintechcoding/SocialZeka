"""Bölme eşiği kaçta olmalı — birkaç görüşme üzerinde, gerçek kodla.

Ölçülen kusur şu örüntü: bir cümle ikiye bölünüyor ve araya karşı tarafın kısa
bir satırı düşüyor.

    ben   "Alo, ne"
    karşı "da"
    ben   "yapıyorsun canım?"

Eşiği yükseltmek bunu azaltır ama başka bir şeyi bozar: iki ayrı sıra tek satıra
yapışır ve karşı tarafın araya giren cevabı o satırın İÇİNDE kalır — okunduğunda
kimin ne zaman konuştuğu kaybolur. İki sayı birlikte okunmalı.

Uygulamanın kendi `resegment_on_gaps` kodu çağrılıyor; değişen tek şey eşik.
"""
import io
import json
import os
import sys

sys.path.insert(0, r'C:\Voice\SocialZeka\worker')

from vt_worker.merge import Segment, Speaker, Word          # noqa: E402
from vt_worker.segmentation import resegment_on_gaps        # noqa: E402

OUT = os.path.dirname(os.path.abspath(__file__))

CALLS = {
    24: 'Uliana · 1 Eyl 15:14 · 12:29',
    14: 'Serdal · 30 Ağu 17:54 · 08:39',
    38: 'Bozkurt · 2 Eyl 13:51 · 07:02',
    16: 'Sinan · 30 Ağu 19:01 · 05:18',
    17: 'Avukat Polonya · 31 Ağu 12:52 · 02:44',
    60: 'Uliana · 3 Eyl 21:51 · 03:20',
}

GAPS = (1.0, 1.5, 2.0, 2.5, 3.0, 4.0)

SHORT = 1.5   # araya düşen parça sayılacak en uzun satır
NEAR = 4.0    # bölünmüş sayılacak en uzun aralık


def load(call):
    path = os.path.join(OUT, f'cikti-{call}.jsonl')
    if not os.path.exists(path):
        return None

    last = None
    for line in io.open(path, encoding='utf-8-sig'):
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        if isinstance(row, dict) and row.get('segments') is not None:
            last = row

    return last


def rebuild(rows, me):
    """Bir kanalın satırlarını worker'ın kendi tipine geri kur."""
    out = []
    for r in rows:
        if bool(r.get('is_me', r.get('speaker') == 'me')) != me:
            continue

        words = [Word(w['start'], w['end'], w['text'], w.get('p')) for w in (r.get('words') or [])]

        out.append(Segment(
            speaker=Speaker.ME if me else Speaker.THEM,
            start=r['start'], end=r['end'], text=r['text'],
            avg_logprob=r.get('avg_logprob'), no_speech_prob=r.get('no_speech_prob'),
            words=words))

    return out


def interleaved(rows, gap):
    mine = resegment_on_gaps(rebuild(rows, True), gap)
    theirs = resegment_on_gaps(rebuild(rows, False), gap)

    return sorted(mine + theirs, key=lambda s: s.start)


def score(lines):
    """(bölünmüş cümle sayısı, karşı tarafın içine gömüldüğü satır sayısı, satır sayısı)"""
    split = 0
    for i in range(1, len(lines) - 1):
        before, middle, after = lines[i - 1], lines[i], lines[i + 1]

        if before.speaker != after.speaker or before.speaker == middle.speaker:
            continue
        if middle.end - middle.start > SHORT:
            continue
        if after.start - before.end > NEAR:
            continue

        split += 1

    # Bir satır, karşı tarafın BİR TAM satırını içine alıyorsa o cevap görünmez olur.
    swallowed = 0
    for line in lines:
        for other in lines:
            if other.speaker == line.speaker:
                continue
            if line.start < other.start and other.end < line.end:
                swallowed += 1
                break

    return split, swallowed, len(lines)


print(f'{"eşik":>5} {"bölünmüş cümle":>15} {"yutulan cevap":>14} {"satır":>7} {"ort. satır sn":>14}')
print('-' * 60)

totals = {}

for gap in GAPS:
    split_all = swallowed_all = lines_all = 0
    span_all = 0.0

    for call in CALLS:
        data = load(call)
        if not data:
            continue

        lines = interleaved(data['segments'], gap)
        split, swallowed, count = score(lines)

        split_all += split
        swallowed_all += swallowed
        lines_all += count
        span_all += sum(l.end - l.start for l in lines)

    if lines_all == 0:
        continue

    totals[gap] = (split_all, swallowed_all, lines_all)
    print(f'{gap:>5.1f} {split_all:>15} {swallowed_all:>14} {lines_all:>7} {span_all / lines_all:>14.1f}')

print()
print('Görüşme başına (bölünmüş cümle / yutulan cevap):')
header = 'görüşme'.ljust(34) + ''.join(f'{g:>10.1f}' for g in GAPS)
print(header)
print('-' * len(header))

for call, label in CALLS.items():
    data = load(call)
    if not data:
        print(f'{("#" + str(call) + " " + label):<34}  (çıktı yok)')
        continue

    cells = []
    for gap in GAPS:
        split, swallowed, _ = score(interleaved(data['segments'], gap))
        cells.append(f'{split}/{swallowed}')

    print(f'{("#" + str(call) + " " + label):<34}' + ''.join(f'{c:>10}' for c in cells))
