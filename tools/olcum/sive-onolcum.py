"""Şive sayacı yazılmalı mı — yazmadan önce ölçülür (PLAN-SOSYALZEKA §6.1).

Plandaki iddia şu: konuşulan şive, dökümde çoğu zaman yoktur. Whisper çıktıyı yazı
diline **normalize eder**, yani "napıyon" ekrana "ne yapıyorsun" diye düşer. O hâlde
metinde sayılan şey konuşmacının şivesi değil, motorun o gün ne kadar normalize
ettiğidir — ve böyle bir sayaç kişiye kendi konuşmasıyla ilgisi olmayan bir eğri
gösterir.

Bu betik iddiayı sayıya çevirir. Kullanıcının **kendi** satırlarında (`is_me = 1`)
şive işaretlerini arar, görüşme başına düşen eşleşmeyi verir ve dinlenecek 40
örneği ayrı bir dosyaya yazar.

Kapı (plan §6.1): görüşme başına **≥ 1 eşleşme** ve dinlemede **kesinlik ≥ 0,6**.
İkisi birden sağlanmazsa dedektör yazılmaz ve Aynam "şive: ölçülmüyor" der —
bugünkü durum budur.

Ekrana yalnız sayı basılır. Eşleşen cümleler konuşma içeriğidir; onlar depoya
girmeyen bir dosyaya yazılır (`.gitignore` `tools/olcum/*.jsonl` satırını tutar).
"""
import json
import os
import re
from collections import Counter

import arsiv

OUT = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(OUT, 'sive-ornek.jsonl')

# Yazı diline normalize edilmemiş biçimler. Hepsi İstanbul konuşma dilinde de
# geçebilir — asıl soru zaten "geçiyor mu" değil, "bu sayı bir şey anlatıyor mu".
PATTERNS = {
    'yom': r'\b\w+yom\b',
    'yon': r'\b\w+yon\b',
    'yoz': r'\b\w+yoz\b',
    'napiyon': r'\bnapiyo\w*\b',
    'gari': r'\bgari\b',
    'hele': r'\bhele\b',
    'valla': r'\bvalla\w*\b',
    'gidiyom-tipi': r'\b\w+cem\b|\b\w+cez\b',
}

COMPILED = {name: re.compile(pattern) for name, pattern in PATTERNS.items()}
SAMPLE_SIZE = 40


def main():
    connection = arsiv.open_read_only()
    if connection is None:
        return

    calls = connection.execute(
        """
        SELECT c.id, COUNT(s.id) AS lines
          FROM call c JOIN segment s ON s.call_id = c.id AND s.is_me = 1
         GROUP BY c.id
        """).fetchall()

    if not calls:
        print('Arşivde kullanıcının kendi satırını taşıyan görüşme yok.')
        return

    per_call = Counter()
    per_pattern = Counter()
    samples = []

    rows = connection.execute(
        """
        SELECT s.call_id, s.start_ms, s.text, s.text_normalised, s.low_confidence
          FROM segment s
         WHERE s.is_me = 1 AND s.suspected_echo = 0
         ORDER BY s.call_id, s.start_ms
        """)

    for row in rows:
        text = row['text_normalised'] or ''
        hits = [name for name, pattern in COMPILED.items() if pattern.search(text)]
        if not hits:
            continue

        per_call[row['call_id']] += len(hits)
        for name in hits:
            per_pattern[name] += 1

        if len(samples) < SAMPLE_SIZE:
            samples.append({
                'call_id': row['call_id'],
                'start_ms': row['start_ms'],
                'patterns': hits,
                'low_confidence': bool(row['low_confidence']),
                'text': row['text'],
            })

    total = sum(per_call.values())
    with_hits = len(per_call)
    rate = total / len(calls)

    print(f'Görüşme (kendi satırı olan): {len(calls)}')
    print(f'Eşleşme: {total} · eşleşen görüşme: {with_hits} · görüşme başına: {rate:.2f}')
    print()
    print('Örüntü başına:')
    for name, count in per_pattern.most_common():
        print(f'  {name:14} {count}')

    print()
    print(f'Kapı: görüşme başına ≥ 1 eşleşme → {"GEÇTİ" if rate >= 1 else "KALDI"} ({rate:.2f})')
    print('Kesinlik kapısı (≥ 0,6) dinlemeyle ölçülür; örnekler:', os.path.basename(SAMPLE))

    with open(SAMPLE, 'w', encoding='utf-8') as f:
        for sample in samples:
            f.write(json.dumps(sample, ensure_ascii=False) + '\n')

    print(f'{len(samples)} örnek yazıldı. Her biri dinlenip "gerçekten şive mi" diye işaretlenir;')
    print('sonuç sayılarıyla docs/ISLEM-GUNLUGU.md\'ye yazılır. Kapı geçilmezse dedektör yazılmaz.')


if __name__ == '__main__':
    main()
