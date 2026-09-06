"""Aynam'ın sayıları ne kadar doğru — kulakla ölçülür (PLAN-SOSYALZEKA §6.2).

Aynam bir sayaçtır ve her sayaç yanılır: motor kelimeyi yanlış duyar, sözlük
"klasik"i küfür sanır, yankılı bir satır iki kez sayılır. Ürün kuralı bunun için
açık — sayının yanında kaç tanesinin dinlendiği ve kaçının doğru çıktığı yazar.

Bu betik o iki sayıyı `verdict` tablosundan okur: kullanıcı Aynam ekranında bir
anı dinleyip "doğru / yanlış duyulmuş / bu o değil" dediğinde oraya bir satır
düşer. Kesinlik = doğru / dinlenmiş.

Kapı (plan §6.2): küfür için **≥ %90**, dolgu için **≥ %85**, her biri en az
**30 dinlenmiş sayım** üzerinden. Altında kalan dedektörün kartı Aynam'dan kalkar,
anlar listede kalır ve sonuç `docs/ISLEM-GUNLUGU.md`'ye olumsuz yazılır.

Eşik motor başına ölçülür (plan, Paket C): aynı ses farklı motorda farklı güven
verir, tek eşik ElevenLabs'ta her kelimeyi "belirsiz" yapar. Tablo motora göre de
kırılır — bir motorda 30 dinleme birikmemişse o satır "yetersiz" der.

Ekrana yalnız sayı basılır; konuşma içeriği yok.
"""
from collections import defaultdict

import arsiv

# verdict.verdict: 0 yanlış duyulmuş · 1 doğru · 2 bu o değil · 3 uyarı isterdim · 4 gereksiz
CORRECT = 1

GATES = {'kufur': 0.90, 'dolgu': 0.85}
MINIMUM = 30

LABELS = {'kufur': 'küfür', 'dolgu': 'dolgu', 'bilgi': 'verilen bilgi', 'ton': 'ses düzeyi'}


def main():
    connection = arsiv.open_read_only()
    if connection is None:
        return

    # Bir arşiv v15'ten eski olabilir — kulak teyidi tablosu o sürümle geldi. Eksik
    # tabloya sorgu atıp yığın izi basmak, "henüz ölçüm yok" demenin kötü bir yolu.
    tables = {row[0] for row in connection.execute(
        "SELECT name FROM sqlite_master WHERE type = 'table'")}

    if 'verdict' not in tables:
        print('Bu arşiv kulak teyidi tablosundan (şema v15) eski. Uygulamayı bir kez açmak')
        print('arşivi bugünkü şemaya taşır; ölçüm ondan sonra yapılır.')
        return

    rows = connection.execute(
        """
        SELECT v.kind, v.verdict, tv.engine
          FROM verdict v
          JOIN call c ON c.id = v.call_id
          LEFT JOIN transcript_version tv ON tv.id = c.transcript_version_id
         WHERE v.kind IN ('kufur', 'dolgu', 'bilgi', 'ton')
        """).fetchall()

    if not rows:
        print('Henüz dinlenmiş sayım yok. Aynam ekranında bir anı dinleyip işaretle;')
        print('kesinlik ancak kulakla ölçülür, ve ölçülmeden kart kalıcı olmaz.')
        return

    by_kind = defaultdict(lambda: [0, 0])
    by_engine = defaultdict(lambda: [0, 0])

    for row in rows:
        listened, correct = by_kind[row['kind']]
        by_kind[row['kind']] = [listened + 1, correct + (row['verdict'] == CORRECT)]

        key = (row['kind'], row['engine'] or '(kaydedilmemiş)')
        listened, correct = by_engine[key]
        by_engine[key] = [listened + 1, correct + (row['verdict'] == CORRECT)]

    print('Tür başına')
    for kind in sorted(by_kind):
        listened, correct = by_kind[kind]
        precision = correct / listened
        gate = GATES.get(kind)

        verdict = '—'
        if gate is not None:
            enough = listened >= MINIMUM
            verdict = ('GEÇTİ' if precision >= gate else 'KALDI') if enough else f'yetersiz ({listened}/{MINIMUM})'

        print(f'  {LABELS.get(kind, kind):14} {correct}/{listened} = %{precision * 100:.0f}'
              + (f'  · kapı %{gate * 100:.0f} → {verdict}' if gate else ''))

    print()
    print('Motor başına (eşik motor başına seçilir)')
    for (kind, engine), (listened, correct) in sorted(by_engine.items()):
        note = '' if listened >= MINIMUM else f'  · yetersiz ({listened}/{MINIMUM})'
        print(f'  {LABELS.get(kind, kind):14} {engine:28} {correct}/{listened} = %{correct / listened * 100:.0f}{note}')

    calls = connection.execute('SELECT COUNT(*) FROM call WHERE state >= 2').fetchone()[0]
    counted = (connection.execute('SELECT COUNT(*) FROM speech_habit').fetchone()[0]
               if 'speech_habit' in tables else 0)
    print()
    print(f'Sayılmış görüşme: {counted} / {calls}')
    print('Sonuç sayılarıyla docs/ISLEM-GUNLUGU.md\'ye yazılır; kapıyı geçemeyen dedektörün kartı kalkar.')


if __name__ == '__main__':
    main()
