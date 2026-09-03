---
name: ses-kaybi-teshis
description: Bir görüşmenin transkriptinde konuşma eksikse sebebini bulur — mikrofon mu, yakalama mı, sıkıştırma mı, yoksa çözümleme mi kaybetti. "Benim sesim yok", "yarısı eksik", "saçma çıkmış", "uydurma cümle var" gibi şikâyetlerde kullan.
---

# Transkriptte kaybolan konuşmayı bulmak

Şikâyet hep aynı biçimde gelir — "konuşmam yok" — ama sebebi dört ayrı yerde olabilir
ve **hangisi olduğu tahmin edilemez, ölçülür.** Sıra önemli: her adım bir sonrakini
gereksiz kılabilir.

## 0. Neyi ölçtüğünü bil

Veritabanı `%LOCALAPPDATA%\VoiceTranscript.Data\voicetranscript.db`, salt okunur aç.

- `call.capture_stats` — yakalamanın kendi raporu, kaydın anındaki gerçeği taşır
- `processing_run` — hangi motorun çözümlediği ve **`speech_coverage`**
- `segment.is_me` — 1 kullanıcı, 0 karşı taraf. Bu bir tahmin değil, dosyanın kaynağı

`speech_coverage` düşükse (0.8 altı) sistem kaybettiğini zaten biliyor demektir.
Bu, aramaya nereden başlayacağını söyler: kayıp çözümlemede.

## 1. Mikrofon gerçekten kaydetmiş mi

`capture_stats` içindeki `peak` ve `silent`:

- `peak` 327 altı → kanal sessiz, sorun yakalamada, buradan ileri gitme
- `peak` 20000 üstü, `silent=0` → **mikrofon sağlam**, sorunu başka yerde ara

Yeni kulaklık şüphesi buradan çürütülür ya da doğrulanır. Kullanıcı donanımı
suçlarken çoğu zaman donanım suçsuzdur; `peak` bunu tek satırda söyler.

## 2. Dosyada ne kadar konuşma var

Arşivi aç (bkz. `arsiv-sesi-acma` skill'i) ve 50 ms bloklarla dBFS ölç:

```python
blk = int(sr * 0.05); n = len(a) // blk
db = 20*np.log10(np.sqrt((a[:n*blk].reshape(n,blk)**2).mean(axis=1))/32768.0 + 1e-9)
konusma = (db > -40).sum() * 0.05      # saniye
medyan  = np.median(db)                # kanalın karakteri
```

**Medyan kanalın kimliğini söyler ve teşhisin anahtarıdır:**

- **−90 dB civarı** → dijital sessizlik. Bu bir loopback (karşı taraf) kanalı.
- **−35…−70 dB** → gürültü tabanı var. Bu bir canlı mikrofon.

Sonra dosyadaki konuşmayı transkriptteki konuşmayla karşılaştır. Fark varsa
kayıp çözümlemede; yoksa kullanıcı gerçekten az konuşmuş ve söylenecek şey budur.

## 3. Aynı sesi yerelde çözümle

Kesin karşılaştırma. Uygulamanın kendi yorumlayıcısını kullan — sistem
Python'unda `faster_whisper` yok:

```
C:/Users/PC/AppData/Local/VoiceTranscript.Data/python/Scripts/python.exe
HF_HOME=C:/Users/PC/AppData/Local/VoiceTranscript.Data/models
python -m vt_worker transcribe    # stdin'den tek satır JSON
{"id":"t","engine":"faster-whisper","mic_path":"...","far_path":"...",
 "model_ref":"large-v3-turbo","device":"cpu","language":"tr"}
```

`engine` adı `faster-whisper`; `local` diye bir motor yok.

Yerel çok, bulut az bulduysa **sorun bulutta ve kanıtın var.** Tersi de bilgidir.

## 4. Servisin anahtarlarını tara

`/health` (kökte, `/v1` altında değil) sunucunun varsayılanlarını söyler.
**Cloudflare Python'un imzasını 1010 ile engeller — `User-Agent` başlığı koy.**
Uzun dosyayı senkron uca gönderirsen 524 alırsın; uygulama 300 saniyelik parçalara
bölüp kuyruk ucunu kullandığı için bu sınıra girmez, elle test ederken sen gireceksin.

Sonra aynı dosyayı anahtarları değiştirerek gönder. Ölçülmüş sonuç:

| anahtar | etkisi |
|---|---|
| **`normalize`** | Sessiz gürültü tabanlı mikrofonda **belirleyici**: true → 4 kelime, false → 62 |
| `vad` | Açık daha iyi. Kapatmak kaliteyi düşürür (62 → 27) |
| `filter_noise` | Sekiz kombinasyonda fark etmedi — **beklenen sonuç**, aşağıya bak |

`filter_noise` adına rağmen bir **metin** filtresidir, ses gürültü filtresi değil: yalnız
12 kanonik uydurma kalıbı, tekrar döngüleri ve `no_speech>0.85` & metin<25 karakter
durumunu eler. Çıktın bunlara girmiyorsa sıfır fark doğrudur, arıza değil. Çalıştığını
yanıttaki `filtered_out` alanından anlarsın: boş dizi "çalıştı, atacak bir şey bulamadı"
demektir; alan hiç yoksa istek o yoldan geçmemiştir.

`normalize` ise ffmpeg `loudnorm=I=-16:TP=-1.5:LRA=11`, tek geçişte **dinamik**. Ölçülmüş
davranışı: gürültü tabanı +32,4 dB, konuşma +27,7 dB — yani **taban konuşmadan 4,7 dB
fazla yükseliyor** ve modelin ihtiyaç duyduğu kontrast, ona yardım etmesi beklenen adım
tarafından daraltılıyor. Sessiz ve boşluklu bir mikrofon kanalında yıkıcı; konuşmanın
yoğun olduğu bir kayıtta faydalı. `linear=true` ile iki geçiş çare değil: gereken kazanç
true-peak boşluğunu aşınca ffmpeg sessizce dinamiğe düşüyor.

## Kural: tek dosyaya bakıp genel anahtar çevirme

Bu hatayı bu projede iki kez yaptık. Oda gürültüsü üzerinde ölçülen `normalize=false`
gerçek konuşmada 157 saniyenin 23'ünü kaybettirdi; sonra sessiz bir mikrofon
kanalında aynı ayar transkripti 15 kat iyileştirdi. **İkisi de doğruydu, çünkü
kanallar farklıydı.**

En az bir sessiz mikrofon, bir yüksek mikrofon ve bir loopback kanalında ölç.
Karar kanal bazında olmalı, genel olarak değil — kodda `chunking.prefers_gain`.

Ve tahmine güvenme: `loudnorm` dinamik olduğu için kazancın nereye düşeceği dosyanın
istatistiğinden hesaplanamaz. O yüzden karar bir kez ölçülür — kapsama `RETRY_COVERAGE`
altındaysa kanal diğer ayarla tekrar çözümlenip **iyisi** tutulur, yenisi değil.

## Uydurma cümleyi kayıp sanma

"Altyazı M.K.", "İzlediğiniz için teşekkür ederim", "Altyapı ve altyapı" —
bunlar Whisper'ın sessizliğe verdiği cevaptır, çeviri hatası değil. Bir satır
bunlardan biriyse orada **konuşma yoktu**; kayıp aramayı bırak.

Liste iki yerde: istemcide `worker/vt_worker/artifacts.py`, sunucuda
`HALLUCINATION_PHRASES`. İkisi de çalışıyor.
