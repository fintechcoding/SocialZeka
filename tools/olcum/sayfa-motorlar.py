"""Tek bir cagrida uc motorun sohbet ekranini yan yana koyan sayfa."""
import difflib
import html
import io
import json
import os

OUT = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(OUT, 'avukat-uc-motor.html')

CALL = 17
CONTACT = 'Avukat Polonya'
WHEN = '31 Ağustos 2026, 12:52'
LENGTH = '02:44'

MOTORLAR = [
    ('local', 'Yerel · large-v3', 'RTX 4050 · faster-whisper', 'cikti-17.jsonl'),
    ('openai', 'OpenAI · whisper-1', 'api.openai.com', 'oai-cikti-17.jsonl'),
    ('ex5', 'ex5 · whisper-1', 'stt.ex5.ai', 'bulut-cikti-17.jsonl'),
]

NL = chr(10)
SHORT, NEAR = 1.5, 4.0


def son_sonuc(name):
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


def bubbles(rows):
    out = []
    for row in sorted(rows, key=lambda r: r['start']):
        mine = is_me(row)
        who = 'Sen' if mine else CONTACT
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


veriler, figurler, ref = {}, {}, None

for key, _, _, name in MOTORLAR:
    d = son_sonuc(name)
    if d:
        veriler[key] = d

ref = sira(veriler['local']['segments'])

for key, d in veriler.items():
    rows = d['segments']
    ws = [w for r in rows for w in (r.get('words') or [])]
    uzun = [w for w in ws if w['end'] - w['start'] > 1.5]
    cov = d.get('speech_coverage') or {}
    figurler[key] = {
        'satır': str(len(rows)),
        'kelime': str(sum(len(r['text'].split()) for r in rows)),
        'kapsama': f'{cov.get("mic", 0):.2f} / {cov.get("far", 0):.2f}',
        'uzun kelime': f'{len(uzun)} · en uzun {max((w["end"]-w["start"] for w in ws), default=0):.1f} sn',
        'yutulan satır': str(yutulan(rows)),
        'bölünmüş cümle': str(bolunmus(rows)),
        'sıra örtüşmesi': ('referans' if key == 'local' else
                           f'%{100*difflib.SequenceMatcher(None, ref, sira(rows), autojunk=False).ratio():.0f}'),
        'geçen': f'{d.get("elapsed_s", 0):.0f} sn',
    }

paneller = []
for key, title, sub, _ in MOTORLAR:
    if key not in veriler:
        continue
    cells = NL.join(
        f'<div class="figure"><dt>{html.escape(k)}</dt><dd>{html.escape(v)}</dd></div>'
        for k, v in figurler[key].items())
    paneller.append(
        f'    <article class="engine {key}">\n'
        f'      <div class="engine-head">\n'
        f'        <h3>{html.escape(title)}</h3>\n'
        f'        <p class="host">{html.escape(sub)}</p>\n'
        f'        <dl class="figures">{cells}</dl>\n'
        f'      </div>\n'
        f'      <div class="chat">{bubbles(veriler[key]["segments"])}</div>\n'
        f'    </article>')

ozet = []
for key, title, _, _ in MOTORLAR:
    if key not in veriler:
        continue
    f = figurler[key]
    kotu = ' class="bad"' if key == 'ex5' else ''
    ozet.append(
        f'<tr{kotu}><th scope="row">{html.escape(title)}</th>'
        f'<td>{f["kapsama"]}</td><td>{f["kelime"]}</td><td>{f["satır"]}</td>'
        f'<td>{f["yutulan satır"]}</td><td>{f["sıra örtüşmesi"]}</td><td>{f["geçen"]}</td></tr>')

template = io.open(os.path.join(OUT, 'kalip-motorlar.html'), encoding='utf-8').read()
page = (template
        .replace('<!--CONTACT-->', html.escape(CONTACT))
        .replace('<!--WHEN-->', f'#{CALL} · {html.escape(WHEN)} · {LENGTH}')
        .replace('<!--SUMMARY-->', (NL + '        ').join(ozet))
        .replace('<!--PANELS-->', NL.join(paneller)))

io.open(TARGET, 'w', encoding='utf-8', newline=NL).write(page)
print(f'{len(paneller)} motor yazildi -> {TARGET}')
for key, title, _, _ in MOTORLAR:
    if key in figurler:
        print(f'  {title}: kapsama {figurler[key]["kapsama"]}, '
              f'{figurler[key]["kelime"]} kelime, yutulan {figurler[key]["yutulan satır"]}')
