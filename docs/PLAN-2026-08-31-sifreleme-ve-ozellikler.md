> **Bu dosya nedir.** 2026-08-31'''de yapilan cok ajanli tasarim calismasinin ciktisi: sifreleme
> mimarisi (uc bagimsiz tasarim, uc juri tarafindan puanlandi) artir arka plan hatti, kisi tasima,
> oto-update ve CI planlari, sonra iki elestirmen (veri kaybi + eksiklik).
>
> Sifrelemede kazanan: **zarf anahtar** (gorusme basina veri anahtari, akan AES-GCM, worker'''a
> isimli boru ile aktarim) — 80/100.
>
> **Bu bir plan dosyasidir, yapilacaklar listesi degil.** Secilip siraya alinanlar YAPILACAKLAR.md
> icinde yasar. Plan degistirilmez; tarihli bir kayittir.

---

# VoiceTranscript — Nihai Uygulama Planı

> **Bu plan çalışan ağaca göre yazıldı, `HEAD`'e göre değil.** `git status`: 19 dosya değişmiş, commit edilmemiş. İçindeki iş **YAPILACAKLAR §2 (isim yakalama) + Signal Desktop desteği** ve **bitmiş**: `CallWindows.Enumerate` artık ilk eşleşmede durmuyor (`CallWindows.cs:206`), `Choose` sesin suçladığı uygulamaya göre süzüyor ve iki aday berabere kalınca tahmin etmeyi reddediyor (`:272-345`), `TitleConfidence` (`:9`) ve `CallDetector.TitleTrust` (`:22, :104, :152-155`) eklendi, `StripUnreadBadge` her iki rozet biçimini de kesiyor (`:394`), `MaxCallDuration` tavanı kondu (`CallDetector.cs:246, :320`), `CallApp.Signal = 3` (`Models.cs:15`) geldi. **Testleri de yazılmış**: `CallWindowsTests.cs` 271 satır (14 yeni test, `Choose` dahil), `CallDetectorTests.cs` 386 satır (`ACallThatNeverGoesQuietIsClosedOffAtTheCeiling` dahil).
>
> **Sonuç: 3. plan (kişi taşıma) eskimiş kaynağa dayanıyor.** Onun "dört halka da doğrulandı" gerekçesi (`CallWindows.Look:106-110`, `WhatsAppShellTitles` iki girdi, `IsShellTitle` yalnız sonek) `HEAD`'de doğru, çalışan ağaçta yanlış. **Onarım işi hâlâ gerekli** — yanlış atfedilmiş görüşmeler kendiliğinden düzelmiyor ve `Choose` tek adaylı Telegram durumunda hâlâ `Possible` döndürüyor — ama **gerekçesi ve `LabelDefaults` tasarımı yeniden yazılmalı**: yeni yakalama katmanı `TitleConfidence` üretiyor, plan 3'ün string-eşitliği tasarımı bu sinyali çöpe atıyor.
>
> Ayrıca eleştirmenin "capture testleri yok" tespiti **yanlış** — yazılmışlar. Onları planın ön koşulu saymıyorum.

---

## 0. Karar özeti

| İş | Seçilen yaklaşım (tek satır) | Kabul edilen tek en önemli ödün |
|---|---|---|
| **§0 Temel** (yeni) | Tek şema-göç mekanizması + fikirsiz sürüm defteri (3=kuyruk, 4=çağrı anahtarı, 5=kişi taşıma, 6=bulut parça), idempotent defter yazımı, kayıt hattı kusurları, silme yolunun tamamlanması, `ArchiveState` ile tembel açılış grafiği | Beş özelliğin hiçbiri başlamadan önce ~2 haftalık davranışsız iş; karşılığında üçünün birbirini bozması engelleniyor |
| **§1b İşleme kuyruğu** | Veritabanı kuyruğun kendisi (`call.state = Queued`), tek tüketici, `BEGIN IMMEDIATE` ile talep, `ICallProcessor` dikişi; `CallOrchestrator` yalnız yakalama yapar | İşleme sırasında etiket diyaloğu yanıtlanmadan çözümleme bitebilir; görüşme kartında bir süre "İsimsiz" görünür |
| **§7 Kişi taşıma/onarım** | Tek işlemsel ilkel (`MoveCall`) + tek günlük tablo (`contact_move`) + tek adım geri alma; birleştirme = N taşıma + kişi silme, aynı `batch_id` | Kişi sınırını aşan kanıt (çapraz `flag`, kapatılmış `commitment`) siliniyor/yeniden açılıyor — günlüklenerek ve geri alınabilir olarak, ama kullanıcının defteri değişiyor |
| **§5+§4 Yayın + güncelleme** (**birleşti**) | Sürüm yalnız git etiketinde; `VoiceTranscript-Setup-<v>-win-x64.exe` + `SHA256SUMS`; kurucu `AppMutex` bekler, uygulamayı **kapatmaya çalışmaz**; sessiz kurulumdan sonra `WizardSilent` ile yeniden başlatır | Kurucu imzasız: SHA-256 bozuk indirmeye karşı korur, ele geçirilmiş depoya karşı korumaz |
| **§3 Şifreleme** | Argon2id → Ana Anahtar → (SQLCipher DBK, dosya başına FK, sırlar zarfı); kilitliyken P-256 ECIES mühürüyle kayıt; yedek/kurtarma **ilk adımda** | Kilitliyken arama tamamen kapalı; kilitli-kaynaklı kayıtlar gizli ama **kimliklendirilmemiş** (aynı Windows hesabı sahte kayıt üretebilir) |

---

## 1. Şifreleme mimarisi

### 1.1 Anahtar hiyerarşisi — kriptografi bilmeden

Beş katman var. Her katmanın tek işi var.

```
  KULLANICI PAROLASI
        │  Argon2id(parola, tuz, m=64-256 MiB, t=3, p=4)   ~1 sn, kasıtlı olarak yavaş
        ▼
  PK  (parola anahtarı, yalnız RAM'de)
        │  AES-GCM ile açar
        ▼
  MK  (ANA ANAHTAR — 32 rastgele bayt)  ◄──── RK ile de açılır
        │                                      │
        │                                      │  RK = HKDF(24 bayt gerçek rastgelelik)
        │                                      │  Argon2 YOK: 192 bit tahmin edilemez.
        │                                      │  40 karakter Crockford Base32 (I/L/O/U yok),
        │                                      │  10 grup × 4 karakter + 1 sağlama baytı.
        │                                      └── BASILI KURTARMA SAYFASI
        │  HKDF(MK, "…") — tek yönlü türetme, geri gidilemez
        ├──► DBK   : SQLCipher ham anahtarı (tüm veritabanı dosyası)
        ├──► SETK  : secrets.vtb (API anahtarları)
        ├──► CKW   : çağrı anahtarlarını saran anahtar
        ├──► META  : kayıt özel anahtarı + .vtj yan dosyaları
        └──► VMAC  : vault.json'un bütünlük etiketi

  Görüşme başına:  CK = 32 rastgele bayt → CKW ile sarılır → `call_key` tablosu + dosya başlığı
  Dosya başına  :  FK = HKDF(CK, fileId)  → o dosyanın çerçevelerini şifreler
```

**Parola değişimi ne yapar, ne yapmaz.** MK sabit kalır, yalnız PK sarmalayıcısı taze bir Argon2 tuzuyla yeniden yazılır. Sonuç: parola değişimi ~4 KB yazar, arşive dokunmaz, saniyeler sürer. `PRAGMA rekey` asla çalışmaz çünkü DBK paroladan değil MK'den türer. `.tmp` ve `.yedek` dosyaları **imha edilir** — yoksa eski sarmalayıcı diskte kalır ve parola değişimi bir iptal değil, bir süs olur.

**Kurtarma anahtarı kendi kendine yeter — bu bir karar.** Eleştirmen haklı: MK'nin sarmalayıcıları yalnız `vault.json` içinde yaşarsa, fidye yazılımı veya bozuk sektör o 4 KB'yi alınca basılı kâğıt hiçbir işe yaramaz. Ama MK'yi `HKDF(kurtarma baytları)` yapmak da yanlış: o zaman MK asla döndürülemez.

**Karar:** MK rastgele kalır, basılı sayfa **iki blok** taşır:
1. 40 karakterlik kurtarma anahtarı (insanın yazacağı),
2. ~120 baytlık **kurtarma zarfı** — MK'nin RK-sarmalayıcısı + `vaultId` + kayıt özel anahtarının META sarmalayıcısı — 200 karakter Base32 olarak **ve** tek bir QR kod olarak.

Böylece kâğıt tek başına arşivi açar, `vault.json` bir kolaylık dosyasına iner, MK döndürülebilir kalır. **Ödün:** sayfa artık arşivin kendisi kadar hassastır ve sihirbaz bunu bu kelimelerle söylemek zorundadır ("Bu sayfa parolanızın yedeği değil, **arşivin anahtarıdır**").

### 1.2 Veritabanına ne oluyor

**Tüm dosya SQLCipher ile şifreleniyor. Alan bazlı şifreleme değil.**

Sebep tek cümle: alan bazlı şifrelemede `segment_fts` şifreli metnin dizini olur, `MATCH` sessizce sıfır satır döndürür — `Schema.cs:8-12`'nin zaten uyardığı tam o hata ("sessiz sıfır satır, veri kaybından ayırt edilemez").

Pakette değişiklik: `Microsoft.Data.Sqlite` → `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_e_sqlcipher 2.1.11` + açık `SQLitePCLRaw.core` sabitlemesi (`Directory.Packages.props:3` merkezî geçişli sabitleme açık) ve `SQLitePCL.Batteries_V2.Init()` çağrısı.

Anahtar şu biçimde verilir: `Password = "x'<64 hex>'"`. MDS bunu `SELECT quote($password)` ile kaçırır, SQLite geri açar, SQLCipher **ham anahtar** olarak alır — PBKDF2 çalışmaz. `Repository.Open()`'ın ~1200 satırı bağlantı başına 256.000 tur PBKDF2 ödemeye dayanamaz, o yüzden bu ölçülmeden hiçbir şey yazılmaz (bkz. §2, Kapı Sıfır).

`Database.cs:34-40`'taki pragma yığınına **`PRAGMA temp_store = MEMORY`** eklenir: onsuz bir FTS5 birleştirmesi düz metin dizin sayfalarını `%TEMP%`e taşırabilir.

**`Directory.Packages.props:16-17`'deki yorum yalan olacak** — "diğer paketlerde FTS5 yok" diyor, oysa `e_sqlcipher.dll` ikilisinde `ENABLE_FTS5`, `unicode61`, `remove_diacritics` var. O yorum düzeltilmezse birileri bu değişikliği geri alır.

**Bedel dürüstçe:** SQLite 3.39.2 (2022) vs bugünkü 3.53.3. `Storage/`'daki en yeni özellik `RETURNING` (3.35.0'da geldi), o yüzden bugün güvenli. Ama dört yıllık yukarı akış düzeltmesi kaybediliyor ve `bundle_e_sqlcipher` Aralık 2024'ten beri sürüm çıkarmadı. Kaçış yolu `bundle_e_sqlite3mc` (SQLite 3.49.1, FTS5 doğrulandı) — ama varsayılan şifresi sabitlenmeden geçilmez.

### 1.3 Ses dosyalarına ne oluyor

Yeni bir kap: **`.vta`** (VTA1). Yakalama iş parçacığında canlı yazılır.

```
[ 128 bayt başlık ]  VTAUDIO1 | biçim | fileId | callId | rol | vaultId
                     | sarmaKind | sarılmış CK/FK | 0..95 üzerinde GCM etiketi
[ çerçeve 0 ] 32.768 bayt düz metin → şifreli + 16 bayt etiket   (adım 32.784)
[ çerçeve 1 ] ...
```

`AudioFormat.WhisperPcm` (`AudioFormat.cs:6`, 32.000 B/sn) için bir çerçeve **1,024 saniye**, ek yük **%0,049**.

Kritik olan şu: **kapta RIFF başlığı da uzunluk alanı da yok.** Düz metin uzunluğu `FileInfo.Length` üzerinden aritmetiktir. Bu yüzden `Checkpoint` (bugün `WavPcmSink.cs:82-96`, `CallRecorder` 30 saniyede bir tetikliyor) yalnızca bir `Flush()`'a iner — geri sarma yok, başlık yaması yok, `TryRepair` düzeneği yok. Ve nonce yeniden kullanımı **yapı gereği** imkânsız hale gelir: çerçeveler yalnız eklenir, tam bir kez mühürlenir, `nonce = nonceSalt(4) || frameIndex_LE(8)` saf bir sayaçtır.

`AAD = fileId || frameIndex || plaintextLength` — birleştirme, yeniden sıralama ve kısa çerçeve ikamesine karşı bağlar.

Okuma tarafı `VtaStream : Stream`, O(1) arama (çerçeve = `pos/32768`), böylece `AudioPlayer.CurrentTime`, `WaveformPeaks`, `ConversationMix`, `AudioClip` tek bir `AudioStore` dikişinin arkasında çalışmaya devam eder. `VtaStream` `CryptographicException`'ı içeride yakalar ve `InvalidDataException` olarak yeniden fırlatır — mevcut tüm `catch` süzgeçleri (`WaveformPeaks.cs:59`, `ConversationMix.cs:153/:170`) çalışmaya devam eder.

**Kuyruk çerçevesi etiketi tutmazsa EOF sayılır.** Kesik bir dosyayı çözmeyi reddetmek, tam da bu biçimin kurtarmak için var olduğu kayıtları yok ederdi. Ama kurtarılan uzunluk `call.duration_ms` ile karşılaştırılır ve eksiklik görünür bir **"eksik görünüyor"** rozeti çıkarır — sessizce kesilmiş görüşme sunulmaz.

**`.wav` ve `.vta` kalıcı olarak bir arada yaşar.** Göçün iptal edilebilir, çökebilir ve devam ettirilebilir olmasını sağlayan tek şey bu.

**Yedeklilik kasıtlı:** CK hem `call_key` tablosunda (`ON DELETE CASCADE`) hem de her dosyanın kendi başlığında bulunur. Yırtık bir 128 baytlık başlık bir saatlik konuşmaya mal olmamalı. Silme, bağlantıyı kesmeden önce başlıktaki anahtar bloğunun üzerine yazar ve başarısızlığı `RemoveFiles`'ın zaten yaptığı gibi bildirir (`Repository.cs:834-864`).

**Kilitli kayıt için ek bir yedeklilik gerekiyor** (eleştirmen bulgusu 5, kabul edildi): kilitliyken veritabanı yok, dolayısıyla `call_key` satırı da yok — başlık **tek** kopyadır. Bu yüzden mühürlü anahtar bloğu `.vtj` yan dosyasına da yazılır ve **yan dosya sesten önce** yazılır. Kilit açıldığında uzlaştırıcı, sesi taşımadan **önce** `call_key` satırını yazar.

### 1.4 Python worker sesi nasıl okuyor

**İsimsiz, devralınan tanıtıcılı borular. Anahtar da, yol da, tek bir düz metin bayt da worker'a veya dosya sistemine ulaşmaz.**

Mevcut stdin yolu imkânsız: `PythonWorkerHost.cs:282` stdin'i kapatıyor (`// the worker reads stdin to EOF`) ve `__main__.py` onu tümüyle okuyor. **İsimli** boru güvensiz: `\\.\pipe\` sıralanabilir ve aynı kullanıcıdaki bir süreç — tam olarak varsayılan saldırgan — bağlanma yarışını kazanıp konuşmanın tamamını alabilir.

`AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)`'ın ad alanı girdisi yoktur. İstemci tanıtıcı dizesi istek JSON'unda `mic_handle`/`far_handle` olarak gider, çocuk süreç devralır, ebeveyn `Process.Start`'tan sonra `DisposeLocalCopyOfClientHandle()` çağırmak zorundadır yoksa okuyucu EOF görmez. Python tarafı: `msvcrt.open_osfhandle` → `os.fdopen` → EOF'a kadar oku → `np.frombuffer('<i2').astype(float32)/32768`.

Motor arayüzü yoldan ndarray'e döner. **Dört çağıran var**: `__main__.py:208`, `models.py:302` (self_test — her önceki tasarımın kaçırdığı), `faster_whisper_engine.py:182/216`, `whispercpp_engine.py:80-92`.

**Üç şey planlarda kapatılmamıştı, burada kapatılıyor:**
1. **Tanıtıcı devralma süreç geneline yayılır.** İstemci tanıtıcısı açıkken başlatılan *her* çocuk bir kopya alır — eşzamanlı bir `ProbeAsync` veya `HealthViewModel.CheckWorkerAsync` yüzünden okuyucu asla EOF görmez. Süreç başlatma tek bir kilit altında serileştirilir.
2. **`PythonWorkerOptions.Timeout` iki saat** (`PythonWorkerHost.cs:37`). Ölü okuyuculu dolu bir boruda yazıcı saatlerce asılı kalır. Kopyalama görevlerine kendi süre sınırı verilir.
3. **STT API anahtarı hâlâ düz metin geçiyor**: `SttEndpoint.ToModelRef()` `baseUrl|apiKey|model` paketleyip `TranscriptionRequest.ModelRef` içine koyuyor, `PythonWorkerHost.cs:279` stdin'e yazıyor. `settings.json`'u şifrelemek buna dokunmuyor. Boru, dosyadan iyidir; ama tasarımda **söylenmelidir**.

### 1.5 Tam metin aramaya ne oluyor — açıkça

**Kilit açıkken: hiçbir şey değişmiyor.** `Schema.cs:96-121` (FTS5 tabloları ve tetikleyicileri), `Repository.Search` ve `CallsMentioning` **sıfır** düzenleme ister. Türkçe katlama (`TurkishText.NormalizeForSearch`) dokunulmadan kalır. "kitap" araması "kitabı" ve "kitaptan"a ulaşmaya devam eder. Bedel: soğuk sayfa G/Ç'sinde **%5-15**.

**Kilitliyken: arama tamamen kullanılamaz.** Ve her sayfa **açık bir kilitli durum** göstermek zorundadır — asla boş sonuç listesi. Sebebi `Schema.cs:8-12`'de zaten yazıyor: sessiz sıfır satır veri kaybından ayırt edilemez.

**Şifrelemenin gizlemedikleri** — sihirbazın ilk ekranında, dipnotta değil:
- `recordings/` klasörü ay klasörleri, dosya adları ve boyutları üzerinden **görüşme zamanlarını, sayısını ve sürelerini sızdırır** (32.000 bayt/saniye + %0,049).
- `logs/vt-*.log` görüşme tamamlanmalarını düz metin kaydeder — **kasıtlı**, çünkü tanılama kilitliyken de çalışmalı.
- Dışa aktarılan klipler, Obsidian notları ve yedekler **kasıtlı olarak açık** çıkar.

### 1.6 Kaldırılamayan artık riskler

1. **Kilitli kayıt gizlidir ama kimliklendirilmemiştir.** Kayıt açık anahtarı zorunlu olarak herkese açık, dolayısıyla Windows hesabına erişen biri geçerli bir mühürlü `.vta` + `.vtj` üretebilir ve uzlaştırıcı onu gerçek bir görüşme olarak alır. Kısmi azaltma: bu satırlar `origin='locked'` taşır ve arayüzde öyle gösterilir. **Bir gizli anahtar olmadan kapatılamaz.**
2. **Anahtarlar süreç ömrü boyunca yönetilen bellekte kalır ve tam temizlenemez.** Veritabanı anahtarı bağlantı dizesinin içinde değişmez bir `String` (ve havuz anahtarı), `CryptographicOperations.ZeroMemory` ona ulaşamaz. WER çökme dökümü, sayfa dosyası veya `hiberfil.sys` hepsini yakalayabilir. Verilen garanti **"diskte okunabilir biçimde değil"**, "RAM'de değil" değil. Hiçbir arayüz metni aksini iddia edemez.
3. **Silinen düz metin silinmiş değildir.** SSD'de aşınma dengeleme yüzünden eski bloklar TRIM'e kadar hayatta kalır. Tek dürüst azaltma: göçten sonra her API anahtarını döndürmek + tam disk şifrelemesi. Bakım ekranı **"üzerine yazıldı"** demeli, asla "silindi".
4. **`vault.json` küçültülmüş ama sıfırlanmamış bir kayıp noktası.** Ayna (`vault.json.2`), her yedekte bulunması, `.vta` varken taze kasa kurmayı reddetmek ve §1.1'deki kurtarma zarfı bunu ciddi ölçüde azaltır — ama disk arızası her ikisini de alırsa kâğıt devreye girer, o yüzden kâğıt saklanmadıysa arşiv gider.
5. **Bulut STT anahtarı boru üzerinden düz geçer** (§1.4, madde 3).
6. **Yakalama iş parçacığındaki sistem çağrısı hızı yaklaşık ikiye katlanır.** `bufferSize 1` ile akış başına 1,024 saniyede bir ~32.784 baytlık `WriteFile`, `lock(_gate)` içinde, WASAPI geri çağrı iş parçacığında. Şifrelemenin kendisi 33-165 mikrosaniye — risk o değil, bloklayan yazma hızı. O iş parçacığı takılırsa sürücü `DATA_DISCONTINUITY` yükseltir ve **gerçek ses kaybolur**. Tasarlanmış ama yapılmamış kaçış: önceden ayrılmış tampon halkasıyla beslenen tek yazıcı iş parçacığı.

---

## 2. Sıra

Depo kendi sırasını zaten yazmış (`YAPILACAKLAR.md:43-57`): 1 → 1b → 2 → 7 → 5 → 4 → 3, gerekçesiyle birlikte — **şifreleme `Repository`'yi, ses okuma/yazma yollarını ve worker protokolünü aynı anda değiştirir, o yüzden o yolların doğru şeklini önce bulmak onları bir kez yazmak demektir.** §2 indi. Bu sıraya uyuyorum ve önüne bir Faz 0 koyuyorum.

### Faz 0 — Temel (hiçbir özellik başlamadan önce)

| # | İş | Boy | Ön koşuludur |
|---|---|---|---|
| 0.1 | Çalışan ağacı commit et; plan 3'ün gerekçesini ve `LabelDefaults` tasarımını `TitleConfidence` üzerine yeniden yaz | S | Hepsi |
| 0.2 | **Tek şema-göç mekanizması + sürüm defteri.** `Database.Migrate` `setting.schema_version`'ı okur (bugün `Database.cs:79-84` yazıyor, **hiç kimse okumuyor**), sıralı `Schema.Migrations` uygular. Sürümler önceden dağıtılır: **3**=kuyruk, **4**=çağrı anahtarı+origin, **5**=`contact_move`, **6**=bulut parça. Kimse kendi başına numara almaz. | M | §1b, §7, §3 — **üçü de bugün Version=3'e çıkıyor** |
| 0.3 | **Defter yazımını idempotent yap.** `AnalysisPipeline.cs:123-124, :148` çıplak `InsertCommitment/InsertClaim/InsertFlag` çağırıyor, `src/`'de `commitment/claim/flag` için hiç `DELETE FROM` yok. `ReplaceLedgerForCall(callId)` eklenir (`ReplaceSegments`'in aynadaki eşi, `Repository.cs:301-337`), kullanıcının dokunduğu `status`/`dismissed_by_user` taşınır. | M | **§1b'nin zorunlu ön koşulu** |
| 0.4 | **Kayıt hattı kusurları (§1).** Y1: `BeginRecordingAsync` `_currentCallId`/`_recorder`'ı koşulsuz eziyor; Y2: `catch` alanlara davranıyor, `CallOrchestrator.cs:305` **canlı** bir kaydediciyi atıyor; Y7: beş saniye eşiği yazılan çerçeveyi ölçüyor, `Discard` (`:367`) `Notice` bile vermeden gerçek bir görüşmeyi siliyor; Y13: `_backend.Dispose()` hiç çağrılmıyor; Y14: `UnlabelledCalls` (`Repository.cs:420-429`) devam eden kaydı döndürüyor; K5: `StartAsync` `targetProcessId`'yi null bırakıyor. | L | §1b (aynı 40 satırı yeniden yazıyor) |
| 0.5 | **Silme yolunu tamamla.** (a) `DeleteCall` (`Repository.cs:807-834`) dosya listesini yalnız `mic_path`/`far_path`'ten kuruyor → NULL yollu satır **hiçbir şey silmeden başarı bildiriyor**; ada göre süpürme eklenir (`call-{id}-mic.*`, `-far.*`, `-butun.*`). (b) `ConversationMix.PathFor` (`:27-38`) uzantıyı sabit yazıyor. (c) `.cloudparts` (`cloud_engine.py:154-164`) **recordings/ içinde** yaşıyor ve hiçbir silme yolu görmüyor → istek alanıyla veri kökü altına taşınır, terminal durumda ve açılışta süpürülür. (d) `-butun.wav.partial` (`ConversationMix.cs:112`) hiç silinmiyor. | M | §3 (bunlar şifreli arşivde düz metin kalır) |
| 0.6 | **`ArchiveState` + tembel açılış grafiği.** Bugün `App.xaml.cs:202-204` Database/Migrate/Repository kuruyor, `:239` `ShellViewModel` kuruyor, o da beş sayfa VM'i kurup `RefreshAll()` çağırıyor. Kilitli/göç eden/geri yüklenen bir arşivde **pencere hiç kurulamaz** — kilitli sayfayı gösterecek kabuk yok. `ArchiveState` (Locked/Migrating/Restoring/Ready) kabuğun üstünde durur, sayfa VM'leri boş kurulur, talep üzerine yükler. | L | **§3'ün en büyük entegrasyon maliyeti**; ayrıca göç ve geri yükleme için de gerekli |
| 0.7 | Yalan söyleyen iki yorumu düzelt: `Directory.Packages.props:16-17` (§1.2) ve `AudioRetentionDays`/`TranscriptRetentionDays` — hiçbir yerde okunmuyorlar, ayar penceresinden kaldırılır (gerçek süpürme yazılana kadar). | S | — |

### Faz 1 — §1b İşleme kuyruğu

| # | İş | Boy | Not |
|---|---|---|---|
| 1.1 | Şema 2→3: `call.attempts` + `call.interrupted` (**iki ayrı sütun**) | S | 0.2 gerekli |
| 1.2 | `Repository` kuyruk işlemleri, `BeginTransaction(deferred: false)` ile | M | 0.2 |
| 1.3 | `ICallProcessor` + `WorkerCallProcessor` (birebir taşıma, ayrı commit) | M | — |
| 1.4 | `WorkerCallProcessor` davranış değişikliği (aşama bilgisi, isimsiz çağrı Transcribed'de durur) | M | 0.3 **zorunlu** |
| 1.5 | `ProcessingQueue` + tüketici + kurtarma + `DisposeAsync` | L | 0.4 |
| 1.6 | `CallOrchestrator`'ı yakalamaya indirge, `OrchestratorState.Processing`'i sil | M | 0.4 |
| 1.7 | Arayüz: `MainWindow` `BeginInvoke` + etiket kuyruğu, Overview sonuç kartı, tepsi bildirimi | M | — |

### Faz 2 — §7 Kişi taşıma ve onarım

| # | İş | Boy | Not |
|---|---|---|---|
| 2.1 | Şema →5: `contact_move` (yabancı anahtarsız, kasıtlı) | S | 0.2 |
| 2.2 | `MoveCall` + `RecomputeCounters` + `AssignContact` devri | L | 0.3 (defter kopyası olmasın diye) |
| 2.3 | `MergeContacts`, `RenameContact`, başlık bağlama onarımı, `UndoBatch` | L | 2.2 |
| 2.4 | `LabelDefaults` — **`TitleConfidence` tüketir** (Likely→işaretli, Possible→işaretsiz, None→asla) | S | 0.1 |
| 2.5 | `MoveCallWindow`, `ContactRepairWindow`, `RenameContactWindow` + strings | L | — |
| 2.6 | Obsidian not temizliği — **önce yeni notu yaz, sonra eskisini sil** | M | — |

### Faz 3 — §5+§4 Yayın hattı + güncelleme (**tek iş**)

| # | İş | Boy |
|---|---|---|
| 3.1 | `global.json` (yalnız `sdk`), `Directory.Build.props` `VersionPrefix/VersionSuffix`, test csproj yorumunun düzeltilmesi | S |
| 3.2 | `publish.ps1 -Version -RequireInstaller`, `version.generated.iss`, `SHA256SUMS` | M |
| 3.3 | `installer/VoiceTranscript.iss`: `AppMutex`, `CloseApplications=no`, `CheckForMutexes` bekleme döngüsü, `WizardSilent` `[Run]` girdisi | M |
| 3.4 | CI + release + release-publish iş akışları, bileşik eylem | M |
| 3.5 | `AppVersion` + `ReleaseAssets` (Core, saf) | M |
| 3.6 | `UpdateService`, `UpdateGuard`, `UpdateAttempt`, `UpdateWindow`, ayar sayfası | L |

### Faz 4 — §3 Şifreleme

**Plan 1'in sırası iki yerde değişti** (eleştirmen bulguları 1 ve 4 kabul edildi):

| # | İş | Boy | Neden bu sırada |
|---|---|---|---|
| 4.0 | **Kapı Sıfır** — dört ölçüm, tek atılacak dalda | M | Kalan her adım ölçülmesi gereken varsayımlara dayanıyor |
| 4.1 | Kasa çekirdeği + sırlar zarfı (`VaultFile`, `Argon2Kdf`, `RecoveryKey`, `KeyRing`, `SecretStore`) | L | API anahtarları `settings.json`'da ve **koşulsuz her yedeğe giriyor** (`BackupService.cs:81-83`) |
| 4.2 | **YEDEK/GERİ YÜKLEME — plan 1'in 13. adımı buraya taşındı** | M | **Anahtar bağımlı tek bayt diske yazılmadan önce yedek yolu anahtar malzemesini taşımalı.** Aksi halde 9 adım boyunca alınan her yedek açılamaz bir arşive geri yüklenir |
| 4.3 | Kilit açma arayüzü + sihirbaz + kurtarma anahtarı kapısı (+ soğuk doğrulama) | L | 0.6 gerekli |
| 4.4 | Obsidian **ve Notion** otomatik dışa aktarımına sert kapı | M | `CallOrchestrator.cs:482` ve `:488` her görüşmeden sonra gözetimsiz çalışıyor |
| 4.5 | VTA1 kabı: biçim, **okuyucu**, `AudioStore` dikişi | L | Okuyucu önce iner: okunamayacak hiçbir şey yazılamaz |
| 4.6 | **Kilitli mod mühürleme + `.vtj` + uzlaştırıcı** — plan 1'in 11. adımı buraya çekildi | L | Plan 1'de 8. adım `.wav`'ı kesiyor, 11. adım kilitli kaydı üç adım sonra getiriyor: **arada her sabahki görüşme kaydedilmiyor** |
| 4.7 | Kaydedici `.vta` yazar; şema →4 (`call_key`, `call.origin`) | M | 4.6 |
| 4.8 | Worker devri: isimsiz borular + ndarray motorları | L | 1.3 (`WorkerCallProcessor`) ve 0.5(c) gerekli |
| 4.9 | Ses göçü **+ ters göç aynı adımda** | L | 4.7 |
| 4.10 | Veritabanı şifrelemesi + `MigrationGuard` + `SqliteOpenMode.ReadWrite` | L | 0.6 gerekli |
| 4.11 | Bulut devam ettirme (protokol olayı) | M | Son |

**Faz 3 ile Faz 4 çakışır:** `UpdateGuard` kasa göçü, kilitli birikim ve kuyruk derinliğini de reddetmelidir — o yüzden 4.9/4.10 indikten sonra `UpdateGuard`'a dönülür (küçük bir iş, ama unutulursa güncelleme yarım kalmış bir göçün üstüne `{app}`'i değiştirir).

---

## 3. Her iş için adımlar

### 3.0 Faz 0 — Temel

1. **`Schema.cs:20` + `Database.cs:66-87`** — `Version` bir sabit değil, `Migrations` dizisinin uzunluğu olur. `Migrate()` `setting`'ten `schema_version`'ı okur, `Statements` (yalnız `CREATE ... IF NOT EXISTS`) geçişini yapar, sonra depolanan sürümden büyük her `Migrations` girdisini aynı işlem içinde uygular, sonunda sürümü yazar. `Schema.cs`'nin başına numara defteri yorumu.
2. **`Repository.cs`, `ReplaceSegments`'in (`:301-337`) yanına** — `ReplaceLedgerForCall(long callId)`: aynı işlemde `commitment`/`claim`/`flag` satırlarını `call_id`'ye göre siler, kullanıcının kapattığı/reddettiği satırların durumunu `(call_id, quote_start_ms, kind)` anahtarıyla taşır. **`AnalysisPipeline.cs:111-148`** bunun üzerinden yazar.
3. **`Repository.cs:301-337`** — `ReplaceSegments` boş bir liste ile dolu bir transkripti değiştirmeyi **reddeder** (işlem içinde sayar, atarsa çağıran başarısız işaretler). Transkript, sesten bedelsiz yeniden üretilemeyen tek eserdir.
4. **`CallOrchestrator.cs:271-311`** — `BeginRecordingAsync` yerel değişkenlerle çalışır, canlı kaydediciyi asla ezmez; `catch` yalnız kendi yerel kaydedicisini atar. **`:339-368`** — beş saniye eşiği duvar saatine bakar, `Discard` `Notice` yükseltir. **`CallRecorder.cs:297-316`** — `_backend.Dispose()`. **`:113-117, :246`** — `targetProcessId` geçirilir.
5. **`Repository.cs:807-834` ve `:903-940`** — silme, saklanan yolların yanı sıra `RecordingDirectoryFor(call.StartedAt)` altında `call-{id}-*` desenini de süpürür. **`ConversationMix.cs:27-38`** uzantıyı kaynaktan türetir ve `Candidates(micPath)` hem `-butun.wav` hem `-butun.vta` döndürür. **`ConversationMix.cs:112`** `.partial` başarısızlıkta silinir.
6. **`worker/vt_worker/engines/cloud_engine.py:154-164`** — `_workspace` artık `f"{wav_path}.cloudparts"` değil, istekten gelen `workspace_dir` altında görüşme başına dizin. **`WorkerProtocol.cs`** alanı taşır, **`CallOrchestrator`** terminal durumda ve açılışta süpürür.
7. **`App.xaml.cs:195-265` + `ShellViewModel.cs`** — `ArchiveState` yukarıda kurulur; sayfa VM'leri boş kurulur; `RefreshAll()` yalnız `Ready`'de çalışır; her sayfa "yüklenmedi" ile "kayıt yok"u ayırır.
8. **`SettingsWindow.xaml` + `SettingsViewModel.cs:86, :245, :522`** — saklama süresi satırı kaldırılır; `strings.tr.json`/`strings.en.json` eşlenir.

### 3.1 §1b İşleme kuyruğu

Plan 2'nin adımları geçerli; şu üç düzeltmeyle:

1. **`Schema.Migrations`'a iki sütun**: `call.attempts` ve `call.interrupted`. `RequeueInterrupted` **`attempts`'i artırmaz** — bir elektrik kesintisi bir deneme değildir (eleştirmen bulgusu 7).
2. **`Repository.ClaimNextForProcessing`** — `connection.BeginTransaction(deferred: false)`, yani `BEGIN IMMEDIATE`. Parametresiz aşırı yükleme `DEFERRED` verir; WAL altında okuyup sonra yazan ertelenmiş bir işlem `SQLITE_BUSY_SNAPSHOT` alır ve `busy_timeout` (`Database.cs:37`) buna uygulanmaz. Sıralama: **`ORDER BY attempts ASC, started_at ASC`** — hiç denenmemiş görüşme, Health sayfasından yeniden kuyruğa alınmış on iki başarısızın önüne geçer.
3. **`WorkerCallProcessor.ProcessAsync`** talep edilen **aşamayı** alır. İsimlendirme sonrası yeniden kuyruğa alma **çözümleme aşamasından** girer, transkripsiyondan değil. Yoksa GPU'sunu kaybetmiş bir makinede 400 segment silinir (0.3 ve `ReplaceSegments` reddi bunu ikinci savunma hattı olarak tutar).
4. `FailExhausted`'ın sebebi deneme sayısını değil **son gerçek `failure_reason`'ı** adlandırır.
5. `CallOrchestrator.Dispose` (`:765`), kaydediciyi atmadan önce `Stop()` + `CompleteCall` + `SetCallState(Queued)` yapar.

### 3.2 §7 Kişi taşıma ve onarım

Plan 3'ün adımları, iki değişiklikle:

1. **`MoveCall` durum kapısı yalnız 2 (Transcribing) ve 4 (Analysing)'ü reddeder.** Durum 3 (Transcribed) yeni tasarımda bir **dinlenme durumudur** — plan 2 isimsiz görüşmeleri oraya park ediyor. Plan 3'ün yazdığı gibi 2/3/4 reddedilirse, `AssignContact` → `MoveCall` devri yüzünden **bir görüşmeyi isimlendirmek** `LabelCallWindow.Save_Click`'te (`:159`, try/catch yok) yakalanmamış bir istisnaya döner. Daha iyisi: kapı kuyruğa sorar ("bu görüşme şu an uçuşta mı?"). `Save_Click` yine de try/catch'e alınır.
2. **`LabelDefaults.ShouldRememberTitle`** artık `TitleConfidence` alır: `Likely` → işaretli, `Possible` → işaretsiz ama sunulur, `None` → kutu hiç gösterilmez. `LabelCallWindow.xaml:86`'daki `IsChecked="True"` kalkar. Mevcut bir bağlama başka kişiye işaret ediyorsa kutu zorla kapatılır ve görünür bir uyarı çıkar — `RememberTitle` (`Repository.cs:143`) sessizce yeniden yönlendiriyor ve arşiv tam olarak böyle bozuldu.
3. `Repository.cs:250-272` `AssignContact` yalnız **yeni** kişiyi yeniden hesaplıyor; `InsertCall` ve `DeleteCall` sayaçlara hiç dokunmuyor — üçü de `RecomputeCounters` üzerinden geçer. Açılışta bir kez `RepairContactCounters()`.

### 3.3 §5+§4 Yayın hattı + güncelleme — **çatışma çözümü**

Plan 4 ve plan 5 aynı özelliğin iki hâli ve **her eserde çelişiyorlar**. Karar tablosu:

| Konu | Plan 4 | Plan 5 | **Karar** | Gerekçe |
|---|---|---|---|---|
| Sürüm özelliği | `<Version>1.0.0</Version>` | `VersionPrefix/Suffix` = `0.0.0-dev` | **Plan 5** | Yerel derleme belli ki bir yayın değil ve her yayının altında sıralanır |
| Kurulum adı | `...-1.1.0.exe` | `...-1.1.0-win-x64.exe` | **Plan 5** | RID açık; ileride ikinci mimari mümkün |
| Sağlama dosyası | `SHA256SUMS.txt` | `SHA256SUMS` | **Plan 5** | `sha256sum` sözleşmesi |
| `CloseApplications` | `no` + `AppMutex` + 30 sn `CheckForMutexes` beklemesi | `yes` + `RestartApplications=no` | **Plan 4** | `MainWindow.OnClosing` `e.Cancel = true` yapıyor; Restart Manager bu pencereyi kapatamaz. `yes` elle çalıştırılan kurucuyu **görüşme ortasında süreç öldürmeye** çevirir |
| Yeniden başlatma | özel `/RELAUNCH` + `WantsRelaunch` | yerleşik `WizardSilent` | **Plan 5** | Yeni `[Code]` gerekmiyor |
| Deneme işareti | var (`updates/attempt.json`) | yok | **Plan 4** | Uygulama kurucu çalışırken ölü; sessizce hiçbir şey yapmayan bir kurucuyu fark etmenin **tek** yolu |
| Taslak/ön-sürüm | belirtilmemiş | `v1.2.0`→taslak, `v1.2.0-rc.1`→ön-sürüm | **Plan 5** | `/releases/latest` ikisini de atlar |
| CI | yok | `ci.yml`+`release.yml`+bileşik eylem | **Plan 5** | — |

Mevcut `installer/VoiceTranscript.iss:93-94`'te **her iki `[Run]` girdisi de `skipifsilent`** taşıyor — yani bugünkü sessiz kurulum kullanıcıyı hiç çalışmayan bir kaydediciyle bırakır. Yeni girdi `skipifsilent` taşımaz, `Check: WizardSilent` ile korunur.

**`UpdateGuard` reddetme listesi** (plan 4'ün listesi + Faz 4'ün ürettiği durumlar): kayıt, işleme, elle kayıt, `--data`, kurucuyla kurulmamış, disk alanı, **kasa göçü çalışıyor**, **`recordings/bekleyen/` boş değil**, **`BackupService.HasPendingRestore`**, **`QueueDepth() > 0`**. Ve `LaunchAsync` "kapıyı iki kez kontrol et" yerine **sessizleştirme** yapar: (1) dedektörü durdur, (2) uçuşta kayıt varsa **tamamen iptal et**, (3) ancak sonra işareti yaz ve kurucuyu başlat. Aksi halde `CallOrchestrator`'ın saniyede bir tikleyen döngüsünde bir saniyelik pencere vardır.

### 3.4 §3 Şifreleme — adımlar

**4.0 Kapı Sıfır** (`tests/VoiceTranscript.Tests/EncryptionGateTests.cs`, atılacak dal):
(a) `bundle_e_sqlcipher` + `Batteries_V2.Init()` ile mevcut `Database.Fts5Available()` (`Database.cs:96-113`) ve `RepositoryTests.Fts5IsCompiledIntoTheSqliteBuild` yeşil mi? (b) `Password="x'<64 hex>'"` ile 1.000 fiziksel açılışın medyanı **2 ms altında** mı (ham anahtar kanıtı) ve yanlış anahtar `Open()`'da atıyor mu? (c) Konsious Argon2id 64/128/256 MiB @ t=3,p=4 gerçek duvar saati. (d) `AnonymousPipeServerStream` istemci tanıtıcısı `python.exe`'ye devroluyor ve `msvcrt.open_osfhandle` EOF'a kadar okuyor mu? (e) **`pywhispercpp.Model.transcribe` float32 ndarray kabul ediyor mu?** — düz-metinsiz worker yolunun dayandığı tek gerçek. Hayırsa whisper.cpp kasa açıkken **devre dışı bırakılır**, sessizce geçici dosyaya düşürülmez.

**4.1** `src/VoiceTranscript.Core/Security/{VaultFile,Argon2Kdf,RecoveryKey,KeyRing,SecretStore}.cs`. `AppSettings.cs:157/232/248` (`AsrApiKey`, `LlmApiKey`, `NotionApiKey`) ve `SttEndpoint.ApiKey` zarfın arkasına taşınır. **`SecretsEnvelope` opak bir dize olmalı ve başarısız çözmede birebir geri dönmeli** — `AppSettings.Load` (`:417-427`) `JsonException`'da sessizce varsayılanlara düşüyor ve bir sonraki `Save` o boşluğu gerçek anahtarların üzerine yazardı. **Yazma sırası ve fsync**: `secrets.vtb` geçici dosyaya → `Flush(flushToDisk: true)` → `File.Replace`, **ancak sonra** `settings.json` (`:441`). `vault.json.2` diske indirilir, **sonra** `vault.json` değiştirilir; kilit açma `vault.json`'u okur, MAC hatasında `vault.json.2`'ye düşer.

**4.2** `BackupService.cs:72-100`'e `data/vault.json` ve `data/secrets.vtb`; `vault.json` **arşivde yoksa `BackupAsync` başarısız olur, başarılı olmaz**. `ApplyPendingRestore` (`:263-273`) mevcut `vault.json`'u veritabanıyla **aynı** `onceki-*` klasörüne taşır. `vaultId` uyuşmazlığı **dosya başına** bir olgudur, geri yükleme başına değil — çünkü `:291-303` kayıtları **birleştiriyor**, yer değiştirmiyor, dolayısıyla iki kuşak `.vta`'nın bir arada olması **meşru**. Eşleşmeyen dosyalar Health sayfasında sayılır; geri yükleme reddedilmez (geri yüklemeye uzanan kişi zaten bir şey kaybetmiştir).

**4.3** `UnlockWindow` (küçük, üstte, Enter gönderir, beş hatadan sonra 1/2/4…30 sn gecikme — **asla kilitleme, asla silme**). `EncryptionWizard`: neyi koruduğu ve **neyi korumadığı** (§1.5) → parola **iki kez** → kurtarma sayfası (büyük, tek aralıklı, **Yazdır** + **Dosyaya kaydet**, **pano düğmesi yok** — Windows 11 pano geçmişi `Clipboard.Clear()`'dan sonra da kalır ve buluta gider) → **üç grup geri yazma, atlama yok** → **soğuk doğrulama**: bellekteki `KeyRing` atılır, `vault.json` diskten yeniden okunur, parola yeniden yazdırılır, MK bayt bayt karşılaştırılır. Ancak bundan sonra göç başlar. Ayarlarda kalıcı bir **"Kurtarma anahtarını sına"** eylemi, bir kez başarılı olana kadar hatırlatır. Sihirbaz `onceki-*` klasörü varken şifrelemeyi **reddeder** (o klasör tam bir düz metin veritabanı + her API anahtarı).

**4.4** `CallOrchestrator.cs:482` (Obsidian) **ve `:488` (Notion)** gözetimsiz çalışıyor. Şifreleme açıkken ikisi de kasa yolunu adlandıran açık bir onay ister; Ayarlar'da ve Health'te kalıcı durum satırı; klip dışa aktarma `SaveFileDialog`'unun üstünde uyarı.

**4.5** `Audio/{VtaFormat,VtaPcmSink,VtaStream,AudioStore}.cs`. `Checkpoint()` `IPcmSink`'e taşınır (bugün arayüzde yok, `IPcmSink.cs`), `CallRecorder._micSink/_farSink` (`:76-77`) `IPcmSink?` olur, `PcmReader` `FileStream`→`Stream` genişler. `Checkpoint` **`Flush(flushToDisk: true)`** yapar — bugünkü `Stream.Flush()` (`WavPcmSink.cs:82-96`) yalnız işletim sistemi sayfa önbelleğine ulaşıyor, `CallRecorder.cs`'nin "çökme, zorla kapatma veya kapanan kapak saniyelere mal olur" yorumu elektrik kesintisi için **doğru değil**. **Okuyucu tek başına iner.**

**4.6** `Security/Sealing.cs` + `Services/LockedIntake.cs`. P-256 açık yarı kasanın MAC'li bölgesinde, özel yarı META altında. Kilitliyken FK ECIES ile mühürlenir (`wrapKind 2`), ses `recordings/bekleyen/`'e gider, **`.vtj` yan dosyası sesten ÖNCE yazılır** ve mühürlü anahtarın ikinci kopyasını taşır. `ReconcileAsync`: satırları yaz → `call_key`'i yaz → sesi ay klasörüne **yeniden şifrelemeden** taşı → `Queued` → yan dosyayı en son sil. Yan dosyasız yetim ses **içe aktarılır, asla silinmez**. Tepsiye kilit rozeti + "Kilidi aç…" + bekleyen sayısı (yan dosyaları sayarak, anahtar gerekmez). Otomatik kilit `Recording`/`Processing` sırasında ertelenir; görüşme ortasında kilitlemek kaydı durdurmaz (kaydedici yalnız FK tutuyor).

**4.7** `CallRecorder.cs:127-128` adları `.vta` olur; `CallOrchestrator` `InsertCall`'dan hemen sonra CK üretir, CKW ile sarar ve **tek bir şifreli bayt diske yazılmadan önce** `call_key` satırını işler. Şema →4. **Düz metin yedek lavabosu yok**: görüşme ortasında lavabo değişimi bir konuşmayı hiçbir satırın işaret etmediği iki dosyaya böler.

**4.8** `Worker/AudioPipeServer.cs`; `WorkerProtocol` `mic_handle`/`far_handle`/`sample_rate`/`channels`/`bits` kazanır, `MicPath`/`FarPath` **kalır** (`WorkerProtocolTests` ve göç edilmemiş `.wav` için). `worker/vt_worker/audio_in.py`; motor imzası dört çağıranda değişir (`__main__.py:208`, `models.py:302`, `faster_whisper_engine.py:216`, `whispercpp_engine.py:80`). Süreç başlatma tek kilit altında serileştirilir (§1.4/1).

**4.9** `Security/VaultMigration.cs`. Dosya başına, en yeniden eskiye: `.vta.partial` → `Flush(flushToDisk: true)` → uçtan uca çöz ve kaynak yükle SHA-256 karşılaştır → atomik `File.Move` → satırı ve `call.audio_sha256`'yı tek işlemde güncelle → **ancak sonra** düz metni sil. İlerleme **türetilir** (satırın uzantısı + doğrulama), saklanmaz. **Canlı kayıt koruması** (eleştirmen bulgusu 12): sihirbaz ve göç `Orchestrator.State == Recording` veya `IsManualRecording` iken **reddeder**; `ended_at` NULL olan veya orkestratörün mevcut görüşme kimliğine eşit her görüşme atlanır; her kaynak dosya `FileShare.None` ile açılır — açılamıyorsa atlanır. **"Şifrelemeyi kapat"** aynı döngüyü ters çalıştırır.

**4.10** `Database.cs:17-24`: `Mode` **`SqliteOpenMode.ReadWrite`** olur; tek açık, günlüklenmiş ilk-kurulum yolu bir nöbetçi dosyayla korunur. Bugünkü `ReadWriteCreate` (`:20`), veritabanı dosyası yokken dizini varken **sessizce boş bir arşiv yaratıyor** ve `Migrate()` onu döşüyor — kullanıcı çalışan bir uygulamada sıfır görüşme görüyor, hiçbir yerde hata yok. Göç: `Schema.Statements` ile **taze anahtarlı** veritabanı kur, temel tabloları FK sırasıyla kopyala (`setting` için `INSERT OR REPLACE` — `Migrate` `schema_version`'ı zaten yazdı), tetikleyicilerin FTS'i doldurmasına izin ver, `'rebuild'` çalıştır, **commit'ten önce doğrula** (`PRAGMA integrity_check`, tablo başına `COUNT(*)`, isabet etmesi gereken gerçek bir `Search` MATCH'i), sonra `wal_checkpoint(TRUNCATE)`, `ClearPool`, yeniden adlandır. `MigrationGuard` işaretini **düz metni silmeden önce** `flushToDisk` ile yazar. Yeniden adlandırma penceresinde orkestratör tikleyicisi durdurulur ve `ProcessingQueue.DisposeAsync` beklenir.

**4.11** `cloud_engine`'in disk çalışma alanı `{"type":"chunk"}` protokol olayına döner; şema →6.

---

## 4. Testler

### Faz 0

- `MigrationTests.EveryPlansSchemaChangeAppliesToAPreExistingDatabase` — v2 şemalı dolu bir veritabanı, birleşik `Migrations` uygulanır; `call.attempts`, `call.interrupted`, `call.origin`, `call_key`, `contact_move` hepsi var, her mevcut satır yollarını korumuş ve `BeginRecordingAsync` "no such column" atmak yerine tamamlanıyor (`CallOrchestrator.cs:298-311` bunu "Kayıt başlatılamadı" olarak yutar ve kayıt sonsuza kadar durur).
- `MigrationTests.TwoFeaturesCannotClaimTheSameSchemaVersion` — `Migrations` hedef sürümleri kesin artan, boşluksuz, tekrarsız; `Schema.Version` en yüksek olan. Derleme zamanı koruması.
- `AnalysisPipelineTests.AnalysingTheSameCallTwiceDoesNotDoubleTheLedger` — aynı çıkarımla iki kez; `GetOpenCommitments`, `GetAllClaims`, `GetFlags` ilk turun sayılarında kalır. **K4; kuyruğun `MaxAttempts=3`'ünden önce var olmak zorunda.**
- `AnalysisPipelineTests.ReanalysingKeepsWhatTheUserAlreadyDecided` — kapatılmış bir taahhüt ve reddedilmiş bir işaret yeniden çözümlemeden sağ çıkar.
- `RepositoryTests.AnEmptyTranscriptionResultNeverDestroysAnExistingTranscript` — 400 segment + boş `ReplaceSegments` → reddedilir, 400 segment ve FTS satırları yerinde, MATCH hâlâ isabet ediyor.
- `RepositoryTests.DeletingACallWithNoStoredPathsStillRemovesItsAudio` — `state = Recorded`, `mic_path` NULL, diskte `-mic.wav`/`-far.wav`/`-butun.wav`; üçü de gidiyor, `FilesRemoved == 3`. Bugün `DeletionResult(0, [])` ve `IsComplete == true` dönüyor.
- `RepositoryTests.DeletingACallAlsoRemovesTheCloudWorkspaceBesideIt` — `part0.wav` + transkript JSON içeren `.cloudparts` yapısı silinir ve sayılır.
- `ConversationMixTests.AHalfBuiltMixIsNotLeftPlayableOnDisk` — `-butun.wav.partial` her iki silme yolunda da kalkar.
- `CallPersistenceTests.ARecordingWhoseCaptureDeliveredNoPacketsIsNotDeletedAsTooShort` — duvar saati 20 dakika, yazılan çerçeve sıfır → **silinmiyor**, `CompleteCall` çalıştı, `Notice` yükseldi. **Y7: hiçbir yerde mesaj olmadan gerçek bir konuşmayı silen tek yol.**
- `CallPersistenceTests.AnAutomaticCallDoesNotSilentlyReplaceAHandStartedRecording` — Y1.
- `CallRecorderTests.TheCaptureBackendIsReleasedEvenAfterAFailedStart` — Y13.
- `ShellTests.TheWindowCanBeBuiltWhileTheArchiveIsUnreadable` — her `Open()`'da atan bir `Repository` ile `ShellViewModel` kurulur, her sayfa durumunu bildirir. Bugün `RefreshAll()` (`ShellViewModel` ctor) yüzünden kurulum atar.
- `WindowSmokeTests.EveryScreenBuildsWithoutThrowing` — **`LabelCallWindow` eklenir** (hiçbir test onu kurmamış; ctor'u `FindResource` ve canlı SQLite sorgusu yapıyor ve markup'ı değişiyor).

### §1b Kuyruk

- `ProcessingQueueTests.TwoCallsFinishingWhileOneIsProcessingAreBothKeptAndRunOldestFirst`
- `ProcessingQueueTests.ACallThatFinishesWhileTheQueueIsBusyIsQueuedInTheDatabaseNotInMemory`
- `ProcessingQueueTests.AnInterruptedJobComesBackAsQueuedRatherThanStuckForever`
- `ProcessingQueueTests.AShutdownMidJobLeavesTheCallQueuedRatherThanFailed` — sahte işlemci `WorkerException("cancelled", …)` atar; `PythonWorkerHost.cs:296-297`'nin ürettiği tam şekil. Bugün kapatmak kaydı **Başarısız** işaretliyor.
- `ProcessingQueueTests.ThreePowerCutsDoNotMarkARecordingPermanentlyFailed` — üç sert öldürmeden sonra `attempts` hâlâ 0, dördüncü tur normal işliyor. **`interrupted` sütununun varlık sebebi.**
- `RepositoryTests.TwoThreadsClaimingConcurrentlyNeitherThrowsNorDoubleClaims` — birleşim tam üç kimlik **ve hiçbir iş parçacığı `SqliteException` görmedi**. İkinci iddia WAL altındaki ertelenmiş işlemi yakalar.
- `ProcessingQueueTests.NeverTriedCallsGoBeforeRetriedOnes` — `ORDER BY attempts ASC, started_at ASC`.
- `ProcessingQueueTests.RequeueingAfterLabellingDoesNotReTranscribe` — transkripsiyon aşamasına hiç girilmedi, çözümleme girildi.
- `ProcessingQueueTests.AnUnnamedCallStopsAfterTranscriptionInsteadOfWritingAnInvisibleLedger`
- `ProcessingQueueTests.ATranscriptWithAnalysisTurnedOffStillReachesADoneState` — `PendingWorkCount()` (`Repository.cs:571-575`, `state IN (0,1,2,3,4)`) sıfır.
- `ProcessingQueueTests.NoNewJobStartsWhileARecordingIsRunning` / `AJobAlreadyRunningIsNotCancelledWhenANewCallStarts`
- `ProcessingQueueTests.EveryFinishedJobReportsWhoItWasWithAndWhatWasDiscussed` / `AFailedJobStillReportsAnOutcomeInsteadOfSayingNothing` (`FailureText.Summarise` cümlesi, "Traceback" yok)
- `ProcessingQueueTests.ARecordingLeftBehindByAHardKillIsReattachedRatherThanLost`
- `ProcessingQueueTests.AnUnreachableRecordingIsNotMarkedSkippedOnTheFirstMiss` — ay klasörü geçici olarak yeniden adlandırılmış; durum yeniden kontrol edilebilir kalır, sorgulanan yol günlüğe yazılır, klasör geri gelince ses yeniden bağlanır. Uzantıdan bağımsız arama (`call-{id}-mic.*`) böylece Faz 4 bunu sessizce kırmaz.
- `CallPersistenceTests.StoppingARecordingReturnsBeforeAnyProcessingHasFinished` / `ASlowCallFinishedHandlerDelaysNeitherTheStopNorTheQueue` / `ProcessingACallNeverTouchesTheRecordingIndicator`

### §7 Kişi

- `RepositoryTests.MovingACallTakesItsCommitmentsClaimsAndFlagsWithIt`
- `RepositoryTests.MovingACallLeavesTheOldContactsCountAndLastCallTellingTheTruth` — `AssignContact` (`:250-272`) bugün yalnız yeni kişiyi hesaplıyor.
- `RepositoryTests.LabellingACallThatWasAlreadyAnalysedWhileUnnamedAdoptsItsLedger`
- `RepositoryTests.AnAutomaticallyAttributedCallCountsTowardsItsContact` / `DeletingOneCallLeavesTheContactsCountTellingTheTruth`
- `ContactRepairTests.NamingACallThatIsWaitingToBeNamedNeverThrows` — durum `Transcribed` + `contact_id` null; `AssignContact` başarılı, NULL defter satırları benimseniyor, kuyruk sinyallendi. **Plan 2/plan 3 çarpışması, plan 2'nin yarattığı tam durumda.**
- `ContactRepairTests.AFlagWhoseTwoQuotesEndUpOnDifferentPeopleIsRemovedRatherThanLeftLying`
- `ContactRepairTests.UndoingAMovePutsTheCallItsLedgerAndTheDeletedFlagBackExactly` (aynı `Id`, aynı `CounterCallId`)
- `ContactRepairTests.MergingTwoContactsMovesEverythingAndLeavesOneContact` / `UndoingAMergeBringsTheContactBackWithItsOriginalIdentity`
- `ContactRepairTests.RenamingOntoAnExistingNameIsRefusedAndNamesTheContactToMergeWith`
- `ContactRepairTests.AWrongTitleBindingCanBeUnboundSoTheNextCallAsksAgain` / `RepointingABindingRedirectsTheNextCallWithoutMovingThePastOnes`
- `ContactRepairTests.TheRepairScreenCountsHowManyCallsAWrongTitleHasAlreadyCaught` — `title_binding.title_pattern` `StripFormatting`'li, `call.observed_title` ham; SQL eşitliği sıfır döner.
- `ContactRepairTests.CallsFiledUnderOneTitleThatEndedUpOnTwoPeopleAreReportedFirst`
- `ContactRepairTests.ContactsWhoseCountersDriftedAreRepairedInOnePass` (ikinci çağrı 0 döner)
- `LabelDefaultsTests.ConfidenceDecidesWhetherATitleIsRemembered` — `Likely`→true, `Possible`→false, `None`→kutu yok, mevcut bağlama başkasına işaret ediyorsa→false.
- `ObsidianExporterTests.AFailedReExportNeverLeavesTheVaultWithoutTheNote` — kasa yazılamaz hale getirilir; **orijinal not yerinde**, hata yolla birlikte bildirildi.

### §5+§4 Yayın

- `VersioningTests.TheBuiltAssemblyCarriesTheVersionTheBuildWasGiven` (`VT_EXPECTED_VERSION`) — `-p:Version=1.2.0-rc.1` tek başına `VersionPrefix`'i varsayılanda bırakır ve ikili 1.0.0 der.
- `VersioningTests.APrereleaseIsOlderThanItsOwnFinalRelease` / `NumericPrereleaseIdentifiersCompareNumericallyNotAlphabetically` (`rc.9 < rc.10`) / `BuildMetadataIsDroppedOnParse` / `ADevelopmentBuildIsOlderThanEveryRelease` / `AnUnparseableInformationalVersionYieldsAVersionRatherThanThrowing`
- `ReleaseAssetTests.TheInstallerScriptProducesTheNameTheClientLooksFor` — gerçek `installer/VoiceTranscript.iss` okunur.
- `ReleaseAssetTests.AReleaseCarryingTwoInstallersIsRefused` / `AReleaseWithNoInstallerIsRefused` / `AnInstallerBuiltFromADifferentTagIsRefused` / `TheChecksumIsFoundByWholeFileNameNotByPrefix` / `AChecksumMismatchRefusesTheInstall` / `ADraftOrPrereleaseIsRefusedEvenWhenHandedDirectly`
- `PackagingTests.TheInstallerAndTheApplicationNameTheSameMutex` — `installer/VoiceTranscript.iss` `AppMutex=` değeri `App.xaml.cs:81`'deki dizeye eşit.
- `PackagingTests.TheInstallerWaitsForTheRunningApplicationAndNeverClosesIt` — `CheckForMutexes` var, `CloseApplications=no` var, `skipifsilent` taşımayan bir `[Run]` girdisi var.
- `UpdateTests.AnUpdateIsRefusedWhileACallIsBeingRecorded` / `…WhileAVaultMigrationIsRunning` / `…WhileLockedRecordingsAreWaiting` / `…WhenTheDataDirectoryWasOverridden` / `…WithoutRoomForTheInstallerTwiceOver`
- `CallPersistenceTests.AnUpdateStartedOneTickBeforeACallDoesNotOrphanTheRecording` — kapı geçilir, bir tik sonra görüşme başlar, kapatma yolu çalışır; `MicPath` dolu, dosyalar çalıyor, durum `Queued`.
- `UpdateTests.AFailedCheckIsSwallowedRatherThanThrown` / `TheReleaseRequestCarriesAUserAgent…` / `AnInterruptedDownloadLeavesNoHalfFileBehind`
- `UpdateTests.TheAttemptMarkerReportsFailureWhenTheVersionDidNotMove`

### §3 Şifreleme

- `BackupServiceTests.ABackupTakenAtEveryStageOfEncryptionCanActuallyBeRestored` — **parametrik**: yalnız kasa / kasa+şifreli ses / kasa+ses+DB. `includeAudio` ile yedekle, veri kökünü **tamamen sil**, geri yükle, parolayla aç, bir kayıt çözülüyor ve `Search` satır döndürüyor. Ayrıca: `vault.json` diskte varken arşivde yoksa `BackupAsync` **atıyor**. (Mevcut `BackupServiceTests`'in zip girdi listesi iddiası, özellik bozukken de yeşil kalacak türden bir testtir.)
- `RecoveryKeyTests.ThePrintedSheetOpensTheArchiveAfterVaultJsonIsDestroyed` — `vault.json` **ve** `vault.json.2` silinir; 40 karakterlik anahtar + basılı kurtarma zarfı arşivi açar.
- `RecoveryKeyTests.TheRecoveryKeyStillWorksAfterAPasswordChange`
- `RecoveryKeyTests.AMistypedKeyAndAKeyFromAnotherArchiveGiveDifferentMessages` — sağlama vs parmak izi; normalizasyon I/L→1, O→0.
- `VaultTests.ArgonRoundTripsTheMasterKeyThroughAPassword` (kalıcı üçlü hızlı makinede yazılıp yavaş makinede açılıyor)
- `VaultTests.AWrongPasswordIsRejectedAsATagMismatchNotAsGarbage` — beşinci hata gecikme üretir, **asla kilitleme, asla silme, diske hiçbir yazma yok**.
- `VaultTests.AMistypedNewPasswordCannotRevokeTheOldOne` — onay alanı uyuşmazsa hiçbir şey yazılmaz; `vault.json.2` ile `vault.json` arasında öldürüldüğünde **tam olarak biri** belirlenimci şekilde açar ve uygulama hangisi olduğunu söyler.
- `VaultTests.EnablingEncryptionVerifiesTheTypedPasswordFromDiskBeforeMigratingAnything`
- `VaultTests.ChangingThePasswordDoesNotRewriteTheArchive` — on `.vta` ve DB'nin SHA-256 + `LastWriteTimeUtc` değişmiyor; yalnız `vault.json`/`.2` yazıldı; **hiçbir `*.tmp`/`*.yedek` hayatta kalmadı**.
- `VaultTests.TheVaultFileIsAuthenticated` — kayıt açık anahtarı / `vaultId` / KDF parametrelerinden birer bayt çevrilir, üçünde de kilit açma **gürültülü** başarısız olur.
- `SecretStoreTests.SecretsRoundTripAndEveryWriteUsesAFreshNonce` (100 kayıt → 100 farklı nonce) **+ bozulmuş `secrets.vtb` sonrası `Load`+`Save` orijinal zarf baytlarını birebir korur** (`AppSettings.cs:417-427`).
- `SecretStoreTests.SecretsAreDurableBeforeSettingsIsRewritten` — `secrets.vtb` `flushToDisk` ile yazılıp değiştirilmeden `settings.json` yazılmıyor.
- `VtaPcmSinkTests.AKilledRecorderLeavesAReadableFile` — `Dispose` ve son `Checkpoint` olmadan 90 sn; süre ±1,1 sn, her örnek kaynakla eşleşiyor; son 1..40 bayt kesildiğinde kuyruk çerçevesi EOF sayılıyor.
- `VtaPcmSinkTests.EveryNonceIsUniqueAcrossAFullCall` — 10 dakika, `Checkpoint` hiç `Seek` yapmıyor ve mühürlenmiş tek bayt yeniden yazılmıyor.
- `VtaPcmSinkTests.SealingNeverExceedsThePacketDeadline` — p99.9 < 2 ms, max < 5 ms, kurulumdan sonra sıfır ayırma; **`WriteSilence` için ayrı zamanlama iddiası** (çok dakikalı bir boşluk `lock(_gate)` içinde yüzlerce eşzamanlı yazma demek); 30 dakikalık soakta `TimelineStats.Discontinuities == 0`.
- `VtaStreamTests.ACorruptFrameDegradesGracefullyEverywhere` + **başlık yırtıkken dosya `call_key` aynasından çözülüyor**.
- `LockedIntakeTests.ALockedRecordingSurvivesADestroyedHeader` — ilk 128 bayt sıfırlanır, `.vtj` kopyasından çözülür; sonra tersi.
- `LockedIntakeTests.ACallThatArrivesWhileLockedIsRecordedInFullAndImportedOnUnlock` — `bekleyen/`'de iki mühürlü `.vta` + yan dosya, okunabilir hiçbir şey yok; kilit açılınca `origin='locked'`, ses **yeniden şifrelenmeden** taşındı (şifreli metnin SHA-256'sı ve mtime'ı aynı).
- `LockedIntakeTests.ACallArrivingWhileLockedIsRecordedAtEveryStepOfTheRollout` — **4.7 derlemesi kilitliyken çalıştırılır ve görüşme tam kaydedilir**; "Kayıt başlatılamadı" değil. 4.6 ile 4.7'nin birlikte inmesini zorlayan test.
- `VaultMigrationTests.CrashAndResumeLosesNothing` (beş nokta) + `ReverseMigrationRestoresThePlaintextArchiveExactly` + **`MigrationRefusesToTouchTheFilesOfALiveRecording`**.
- `AudioPipeTests.TheWorkerReceivesReadablePcmAndNoPlaintextTouchesDisk` (veri kökü + `%TEMP%` anlık görüntüleri özdeş) + **`AConcurrentProbeDoesNotStrandTheTranscriptionPipe`**.
- `EncryptedDatabaseTests.Fts5SearchReturnsTheExpectedRowsUnderSqlCipher` — `GetNativeLibraryName() == "e_sqlcipher"`, "kitap"→"kitabı"/"kitaptan", `ORDER BY rank` sırası düz metin derlemesiyle satır satır aynı, yanlış anahtar `Open()`'da atıyor, 1.000 havuzlu açılışın medyanı < 2 ms.
- `DatabaseTests.AMissingDatabaseFileIsRefusedRatherThanRecreated` — dosya silinip dizin ve `-wal` bırakılır; `Open()` yolu adlandırarak atar, boş bir arşiv **yaratmaz**.
- `DatabaseMigrationTests.MigratingTheDatabaseKeepsEveryRowAndEveryIndexEntry` + `APowerCutBetweenDeletingThePlaintextAndTheRenameIsRecoverable` + `NoConnectionCanBeOpenedWhileTheDatabaseIsBeingRenamed`.
- `EndToEndArchiveTests.OneConversationSurvivesEveryFeatureAtOnce` — şifreli kasa + kuyruk çalışıyor + uygulama kilitliyken görüşme başlıyor → kaydet, aç, çöz, çözümle, başka kişiye taşı, klip çıkar, yedekle, sil, geri yükle, aç → ses çözülüyor, transkript aranıyor, **defterde her kaydın tam bir kopyası var**, klip çalıyor. **Beş özelliğin birbirine ne yaptığını yakalayacak tek test.**
- `ConfigurationTests`/`DataDirectoryTests:227-249` — `EverySettingTheWindowDoesNotEditComesBackUnchanged` yansımayla **her** `AppSettings` özelliğini geziyor. API anahtarları zarfın arkasına geçince bu test yapı gereği kırılır: **zayıflatılmaz**, zarf yansımanın gezdiği bir alan olarak kalır ve birebir yuvarlanma iddiası eklenir.

---

## 5. Riskler ve kabul edilen ödünler

**Bilerek kabul edilenler**

1. **Kilitliyken arama yok.** Kilitli bir kasada `Repository`'nin ~1200 satırındaki her `using var connection = Open()` atar. Karşılığı: kilit açıkken FTS5 hiç değişmiyor ve Türkçe katlama korunuyor.
2. **Kilitli kaynaklı kayıtlar kanıt olarak daha zayıf.** Kimliklendirilemiyorlar (§1.6/1). Ürünün tezi "birinin ne dediğinin savunulabilir kaydı" olduğu için bu gerçek bir boşluk — kapatılmadı, **söylendi**.
3. **RAM'deki anahtarlar temizlenemiyor.** Garanti "diskte okunabilir değil".
4. **Silinen düz metin silinmiş değil.** Bakım ekranı "üzerine yazıldı" der ve API anahtarlarını döndürmeyi ister.
5. **Klipler, Obsidian notları ve yedekler kasıtlı olarak açık.** Her çıkışta uyarı var; otomatik Obsidian **ve Notion** dışa aktarımı şifreleme açıkken açık onay ister.
6. **SQLite dört yıl geriye gidiyor** ve `bundle_e_sqlcipher` ölü bir paket hattı.
7. **Argon2 muhtemelen 256 MiB'nin altına ayarlanacak.** Konscious saf yönetilen (bilinçli seçim, `cublas64_12.dll`'in bu projeye maliyeti düşünülünce) ve 2022'den beri bakımsız. Zayıf bir kullanıcı parolası gerçek tavan olmaya devam ediyor.
8. **Kurucu imzasız.** SHA-256 aynı yayında yaşıyor: bozuk indirmeyi yakalar, ele geçirilmiş depoyu yakalamaz. TLS gerçek bütünlük sınırıdır.
9. **Kişi sınırını aşan kanıt siliniyor.** "Serdal on sekiz bin dedi, oysa daha önce on iki demişti" satırının önceki alıntısı Uliana'ya aitse, o satır **yalan söylüyor**. Silmek + günlüklemek + geri alınabilir yapmak seçildi.
10. **`onceki-*` klasörü şifrelemeyi engelliyor.** `BackupService.cs:245-247` "hiçbir şey silinmez, yanlış geri yükleme olursa eldeki hâlâ diskte" diye söz veriyor; bu söz ile şifreleme çelişiyor. Karar: sihirbaz reddeder ve açıkça silmeyi önerir.

**Hâlâ ters gidebilecekler**

- **Yakalama iş parçacığındaki bloklayan yazma hızı** (§1.6/6). Kabul testi bunu yakalamak için var; kaçış tasarlandı ama yapılmadı.
- **`WriteSilence`** hattaki tek analiz edilmemiş yol.
- **`ArchiveState` dönüşümü** en büyük entegrasyon maliyeti ve dikkatsiz yapılırsa çökme raporlarının en olası kaynağı.
- **Üç plan aynı 40 satırı düzenliyor**: `PythonWorkerHost.cs`/`WorkerProtocol.cs` (0.5c, 1.3, 4.8). Sıra bozulursa üçü de yeniden yazılır.
- **`WavPcmSinkTests`, `WaveformPeaksTests`, `ConversationMixTests`, `AudioClipTests`** hepsi elle RIFF baytı yazıyor. `.wav` yolunu doğrulamaya devam edecekler ve `.vta` yolu hakkında **hiçbir şey kanıtlamayacaklar** — her birinin şifreli ikizi toplam işin ciddi bir payı.
- **`PythonWorkerHostTests` Python PATH'te yoksa atlanıyor**, ASR testleri `VT_RUN_ASR_TESTS` arkasında. Motor imza değişikliği CI'nin varsayılan olarak çalıştırmadığı bileşene iniyor. Boru alanları isteğe bağlı inmeli ki `.wav` yolu uçtan uca çalışsın.
- **Signal hiçbir planda yok.** Kişi anahtarı `(name_normalised, app)` olduğu için aynı kişi Signal'de **dördüncü** bir satır; şifreleme sihirbazı, onay ekranları ve dışa aktarma metinleri **üç** uygulamayı adlandırmalı.

---

## 6. Açık sorular

Karar verebildiklerimi verdim (kurtarma zarfı, `e_sqlcipher` varsayılanı, DPAPI **yok**, kliplerin açık kalması, `MoveCall` kapısının daraltılması, kuyruk sırası, plan 4/5 birleşimi, `onceki-*` reddi). Geriye gerçekten sizin kararınız olan dört şey kalıyor:

1. **Çalışan ağaçtaki 16+3 dosya bitmiş iş mi?** §2 ve Signal tam ve testli görünüyor ama commit edilmemiş. Cevap, plan 3'ün yeniden mi yazılacağını yoksa geri mi alınacağını belirliyor — ve bu belgedeki en yüksek kaldıraçlı soru.
2. **Signal kaydı gerçekten isteniyor mu?** `AppSettings.cs:73`'te varsayılan **açık**, commit edilmemiş bir işle geldi ve beş planın her onay/dışa aktarma ekranı iki uygulama adlandırıyor.
3. **Kurtarma sayfası "arşivin anahtarı" olarak basılacak — kabul mü?** §1.1'deki karar kâğıdı arşiv kadar hassas yapıyor. Alternatifi, kâğıdın yalnızca parola kaybına karşı işe yaraması ve `vault.json` kaybının arşivi bitirmesi. Bunu sizin adınıza karar veremem çünkü kâğıdı saklayacak olan sizsiniz.
4. **`logs/vt-*.log`** görüşme tamamlanmalarını düz metin kaydediyor ve kilitliyken okunabilir kalmalı (tanılamanın çalışması için). Önerim: saklamayı 7 güne indir ve kişi adlarını günlükten çıkar (görüşme kimliği kalsın). Kabul mü, yoksa günlük olduğu gibi mi kalsın?