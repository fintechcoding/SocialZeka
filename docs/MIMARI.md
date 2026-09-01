# Mimari

Bu belge **neyin nerede olduğunu ve neden orada olduğunu** anlatır. Sıradan bir modül listesi
değil: her kararın yanında, o kararın neyi engellediği yazıyor. Bir sonraki kişi (veya oturum)
bir şeyi değiştirmeden önce buraya bakmalı, çünkü buradaki kararların çoğu **sessizce bozulan**
şeylere karşı alınmış.

---

## Tek cümlelik fikir

> Mikrofondan giden **biziz**, hoparlörden gelen **karşı taraf**.

İki akış ayrı kaydedilirse kimin ne söylediği tahmin edilmez, **bilinir.** Projedeki hemen her
tasarım kararı bu cümlenin sonucu:

- Konuşmacı ayrıştırma (diarization) modeline gerek yok → 6 GB VRAM'de yer açılır, üst üste
  konuşmada bile doğru çalışır (diarization'ın sistematik olarak yanıldığı yer tam da orası).
- Dalga formu iki bantlı çizilebilir — tek karışık akış kaydeden hiçbir araç bunu çizemez.
- Grup aramalarında bu basitlik bozulur (karşı tarafta birden fazla kişi tek akışta karışır), o
  yüzden grup aramaları **yalnızca ses** olarak kaydedilir, yazıya dökülmez.

---

## Katmanlar

```
VoiceTranscript.App          WPF · tepsi · bütün ekranlar · orkestrasyon
        │
        ├── VoiceTranscript.Capture     WASAPI yakalama · pencere/oturum gözlemi
        ├── VoiceTranscript.Worker      Python alt sürecinin C# tarafı (Job Object, IPC)
        └── VoiceTranscript.Core        alan modeli · SQLite · çözümleme · ses dosyası işleri
                                        (UI'ye ve Windows'a bağımlı DEĞİL — bu yüzden test edilebilir)

worker/vt_worker             Python · faster-whisper · CUDA · parçalama · birleştirme
```

**`Core` neden Windows'a bağımlı değil:** geliştirme makinesinde ses donanımı yok. `Core`'da
tutulan her şey (RIFF okuma, ses birleştirme, Türkçe metin, çözümleme, depo) **ses kartı olmadan
test edilebiliyor**. Bir şeyi `Core`'dan `App`'e taşımak, onu test edilemez yapmak demektir.

---

## Ses yolu

```
 mikrofon ──┐
            ├─► WasapiCaptureBackend ──► TimelineWriter ──► <görüşme>-mic.wav
 hoparlör ──┘        (QPC damgalı)                          <görüşme>-far.wav
   (loopback)                                                      │
                                                                   ▼
                                    döküm arşivde, sessizlik kırpılmış
                                                                   │
                                              OpusArchive ──► <görüşme>-mic.ogg   (saatte ~10 MB)
                                              (24 kbit/s VBR)  <görüşme>-far.ogg   WAV silinir
                                                                   │
                                          AudioMaterialiser ──► cache/audio/<hash>-mic.wav
                                          (okuyan herkes PCM ister; .ogg gerektiğinde çözülür)
                                                                   │
                                              ConversationMix ──► <görüşme>-butun.wav
                                              (talep üzerine, önbellekli)
```

### `OpusArchive` — neden işlemden sonra, neden iki dosya

İki 16 kHz PCM akışı saatte 230 MB; bir ayın görüşmeleri onlarca gigabayt ses, dinlenecek toplam
süre belki birkaç dakika. Opus tam bu sinyal için tasarlandı: 24 kbit/s'te bir messenger
görüşmesi ayırt edilemez, dosya yirmi kat küçülür.

- **Yalnızca döküm arşive girdikten sonra.** Yazıya dökme PCM orijinali okur; dökümü olmayan
  kayıt hiç sıkıştırılmaz — o görüşme için ses tek kayıttır, arasına kodek koyulmaz.
- **İki dosya kalır.** Ayrım, konuşmacı atfını tahmin değil olgu yapan şeydir; sıkıştırma onu
  karıştırmaz.
- **Çözerek doğrulanır.** Yan dosyaya kodlanır, geri çözülüp örnek sayısı orijinalle
  karşılaştırılır, ancak ondan sonra WAV silinip satır yeni yolu öğrenir. Çökme ya orijinali ya
  doğrulanmış kopyayı bırakır, ikisini birden değil.
- **Okuyucular Opus bilmez.** `PcmReader.Open`, oynatıcı, kesit, dalga formu ve worker'a giden
  yol `AudioMaterialiser.EnsurePcm`'den geçer: `.wav` olduğu gibi döner, `.ogg` bir kez önbelleğe
  çözülür. Önbellek türetilmiş veridir — 2 GB üstünde en eskisi silinir, görüşme unutulunca
  kopyası da gider.
- **Eski kayıtlar açılışta.** En düşük öncelikli ayrı bir iş parçacığında, işlem kuyruğunun
  dışında, birer birer. Diskin gerçekten geri geldiği yer burası.
- Uzantı `.opus` değil `.ogg`: Obsidian ve Windows kabuğu söylenmeden çalar.

### Sessizce bozulan yerler

| Tuzak | Ne olur | Çözüm |
|---|---|---|
| Loopback sessizlikte paket göndermez | 60 dk aramada loopback dosyası ~27 dk olur, **33 dk kayma** | QPC damgalı duvar-saati tamponu; boşluklar sıfırla doldurulur |
| Yanlış endpoint dinlemek | 60 dakika **dijital sessizlik** kaydedilir, hata yok | `eCommunications` rolü; kullanıcı cihaz seçebilir |
| `WithDevice` + `WithDefaultDeviceStreamRouting` | NAudio reddeder, yakalama hiç başlamaz | Birlikte kullanılmaz; `WithCommunicationsMode` ile açık cihaz |
| `WithCommunicationsMode` yokken AEC | Yankı engelleme **sessizce çalışmaz** | Communications modu şart; AEC referansı denenir, başarısızsa kayıt sürer |
| Bluetooth kulaklık iki endpoint gösterir | Hands-free (16 kHz) ile A2DP karışır | `AudioDeviceCatalog.LooksHandsFree` ada bakarak ayırır |
| WAV'da `LIST` yığını | Ses bir bayt kayar, yanlış hızda çalar | `PcmReader` yığınları **yürür**, sırayı varsaymaz |

### `ConversationMix` — neden sonradan üretiliyor

Kayıt anında üretmek makineyi görüşme sırasında meşgul eder (ve **fark edilir** — ürünü bitiren
şey budur). Sonradan üretmek ayrıca **diskte zaten duran eski kayıtların hepsine** bu özelliği
kazandırır. Türetilmiş olduğu için serbestçe silinebilir — ama bu yüzden **silme işleminde
unutulması kolaydır**, ki o dosya konuşmanın tamamının çalınabilir bir kaydıdır. `DeleteCall` ve
`DeleteContactCompletely` ikisi de onu siler.

---

## Arama tespiti

Sinyal sırası: **WASAPI capture oturumu aktif** (dile bağımlı değil) > render oturumu > arama
penceresi varlığı > pencere başlığı.

```
IDLE ──R↑ / pencere──► RINGING ──C↑ (3sn)──► IN_CALL ──C↓&R↓ (6sn)──► ENDED
```

**Histerezis neden zorunlu:** `AudioSessionStateActive` "en az bir akış çalışıyor" demektir.
WebRTC'nin süreksiz iletimi (DTX) ve sessize alma tuşu, oturumu **arama ortasında** Inactive'e
düşürür. Tek örneğe tepki vermek bir konuşmayı düzinelerce parçaya böler.

**Neden pencere başlığına dayanmıyoruz:** kullanıcı Windows'u Türkçe kullanıyor; metne dayalı her
sezgi bir yerelleştirme mayınıdır. Başlık yalnızca **kişi adı** için kullanılır, tespit için değil.

### `CallWindows` — kişi adı

Kural **şekle göre**, uygulamaya göre değil: *izlenen bir uygulamanın üst düzey penceresinin
başlığı, o uygulamanın kendi adı değilse, kişinin adıdır.*

- **Telegram**: arama paneli ayrı bir üst düzey penceredir ve başlığı kişinin adıdır
  (`calls_panel.cpp` → `window()->setTitle(_user->name())`). Bedava.
- **WhatsApp**: ana pencerenin başlığının sabit "WhatsApp" olduğu doğrulandı. **Arama**
  penceresinin de öyle olup olmadığı doğrulanmadı — geliştirme makinesi WhatsApp'a
  dokunmuyor. Varsayım koda gömülmedi; çıkarsa kendiliğinden çalışır.

Başlıklar `Clean()` ile temizlenir: bidi kontrol karakterleri atılır, NFKC uygulanır. **Neden
kritik:** görünmez bir U+200E taşıyan ad, elle yazılan aynı adla eşleşmez. Sonuç aynı kişi için
**ikinci bir kişi kaydı** ve ikiye bölünmüş bir geçmiştir — iki yarı da tam görünür, defter
sadece aradaki fiyat değişimini fark etmez olur.

---

## Transkripsiyon

**Kısa ömürlü alt süreç, iş başına bir tane.** Süreç çıkışı, VRAM'in tamamını sürücüye geri veren
tek kesin mekanizmadır (`del model` + `empty_cache()` CTranslate2'de işe yaramaz — kendi
ayırıcısı var). Aynı zamanda uyku sonrası geçersizleşen CUDA context sorununu bedavaya çözer.

### GPU seçimi ve cuBLAS

```
get_cuda_device_count()  →  SÜRÜCÜYE sorar   →  kart var mı?
missing_cuda_dlls()      →  YÜKLEYİCİYE sorar →  kart kullanılabilir mi?
```

Bu ikisi **farklı sorulardır** ve karıştırmak projenin en pahalı hatasıydı: ekran "CUDA hazır ✅"
derken iş, ilk `encode()` çağrısında `cublas64_12.dll` bulunamadı diye ölüyordu — üstelik
**görüşme bittikten sonra**, kaydın tek kopyasının elde kaldığı an.

Kurallar:

- `_resolve_device()` cuBLAS yüklenemiyorsa GPU'yu **reddeder**, işlemciye düşer.
- `_start()` iş sırasında GPU çökerse bir kez işlemcide yeniden dener. faster-whisper **tembel**
  olduğu için hata `transcribe()` çağrısından değil **döngüden** gelir — yalnızca çağrıyı sarmak
  hiçbir şey yakalamaz.
- `gpu.select_device()` birden fazla NVIDIA kartında **en çok belleğe sahip olanı** seçer.
  (Intel/AMD tümleşik kartlar zaten CUDA cihazı değildir, seçilme riski yoktur.)

> **Yavaş olmak başarısızlık değil. Görüşmeyi kaybetmek başarısızlıktır.**

### Windows DLL tuzağı

Python 3.8'den beri Windows, C uzantılarının bağımlılıklarını `PATH`'te aramaz. `pip install
nvidia-cublas-cu12` tek başına **yetmez**; `os.add_dll_directory()` gerekir
(`vt_worker/dll_paths.py`). faster-whisper README'sindeki `LD_LIBRARY_PATH` tavsiyesi
Linux'a aittir ve burada **sessizce hiçbir şey yapmaz** — bu arıza genellikle "CUDA kurulumu
bozuk" diye yanlış teşhis edilir.

**Asla yapılmayacaklar:** `onnxruntime-gpu` (VRAM çalar *ve* cuDNN 9 bağımlılığını geri getirir),
`torch` (kendi cuBLAS kopyasını gölgeler). Gerekçeler `worker/requirements.txt` içinde.

---

## Depo ve arama

SQLite, `journal_mode=WAL`, `foreign_keys=ON` (**bağlantı başına**), `secure_delete=ON`.

**Türkçe tam metin arama tuzağı:** FTS5'in `unicode61` tokenizer'ı standart Unicode küçültmesi
yapar ve bu Türkçe için **yanlıştır** (`İ/i`, `I/ı`). `ışık` araması `IŞIK`'ı bulmaz — ve **hata
vermez**, sadece boş döner. Çözüm: normalize edilmiş gölge sütun + sorgunun aynı katlamadan
geçirilmesi (`TurkishText.NormalizeForSearch`). Aynı tuzak `SearchContacts`'te de var.

**Bağlantı havuzu:** `SqliteConnection.ClearAllPools()` süreç genelindedir ve testleri birbirine
düşürür. `Database.ClearPool()` bağlantı dizesine göre kapsamlıdır — testlerde o kullanılır.

---

## Çözümleme — skor değil kanıt

Dört katman: LLM çıkarımı → **deterministik çapraz kontrol (SQL, LLM yok)** → dar LLM hakemliği →
defter.

**Neden güven skoru yazılmıyor:** cömert %82 duyarlılık/seçicilik ve gerçekçi %5 yaygınlıkla,
"bu kişi yalan söylüyor" bayraklarının **%81'i yanlış çıkar**. Ayrıca beyaz-kutu problar bile
şans seviyesinde kalıyor (*Beyond Liars' Bench*, arXiv:2607.20479).

**Değişmez kural:** her kalem **birebir alıntı + tıklanabilir zaman damgası** taşır ve alıntının
kaynak metinde gerçekten geçtiği Python'da doğrulanır. Uydurma alıntı üreten bir sistem, hiç
sistem olmamasından kötüdür.

**Transkript metni güvenilmez girdidir.** Arayan kişi "önceki talimatları yoksay" diyebilir;
içerik sınırlanmış veri bloklarına sarılır ve LLM çıktısı asla yan etki tetiklemez.

---

## Günlük (`AppLog`)

İki makineli geliştirmenin tek teşhis kanalı. **Paylaşılmak üzere** yazılır, bu yüzden içine ne
konduğu bir gizlilik kararıdır: konuşma metni, kişi adı, API anahtarı ve içinde ad geçen dosya
yolu **asla** yazılmaz. Dosyanın başında bunu söyleyen bir blok var — göndermeye karar veren
kişinin hepsini okumak zorunda kalmadan ne gönderdiğini bilmeye hakkı var.

Yakalananlar: uygulama durumu, orkestratör bildirimleri, worker'ın stderr'i (GPU seçimi ve
düşüşler burada), yakalanmamış hatalar (dispatcher + AppDomain + gözlemlenmemiş görevler).

---

## Değiştirmeden önce bakılacak yerler

| Değiştireceğin şey | Önce oku |
|---|---|
| Ses yakalama | `WasapiCaptureBackend` sınıf yorumu — üç şey load-bearing ve üçü de bir kez yanlış yapıldı |
| Paket sürümleri | `worker/requirements.txt` — her pin'in gerekçesi yazılı |
| Silme | `Repository.DeleteCall` / `DeleteContactCompletely` — türetilmiş dosyalar unutulmasın |
| Türkçe metin | `TurkishText` + `CallWindows.Clean` — katlama ve görünmez karakterler |
| Testler | `test.ps1` — `dotnet test` bu TFM'de "sıfır test" der, modül doğrudan çalıştırılır |
