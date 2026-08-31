# Yapılacaklar — canlı liste

Bu dosya **şu an elde kalan işin** listesi. `PLAN.md` projenin tasarım planıdır ve büyük ölçüde
tamamlanmıştır; burası ondan farklı olarak **kullandıkça çıkan hataların** ve devam eden işin
takip edildiği yerdir. `docs/ISLEM-GUNLUGU.md` ise biten işlerin neden öyle yapıldığını anlatır.

Bir madde bitince buradan silinmez — `[x]` işaretlenir ve gerekçesiyle birlikte
`docs/ISLEM-GUNLUGU.md` dosyasına taşınır. Silinen bir madde, altı ay sonra "bu neden böyleydi"
sorusuna cevap veremez.

**Durum işaretleri**

| İşaret | Anlamı |
|---|---|
| `[ ]` | Yapılacak |
| `[~]` | Üzerinde çalışılıyor |
| `[x]` | Bitti ve doğrulandı |
| `[?]` | Sebebi henüz belli değil, araştırılıyor |
| `[!]` | Engellendi — dışarıdan bir şey bekliyor |

---

## Nasıl kullanılır

Uygulamayı kullanırken bir aksaklık görürsen **§9 Kullanırken bulunanlar** başlığının altına
tek satır yaz. Şu üç şey yeterli, gerisini kod tarafında ben bulurum:

1. Ne yapıyordun (hangi uygulama, arayan sen misin karşı taraf mı, kaç dakika)
2. Ne olmasını bekliyordun
3. Bunun yerine ne oldu

Ekran görüntüsü veya `%LOCALAPPDATA%\VoiceTranscript.Data\logs` içindeki günlük dosyası varsa
çok daha hızlı çözülür — o günlük tam da bu iş için yazılıyor.

---

## Sıra ve öncelik

**Karar (2026-08-31):** öncelik sistemin çalışır hale gelmesi. Şifreleme son faza bırakıldı.

Gerekçe: bugün ürün temel işini yapamıyor — görüşme bitince sonuç çıkmıyor, isimler yanlış
yakalanıyor, yanlış kişiye düşen kayıt düzeltilemiyor. Çalışmayan bir arşivi şifrelemek, kimsenin
okumadığı bir şeyi korumak olur. Ayrıca şifreleme `Repository`, ses yazma/okuma yolları ve Python
worker protokolünü birden değiştiriyor; önce bu yolların doğru şeklini bulmak, sonra şifrelemek
**bir kez** yazmak demek. Ters sırası iki kez yazmaktır.

| Faz | İçerik | Neden bu sırada |
|---|---|---|
| **1** | §1 — Görüşme sonrası akış: özet üretilsin, işleme engellemesin, kayıt ekranı çıksın | Ürünün temel vaadi bu. Çalışmıyor, **ve ses kaybediyor** (§1.4 zincir A). |
| **1b** | §1b — İş kuyruğu, parça parça gönderme, ilerleme ekranı | §1 ile aynı kodu değiştiriyor; ayrı turlarda yapmak `CallOrchestrator`'ı iki kez yazmak olur. K3 ve K4 burada kapanıyor. |
| **2** | §2 — WhatsApp/Telegram isim yakalama | Yanlış isim, yanlış kişiye kayıt demek; §7'nin sebebini kaynağında kurutur. |
| **3** | §7 — Kayıt taşıma, kişi birleştirme, başlık bağı düzeltme | §2 düzelse bile geçmiş kendiliğinden düzelmez; ayrıca tespit hiçbir zaman %100 olmayacak. |
| **4** | §5 — Sürümleme + CI | Oto-update'in üzerine kurulacağı zemin. Önce yayın hattı, sonra güncelleyici. |
| **5** | §4 — Oto-update | "Tekrar indir-kur-derle istemiyorum" isteğini karşılar. |
| **6** | §3 — Şifreleme | **Son faz.** En büyük ve en riskli iş; üstüne inşa edeceği yollar 1–3'te değişiyor. |

§6 (geliştirme ortamı) sıraya girmez — 1. fazın önkoşulu, .NET SDK gelir gelmez doğrulanır.

---

## 1. Kritik — görüşme bitince sonuç çıkmıyor

Bildirilen belirti: *"görüşme yaptım, arkada sesleri işlemeye çalışıyordu, kaydediliyor ve çıktı
kaydedildi sanırım ama görüşme bitince kim aradı ne yaptı bu çıkmadı."*
Ve: *"işleme kısmında bir hata oluşursa ve o sırada bir görüşme yaptıysam, o görüşme bitince
kayıt ekranı çıkmadı."*

Kod incelemesinde üç ayrı kusurun üst üste bindiği görüldü. Üçü de tek başına bu belirtiyi
üretebiliyor, dolayısıyla üçü de düzeltilecek.

- [x] **1.1 — Özet yalnızca "taahhüt/iddia/bayrak" bulunursa yazılıyor.**
  `src/VoiceTranscript.Core/Analysis/AnalysisPipeline.cs:340`
  `if (commitments.Count == 0 && claims.Count == 0 && flags.Count == 0) return null;`
  Söz verilmemiş, rakam veya tarih geçmemiş sıradan bir sohbette **hiç özet üretilmiyor**.
  *Düzeltme:* özet her zaman yazılsın; taahhüt/iddia yoksa metnin kendisinden "ne konuşuldu"
  özeti çıkarılsın. Ayrıca çözümleme hiç çalışmasa bile en azından konuşulanların ilk
  satırlarından oluşan bir yedek özet kaydedilsin — özet yokluğu sessiz kalmasın.

- [x] **1.2 — İşleme bitince kullanıcıya hiçbir şey söylenmiyor.**
  `src/VoiceTranscript.App/Services/CallOrchestrator.cs:498` (`ProcessAsync` sonu)
  İşleme biter, `State = Idle` olur, o kadar. Bildirim yok, pencere yok. Özet üretilse bile
  kullanıcının gidip araması gerekiyor. **"Görüşme bitti → şu kişiyle konuştun → şunlar
  konuşuldu" ekranı projede hiç yok.**
  *Düzeltme:* işleme bitince görüşme özeti kartı gösterilsin (kişi, süre, uygulama, özet,
  taahhütler, bayraklar; "metni aç" ve "sesi dinle" düğmeleriyle). İşleme başarısız olduysa da
  bir şey gösterilsin — sessizce kaybolmasın.

- [x] **1.3 — İsimlendirme penceresi işlemeyi kilitliyor.**
  `src/VoiceTranscript.App/MainWindow.xaml.cs:56`
  `Dispatcher.Invoke` (Async değil) çağıran arka plan iş parçacığını bloke eder; içindeki
  `ShowDialog()` de pencere kapanana kadar dönmez. Sonuç: `CallOrchestrator.cs:404`
  `await ProcessAsync(...)` **pencere kapatılmadan hiç başlamıyor.** Pencere arkada kalırsa
  (tepsi uygulaması, ana pencere gizli, `Owner = null`) yazıya dökme de çözümleme de hiç çalışmaz.
  *Düzeltme:* `InvokeAsync` kullanılsın ve isimlendirme işlemenin **önüne** değil **yanına**
  alınsın — kayıt işlenirken kullanıcı ismi girebilmeli.

- [x] **1.4 — İşleme hatalıyken yapılan görüşmede kayıt ekranı hiç çıkmıyor.** ✅ **ÇÖZÜLDÜ — sebep bulundu**

  82 ajanlı denetim tamamlandı (2026-08-31). Tam rapor: [`DENETIM-2026-08-31.md`](DENETIM-2026-08-31.md).
  74 bulgu üretildi, her biri çürütülmeye gönderildi, **59'u ayakta kaldı.**

  **Baş sebep — ve düşündüğümden çok daha kötü.** §1.3'te "isimlendirme penceresi *işlemeyi*
  kilitliyor" demiştim. Gerçek şu: **tespit döngüsünün tamamını donduruyor.** Zincir, kanıtlanmış:

  1. Tespit tek bir arka plan döngüsü: `while (await ticker.WaitForNextTickAsync(...)) Tick();`
     (`CallOrchestrator.cs:133`). `Tick()` **senkron** (`:148`).
  2. `Tick` bitirme işini "ateşle ve unut" başlatıyor (`:177`) — ama `FinishRecordingAsync`
     içindeki **tek `await`, `CallFinished?.Invoke`'tan sonra** (`:404` vs `:395`). Yani olay
     ticker iş parçacığında **senkron** tetikleniyor.
  3. Abone bloklayan çağrı yapıyor: `Dispatcher.Invoke(...)` (`MainWindow.xaml.cs:56`) →
     `ShowDialog()` (`:151`). İkisi de pencere kapanana kadar dönmüyor.
  4. **Sonuç: pencere açık kaldığı sürece ticker iş parçacığı `Tick()` içinde park hâlinde.**
     `Sample()` çağrılmıyor, `Observe()` çalışmıyor. `PeriodicTimer` kaçırılan tikleri
     kuyruklamıyor, düşürüyor.
  5. O sırada yapılan görüşme **hiç görülmüyor**: `Started` yok → veritabanına satır yok →
     WAV yok → `Ended` yok → kayıt ekranı yok. **Ses kalıcı olarak kayboluyor.**

  Pencere kaçırılması da tasarımdan: `LabelCallWindow.xaml:9-10` `Topmost="True"` **ve**
  `ShowInTaskbar="False"` — görev çubuğunda düğmesi yok, öne çıkmayı bıraktığında geri dönüş
  yolu bırakmıyor.

  Diyalog yalnızca yeni kişide açılıyor (`NeedsLabel`, `CallOrchestrator.cs:400`) — yani bu
  istisna değil, **normal yol**.

  **Ayrıca üç bağımsız zincir daha var, dördü de gerçek ve ayrı ayrı düzeltilmeli:**
  - **B** — `FinishRecordingAsync`'in `:362-404` arası tamamen korumasız; `async Task` senkron
    fırlatmadığı için `Tick`'in `catch`'i (`:135`) göremiyor. Sessizce her şey durur.
  - **C** — Detektör `InCall`'da takılabiliyor; `Ended` hiç üretilmiyor. `Ringing` için maksimum
    süre koruması var (`CallDetector.cs:234`), `InCall` için **yok**.
  - **D** — 5 saniyeden kısa sayılan kayıt sessizce siliniyor (`:364`), `CompleteCall` ve
    `CallFinished` **öncesinde** dönülüyor, hiç `Notice` çıkmıyor.

  **Hangisinin yaşandığını ayırt etmek (5 dakika).** Hedef makinede:

  ```sql
  SELECT id, started_at, state, duration_ms, mic_path FROM call ORDER BY started_at DESC LIMIT 10;
  ```

  | İz | A (donmuş döngü) | B (sessiz istisna) | C (takılı InCall) | D (sessiz silme) |
  |---|---|---|---|---|
  | `call` satırı | **yok** | var | var | var |
  | Durum | — | `Recorded(0)`/`Queued(1)` | `Recorded(0)` | `Skipped(7)` |
  | Diskteki WAV | **yok** | var | **anormal büyük** | **silinmiş** |
  | Günlük | o aralıkta "kayıt" satırı yok | "Beklenmeyen görev hatası" | "→ Recording" var, "→ Idle" yok | sessiz |

  <details><summary>İlk teşhis (denetimden önce)</summary>
  Kullanıcının doğrudan bildirdiği durum. Çok ajanlı bir kod denetimi ile eşzamanlılık ve hata
  yolları taranıyor. Şüpheliler: `_recorder`/`_currentCallId` alanlarının kilitsiz paylaşılması,
  `_gpu` semaforu arkasında sıraya girme, `CallFinished` olayının çok aboneli olması (bir abone
  hata fırlatırsa sonrakiler hiç çalışmaz), `Tick()` içindeki erken `return`'ün durum makinesini
  askıda bırakması. **Denetim sonucu gelince bu madde kesinleşecek.**
  </details>

  **Denetimden çıkan diğer kritik bulgular** (tamamı raporda, buradakiler §1'e girenler):

  - **K2 — `CallFinished` çok aboneli ve izolasyonsuz.** Abone sırası: günlük
    (`App.xaml.cs`) → etiket penceresi (`MainWindow.xaml.cs:56`) → liste yenileme
    (`ShellViewModel.cs:87`). Çok abonelikli delege **ilk fırlatanda durur**, yani tek bir
    abonedeki hata hem kayıt ekranını hem yenilemeyi hem de `:404`'teki işlemeyi birden götürür.
  - **K3 — `catch (OperationCanceledException)` token filtresiz** (`CallOrchestrator.cs:490`).
    `HttpClient.Timeout` (10 dk) `TaskCanceledException` fırlatıyor, buraya düşüyor ve görüşme
    sessizce `Queued`'a geri alınıyor — `Failed` değil, uyarı yok. Görüşme sonsuza kadar
    "Sırada" görünüyor, "Tekrar dene" kartında hiç çıkmıyor, **her açılışta yeniden denenip
    analiz kayıtlarını çoğaltıyor.** Üstelik `ProcessAsync`'e hiçbir çağrı token geçirmiyor,
    yani bu `catch` gerçek iptalle asla tetiklenemiyor.
  - **K4 — Analiz sonuçları eklemeli yazılıyor** (`AnalysisPipeline.cs:123,148`).
    `InsertCommitment`/`InsertClaim`/`InsertFlag` düz `INSERT`; `ReplaceSegments`'in aksine önce
    silme yok, `SaveSummary`'nin aksine `ON CONFLICT` yok, şemada tekillik kısıtı yok. Her
    yeniden işleme kişinin defterine **sözlerin ve fiyatların ikinci bir tam kopyasını** ekliyor.
    K3 ile birleşince kendi kendini besliyor.
  - **K5 — "Uygulama bazlı yakalama" açıkken hiçbir görüşme kaydedilemiyor.**
    `_recorder.StartAsync(directory, name)` üç argümanlı aşırı yüklemeyi çağırıyor,
    `targetProcessId` `null` kalıyor, `ProcessLoopbackCaptureBackend` ilk iş olarak
    `ArgumentNullException` fırlatıyor. `CreateBackend`'in cihaz yakalamaya düşme yolu da **ölü
    kod**: `try` yalnızca kurucuyu sarıyor, gerçek hata `StartAsync`'te yani `try` dışında
    oluşuyor. *Hafifletici:* varsayılan kapalı (`AppSettings.cs:131`), yani bugün tetiklenmiyor.
  - **Y1 — "Zaten kayıt var" koruması yok** (`CallOrchestrator.cs:270-296`). `_currentCallId` ve
    `_recorder` koşulsuz üzerine yazılıyor. Elle başlatılan kayıt, algılanan bir görüşme
    başlayınca **sessizce terk ediliyor** — `Stop`/`Dispose` edilmiyor, satırı tamamlanmıyor,
    mikrofon açık kalıyor.
  - **Y2 — `BeginRecordingAsync`'in `catch`'i alan üzerinden çalışıyor**, yerel değil. `await`
    askıdayken alanlar başka bir çağrıya ait olabiliyor; `catch` **canlı** kaydediciyi
    `Dispose` ediyor. Kaydedilmekte olan görüşme, başka bir çağrının hatası yüzünden ortasından
    kesiliyor.

- [x] **1.5 — İşleme, uygulamanın hiçbir işini engellememeli.** *(mimari şart)*
  Ses işleme ve çözümleme dakikalarca sürebiliyor. Bu süre boyunca:
  - **Arayüz donmamalı.** Kullanıcı arşivde gezinebilmeli, arama yapabilmeli, ses dinleyebilmeli.
  - **Yeni arama yakalanabilmeli.** Bir görüşme işlenirken gelen yeni arama tespit edilip
    kaydedilmeli — sıraya girip kaçırılmamalı.
  - **Yeni görüşme bitince "kim aradı" ekranı çıkmalı.** Önceki görüşmenin işlenmesi bunu
    bekletmemeli.

  Şu anki tasarım bunu karşılamıyor; üç ayrı yerden tıkanıyor:
  1. `MainWindow.xaml.cs:56` bloke eden `Dispatcher.Invoke` + `ShowDialog()` → §1.3.
  2. `CallOrchestrator.cs:404` `await ProcessAsync(...)` doğrudan `FinishRecordingAsync` içinde
     çağrılıyor; yani "kaydı bitir" ile "kaydı işle" tek bir zincire bağlanmış durumda.
  3. `CallOrchestrator.cs:56` `_gpu` semaforu (1,1) — ikinci görüşmenin işlenmesi birincinin
     bitmesini bekliyor. Bu **doğru ve kalmalı** (Whisper ile çözümleme modeli aynı anda 6 GB'a
     sığmıyor), ama o kuyruğa girmesi gereken yalnızca *işleme* olmalı; *kayıt* ve *isimlendirme*
     asla o kuyruğa takılmamalı.

  *Düzeltme:* kayıt/tespit yolu ile işleme yolu birbirinden ayrılsın. `FinishRecordingAsync`
  kaydı kapatıp kuyruğa atsın ve **hemen dönsün**; kuyruğu ayrı bir tüketici sırayla boşaltsın.
  Kuyruk zaten diskte var (`call.state = Queued`) — çökme sonrası `ProcessBacklogAsync` onu
  topluyor. Eksik olan tek şey, canlı yolun da aynı kuyruğu kullanması. Böylece GPU sırası
  yalnızca işlemeyi yavaşlatır, kaydı ve kullanıcı akışını değil.

- [x] **1.6 — İşleme durumu görünür olsun.** Uzun süren bir işlemenin sessizce sürmesi
  "bir şey olmuyor" hissi veriyor. Hangi görüşmenin hangi aşamada olduğu (sırada / yazıya
  dökülüyor / çözümleniyor), kaç tanesinin beklediği ve kabaca ne kadar kaldığı ana ekranda
  görünsün. `RecentCall.Status` (`OverviewViewModel.cs:80`) bunun bir kısmını zaten üretiyor;
  eksik olan ilerleme yüzdesi ve kuyruk derinliği.

---

## 1b. İş kuyruğu, parça parça gönderme ve ilerleme ekranı

Kullanıcının isteği (2026-08-31): *"CPU ve GPU kullanamadığımızda AI'ye gönderdiğimizde bir kuyruk
sistemi olmalı, konuşmaları bölme, bölerek gönderme vs. olmalı, bir de progress ekranı olsa iyi
olur — bu sesi yazıya dönüştürme süreçlerinde bir yerlerde lazım olabilir, bu AI'ye gönderirken,
çünkü part part gönderilecek."*

### Önce iyi haber: büyük kısmı zaten yazılmış

| Parça | Durum | Nerede |
|---|---|---|
| Ses parçalama | ✅ Var ve iyi | `worker/vt_worker/chunking.py` — `plan_chunks()` **en sessiz noktadan** bölüyor, kelime ortasından kesmiyor |
| Metin parçalama | ✅ Var | `TranscriptChunker.Split()` + `BuildRollingContext()` — parçalar arası bağlam taşıyor |
| İlerleme protokolü | ✅ Uçtan uca var | Python `{"stage":"mic","percent":42.0}` yayınlıyor (`__main__.py:197,204`), C# `WorkerProgress` ayrıştırıyor (`WorkerProtocol.cs:188`) |
| Bulut yük devretme | ✅ Var | `TranscribeInCloudAsync` — bir uç yanıt vermezse sonrakini deniyor, hepsini adıyla raporluyor |
| **Ekrana bağlanması** | ❌ **Yok** | `CallOrchestrator.cs:588`, `:637`, `:678` — üçünde de `progress: null` |
| Dayanıklı kuyruk | ❌ Yok | Yalnızca `call.state` var; parça bazında durum yok |
| Devam edebilirlik | ❌ Yok | 12 parçanın 7'si başarısız olursa **baştan başlıyor** |

İlerleme uçtan uca **üretilip son adımda çöpe atılıyor** — ölü ayarlarla (§8) aynı kusur sınıfı.

- [ ] **1b.1 — Dayanıklı iş kuyruğu.** Bulut işi **GPU semaforunun arkasında beklememeli** —
  buluta giden iş GPU kullanmıyor, yerel bir işin arkasında sıraya girmesi saf gecikme.
  Kalıcı (diskte), sıralı, öncelikli (kullanıcının beklediği görüşme, arşiv artığının önünde),
  çökme sonrası devam eden. Geçici hata (hız sınırı, zaman aşımı) ile kalıcı hatayı (bozuk
  anahtar, desteklenmeyen biçim) ayırt etsin; geri çekilmeli yeniden deneme; sınırı aşınca
  kullanıcının **görebildiği** bir ölü mektup durumu.

- [x] **1b.2 — ⚠️ K3: zaman aşımı sessizce sonsuz kuyruğa dönüyor.** *(denetimden, kritik)*
  `CallOrchestrator.cs:490` `OperationCanceledException`'ı **token süzgeci olmadan** yakalıyor.
  `HttpClient.Timeout` (10 dk) `TaskCanceledException` fırlatıyor, buraya düşüyor, görüşme
  sessizce `Queued`'a dönüyor — `Failed` değil, uyarı yok. Sonuç: görüşme sonsuza kadar "Sırada"
  görünüyor, "Tekrar dene" kartında hiç çıkmıyor, **her açılışta yeniden deneniyor.**
  Üstelik `ProcessAsync`'e hiçbir çağrı gerçek token geçirmiyor, yani bu `catch` gerçek iptalle
  asla tetiklenemiyor. Kuyruk işinin parçası olarak düzeltilecek.

- [ ] **1b.3 — Parça bazında durum ve kaldığı yerden devam.** Bugün C# tarafı kaç parça
  olduğunu bile görmüyor (parçalama Python'un içinde). Bir parça başarısız olursa **tamamı**
  yeniden gönderiliyor: para kaybı **ve** görüşmenin ikinci kez makineden çıkması.
  Her parçanın durumu diskte tutulsun, devam tam kaldığı yerden olsun, sıralı birleştirilsin.
  Kalıcı olarak başarısız bir parça metinde **görünür** olsun — sessizce eksik kalmasın.

- [x] **1b.4 — ⚠️ K4: analiz sonuçları eklemeli yazılıyor.** *(denetimden, kritik)*
  `AnalysisPipeline.cs:123,148` düz `INSERT` kullanıyor. `ReplaceSegments`'in aksine önce silme
  yok, `SaveSummary`'nin aksine `ON CONFLICT` yok, şemada tekillik kısıtı yok. **Her yeniden
  işleme, kişinin defterine sözlerin ve fiyatların ikinci bir tam kopyasını ekliyor.**
  K3 ile birleşince kendi kendini besliyor: sessiz yeniden kuyruk → her açılışta yeniden işleme →
  her seferinde defter büyüyor. Devam edebilirlik bunu düzeltmeden eklenemez.

- [x] **1b.5 — İlerleme ekranı.** Üç `progress: null` çağrısından ekrana kadar tam yol.
  Göstermesi gerekenler: hangi aşama (yükleniyor / mikrofon / karşı taraf / birleştirme /
  çözümleniyor), yüzde, **kaçıncı parça / kaç parça**, kuyrukta kaç iş var, kabaca ne kadar kaldı.
  ⚠️ Tespit iş parçacığını **asla** bloke etmemeli (§1.4 zincir A'nın sebebi tam olarak buydu).
  Tepsiye kapatılıp geri açıldığında da doğru durumu göstermeli.

- [ ] **1b.7 — İşlem durumu ekranı: her görüşmenin yazıya dökme ve çözümleme durumu.** ⬆️ *öncelik yükseldi*

  Kullanıcı isteği (2026-08-31): *"Bu görüşmelerin transcript durumlarını, AI analiz durumlarını
  görebileceğimiz bir ekran olsa güzel olmaz mı? Eksik, hatalı olanları tekrar gönderebiliriz,
  tekrar yapabiliriz."*

  **Karar: yapılacak — ve §1b.5'teki ilerleme ekranının ayrısı değil, aynısı.** "Şu an ne oluyor"
  ile "ne oldu, nesi eksik" aynı sorunun iki zamanı. İki ayrı ekran, kullanıcıyı iki yere bakmaya
  zorlar ve ikisi de yarım kalır.

  **Kullanımdan gelen ikinci gerekçe (2026-08-31):** *"CPU'daysa yavaş olduğu için yazıya dökme
  sırasını veya durumunu görebileceğimiz bir ekran olsa güzel olur."* GPU yoksa transkripsiyon
  gerçek zamanın katbekat üstünde sürüyor; kullanıcı o sırada **hiçbir şey göremiyor** ve
  uygulamanın takıldığını mı yoksa çalıştığını mı bilemiyor. Bu, ekranı "iyi olur"dan
  "olmadan kullanılmıyor"a taşıyor.

  **Bu ekran olsaydı, denetimin bulduğu kusurların çoğunu kullanıcı kendisi yakalardı:**
  K3'ün sonsuza kadar "Sırada" bıraktığı görüşme, zincir C'nin `Recorded(0)`'da dondurduğu kayıt,
  zincir D'nin sessizce `Skipped` yaptığı kayıt — hepsi bu listede **göze batardı.** Bugün
  hiçbiri görünmüyor.

- [ ] **1b.8 — ⚠️ Durum modeli "yazı tamam, analiz bozuk" diyemiyor.** *(gerçek kusur, bu işin önkoşulu)*
  `ProcessingState` (`Models.cs:30`) **tek ve doğrusal** bir sıralama:
  `Recorded → Queued → Transcribing → Transcribed → Analysing → Analysed`, artı `Failed` ve
  `Skipped`. Veritabanında tek bir `state` sütunu var (`Schema.cs:63`) ve tek bir
  `failure_reason`.

  Yani **"yazıya dökme başarılı ama çözümleme başarısız" durumu ifade edilemiyor.** `Failed`
  hangi aşamanın başarısız olduğunu söylemiyor. Kullanıcının "eksik, hatalı olanları" ayırt
  edebilmesi için durum ikiye ayrılmalı: **yazıya dökme durumu** ve **çözümleme durumu** ayrı
  alanlar olsun (her biri kendi hata sebebiyle).

- [ ] **1b.9 — ⚠️ "Tekrar dene" her şeyi baştan yapıyor.** *(gerçek kusur — para ve gizlilik)*
  `ReprocessAsync` (`CallOrchestrator.cs:264`) yalnızca `SetCallState(Queued)` deyip
  `ProcessAsync`'i çağırıyor; o da `TranscribeAsync` **ve** `AnalyseAsync`'i sırayla çalıştırıyor.
  Yani **sadece çözümleme başarısız olduysa bile görüşme baştan yazıya dökülüyor.**
  Bulut STT açıksa bu, aynı görüşme için **ikinci kez para ödemek ve sesi ikinci kez makineden
  çıkarmak** demek — üstelik zaten elde olan bir metni yeniden üretmek için.

  §1b.4'teki K4 ile birleşince tablo daha da kötü: başarısız bir çözümlemeyi yeniden denemek
  hem yeniden yazıya döküyor **hem de** deftere kopya kayıt ekliyor.

  *Düzeltme:* aşama bazında yeniden deneme. "Yalnızca çözümlemeyi tekrarla" var olan metni
  kullansın; "yalnızca yazıya dökmeyi tekrarla" sesi yeniden işlesin; "tamamen baştan yap" ayrı
  ve açıkça istenen bir eylem olsun.

- [ ] **1b.10 — Ekranın gösterecekleri.** Görüşme başına: kişi, tarih, süre, uygulama;
  **yazıya dökme durumu** (sırada / çalışıyor %N / tamam / başarısız — sebebiyle / atlandı) ve
  **çözümleme durumu** aynı biçimde; parça ilerlemesi (7/12); kuyruktaki sırası; hangi motorun
  kullanıldığı (yerel mi bulut mu — ses makineden çıktıysa görünsün).
  Eylemler: yalnızca çözümlemeyi tekrarla · yalnızca yazıya dökmeyi tekrarla · tamamen baştan ·
  sıradan çıkar · metni aç · sesi dinle.
  Süzgeçler: yalnızca başarısızlar · yalnızca bekleyenler · yalnızca metni olmayanlar.
  Toplu eylem: seçilenleri tekrar dene (§7.7'deki toplu isimlendirmeyle aynı ihtiyaç).
  Bir kısmı zaten var ve genişletilebilir: `OverviewViewModel.RecentCall.Status`
  (`OverviewViewModel.cs:80`), `FailedCalls()` + `RequeueFailed()` (`:123`) ve
  `AttentionAction.RetryFailed`.

- [ ] **1b.6 — Yerelden buluta düşme kararı görünür olsun.** "CPU/GPU kullanamadığımızda buluta
  gönder" akışı var (`ResolveAsrModel` + `LocalTranscriptionUsableAsync`) ama kararın **neden**
  alındığı kullanıcıya görünmüyor. Ses makineden çıkıyorsa sebebi de yazılmalı.

**Tasarım çalışması sürüyor** (kuyruk dayanıklılığı, parça devam edebilirliği, ilerleme arayüzü —
üç açı + üç eleştirmen). Sonuç gelince bu maddeler kesinleşecek.

---

## 2. WhatsApp / Telegram isimleri yanlış yakalanıyor — ✅ 2026-08-31 YAPILDI

**Kullanıcıdan gelen kilit bilgi (bunsuz doğru çözüm bulunamazdı):**

> *"WhatsApp'ta, Telegram'da bir çağrı olunca **yeni pencerede** arayan kişinin, konuşulan kişinin
> ismi yazıyor."*

Bu, buradan doğrulanamayan tek şeydi ve çözümün tamamını belirledi. Tek bir anlık görüntüde çağrı
paneli ile "o an açık sohbeti gösteren ana pencere" **birbirinden ayırt edilemiyor** — ikisi de
"başlığı uygulamanın kendi adı olmayan bir pencere". Farkı yaratan şey **belirme**: çağrı paneli
bir saniye önce yoktu. Bu da ancak ardışık iki anket karşılaştırılarak görülüyor.

**Yapılanlar:**

| Ne | Nasıl |
|---|---|
| Önek rozeti tanınıyor | `IsShellTitle` artık `"(3) WhatsApp"` ve `"Telegram (3)"` biçimlerinin ikisini de eliyor. Yalnızca parantez içindeki **rakam dizisi** rozet sayılıyor, `"Ahmet (iş)"` bozulmuyor. |
| Yeni pencere = çağrı | `Choose` önceki anketi alıyor; **yeni beliren** aday `Likely` güvenle seçiliyor. |
| Belirsizlikte susuyor | Yeni pencere yoksa ve birden fazla aday varsa ön plandaki `Possible` ile öneriliyor; o da yoksa **isim yazılmıyor**. Yanlış isim, isimsizden kötü — çünkü "hatırla" kutusu onu kalıcı yanlış eşleşmeye çeviriyor. |
| Doğru uygulamadan okunuyor | `Look` artık ses oturumunun suçladığı uygulamayı alıyor; başlık başka messenger'ın penceresinden gelemiyor. |
| Daha iyi başlık üzerine yazabiliyor | `TitleTrust` eklendi; `CallDetector` ilk başlığı kilitlemek yerine daha güvenilirini kabul ediyor. |
| Süreç listesi genişledi | Store Telegram paket kimliği, Telegram çatalları (AyuGram, 64Gram, Kotatogram), WhatsApp Business, **ve Signal Desktop**. |
| Tanı çıktısı | `CallWindows.Describe` + `AudioSessionWatcher.DescribeWindows` — bütün pencereleri başlık/sınıf/boyut/ön plan ve "kişi adı olabilir mi" kararıyla döküyor. |

**Yol üstünde kapanan denetim bulguları.** Pencere bayrağı üç ayrı kararı birden sürüyordu; artık
yalnızca isim için kullanılıyor, çağrı durumu **yalnızca sesten** geliyor — ki `CallDetector`'ın
kendi belgesi zaten bunu söylüyordu ve kod onu ihlal etmişti:

- **Y3** — Açık sohbet penceresi detektörü sürekli `Ringing`'de tutuyordu; 3 dakikada bir zil zaman
  aşımına uğrayıp `Abandoned` üretiyor, bu da **elle başlatılmış kaydı siliyordu.** Artık zil
  yalnızca sesle başlıyor.
- **O8** — Messenger'ı tepsiye küçültmek görüşmeyi ortasından bitiriyordu. Artık pencerenin
  kaybolması çağrıyı bitirmiyor; sessizlik bitiriyor.
- **Y4** — `InCall` için üst sınır yoktu. Ses oturumu takılı kalırsa kayıt sonsuza kadar sürüyordu.
  4 saatlik tavan eklendi ve tavana çarpınca **normal bir `Ended`** üretiyor, yani kayıt kayıp
  değil — bitmiş, satırına yazılmış ve isimlendirmeye sunulmuş oluyor.
- **O7 / Y10** — Uygulama atfı artık **yalnızca sesten**. Pencere varlığı iki messenger için birden
  doğru olabildiğinden, çağrı sonundaki sessiz örneklerde atıf diğer uygulamaya kayıyordu.

**Testler:** 20 yeni test. Toplam **474 · 470 geçti · 0 kırık · 4 atlandı.**
Karar `CallWindows.Choose` içinde ve **saftır** — gerçek arama olmadan tamamen sınanabiliyor,
ki bu makinede zaten mümkün değil.

**Kalan:** §2.5 ve tanı çıktısının arayüze bağlanması (§6.7).

---

Kural şu an: *"izlenen uygulamanın, başlığı uygulamanın kendi adı olmayan her penceresi = kişi adı."*
Bu kural dört yerden sızıyor.

- [x] **2.1 — Okunmamış sayacının önek biçimi yakalanmıyor.**
  `src/VoiceTranscript.Capture/CallWindows.cs:145`
  Kod sadece **sonek** biçimini biliyor (`"Telegram (3)"`). İstemciler sayacı yaygın olarak
  **önek** yazar: `"(3) WhatsApp"`, `"(12) Telegram"`. Bu başlık "kişi adı" sayılıyor ve arşivde
  **"(3) WhatsApp"** adlı bir kişi açılıyor. Her farklı okunmamış sayısı = ayrı bir kişi, yani
  bir kişinin geçmişi onlarca parçaya bölünüyor.
  *Düzeltme:* her iki biçim de tanınsın; sayaç başlıktan ayıklandıktan sonra kalan metin
  uygulamanın kendi adıysa kişi sayılmasın.

- [x] **2.2 — Telegram'ın ana penceresi zaten açık sohbetin adını taşıyor.**
  `src/VoiceTranscript.Capture/CallWindows.cs:109`
  Görüşme paneli ayrı bir pencere, ama ana pencere de "shell başlığı değil" testini geçiyor.
  `EnumWindows` ilk eşleşmede `return false` ile duruyor — yani arayan kişi yerine **o an açık
  olan sohbetin adı** kaydedilebiliyor.
  *Düzeltme:* bütün eşleşen pencereler toplansın, aralarından görüşme paneli olma ihtimali en
  yüksek olan seçilsin (pencere sınıfı, boyut, ön planda olma, çağrı başladıktan sonra açılmış
  olma). Tek bir aday yoksa isim yazılmasın — yanlış isim, isimsizden kötüdür.

- [x] **2.3 — İlk görülen başlık kalıcı olarak kilitleniyor.**
  `src/VoiceTranscript.Core/Detection/CallDetector.cs:106`
  Ana pencere başlığı çağrı panelinden önce yakalanırsa, doğru isim bir saniye sonra gelse bile
  artık yazılmıyor.
  *Düzeltme:* başlık güven derecesiyle saklansın; daha güvenilir bir kaynak gelirse üzerine yazsın.

- [x] **2.4 — İzlenen süreç listesi eksik.**
  `src/VoiceTranscript.Capture/TargetProcesses.cs:26-29`
  Microsoft Store'dan kurulan Telegram Desktop'ın paket kimliği yok, WhatsApp Business yok.
  Ayrıca `" - WhatsApp"` gibi sonekler temizlenmiyor.

- [ ] **2.5 — İsim hiç bulunamadığında ne olacağı belirsiz.**
  Şu an isim bulunamazsa `LabelCallWindow` soruyor — ama §1.3 yüzünden o pencere görünmeyebiliyor.
  İsim yakalama düzeldikten sonra bile WhatsApp'ta isim garanti değil; isimsiz görüşmelerin
  toplandığı ve toplu isimlendirilebildiği bir yer gerekli (kısmen `AttentionAction.ShowUnlabelled`
  var, yeterliliği sınanacak).

---

## 3. Veri şifreleme — kullanıcı parolası ile

**Karar:** veritabanı **ve** ses dosyaları, kullanıcının belirlediği parola ile şifrelenecek.
(DPAPI değil — Windows hesabına erişen birinin de açamaması istendi.)

- [ ] **3.1 — Anahtar türetme ve depolama.** Parola → Argon2id (yoksa PBKDF2-SHA256, yüksek
  yineleme) → ana anahtar. Parola diske **hiç** yazılmaz; yalnızca doğrulama etiketi ve tuz yazılır.
- [ ] **3.2 — Veritabanı şifreleme.** SQLite dosyasının kendisi şifrelenecek (SQLCipher benzeri
  bir yol veya uygulama katmanında alan bazlı şifreleme). Hangisinin seçileceği, mevcut FTS5 tam
  metin aramasının çalışmaya devam etmesi şartına bağlı — **şifreleme aramayı bozmamalı**.
- [ ] **3.3 — Ses dosyası şifreleme.** AES-256-GCM, dosya başına ayrı nonce. Oynatma ve dışa
  aktarma yollarının şifre çözerek çalışması gerekiyor (`AudioPlayer`, `ClipExporter`,
  `ObsidianExporter`, `NotionExporter`, `BackupService`).
- [ ] **3.4 — Açılışta parola ekranı.** Uygulama parola girilene kadar **kayıt yapamaz.**
  ⚠️ Bu, bilinçli olarak kabul edilen bir ödün: Windows yeniden başladıktan sonra parola
  girilmemişse o sırada gelen arama kaydedilemez. Etkisini azaltmak için:
  - [ ] Kilitliyken tepsi simgesi ve bildirim açıkça "kilitli — kayıt yapılmıyor" desin.
  - [ ] Windows açılışında parola ekranı öne gelsin (sessizce tepside beklemesin).
  - [ ] İsteğe bağlı "bu makinede hatırla" seçeneği (parolayı DPAPI ile sarmalar; güvenlikten
        ödün verir, kullanıcı açıkça seçerse açılır).
- [ ] **3.5 — Kurtarma anahtarı.** Parola unutulursa arşivin tamamı kalıcı olarak kaybolur. Bu
  kabul edilemez bir sessiz risk; kurulumda bir kez gösterilen, yazdırılabilir kurtarma anahtarı
  üretilecek ve kullanıcı "yazdım" diye onaylayana kadar devam edilmeyecek.
- [ ] **3.6 — Parola değiştirme ve mevcut arşivi şifreleme.** Şifreleme sonradan açılırsa var olan
  veritabanı ve ses dosyaları geçirilecek; işlem yarıda kesilirse veri kaybolmayacak biçimde
  (önce yaz, sonra takas) yapılacak.
- [ ] **3.7 — API anahtarları.** `settings.json` içindeki OpenAI/Notion/STT anahtarları da aynı
  ana anahtarla şifrelensin.

---

## 4. Oto-update — GitHub Releases, kullanıcı onayıyla

**Karar:** uygulama güncellemeyi **kontrol eder ve haber verir**; indirme ve kurulum ancak
kullanıcı onaylayınca yapılır. Kendiliğinden sessizce güncellemez.

- [ ] **4.1 — Sürüm kaynağı.** `csproj`'a `Version` eklensin; şu an hiçbir yerde tanımlı değil
  (`Directory.Build.props`'ta yok, `installer/VoiceTranscript.iss:17` sabit `1.0.0`,
  `publish.ps1:87` sabit `1.0.0`). Tek bir yerden okunsun.
- [ ] **4.2 — Güncelleme denetimi.** GitHub Releases API'sinden en son yayını oku, sürümü
  karşılaştır. Ağ yoksa veya API hata verirse **sessizce geç** — güncelleme denetimi asla
  uygulamanın açılışını engellememeli.
- [ ] **4.3 — Onay ekranı.** "v1.2.0 çıktı. Değişenler: … [Şimdi güncelle] [Sonra] [Bu sürümü atla]"
  Yayın notları GitHub'daki release gövdesinden gösterilsin.
- [ ] **4.4 — İndirme ve kurulum.** `Setup.exe` indirilir, **SHA-256 doğrulanır**, `/SILENT` ile
  çalıştırılır, uygulama kapanıp yeni sürümle açılır. Doğrulama başarısızsa kurulum yapılmaz.
- [ ] **4.5 — Kayıt sırasında güncelleme yapılmasın.** Görüşme kaydedilirken veya işlenirken
  güncelleme başlatılamamalı; iş bitene kadar beklesin.
- [ ] **4.6 — Ayarlar.** "Güncellemeleri denetle" açık/kapalı, "şimdi denetle" düğmesi, son
  denetim zamanı ve yüklü sürüm gösterilsin.

---

## 5. Sürümleme ve yayınlama (CI)

Amaç: *"geliştirdikçe tekrar indir-kur-derle istemiyorum."* Etiket atılınca gerisi kendiliğinden
olsun.

- [ ] **5.1 — GitHub Actions iş akışı.** `windows-latest` üzerinde `dotnet publish` + Inno Setup
  ile kurulum paketi üretilsin.
- [ ] **5.2 — Etiketle tetiklenen yayın.** `git tag v1.2.0 && git push --tags` → derleme →
  `Setup.exe` ve `SHA256SUMS` release'e eklenir.
- [ ] **5.3 — Testler CI'da koşsun.** Her itmede çalışsın; kırıksa yayın yapılmasın.
  ⚠️ **`dotnet test` kullanılmayacak.** .NET 10 SDK'da xUnit v3 modülü için çalışmıyor: eski VSTest
  köprüsü kaldırılmış, yeni protokole geçirildiğinde de `net10.0-windows` hedefi için
  **"Zero tests ran"** diyor. Denendi ve doğrulandı (2026-08-31): `dotnet.config` ile
  `[dotnet.test.runner]` tanınmadı; `global.json` ile `"test": {"runner": ...}` hatayı
  "VSTest desteklenmiyor"dan "Zero tests ran"e taşıdı, ikisi de test koşturmadı.
  Bu zaten bilinen bir karar — `test.ps1:3` sebebini yazıyor.
  *Doğru yol:* CI `test.ps1` çağırsın veya test modülünü doğrudan çalıştırsın
  (`tests/VoiceTranscript.Tests/bin/.../VoiceTranscript.Tests.exe`). Çıkış kodları doğru,
  filtreler çalışıyor, 439 testin hepsi bulunuyor.
  ⚠️ Ayrıca: `VoiceTranscript.Tests.csproj:10` yorumu "global.json bu işi tamamlar" diyor ama
  öyle bir dosya depoda yok ve olsa da çalışmıyor. **Yorum yanıltıcı, düzeltilmeli.**
- [ ] **5.4 — Sürüm numarası etiketten okunsun.** Etiket, `csproj` ve installer aynı sayıyı
  göstersin; elle üç yerde güncellenmesin.

---

## 6. Geliştirme ortamı

- [x] **6.1 — .NET 10 SDK.** Kuruldu: `10.0.400`, `C:\Program Files\dotnet\dotnet.exe`.
  (PATH'e henüz girmemiş; tam yolla çağrılıyor.)
  **Taban çizgisi alındı (2026-08-31):**
  - `dotnet build VoiceTranscript.slnx -c Debug` → **0 hata**, 22 uyarı (NAudio'nun eski
    `MMDevice.AudioClient` API'si ×2, gerisi xUnit'in `CancellationToken` önerisi). 1 dk 19 sn.
  - Test modülü doğrudan → **439 test, 435 geçti, 0 kırık, 4 atlandı.** 14,5 sn.
    Atlananların hepsi `PythonWorkerHostTests` — bu makinede Python/Whisper yok, beklenen.
  Yani **kod tabanı sağlam.** Bildirilen hatalar derleme veya test kırıklığından değil,
  §1 ve §2'deki mantık kusurlarından geliyor.

- [x] **6.5 — ⚠️ `DataRoot` ayarı ölü.** *(gerçek kusur — 2026-08-31 düzeltildi)*

  **Yapıldı.** `--data <klasör>` komut satırı anahtarı eklendi ve `DataRoot` ayarı da canlandırıldı.
  Öncelik: komut satırı > ayar > varsayılan.
  - `AppPaths.ResolveRoot` / `DataDirectoryFrom` / `AsksForDataDirectory`
    (`AppPaths.cs`) — karar saf ve test edilebilir, Win32 veya WPF'e bağlı değil.
  - `App.xaml.cs` başlangıcı yeniden sıralandı: veri klasörü **günlük açılmadan önce** çözülüyor,
    yoksa günlük yanlış klasöre yazardı.
  - Hatalı `--data` (arkasında klasör yok) **sessizce varsayılana düşmüyor** — uygulama açıklama
    verip kapanıyor. Sessiz geri düşüş, dev derlemesini tam da kaçınılmak istenen gerçek arşive
    yönlendirirdi.
  - Klasör oluşturulamazsa (yanlış yol, olmayan sürücü) Türkçe açıklamayla kapanıyor; daha önce
    günlük henüz açılmadığı için bu, kimseye hiçbir şey söylemeyen bir çökme olurdu.
  - Günlüğün ilk satırı artık hangi veri klasörünün kullanıldığını yazıyor, varsayılan değilse
    açıkça işaretliyor.
  - **15 yeni test** (`tests/VoiceTranscript.Tests/DataDirectoryTests.cs`).

  Kullanımı: `VoiceTranscript.exe --data C:\vt-dev`

  <details><summary>Kusurun özgün tanımı</summary>
  `AppSettings.cs:251` bir `DataRoot` alanı tanımlıyor — "veri dizinini geçersiz kılar" diyor —
  ama **projede hiçbir yerde okunmuyor.** Tek geçtiği yer kendi tanımı. `AppPaths` yapıcısı zaten
  `root` parametresini kabul ediyor (`AppPaths.cs:18`) ama `App.xaml.cs:97` onu argümansız
  çağırıyor, yani ayar hiçbir şey yapmıyor.
  Bu, projede daha önce görülmüş bir kusur biçiminin tekrarı — `RecordAutomatically` de uzun süre
  tanımlıydı ama okunmuyordu, dolayısıyla otomatik kayıt hiçbir yolla kapatılamıyordu
  (bkz. `CallOrchestrator.cs:157` yorumu).
  **Bu neden şimdi önemli:** geliştirme, uygulamanın gerçekten kullanıldığı makineye taşınıyor.
  Deneysel bir derlemenin gerçek görüşme arşivinin üstünde çalışmaması gerekiyor; bunu sağlayacak
  ayar tam da bu. **Makineye geçmeden önceki ilk iş.**
  *Düzeltme:* `App.xaml.cs` `new AppPaths(Settings.DataRoot)` çağırsın. Ama ayarlar dosyası veri
  kökünün *içinde* olduğu için tavuk-yumurta var: önce varsayılan kökten ayarlar okunup sonra
  köke geçilmeli, veya kök ayrı bir yerden (komut satırı argümanı / ortam değişkeni) verilmeli.
  Geliştirme için en temizi bir komut satırı anahtarı (`--data <klasör>`), çünkü ayar dosyasına
  hiç dokunmaz ve kurulu sürümü etkilemez. Bulut klasörü denetimi (`DetectCloudSync`) yeni köke de
  uygulanmalı.
  </details>

  Not: bulut klasörü denetimi (`DetectCloudSync`) zaten çözülen köke uygulanıyor — o kontrol
  `Paths.Recordings` üzerinde ve klasör çözüldükten sonra çalışıyor, yani `--data` ile verilen
  klasör de OneDrive/Dropbox içindeyse uygulama yine açılmıyor.

- [ ] **6.6 — İki makineli çalışma düzeni.** **Karar (2026-08-31): geliştirme burada, gerçek
  testler kullanılan makinede.**

  | | Bu makine (`DESKTOP-4LOD265`) | Kullanılan makine |
  |---|---|---|
  | Kod yazma, derleme | ✅ | — |
  | Birim testleri (`dotnet test`) | ✅ | — |
  | CI / yayın hattı | ✅ | — |
  | Gerçek WhatsApp/Telegram araması | ❌ imkânsız | ✅ tek yer |
  | İsim yakalama doğrulaması (§2) | ❌ `CallWindowsTests.cs:9` bunu yazıyor | ✅ |
  | Kayıt ekranı / işleme akışı (§1) | ❌ gerçek arama gerekiyor | ✅ |
  | CUDA / Whisper hızı | ❌ | ✅ |

  Bu bölünme, §2'nin **burada doğrulanamayacağı** anlamına geliyor. Dolayısıyla o iş, pencere
  başlığı mantığını saf ve test edilebilir tutacak biçimde yazılmalı: Win32 çağrıları ince bir
  katmanda kalsın, karar veren kod (hangi başlık kişi adıdır, hangisi değildir; adaylar arasından
  hangisi seçilir) argüman olarak *başlık listesi* alan saf bir fonksiyon olsun. Böylece kararın
  tamamı burada test edilir, orada yalnızca "gerçekten hangi başlıklar geliyor" doğrulanır.
  Bunun için orada bir **tanı kipi** gerekiyor (§6.7).

  Karşı tarafta test etmeye başlamadan önce:
  - [ ] Arşivi yedekle (Sağlık ekranı → "yedekle" / "sesle birlikte", `HealthPage.xaml:163`).
  - [ ] §6.5 bitmiş olsun — dev derlemesi gerçek arşivin üstünde çalışmasın.
  - [ ] .NET 10 SDK o makineye de kurulsun (veya yayınlanmış derleme kopyalansın).
  - [ ] Tek örnek kilidi (`App.xaml.cs:81`) yüzünden kurulu sürüm tepsiden kapatılsın.

- [ ] **6.7 — Pencere tanı kipi.** `--pencereler` (veya sağlık ekranında bir düğme) çalıştırıldığında
  WhatsApp/Telegram süreçlerine ait **bütün** üst düzey pencereleri, başlıklarıyla, pencere
  sınıflarıyla, boyutlarıyla ve görünürlük durumlarıyla günlüğe yazsın. §2'yi tahminle değil
  gerçek veriyle çözmenin tek yolu bu: bugün `CallWindows.Look` ilk eşleşmede duruyor
  (`CallWindows.cs:109`), yani neyin elendiğini kimse görmüyor. Çıktı, `AppLog`'un gizlilik
  kuralına uymalı — başlıklar kişi adı içerebileceği için bu kip **yalnızca istendiğinde** çalışsın
  ve çıktısının kişi adı taşıyabileceği açıkça söylensin.
- [x] **6.2 — GitHub CLI.** Kurulu ve `fintechcoding` hesabıyla girişli (`repo`, `workflow`
  yetkileri var). Remote zaten bağlı: `https://github.com/fintechcoding/VoiceTranscript`.
  Ayrı bir SSH anahtarına gerek yok — HTTPS + token ile push çalışıyor.
- [ ] **6.3 — Inno Setup.** Kurulum paketi üretmek için gerekli (`winget install JRSoftware.InnoSetup`).
  Kuruldu: **6.7.3**. CI'da da kurulacak (§5.1).
- [x] **6.4 — Python ortamı.** Kuruldu: Python **3.12.10**
  (`%LOCALAPPDATA%\Programs\Python\Python312\python.exe`) + pytest 9.1.1.
  **`worker/` testleri: 56 test, hepsi geçti** (15,5 sn).
  Not: worker testleri yalnızca stdlib ve `pytest` istiyor — `requirements.txt`'teki ağır
  bağımlılıklar (ctranslate2, faster-whisper, onnxruntime, nvidia-cublas) **kurulmadı ve
  gerekmiyor.** Bu makinede GPU yok, gerçek transkripsiyon zaten burada denenemez; o kısım
  kullanılan makinede sınanacak.

### Taban çizgisi özeti (2026-08-31, bu makine)

| Takım | Sonuç |
|---|---|
| Derleme (`dotnet build`) | **0 hata** · 22 uyarı |
| C# (`VoiceTranscript.Tests.exe`) | 439 test · **435 geçti · 0 kırık** · 4 atlandı |
| Python (`pytest`, `worker/`) | 56 test · **56 geçti** |

**Sonuç: kod tabanı sağlam.** Bildirilen hatalar derleme veya test kırıklığından değil, §1 ve
§2'deki mantık kusurlarından geliyor.

Ama asıl önemli çıkarım şu: **495 test yeşilken görüşme sonrası akış çalışmıyor.** Demek ki
hiçbir test o akışı uçtan uca sürmüyor. Bu, projenin kendisinin daha önce yaşayıp yazdığı kör nokta:

> *"Bu dikişin varlığı süs değil: bitmiş bir kaydın dosya yollarını ve süresini satırına geri
> yazan adım tamamen eksikti ve birkaç yüz testlik bir takımdan sağ çıktı, çünkü hiçbiri bir
> kaydı ses kartı olmadan baştan sona sürükleyemiyordu."* — `CallOrchestrator.cs:78`

Aynı kör nokta §1.4'ü de bugüne kadar gizlemiş olabilir. Dolayısıyla §1 düzeltmesinin **ayrılmaz
parçası**, kaydı baştan sona süren bir test olmak zorunda: tespit → kayıt → bitiş → kuyruk →
işleme → özet → kayıt ekranı. Altyapı zaten var ve kullanılmıyor — `CallOrchestrator` yapıcısı
`captureBackend` enjeksiyon noktası taşıyor (`CallOrchestrator.cs:83`) ve `FileAudioSource` mevcut,
yani ses kartı olmadan sürülebilir.

---

## 7. Kayıtları başka kişiye taşıma ve kişi düzeltme

**Somut senaryo (kullanıcının bildirdiği):**

> Serdal'la yaptığım bir görüşme, sistemde **Uliana'nın altına** kaydedilmiş. Bunu elle
> Uliana'dan alıp Serdal'a taşıyabilmem lazım.

Bu bir kenar durum değil, **beklenen durum.** WhatsApp'ta kişi tespiti yapısı gereği tam güvenilir
olamaz (§2), dolayısıyla yanlış atama her zaman olacak. Ürünün buna cevabı "daha iyi tahmin
etmek" değil, **yanlışı bir tıkla düzeltebilmek** olmalı. Otomatik tespit ne kadar iyileşirse
iyileşsin bu ekran gerekli.

Dikkat: taşınacak kayıt **isimsiz değil, zaten Uliana'ya atanmış** durumda. Bugünkü kod yalnızca
*isimsiz* bir kaydı ilk kez atamayı biliyor (`LabelCallWindow` → `AssignContact`). "Atanmış bir
kaydı başka kişiye taşımak" hiç yapılmamış bir işlem — ve §7.2 ile §7.3'teki kusurlar tam olarak
bu yolda ortaya çıkıyor.

### Neden Serdal'ın görüşmesi Uliana'ya gitti — kök sebep

Bu tek seferlik bir yanlış tahmin değil; **öğrenilmiş ve kendini tekrar eden** bir hata. Zincir:

1. **İlk görüşmede pencere başlığı kişi adı sanıldı.** `CallWindows.Look` (`CallWindows.cs:106`)
   şu kuralı uyguluyor: *"başlığı uygulamanın kendi adı olmayan her pencere = kişi adı."*
   Ama gerçek başlık çoğu zaman kişi adı değil — Türkçe Windows'ta WhatsApp'ın arama penceresi
   genel bir metin taşıyabiliyor (örn. "Sesli arama"), Telegram'ın ana penceresi ise **o an açık
   olan sohbetin adını** taşıyor (§2.2). İkisi de "uygulamanın kendi adı" olmadığı için kişi adı
   sayılıyor.
2. **O başlık kalıcı olarak Uliana'ya bağlandı.** İsimlendirme penceresindeki "bu pencere
   başlığını bu kişiyle eşleştir" kutusu **varsayılan olarak işaretli**
   (`LabelCallWindow.xaml:86`). Kullanıcı "Uliana" yazıp kaydedince `RememberTitle`
   (`Repository.cs:132`) o başlığı `title_binding` tablosuna Uliana olarak yazdı.
3. **Sonraki görüşme Serdal'la yapıldı ama aynı başlık göründü.** `BeginRecordingAsync`
   (`CallOrchestrator.cs:257`) → `ResolveTitle` (`Repository.cs:150`) → **Uliana** döndü.
   Kayıt doğrudan Uliana'ya yazıldı.
4. **Ve kayıt ekranı hiç çıkmadı.** `NeedsLabel`, `GetCall(callId)?.ContactId is null` ile
   hesaplanıyor (`CallOrchestrator.cs:399`); kişi zaten "bilindiği" için `false` oldu ve
   `PromptForLabel` ilk satırda geri döndü (`MainWindow.xaml.cs:137`). Kullanıcıya sorulmadı.

Sonuç: hata **sessiz, kalıcı ve kendini besleyen.** Bir kez yanlış bağ kurulduğunda o başlıkla
gelen her görüşme aynı yanlış kişiye gider ve kullanıcı hiçbir zaman düzeltme fırsatı görmez.
Bu, "kayıt ekranı çıkmadı" şikâyetinin §1.4'ten **bağımsız ikinci bir sebebi.**

Dolayısıyla kaydı taşımak tek başına yetmez — **bağı da düzeltmek gerekir** (§7.4), yoksa bir
sonraki görüşme yine Uliana'ya gider.

- [x] **7.1 — Bir görüşmeyi başka kişiye taşı.** Görüşme satırında sağ tık / "kişiyi değiştir" →
  var olan kişilerden seç (arayarak) **veya** yeni kişi oluşturup ata. Uliana'nın sayfasından
  Serdal'a taşımak iki tıkla olmalı.
  `Repository.AssignContact` (`Repository.cs:251`) altyapının bir kısmını sağlıyor ama **taşıma
  için yetersiz** — bkz. §7.2 ve §7.3. Önce onlar düzeltilmeli, sonra arayüz.
  Taşıma geri alınabilir olsun ("geri al" veya en azından onay), çünkü yanlış taşıma da mümkün.

- [x] **7.2 — ⚠️ Taşıma sırasında eski kişinin sayaçları güncellenmiyor.** *(gerçek kusur)*
  `Repository.cs:262-269` yalnızca **yeni** kişinin `call_count` ve `last_call_at` değerlerini
  yeniden hesaplıyor. Bir kayıt A'dan B'ye taşınınca A'da **eski sayaç kalıyor** — A hâlâ o
  görüşme kendisindeymiş gibi görünüyor. Bu kusur bugün görünmüyor çünkü atama yalnızca bir kez,
  isimsiz kayda yapılıyor; taşıma özelliği geldiği anda ortaya çıkar.
  *Düzeltme:* `AssignContact` eski kişiyi de yeniden hesaplasın (aynı işlem içinde).

- [x] **7.3 — ⚠️ Defter kayıtları görüşmeyle birlikte taşınmıyor.** *(gerçek kusur)*
  `commitment` (`Schema.cs:207`), `claim` (`Schema.cs:228`) ve `flag` (`Schema.cs:247`) tablolarının
  hepsi kendi `contact_id` sütununu taşıyor. Görüşme B'ye taşınınca bu satırlar A'da kalır — yani
  **söz A'da, görüşme B'de** olur. Defterin tamamı kişi başına tutulduğu için bu, iki kişinin
  geçmişini birden bozar: A'da olmayan bir sözün tarihi geçmiş görünür, B'de ise hiç söz yokmuş gibi.
  *Düzeltme:* taşıma tek bir işlemde `call`, `commitment`, `claim` ve `flag` satırlarını birlikte
  taşısın.

- [x] **7.4 — Yanlış öğrenilmiş başlık bağı temizlensin.** Bir kayıt yanlış kişiye düştüyse
  sebebi çoğu zaman `title_binding` tablosundaki hatalı bir eşleşmedir (`Repository.cs:132`) —
  ve düzeltilmezse **sonraki her görüşme de aynı yanlış kişiye gider.** Taşıma ekranı "bu başlığı
  bundan sonra da bu kişiye bağla / bağı kaldır" seçeneği sunsun.

- [x] **7.5 — İki kişiyi birleştir.** Aynı insan iki ayrı kişi olarak oluştuysa (§2.1'deki
  "(3) WhatsApp" durumu, veya elle farklı yazım) birleştirme gerekiyor: tüm görüşmeler, sözler,
  iddialar, bayraklar ve başlık bağları hedef kişiye taşınsın, kaynak kişi silinsin. Şu an böyle
  bir metot yok; `DeleteContactCompletely` (`Repository.cs:903`) var ama o **veriyi siliyor**,
  birleştirmiyor.

- [x] **7.6 — Kişiyi yeniden adlandır.** `UpsertContact` (`Repository.cs:44`) ada göre çalıştığı
  için yeniden adlandırma ayrı bir işlem gerektiriyor; aksi halde yeni ad = yeni kişi olur.

- [~] **7.7 — Toplu düzeltme.** İsimsiz veya yanlış kişiye düşmüş kayıtları tek ekranda listeleyip
  toplu atama. Tek tek pencere açmak, on iki kayıt için on iki pencere demek.

---

## 8. Ölü ayarlar ve tutulmayan sözler

Bunlar §6.5'i incelerken çıktı. Hepsi aynı biçimde: **ayar var, arayüzde görünüyor, hiçbir şey
yapmıyor.** Projenin daha önce bu biçimde bir kusuru olmuş ve kod içinde şöyle yazılmış:

> *"Bu ayar en baştan beri vardı ve hiçbir yerde okunmuyordu, dolayısıyla otomatik kayıt aslında
> hiçbir yolla kapatılamıyordu."* — `CallOrchestrator.cs:157`

Aynı sınıf hata en az dört yerde daha var.

- [x] **8.1 — Saklama süresi ayarı tutmadığı bir söz veriyor.** *(2026-08-31 yapıldı)*

  **Yapıldı.** Ayar artık gerçekten çalışıyor ve söz verdiği şeyi yapıyor.

  - `Repository.AudioToSweep(gün)` — süresi dolmuş, **silinmesi güvenli** kayıtları verir.
    Sıfır ve negatif gün boş liste döner: **varsayılan hâlâ süresiz saklamak.**
  - `Repository.ForgetAudio(callId)` — yalnızca `.wav` dosyalarını siler, satırı ve dökümü
    bırakır, `mic_path`/`far_path` alanlarını `NULL` yapar. (Var olmayan bir dosyaya işaret eden
    yol, oynatıcının kimsenin açıklayamayacağı biçimde bozulması demek.) Dosya kilitliyse
    satır temizlenmez — bir sonraki süpürme tekrar dener.
  - `App.SweepOldAudioAsync()` — açılıştan **sonra**, 20 saniye gecikmeyle çalışır (kayıt dosyayı
    açık tutar; süpürme hiçbir zaman onunla yarışacak kadar acil değil) ve ne sildiğini günlüğe
    yazar. Sessizce silen bir süpürme, kayıtların kaybolmasından ayırt edilemez.

  **Muafiyet gerçek olanlarla değiştirildi.** Eski ekran *"sabitlenmiş görüşmeler etkilenmez"*
  diyordu; üründe hiçbir yer sabitleme yapmıyor, yani **kimsenin kullanamayacağı bir güvence**
  veriliyordu. Yerine kullanıcının gerçekten erişebildiği iki işaret kondu: **panoya eklenmiş**
  ya da **not yazılmış** bir görüşmenin sesine dokunulmaz. Panodan çıkarılınca yeniden süpürülebilir
  olur — aksi hâlde pano tek yönlü bir kapı olurdu.

  Ekran metni de yapabildiğini söyleyecek şekilde yeniden yazıldı (`strings.tr.json` /
  `strings.en.json`). Kapsam: `RetentionTests.cs` — 8 test.

- [x] **8.2 — ⚠️ `TranscriptRetentionDays` tamamen ölü.** *(2026-08-31 kaldırıldı)*

  **Kaldırıldı.** Arayüzde yoktu, hiçbir yerde okunmuyordu ve §8.1'de yazılan tasarımın tam
  tersini vaat ediyordu: süpürme **bilerek** dökümü saklıyor, çünkü küçük olan ve saklamaya değen
  parça o. Eski `settings.json` dosyalarındaki anahtar sessizce yok sayılır.

- [x] **8.3 — ⚠️ Ayar kaydetmek bazı alanları sıfırlıyor.** *(gerçek kusur — 2026-08-31 düzeltildi)*

  **Yapıldı.** `ToSettings()` artık sıfırdan kurmuyor, pencerenin açıldığı kaydı düzeltiyor:
  `_original with { ... }` (`SettingsViewModel.cs`). `MainWindow.OpenSettings` içindeki elle
  kurtarma listesi kaldırıldı — artık gereksiz ve zaten yanlış biçimdi: her yeni ayarda elle
  güncellenmesi gerekiyordu ve unutulduğunda sessizce veri düşürüyordu. Yeni bir ayar artık
  varsayılan olarak korunuyor.

  <details><summary>Kusurun özgün tanımı</summary>
  `SettingsViewModel.ToSettings()` (`SettingsViewModel.cs:469`) **sıfırdan yeni bir `AppSettings`
  kuruyor**, yani listelemediği her alan varsayılana dönüyor. Karşılaştırma yaptım — 34 alandan
  5'i listede yok:

  | Alan | Durum |
  |---|---|
  | `SetupCompletedAt` | `MainWindow.cs:174` `with` ile kurtarılıyor ✅ |
  | `TranscribeGroupCalls` | `with` ile kurtarılıyor ✅ |
  | `Language` | `with` ile kurtarılıyor ✅ |
  | `TranscriptRetentionDays` | **kurtarılmıyor — her kayıtta sıfırlanıyor** ❌ |
  | `DataRoot` | **kurtarılmıyor — her kayıtta siliniyor** ❌ |

  `DataRoot` bugün zaten ölü olduğu için etkisi görünmüyor; ama §6.5 ile canlandırılırsa,
  kullanıcı ayarları bir kez kaydettiğinde **taşınmış arşivi görünmez olur.** Bu yüzden §6.5
  bunu da düzeltmeden tamamlanamaz.
  *Düzeltme — yamadan daha iyisi:* `ToSettings()` sıfırdan kurmak yerine mevcut ayarları alıp
  üzerine yazsın: `ToSettings(AppSettings current) => current with { ... }`. Böylece **ileride
  eklenen hiçbir alan sessizce düşemez** — bugün elle bakım gerektiren `with` kurtarma listesi de
  gereksiz kalır. Kusurun tekrarını yapısal olarak imkânsız kılan biçim budur.
  </details>

- [x] **8.4 — Bu sınıf hata için test.** *(2026-08-31 yazıldı)*
  Yukarıdakilerin hiçbiri 439 testten birine takılmamıştı.
  `tests/VoiceTranscript.Tests/DataDirectoryTests.cs` içinde `SettingsSurviveSavingTests`:
  yansımayla `AppSettings`'in **bütün** alanlarını geziyor ve ayarlar ekranının düzenlemediği
  her alanın **aynı değerle** döndüğünü doğruluyor. Ekranın değiştirmeye yetkili olduğu alanlar
  açık bir listede; sıfırdan kurmaya geri dönülürse test anında kırılıyor, ekrana yeni bir alan
  eklenip listeye yazılmazsa da kırılıyor.

**Taban çizgisi (§8 sonrası):** 454 test · 450 geçti · 0 kırık · 4 atlandı.

---

## 9. Kullanırken bulunanlar

> Buraya sen yaz. Biçim serbest; ne yaptığın, ne beklediğin, ne olduğu yeterli.
> Tarih at ki hangi sürümde olduğunu bilelim.

### 2026-08-31

- ~~Görüşme bitince "kim aradı ne yaptı" çıkmadı.~~ → §1.1, §1.2 olarak alındı.
- ~~WhatsApp ve Telegram'dan isimler doğru yakalanmıyor.~~ → §2 olarak alındı.
- ~~İşleme kısmında hata varken yapılan görüşmede kayıt ekranı çıkmadı.~~ → §1.4 olarak alındı,
  araştırılıyor.

<!-- Yeni maddeler buraya -->

---

## 10. İlişki analitiği — konuşma dengesi ve ton

> **Kullanıcı isteği, 2026-08-31.** İleri faz. Şu an yapılmıyor, ama veri modeli buna hazır
> olduğu için burada duruyor.

İstenen: *"bu projeyle ilişkilerimin durumunu ve tonunu öğreneceğim, nasıl daha iyi olabilir —
daha çok ben mi konuşmuşum, daha çok ben mi dinlemişim, ben mi bilgi vermişim, ben mi bilgi
almışım."*

### Neden bunun büyük kısmı zaten elimizde

İki taraf **ayrı dosyalara** kaydediliyor ve `segment` tablosunda `is_me` bir tahmin değil, sesin
hangi akıştan geldiği bilgisi. Tek akışlı kaydeden hiçbir araç bunu yapamaz. Yani konuşma payı,
söz kesme ve sessizlik dağılımı **modele hiç sorulmadan**, doğrudan sayılabilir.

Kişiler sayfasında zaten bir "Konuşma payı" şeridi var (yüzdeler + söz kesme sayısı). Eksik olan,
bunun **zaman içindeki seyri** ve **kişiler arası karşılaştırma**.

### 10.1 Sayılarak çıkarılabilecekler — model gerekmez

| Soru | Nasıl hesaplanır |
|---|---|
| Daha çok ben mi konuştum | `sum(end_ms - start_ms)` — `is_me` kırılımıyla |
| Kim söz kesiyor | `overlaps_other_speaker`, başlatan tarafa göre |
| Sıra alışverişi ne kadar canlı | Konuşmacı değişim sayısı / dakika |
| Kim soru soruyor | Soru işaretiyle biten segment sayısı, `is_me` kırılımı |
| Sessizlikler | Segmentler arası boşluk, kim doldurmuş |
| Zaman içinde değişiyor mu | Aynı ölçüler, görüşme tarihine göre seri |

Bunların hiçbiri LLM istemiyor, dolayısıyla **AI servisi kapalıyken de çalışır** ve geriye dönük
olarak mevcut arşivin tamamına uygulanabilir.

### 10.2 Bilgi verme / bilgi alma — model gerekir

"Ben mi bilgi verdim, ben mi aldım" sayılamaz; ifadenin ne yaptığına bakmak gerekir. Mevcut
çıkarım şeması zaten `taahhut` ve `iddia` üretiyor; buna **konuşma edimi** eklenebilir: soru,
bilgi verme, söz verme, ricada bulunma. Kimin ürettiği zaten `is_me` ile biliniyor.

### 10.3 Ton — dikkatli olunacak yer

**Bu ürün duygu okuma verdikçe yalan söylemeye başlar.** Metinden ton çıkarımının doğruluğu
ölçülüdür ve düşüktür; "bu kişi sana soğuk davranıyor" cümlesi yanlış olduğunda kullanıcının
gerçek ilişkisine zarar verir. §-boyunca korunan kural burada da geçerli: **hüküm verme, alıntıla
ve say.**

Dolayısıyla ton için yapılacak olan, bir puan değil: *"son 3 görüşmede sen ortalama %70 konuştun,
önceki 10 görüşmede %45"* gibi **ölçülen bir değişimi göstermek** ve alıntıyla desteklemek.
Yorumu kullanıcı yapar.

### 10.4 Nerede görünür

- Kişi sayfasında yeni bir sekme: zaman içinde konuşma payı, soru dengesi, söz kesme
- Kişiler arası karşılaştırma (kiminle nasıl konuşuyorum)
- Görüşme penceresinde tek görüşmenin dengesi

### Ön koşullar

- §1b (kuyruk) ve §3 (şifreleme) önce; bu ikisi `segment` okuma yollarını değiştiriyor
- Geriye dönük hesap için toplu bir yeniden tarama işi gerekir — ses değil, yalnızca metin

---

## 11. Kullanıcı istekleri — 2026-08-31 / 09-01 test turu

> Kullanırken söylenenler, sırayla. Yapıldıkça `[x]` işaretlenir, silinmez.

### Yapıldı

- [x] Genel bakışta görüşmeye tıklayınca açılsın — v0.9.13
- [x] Ayarlarda bulut anahtarı kaydedilmiyordu — v0.9.12
- [x] Worker zaman aşımı 2 saat → 8 saat — v0.9.12
- [x] Durum sayfası sekmelere ayrılsın (Sistem · Güncelleme · Veriler · Yapay zekâ · İşlemler) — v0.9.15
- [x] Model listesi çöplük; en iyiler üstte olsun — v0.9.15
- [x] Sürüm görme + elle güncelleme denetimi ekranı — v0.9.10
- [x] Claude API, OpenAI API, OpenRouter canlı model listesi + arama — v0.9.6+
- [x] Kapanışta kuyruktaki görüşmeler başarısız işaretleniyordu — v0.9.16
- [x] Worker hatası "exited with code 1" diyordu, sebep kayboluyordu — v0.9.18
- [x] "Yeniden işle" sessizce çalışıyordu, bildirim görünmüyordu — v0.9.18
- [x] Genel bakıştaki "işlem bekliyor" sayacı yanlıştı — v0.9.17
- [x] Arama, olmuş konuşma için "yok" diyebiliyordu — v0.9.14
- [x] Defterde zaman damgası tıklanabilir görünüp çalışmıyordu — v0.9.14

### Sırada

- [ ] **11.1 Kişi penceresi.** Kişiye çift tıklayınca ayrı pencere: görüşmeleri,
      satır düzeyinde arama, defter, ve **kişi notu**. Not için `contact.notes`
      sütunu zaten var ve hiçbir şey yazmıyor. Tasarım: `docs/` çalışma çıktısı.
- [ ] **11.2 Yeniden işlerken yöntem seçimi.** Yerel / GPU / OpenAI / başka bulut
      sağlayıcı arasından seçerek yeniden çevirme. Şu an hep ayarlardaki yolu
      kullanıyor.
- [ ] **11.3 Transcript kalitesi göstergesi.** Bir görüşmenin metninin ne kadar
      güvenilir olduğu (belirsiz satır oranı, hangi motorla çevrildiği, hız) ve
      oradan "başka modelle yeniden çevir".
- [ ] **11.4 Kullanım ekranı gelişmiş hâli.** Model bazında kırılım, gün seçimi,
      kota, çubuk grafik. Şu an düz metin.
- [ ] **11.5 Yapay zekâ satırlarında "aç" düğmesi ve hızlı ayar.** Satırdan
      doğrudan o servisin ayarına gitmek.
- [ ] **11.6 Günlüğü temizle düğmesi.** Durum → Veriler altında.
- [ ] **11.7 Ses kayıtlarını sıkıştırarak sakla.** 46 dakikalık görüşme şu an
      171 MB (iki akış × 85.6). Opus'ta ~10 MB. Ayrı akış tasarımı korunmalı.
- [ ] **11.8 Genel bakışı zenginleştir + pano (kanban).** "Önemli görüşmeler"
      diye sürüklenip bırakılabilen, kategorize edilebilen bir pano.
      Tasarım hazır: dört sabit şerit (Bakılacak · Bende · Onlarda · Kapandı),
      kart = görüşme, `call.is_pinned` kullanılmıyor — çengel orada.
- [ ] **11.9 Hatırlatmalar.** Panodaki kartlara tarih verip o gün hatırlatma.

### Bu turda eklenenler (2026-09-01)

- [x] 11.1 Kişi penceresi — çift tık; görüşmeler, satır düzeyinde arama, defter, kişi notu
- [x] 11.2 Yeniden işlerken yöntem seçimi — ASR motoru ya da LLM modeli, ölçülen hızlarıyla
- [x] 11.6 Günlüğü temizle düğmesi
- [x] Yalnızca yeniden çözümleme — sesi baştan çevirmeden defteri yeniden kurar
- [x] Görüşme penceresinde "çözümlenmemiş" için eylem — boş sekme yerine düğme

### Açık kalan hata

- [ ] **Bulut çevirisi bazı kayıtlarda patlıyor.** 11 görüşme 208× hızla başarıyla
      çevrildi; iki tanesi patlıyor. Maydin (00:43) **9 satır çevirip sonra**
      hata verdi — yani parça parça yükleme yolunda. v0.9.18 artık worker'ın
      gerçek mesajını gösteriyor; bir sonraki log sebebi söyleyecek.

### Yeni istekler — 2026-09-01

- [ ] **11.10 Görüşme penceresi daha zengin olsun.** Çözümleme yoksa ne yapılacağı
      artık var; ama ekranda daha fazlası olmalı — kalite göstergesi, hangi motorla
      çevrildiği, kaç belirsiz satır, oradan yeniden çevirme.
- [ ] **11.11 Yapay zekâ satırlarına "aç" ve hızlı ayar düğmeleri.** Satırdan
      doğrudan o servisin ayarına gitmek.
- [ ] **11.12 Kullanım ekranı gelişmiş hâli.** Model kırılımı, gün seçimi, kota,
      çubuk grafik. Şu an düz metin ve çalışıyor ama sade.
- [ ] **11.13 Genel olarak UI derinleştirme.** Kullanıcının sözleri: "çok
      detaylandır, gelişmiş UI düşün ve tasarla, basitleştirme."

### 2026-09-01 turu — durum

Yapıldı: 11.1 kişi penceresi · 11.2 yöntem seçimi · 11.3 metin kalitesi ·
11.5/11.11 servisten ayara kısayol · 11.6 günlüğü temizle · 11.8 pano ·
11.9 hatırlatmalar · 11.10 görüşme penceresinde çözümleme eylemi ·
11.12 kullanım grafiği (7/30/tüm zaman + günlük çubuklar)

**Bulut çevirisi hatası kapandı.** Sebep: kataloğa eklediğim `gpt-4o-transcribe`
ve `gpt-4o-mini-transcribe` kelime düzeyinde zaman damgası veremiyor
(`verbose_json` reddediliyor, gerçek API'ye karşı doğrulandı). Katalogdan
çıkarıldı. `whisper-1` doğru çalışıyor — 11 görüşme 208× hızla çevrildi.

#### 11.7 Ses sıkıştırma — neden yapılmadı

İstenen doğru: 46 dakikalık görüşme iki akış için 171 MB, Opus'ta ~10 MB olurdu.
Ama kayıt biçimini değiştirmek şunların hepsini kırar ve bu makinede **ses
donanımı olmadığı için hiçbiri sınanamaz**:

| Ne | Nasıl okuyor | Opus'ta ne olur |
|---|---|---|
| Dalga formu, çalar | NAudio `AudioFileReader` | Opus/Ogg okumaz |
| Karıştırma, kesit çıkarma | NAudio | aynı |
| Python worker | `import wave` (4 dosyada) | WAV bekliyor |
| Bulut yükleme | zaten Opus'a çeviriyor | etkilenmez |

Yani sıkıştırma, kayıt yolunun tamamının yeniden yazılması demek — ve kayıt
yolu bu üründe hiç kırılmaması gereken yol. Sınanamayan bir değişikliği oraya
sokmak doğru değil.

**Yapılabilecek olan, sırayla:**
1. Yazıya döküldükten *sonra* sıkıştır, çalma/dalga formu için gerektiğinde çöz.
   Bir kod çözücü gerektirir (PyAV zaten bulut yolunda var).
2. Ya da 16 kHz mono'yu koru ama sessizliği kırp — konuşma ayrı akışlarda
   olduğu için her akışın yarısından fazlası sessiz. Kayıpsız, %50+ kazanç.
3. Ya da hiç dokunma ve saklama süresi ayarını (§8) gerçekten uygula.

İkincisi en az riskli ve en çok kazandıran. Hedef makinede ölçülmeli.

## 12. 31 Ağustos gece turu — kullanıcı notları

Kullanıcının test sırasında bildirdikleri, geldikleri sırayla. Karşısında durumu.

- [x] **12.1 — Gece boyu süren yeniden deneme dizisi.** Log 00:07–00:40 arası 33 deneme
  gösteriyor, dakikada bir. Kendini besleyen döngü değil — kuyruk 00:40'ta kendiliğinden boşaldı.
  Gerçek kusur: her "Tekrar dene" basışı aynı görüşmeyi kuyruğa **bir kopya daha** ekliyordu ve
  her kopya 60 sn GPU soğuması bekliyordu. İki düzeltme: kuyruk artık tekilleştiriyor
  (`_inQueue`), GPU soğuması buluta yüklerken uygulanmıyor (`MightUseGpu`).
- [x] **12.2 — Bulut 404'ü adresini söylemiyor.** "OpenAI: 404: Invalid URL" gerçek
  api.openai.com'a karşı imkânsız — istek başka yere gidiyor (büyük olasılıkla OpenRouter
  denemesinden kalan taban URL). Worker artık her hatada tam URL'yi veriyor; orkestratör her
  denemeden önce "deneniyor: ad @ adres · model" satırı yazıyor. Bir sonraki log kendi cevabını
  taşıyacak.
- [x] **12.3 — "Yalnızca yeniden çözümle" hiçbir şey yapmıyordu** otomatik çözümleme kapalıysa:
  istek tüketiliyor, durum değişiyor, çözümleme çalışmıyor, kimseye bir şey söylenmiyordu. Açık
  istek artık ayarı eziyor.
- [x] **12.4 — Çözümleme günlüksüzdü.** Başlangıç (görüşme, satır sayısı, sağlayıcı, adres,
  model), bitiş (süre, söz/iddia/red sayıları) ve hata (süreyle) artık günlükte. Yapılandırılmış
  servis yokken sessizce atlanan dal da günlüğe yazıyor.
- [x] **12.5 — Yeniden işleme ekranında bulut/yerel ayrımı yok.** Liste "Bu makinede" /
  "Buluta gönderilir" başlıklarıyla gruplanıyor; makine önce. Ayrıca "ses makineden çıkar"
  rozeti var olmayan bir özelliğe bağlıydı (`SendsAudioOffMachine` — gerçek adı
  `SendsDataOffMachine`), bu yüzden **yerel modellerde de** görünüyordu. Düzeltildi.
- [ ] **12.6 — Ana sayfa bir çalışma alanı olmalı.** Kullanıcının tarifi: ayrı kanban sayfası
  değil; Genel bakış'ta "önemli görüşmeler" paneli — sürükleyip atabileceği, silebileceği,
  kaydırabileceği. "Anasayfa boş duruyor, workspace gibi orası." → Plan ajanına verildi.
- [ ] **12.7 — Kişi penceresi bir detay sayfası olmalı.** Foto ekleme, doğum tarihi, kişi
  hakkında yapılandırılmış bilgiler, "bir sürü alan olabilir". → Plan ajanına verildi.
