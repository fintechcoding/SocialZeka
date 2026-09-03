---
name: gorusme-verisine-bakma
description: VoiceTranscript'in canlı veritabanına, günlüğüne ve kayıtlarına bakmanın yolları. Bir şikâyeti veriye dayandırmak, göç yazmak ya da bir değişikliği gerçek kayıtlarla sınamak gerektiğinde kullan.
---

# Canlı veriye bakmak

Bu üründe her şikâyet ölçülebilir. "Sesim yok", "sıralama bozuk", "parola çalışmıyor" —
üçünün de cevabı diskte duruyor. **Tahmin etmeden önce bak.**

## Nerede

Varsayılan yol değil — uygulama `--data` ile başka yere kurulmuş olabilir:

```
%LOCALAPPDATA%\VoiceTranscript.Data\     ← bu makinede burası
    voicetranscript.db
    logs\vt-YYYY-MM-DD.log
    recordings\YYYY-MM\call-N-{mic,far}.ogg
    python\Scripts\python.exe            ← worker'ın kendi yorumlayıcısı
    models\                              ← HF_HOME olarak ver
    settings.json                        ← API anahtarları burada, DİKKAT
```

Bulamazsan `AppPaths.cs`'i oku, sonra `C:\` altında `voicetranscript.db` ara ve
`\Temp\` altındakileri ele — orada onlarca yedek kopya var.

**Her zaman salt okunur aç.** Uygulama çalışıyorken yazmak bozar:

```python
sqlite3.connect(f"file:{db}?mode=ro", uri=True)
```

Uygulamanın çalışıp çalışmadığını `Get-Process VoiceTranscript*` ile denetle.
Çalışıyorsa veritabanına dokunma; toplu yeniden çözümleme gibi işleri
kullanıcının kendi arayüzünden yapması gerekir (İşlemler sayfası → seç →
Yeniden yazıya dök; `Requeue` satırları `Queued` yapıp işlemciyi tetikler).

## Windows konsolu Türkçe yazamıyor

`UnicodeEncodeError: 'charmap' codec can't encode character '\u0131'`. Her Python
çağrısına `PYTHONIOENCODING=utf-8` koy, yoksa "ı" harfi her şeyi düşürür.

## Sorular ve cevaplarının olduğu yer

| soru | yer |
|---|---|
| Mikrofon gerçekten kaydetmiş mi | `call.capture_stats` → `peak`, `silent` |
| Hangi motor çözümledi, ne kadarını | `processing_run.engine`, `.speech_coverage` |
| Kim ne dedi | `segment.is_me` (1 = kullanıcı) — tahmin değil, dosyanın kaynağı |
| Üst üste konuşma | `segment.overlaps_other_speaker` |
| Kelime zaman damgaları | `segment.words` — JSON, `[[baslangicMs, bitisMs, "kelime"], ...]` |
| Kim nasıl etiketlendi | `call.contact_source` = `user` / `title` / `voice` |

`segment` tablosunda `speaker` diye bir sütun **yok**; `is_me` var.

## Göç yazarken

Kural `Migrations.cs:18-22`: adım yalnız N→N+1 yapar, aynı değişiklik `Schema.cs`
baseline'ında da olur, yayınlanmış adım hiç değiştirilmez. `Schema.Version`
son adımla **eşit** olmalı — `MigrationTests` bunu şart koşuyor, ve
`AnUpgradedDatabaseMatchesAFreshOne` yeni tablo/sütun için bir yoklama ister.

**Ve canlı veritabanının kopyasında çalıştır.** Öncesi ve sonrası satır sayıları
ile `PRAGMA integrity_check`. Göç 12 böyle sınandı: 93 ms, 51 görüşme ve 2650
segment bozulmadan.

Kopyayı `Database(path).Migrate()` çağıran tek dosyalık bir konsolla sürebilirsin;
`VoiceTranscript.Core`'a başvurması ve `UseWPF` olması yeter.

## Günlük

`AppLog.Level` üç kademeli (`Normal` / `Verbose` / `Debug`), Durum sayfasından
seçiliyor. Konuşma metni, kişi adı ve API anahtarı **hiçbir kademede** yazılmaz —
bu söz kullanıcıya verilmiş, günlüğe satır eklerken koru.

Bir işlemin izi yoksa şunu sor: veriyi değiştiriyor mu? Yedekleme ve geri yükleme
hiç kaydedilmiyordu ve bir sorun yaşandığında geriye bakacak hiçbir şey yoktu.

## Ses dosyalarıyla iş yapmak

`.ogg` arşivlerini açmak için `arsiv-sesi-acma`. Worker'ı elle sürmek için:

```
PY="…/VoiceTranscript.Data/python/Scripts/python.exe"     # sistem Python'unda faster_whisper YOK
export HF_HOME="…/VoiceTranscript.Data/models"
$PY -m vt_worker transcribe   # stdin'den tek satır JSON
```

Bulut motorunun adı `cloud-ex5`, ve adres/anahtar/model **`model_ref` içinde**
`"base|key|model"` biçiminde gider — ayrı alanlar değil.
