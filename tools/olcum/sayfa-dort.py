"""Iki cagrida dort motorun sohbet ekranini yan yana koyan sayfa."""
import difflib
import html
import io
import json
import os

OUT = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(OUT, 'dort-motor.html')

NL = chr(10)
SHORT, NEAR = 1.5, 4.0

MOTORLAR = [
    ('local', 'Yerel · large-v3', 'RTX 4050 · faster-whisper'),
    ('openai', 'OpenAI · whisper-1', 'api.openai.com'),
    ('deepgram', 'Deepgram · nova-3', 'api.deepgram.com'),
    ('ex5', 'ex5 · whisper-1', 'stt.ex5.ai · mlx-whisper'),
]

CALLS = [
    {
        'id': 17, 'contact': 'Avukat Polonya', 'when': '31 Ağustos 2026, 12:52',
        'length': '02:44', 'note': 'Kısa görüşme, tek parça halinde yüklendi — parçalama yok.',
        'files': {'local': 'cikti-17.jsonl', 'openai': 'oai-cikti-17.jsonl',
                  'deepgram': 'cikti-dg17.jsonl', 'ex5': 'bulut-cikti-17.jsonl'},
    },
    {
        'id': 22, 'contact': 'Gurhan Abi', 'when': '1 Eylül 2026, 17:50',
        'length': '18:49', 'note': 'Uzun görüşme, 300 saniyelik dört parça halinde yüklendi. '
                                   'Konuşmanın 130 saniyesinde ikisi birden konuşuyor.',
        'files': {'local': 'cikti-yerel22.jsonl', 'openai': 'cikti-oai22.jsonl',
                  'deepgram': 'cikti-dg22.jsonl', 'ex5': 'cikti-ex522.jsonl'},
    },
]


def son(name):
    path = os.path.join(OUT, name)
    if not os.path.exists(path):
        return None
    found = None
    for line in io.open(path, encoding='utf-8-sig'):
        line = line.strip()
        if not line:
            continue
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if isinstance(row, dict) and row.get('segments') is not None:
            found = row
    return found


def is_me(r):
    return bool(r.get('is_me', r.get('speaker') == 'me'))


def clock(seconds):
    total = int(seconds)
    return f'{total // 60:02d}:{total % 60:02d}'


def sira(rows):
    seq = ['B' if is_me(r) else 'K' for r in sorted(rows, key=lambda r: r['start'])]
    return [s for i, s in enumerate(seq) if i == 0 or s != seq[i - 1]]


def yutulan(rows):
    return sum(1 for r in rows
               if any(is_me(o) != is_me(r) and r['start'] < o['start'] and o['end'] < r['end']
                      for o in rows))


def bolunmus(rows):
    lines = sorted(rows, key=lambda r: r['start'])
    n = 0
    for i in range(1, len(lines) - 1):
        a, b, c = lines[i - 1], lines[i], lines[i + 1]
        if is_me(a) != is_me(c) or is_me(a) == is_me(b):
            continue
        if b['end'] - b['start'] > SHORT or c['start'] - a['end'] > NEAR:
            continue
        n += 1
    return n


def bubbles(rows, contact):
    out = []
    for row in sorted(rows, key=lambda r: r['start']):
        mine = is_me(row)
        who = 'Sen' if mine else contact
        flags = []
        if row.get('low_confidence'):
            flags.append('belirsiz')
        if row.get('overlaps_other_speaker'):
            flags.append('üst üste')
        marks = ''.join(f'<span class="flag">{html.escape(f)}</span>' for f in flags)
        out.append(
            f'<div class="turn {"mine" if mine else "theirs"}">'
            f'<div class="meta"><span class="who">{html.escape(who)}</span>'
            f'<span class="at">{clock(row["start"])}</span>{marks}</div>'
            f'<p>{html.escape(row["text"].strip())}</p></div>')
    return NL.join(out)


sections, summary = [], []

for call in CALLS:
    veriler = {k: son(v) for k, v in call['files'].items()}
    veriler = {k: v for k, v in veriler.items() if v}
    if 'local' not in veriler:
        continue

    ref = sira(veriler['local']['segments'])
    panels, rows_html = [], []

    for key, title, host in MOTORLAR:
        d = veriler.get(key)
        if not d:
            continue
        rows = d['segments']
        ws = [w for r in rows for w in (r.get('words') or [])]
        cov = d.get('speech_coverage') or {}
        kelime = sum(len(r['text'].split()) for r in rows)
        ort = difflib.SequenceMatcher(None, ref, sira(rows), autojunk=False).ratio()
        ortusme = 'referans' if key == 'local' else f'%{100 * ort:.0f}'
        kapsama = f'{cov.get("mic", 0):.2f} / {cov.get("far", 0):.2f}'

        cells = [
            ('kapsama', kapsama),
            ('kelime', str(kelime)),
            ('satır', str(len(rows))),
            ('yutulan satır', str(yutulan(rows))),
            ('sıra örtüşmesi', ortusme),
            ('geçen', f'{d.get("elapsed_s", 0):.0f} sn'),
        ]

        figures = NL.join(
            f'<div class="figure"><dt>{html.escape(k)}</dt><dd>{html.escape(v)}</dd></div>'
            for k, v in cells)

        panels.append(
            f'    <article class="engine {key}">\n'
            f'      <div class="engine-head">\n'
            f'        <h3>{html.escape(title)}</h3>\n'
            f'        <p class="host">{html.escape(host)}</p>\n'
            f'        <dl class="figures">{figures}</dl>\n'
            f'      </div>\n'
            f'      <div class="chat">{bubbles(rows, call["contact"])}</div>\n'
            f'    </article>')

        bad = ' class="bad"' if key == 'ex5' else ''
        rows_html.append(
            f'<tr{bad}><th scope="row">{html.escape(title)}</th>'
            f'<td>{kapsama}</td><td>{kelime}</td><td>{len(rows)}</td>'
            f'<td>{yutulan(rows)}</td><td>{bolunmus(rows)}</td>'
            f'<td>{ortusme}</td><td>{d.get("elapsed_s", 0):.0f} sn</td></tr>')

    summary.append(
        f'<tr class="head"><th scope="row" colspan="8">#{call["id"]} '
        f'{html.escape(call["contact"])} · {call["length"]}</th></tr>' + ''.join(rows_html))

    sections.append(
        f'\n<section class="call" id="call-{call["id"]}">\n'
        f'  <header class="call-head">\n'
        f'    <div>\n'
        f'      <h2>{html.escape(call["contact"])}</h2>\n'
        f'      <p class="when">#{call["id"]} · {html.escape(call["when"])} · {call["length"]}</p>\n'
        f'      <p class="callnote">{html.escape(call["note"])}</p>\n'
        f'    </div>\n'
        f'  </header>\n\n'
        f'  <div class="pair" data-view="both">\n' + NL.join(panels) + '\n  </div>\n</section>')

template = io.open(os.path.join(OUT, 'kalip-dort.html'), encoding='utf-8').read()
page = (template
        .replace('<!--SUMMARY-->', NL.join(summary))
        .replace('<!--SECTIONS-->', NL.join(sections)))

io.open(TARGET, 'w', encoding='utf-8', newline=NL).write(page)
print(f'{len(sections)} görüşme yazıldı -> {TARGET}')
