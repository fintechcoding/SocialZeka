"""Ölçülecek görüşmelerin arşiv dosyalarını çözücüye verilecek biçimde listeler."""
import os
import sqlite3

CALLS = (24, 14, 38, 16, 17)

OUT = os.path.dirname(os.path.abspath(__file__))
DB = os.path.join(os.environ['LOCALAPPDATA'], 'SocialZeka.Data', 'voicetranscript.db')

c = sqlite3.connect(f'file:{DB}?mode=ro', uri=True)
c.row_factory = sqlite3.Row

pairs = []
for call in CALLS:
    row = c.execute('SELECT mic_path, far_path FROM call WHERE id = ?', (call,)).fetchone()

    for tag, path in (('mic', row['mic_path']), ('far', row['far_path'])):
        target = os.path.join(OUT, f'call-{call}-{tag}.wav')
        pairs.append(f'{path}|{target}')

with open(os.path.join(OUT, 'args.txt'), 'w', encoding='utf-8') as f:
    f.write('\n'.join(pairs))

print(len(pairs), 'dosya çözülecek')
