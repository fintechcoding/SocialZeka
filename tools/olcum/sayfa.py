"""Uc motoru yan yana koyan sayfayi uretir: yerel, bulut, ve onarilmis bulut."""
import copy
import difflib
import html
import io
import json
import os
import re
import sys

import numpy as np

sys.path.insert(0, r'C:\Voice\VoiceTranscript\worker')

from vt_worker.merge import Segment, Speaker, Word, merge_streams   # noqa: E402
from vt_worker.segmentation import resegment_on_gaps               # noqa: E402
from vt_worker.timestamps import MAX_WORD_SECONDS, repair_stretched_words  # noqa: E402

OUT = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(OUT, 'karsilastirma.html')

CALLS = {
    24: ('Uliana', '1 Eylül 2026, 15:14', '12:29'),
    14: ('Serdal', '30 Ağustos 2026, 17:54', '08:39'),
    38: ('Bozkurt', '2 Eylül 2026, 13:51', '07:02'),
    16: ('Sinan', '30 Ağustos 2026, 19:01', '05:18'),
    60: ('Uliana', '3 Eylül 2026, 21:51', '03:20'),
    17: ('Avukat Polonya', '31 Ağustos 2026, 12:52', '02:44'),
}

SHORT, NEAR = 1.5, 4.0
NEWLINE = chr(10)
FOLD = str.maketrans('ÂÎÛâîû', 'AIUaiu')


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


def is_me(row):
    return bool(row['is_me']) if 'is_me' in row else row.get('speaker') == 'me'


def to_segments(data, me):
    out = []
    for row in sorted(data['segments'], key=lambda r: r['start']):
        if is_me(row) != me:
            continue
        out.append(Segment(
            speaker=Speaker.ME if me else Speaker.THEM,
            start=row['start'], end=row['end'], text=row['text'],
            avg_logprob=row.get('avg_logprob'), no_speech_prob=row.get('no_speech_prob'),
            words=[Word(w['start'], w['end'], w['text'], w.get('p'))
                   for w in (row.get('words') or [])]))
    return out


def to_rows(segments, marked):
    """Segmentleri sayfanin okudugu satir bicimine cevirir."""
    out = []
    for s in sorted(segments, key=lambda s: s.start):
        touched = any((round(w.end, 3), w.text) in marked for w in s.words)
        out.append({
            'start': s.start, 'end': s.end, 'text': s.text,
            'is_me': s.speaker == Speaker.ME,
            'low_confidence': s.is_low_confidence,
            'overlaps_other_speaker': s.overlaps_other_speaker,
            'repaired': touched,
            'words': [{'start': w.start, 'end': w.end, 'text': w.text} for w in s.words],
        })
    return out


def repaired(cloud):
    """Bulut ciktisini, uygulamanin kendi onarim ve bolme adimlarindan gecirir."""
    mic, far = to_segments(cloud, True), to_segments(cloud, False)

    marked = {
        (round(w.end, 3), w.text)
        for side in (mic, far) for s in side for w in s.words
        if w.end - w.start > MAX_WORD_SECONDS
    }

    fixed = [resegment_on_gaps(repair_stretched_words(copy.deepcopy(side)))
             for side in (mic, far)]

    merged = merge_streams(fixed[0], fixed[1])
    return to_rows(merged.segments, marked), len(marked)


def clock(seconds):
    total = int(seconds)
    return f'{total // 60:02d}:{total % 60:02d}'


def split_count(rows):
    lines = sorted(rows, key=lambda r: r['start'])
    count = 0
    for i in range(1, len(lines) - 1):
        a, b, c = lines[i - 1], lines[i], lines[i + 1]
        if is_me(a) != is_me(c) or is_me(a) == is_me(b):
            continue
        if b['end'] - b['start'] > SHORT or c['start'] - a['end'] > NEAR:
            continue
        count += 1
    return count


def swallowed(rows):
    """Karsi tarafin bir sirasini butunuyle icine alan satir sayisi — ekrandaki asil kusur."""
    return sum(
        1 for r in rows
        if any(is_me(o) != is_me(r) and r['start'] < o['start'] and o['end'] < r['end']
               for o in rows))


def order(rows):
    seq = ['B' if is_me(r) else 'K' for r in sorted(rows, key=lambda r: r['start'])]
    return [s for i, s in enumerate(seq) if i == 0 or s != seq[i - 1]]


def turn_similarity(reference, other):
    return difflib.SequenceMatcher(None, order(reference), order(other), autojunk=False).ratio()


def fold(word):
    return re.sub(r'[^\w]', '', word.translate(FOLD).casefold())


def words_of(rows, me):
    out = []
    for row in sorted(rows, key=lambda r: r['start']):
        if is_me(row) != me:
            continue
        for w in row.get('words') or []:
            text = fold(w['text'])
            if text:
                out.append((text, float(w['start'])))
    return out


def stamp_agreement(local, other):
    deltas = []
    for me in (True, False):
        a, b = words_of(local, me), words_of(other, me)
        if not a or not b:
            continue
        m = difflib.SequenceMatcher(None, [w for w, _ in a], [w for w, _ in b], autojunk=False)
        for block in m.get_matching_blocks():
            for k in range(block.size):
                deltas.append(abs(b[block.b + k][1] - a[block.a + k][1]))
    if not deltas:
        return None, None
    d = np.array(deltas)
    return float(np.median(d)), float((d <= 0.5).mean())


def bubbles(rows, contact):
    out = []
    for row in rows:
        side = 'mine' if is_me(row) else 'theirs'
        who = 'Sen' if is_me(row) else contact

        flags = []
        if row.get('repaired'):
            flags.append(('fix', 'onarıldı'))
        if row.get('low_confidence'):
            flags.append(('', 'belirsiz'))
        if row.get('overlaps_other_speaker'):
            flags.append(('', 'üst üste'))

        marks = ''.join(
            f'<span class="flag {kind}">{html.escape(text)}</span>' for kind, text in flags)
        moved = ' moved' if row.get('repaired') else ''

        out.append(
            f'<div class="turn {side}{moved}">'
            f'<div class="meta"><span class="who">{html.escape(who)}</span>'
            f'<span class="at">{clock(row["start"])}</span>{marks}</div>'
            f'<p>{html.escape(row["text"].strip())}</p></div>')

    return NEWLINE.join(out)


def figures(cells):
    return NEWLINE.join(
        f'<div class="figure"><dt>{html.escape(k)}</dt><dd>{html.escape(v)}</dd></div>'
        for k, v in cells)


def panel(kind, title, cells, rows, contact):
    head = (f'    <article class="engine {kind}">\n'
            f'      <div class="engine-head">\n'
            f'        <h3>{html.escape(title)}</h3>\n'
            f'        <dl class="figures">{figures(cells)}</dl>\n'
            f'      </div>\n')
    return head + f'      <div class="chat">{bubbles(rows, contact)}</div>\n    </article>'


sections, summary_rows = [], []
total = {'once': [], 'sonra': [], 'yut_once': 0, 'yut_sonra': 0}

for call, (contact, when, length) in CALLS.items():
    local_data, cloud_data = load(call, 'yerel'), load(call, 'bulut')
    if not local_data or not cloud_data:
        continue

    local_rows = local_data['segments']
    cloud_rows = cloud_data['segments']
    fixed_rows, repaired_words = repaired(cloud_data)

    sim_cloud = turn_similarity(local_rows, cloud_rows)
    sim_fixed = turn_similarity(local_rows, fixed_rows)

    yut_local = swallowed(local_rows)
    yut_cloud = swallowed(cloud_rows)
    yut_fixed = swallowed(fixed_rows)

    median, within = stamp_agreement(local_rows, cloud_rows)

    total['once'].append(sim_cloud)
    total['sonra'].append(sim_fixed)
    total['yut_once'] += yut_cloud
    total['yut_sonra'] += yut_fixed

    if sim_fixed > sim_cloud + 0.005:
        verdict = 'gain'
    elif sim_fixed >= sim_cloud - 0.005:
        verdict = 'same'
    else:
        verdict = 'loss'

    summary_rows.append(
        f'<tr><th scope="row">#{call} {html.escape(contact)}</th>'
        f'<td>{length}</td>'
        f'<td>{len(local_rows)} / {len(cloud_rows)} / {len(fixed_rows)}</td>'
        f'<td>{yut_local} / {yut_cloud} / <b>{yut_fixed}</b></td>'
        f'<td>{repaired_words}</td>'
        f'<td>%{100 * sim_cloud:.0f}</td>'
        f'<td class="{verdict}">%{100 * sim_fixed:.0f}</td></tr>')

    def word_count(rows):
        return sum(len(r['text'].split()) for r in rows)

    cov = cloud_data.get('speech_coverage') or {}
    lcov = local_data.get('speech_coverage') or {}

    panels = NEWLINE.join([
        panel('local', 'Yerel · large-v3', [
            ('satır', str(len(local_rows))),
            ('kelime', str(word_count(local_rows))),
            ('yutulan satır', str(yut_local)),
            ('kapsama', f'{lcov.get("mic", 0):.2f} / {lcov.get("far", 0):.2f}'),
            ('bölünmüş cümle', str(split_count(local_rows))),
        ], local_rows, contact),
        panel('cloud', 'Bulut · ex5 whisper-1', [
            ('satır', str(len(cloud_rows))),
            ('kelime', str(word_count(cloud_rows))),
            ('yutulan satır', str(yut_cloud)),
            ('kapsama', f'{cov.get("mic", 0):.2f} / {cov.get("far", 0):.2f}'),
            ('bölünmüş cümle', str(split_count(cloud_rows))),
        ], cloud_rows, contact),
        panel('fixed', 'Bulut · damga onarımlı', [
            ('satır', str(len(fixed_rows))),
            ('kelime', str(word_count(fixed_rows))),
            ('yutulan satır', str(yut_fixed)),
            ('onarılan kelime', str(repaired_words)),
            ('bölünmüş cümle', str(split_count(fixed_rows))),
        ], fixed_rows, contact),
    ])

    header = (
        f'\n<section class="call" id="call-{call}">\n'
        f'  <header class="call-head">\n'
        f'    <div>\n'
        f'      <h2>{html.escape(contact)}</h2>\n'
        f'      <p class="when">#{call} · {html.escape(when)} · {length}</p>\n'
        f'    </div>\n'
        f'    <div class="agree">\n'
        f'      <span>sıra örtüşmesi <b>%{100 * sim_cloud:.0f}</b> → '
        f'<b class="{verdict}">%{100 * sim_fixed:.0f}</b></span>\n'
        f'      <span>yutulan satır <b>{yut_cloud}</b> → '
        f'<b class="{verdict}">{yut_fixed}</b></span>\n'
        f'      <span><b>{median:.2f} sn</b> damga farkı · '
        f'<b>%{100 * within:.0f}</b> ±0,5 sn içinde</span>\n'
        f'    </div>\n'
        f'  </header>\n\n'
        f'  <div class="pair" data-view="both">\n')

    sections.append(header + panels + '\n  </div>\n</section>')

avg_before = sum(total['once']) / len(total['once'])
avg_after = sum(total['sonra']) / len(total['sonra'])

summary_rows.append(
    f'<tr class="total"><th scope="row">ortalama</th><td>—</td><td>—</td>'
    f'<td>— / {total["yut_once"]} / <b>{total["yut_sonra"]}</b></td><td>—</td>'
    f'<td>%{100 * avg_before:.0f}</td>'
    f'<td class="gain">%{100 * avg_after:.0f}</td></tr>')

template = io.open(os.path.join(OUT, 'kalip.html'), encoding='utf-8').read()

page = (template
        .replace('<!--SUMMARY-->', (NEWLINE + '        ').join(summary_rows))
        .replace('<!--SECTIONS-->', NEWLINE.join(sections)))

io.open(TARGET, 'w', encoding='utf-8', newline=NEWLINE).write(page)

print(f'{len(sections)} gorusme yazildi -> {TARGET}')
print(f'sira ortusmesi ortalama: %{100 * avg_before:.0f} -> %{100 * avg_after:.0f}')
print(f'yutulan satir toplam: {total["yut_once"]} -> {total["yut_sonra"]}')
