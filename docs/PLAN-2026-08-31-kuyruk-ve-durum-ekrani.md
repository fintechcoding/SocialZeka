> **Bu dosya nedir.** 2026-08-31'de yapilan cok ajanli tasarim calismasinin ciktisi: dayanikli
> is kuyrugu, parca parca gonderme ve devam edebilirlik, bolunmus transkript/cozumleme durum
> modeli, ve islem durumu ekrani. Dort bagimsiz tasarim acisi, ardindan uc elestirmen
> (veri kaybi, maliyet/gizlilik, butunluk).
>
> **Bu bir plan dosyasidir, yapilacaklar listesi degil.** Secilip siraya alinanlar YAPILACAKLAR.md
> icinde yasar. Plan degistirilmez; tarihli bir kayittir.
>
> Not: plan calisma dizinindeki commit'lenmemis ise gore yazildi ve o is bu arada
> `duzeltmeler/gorusme-akisi-ve-kisi-onarimi` dalinda commit edildi (a3763dd).

---

# Kuyruk, parça parça gönderme ve İşlem Durumu ekranı — uygulama planı

**Taban: `main` @ `7f66d2b` + çalışma dizinindeki commit'lenmemiş değişiklikler (28 dosya, +2841/-226).**
Aşağıdaki her satır numarası **şu anki diskteki hâlden** doğrulandı, HEAD'den değil. Bu önemli: dört tasarım da HEAD'e göre yazılmıştı ve `CallOrchestrator.cs` o zamandan beri ~+230 satır büyüdü, `Repository.cs` ~+400. Eski satır numaralarına göre iş yapılırsa değişiklik yanlış metoda düşer.

**Başlamadan önce yapılması gereken tek şey:** çalışma dizinindeki iş commit'lensin. 28 dosya içinde A1 düzeltmesi, `MoveCallWindow`, taşınabilir veri dizini ve Signal desteği var — bunlar tek bir değişiklik değil. Bu planın üstüne inşa edilecek taban belirsizse `git bisect` işe yaramaz.

---

## 0. Ne zaten var, ne eksik

| Konu | Durum | Kanıt |
|---|---|---|
| **Ses parçalama** | ✅ Var, çalışıyor. En sessiz noktadan böler. | `worker/vt_worker/chunking.py` `plan_chunks` / `slice_wav`; testleri `worker/tests/test_chunking.py` |
| **Bulut parça önbelleği** | ✅ Var ama görünmez ve sızdırıyor | `cloud_engine.py:154-162` (`{wav}.cloudparts`), anahtar `:175` |
| **Transkript parçalama (analiz)** | ✅ Var | `Core/Analysis/TranscriptChunker.cs` |
| **İlerleme protokolü (uçtan uca)** | ✅ Var, **üç yerde çöpe atılıyor** | Python `__main__.py:197,206,220` → `WorkerProgress` (`WorkerProtocol.cs:188-194`) → `PythonWorkerHost.TranscribeAsync(..., IProgress<WorkerProgress>?, ct)` → **`progress: null`** `CallOrchestrator.cs:884, 933, 974` |
| **Bulut yedekleme (endpoint failover)** | ✅ Var | `CallOrchestrator.cs:850-905` |
| **İşlem kuyruğu (bellekte)** | ✅ Var | `Channel<long> _processing` `:160`, `Enqueue` `:322`, `ProcessQueueAsync` `:299-320` |
| **A1 (tespit iş parçacığı bloklanması)** | ✅ **Düzeltilmiş.** `Tick()` artık sadece kanala yazıyor; `MainWindow.xaml.cs` `Dispatcher.InvokeAsync` kullanıyor | `CallOrchestrator.cs:151,263`; `FinishRecordingAsync` `:494` artık `Task`, `ProcessAsync`'i beklemiyor (`:564 Enqueue`) |
| **K3 (timeout → sessiz Queued)** | ✅ **Düzeltilmiş.** | `CallOrchestrator.cs:731` `when (cancellationToken.IsCancellationRequested)`, `:735-743` timeout → `Failed` |
| **Kişi sahipliği (etiketleme sonrası defter)** | ✅ **Düzeltilmiş.** `AssignContact` artık `commitment`/`claim`/`flag`'in `contact_id`'sini de taşıyor | `Repository.cs:269-292` |
| — | — | — |
| **S1: aşama bazlı durum** | ❌ Yok. Tek `state` sütunu, tek `failure_reason` | `Schema.cs:62-63`; `ProcessingState` `Domain/Models.cs` |
| **S2: aşama bazlı tekrar** | ❌ Yok. `ReprocessAsync` her şeyi baştan yapar | `CallOrchestrator.cs:403-407` → `ProcessAsync:658` → `TranscribeAsync:679` koşulsuz |
| **K4: defter çoğaltması** | ❌ Yaşıyor. Üç çıplak INSERT döngüsü, benzersizlik kısıtı yok | `AnalysisPipeline.cs:123,124,148`; `Repository.cs:834,865,892` |
| **Kuyruğun kalıcılığı** | ❌ Yok. Kanal bellekte; süreç ölünce sıra kaybolur | `_processing` `:160` |
| **Aynı çağrının iki kez işlenmesini engelleme** | ❌ Yok. `Enqueue` çıplak `TryWrite` | `:322`; `ProcessBacklogAsync:1042` + `MainWindow` "Tekrar dene" ikisi de kuyruğa basar |
| **Parça durumunun C# tarafında bilinmesi** | ❌ Yok. Python `"3/5 yükleniyor"` üretiyor, `__main__.py:204` `_stage`'i atıyor | |
| **Şema sürüm makinesi** | ❌ Yok. `Migrate()` sürümü **yazıyor ama hiç okumuyor** | `Database.cs:66-87` |
| **Saklama süpürücüsü** | ❌ Yok. `AudioRetentionDays`/`TranscriptRetentionDays` hiçbir yerde tüketilmiyor | `AppSettings.cs`; `DeleteCall` `.cloudparts`'a dokunmuyor (`Repository.cs:1030-1057`) |
| **İşlem durumu ekranı** | ❌ Yok. `IsFailed` (`OverviewViewModel.cs:98`) hesaplanıyor, hiçbir XAML'e bağlı değil | |

**Özet:** ilerleme makinesinin çoğu yazılmış. Yazılacak olan şey **kalıcılık** (aşama durumu, parça durumu, kuyruk satırı), **tekilleştirme** (tek talep sahipliği + defter anahtarı) ve **ekran**.

---

## 1. Durum modeli

### 1.1 Neden yeni tablo, `call`'a yeni sütun değil

`Database.Migrate()` (`Database.cs:66-87`) `Schema.Statements`'ı sırayla tek transaction'da çalıştırıyor ve `schema_version`'ı **yazıp hiç okumuyor**. `CREATE TABLE IF NOT EXISTS` var olan bir tabloyu değiştiremez, `ALTER TABLE` makinesi yok. Yani `call`'a sütun eklemek bugün mevcut bir veritabanında **sessizce hiçbir şey yapmaz**. Yeni tablo tek güvenli şekil.

### 1.2 Şema (Sürüm 2 → 3)

```sql
CREATE TABLE IF NOT EXISTS call_stage (
    call_id          INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
    stage            INTEGER NOT NULL,           -- 0 transkript, 1 çözümleme, 2 dışa aktarma
    status           INTEGER NOT NULL DEFAULT 0,
    -- 0 Pending 1 Queued 2 Running 3 Done 4 Partial 5 Failed 6 Skipped
    -- Bu sayılar diske yazılıyor. ASLA yeniden numaralandırma; sadece sona ekle.

    inferred         INTEGER NOT NULL DEFAULT 0, -- 1 = göç sırasında tahmin edildi, kaydedilmemişti
    lane             INTEGER,                    -- 0 yerel(GPU) 1 uzak; NULL = henüz bilinmiyor
    priority         INTEGER NOT NULL DEFAULT 50,

    attempts         INTEGER NOT NULL DEFAULT 0, -- gerçek başarısızlıklar
    interruptions    INTEGER NOT NULL DEFAULT 0, -- kapanma/çökme; ASLA ölümcül değil
    uploads_attempted INTEGER NOT NULL DEFAULT 0,-- makineden gerçekten çıkan POST sayısı
    max_attempts     INTEGER NOT NULL DEFAULT 5,
    next_attempt_at  TEXT,

    lease_owner      TEXT,
    leased_until     TEXT,

    queued_at        TEXT,
    started_at       TEXT,
    finished_at      TEXT,
    heartbeat_at     TEXT,

    parts_done       INTEGER NOT NULL DEFAULT 0, -- yalnızca yukarı yazılır (su seviyesi)
    parts_total      INTEGER NOT NULL DEFAULT 0,

    engine           TEXT,
    endpoint_id      TEXT,
    off_machine      INTEGER,     -- NULL = bilinmiyor. ASLA DEFAULT 0 yapma.

    failure_class    INTEGER NOT NULL DEFAULT 0, -- 0 yok 1 geçici 2 kalıcı 3 yapılandırma 4 çevrimdışı 5 hız-limiti
    failure_code     TEXT,        -- kapalı sözcük. AppLog'a gidebilecek TEK yarı.
    failure_detail   TEXT,        -- servisin ham metni. YALNIZCA veritabanı + ekran.

    work_dir         TEXT,
    PRIMARY KEY (call_id, stage)
);
CREATE INDEX IF NOT EXISTS ix_call_stage_claim
    ON call_stage(status, lane, priority, next_attempt_at, call_id);
```

```sql
CREATE TABLE IF NOT EXISTS call_part (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    call_id       INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
    stage         INTEGER NOT NULL,
    stream        TEXT    NOT NULL DEFAULT '',   -- 'mic' | 'far' | '' (çözümleme)
    part_index    INTEGER NOT NULL,
    part_total    INTEGER NOT NULL,
    start_ms      INTEGER NOT NULL,   -- SABİTLENMİŞ sınır. Bkz. §3.2 — hayat kurtarır.
    end_ms        INTEGER NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,    -- 0 bekliyor 1 bitti 2 başarısız 3 vazgeçildi
    attempts      INTEGER NOT NULL DEFAULT 0,
    endpoint_id   TEXT,
    uploaded_bytes INTEGER NOT NULL DEFAULT 0,
    source_version INTEGER NOT NULL DEFAULT 0,   -- çözümleme için: COALESCE(MAX(segment.id),0)
    failure_code  TEXT,
    failure_detail TEXT,
    completed_at  TEXT,
    UNIQUE(call_id, stage, stream, part_index)
);
CREATE INDEX IF NOT EXISTS ix_call_part_call ON call_part(call_id, stage);

-- Cevap ayrı tabloda: parça satırı ekranda her görünür çağrı için taranıyor,
-- payload ise overflow sayfalarında duran ve sadece devam ederken okunan yığın.
CREATE TABLE IF NOT EXISTS call_part_answer (
    part_id    INTEGER PRIMARY KEY REFERENCES call_part(id) ON DELETE CASCADE,
    payload    TEXT    NOT NULL,
    bytes      INTEGER NOT NULL DEFAULT 0,
    created_at TEXT    NOT NULL
);
```

`commitment`, `claim`, `flag`'e `dedupe_key TEXT` + kısmi benzersiz indeks (§3.4).

**Neden diskte:** `status`/`failure_*` → kullanıcının sorusu (“ne eksik?”) süreç öldükten günler sonra soruluyor. `parts_done` + `call_part_answer.payload` → gönderilmiş bir parça **hem ödenmiş hem dışa aktarılmış** bir konuşma; tekrar göndermek ikisini de tekrar yapar. `off_machine`/`endpoint_id` → “bu konuşmayı kim duydu” sonradan hesaplanamaz. `lease_owner`/`leased_until` → yeniden başlatılan bir süreç, “biri çalışıyor” ile “yarıda kalmış” arasındaki farkı ancak böyle anlar.

**Neden diskte değil:** canlı yüzde, canlı adım metni. Yeniden başlatmadan sonra anlamsız, ve her ilerleme olayına yazma `ReplaceSegments`'ın (`Repository.cs:524`, yüzlerce satırı FTS tetikleyicisiyle tek transaction'da tutar, `busy_timeout=5000`, `secure_delete=ON`) yazma penceresiyle çakışır.

### 1.3 Göç makinesi (önce bu, tek başına)

`Database.Migrate()` şunu yapacak, bu sırayla:

```
1. mevcut = setting.schema_version  (yoksa 0)
2. eğer 0 < mevcut < Schema.Version:
       VACUUM INTO '{db}.pre-v3'      -- transaction AÇILMADAN ÖNCE
3. Schema.Statements                  -- yalnızca CREATE ... IF NOT EXISTS
4. Schema.Migrations içinden To > mevcut olan her adım
5. schema_version = 3; commit
```

**`VACUUM INTO`, `File.Copy` değil.** `Open()` `journal_mode = WAL` kuruyor (`Database.cs`), ve `BackupService.cs` bunu zaten yazılı olarak söylüyor: ana dosyayı tek başına kopyalamak son dakikaların yazılarını sessizce kaybeder. Bu uygulamanın normal çıkışı öldürülmektir, yani göç anında commit'lenmiş veriyi tutan bir `-wal` dosyası **beklenen** durumdur. Bir sonraki adımda defterden satır siliyoruz.

**Kural (kod incelemesinde zorunlu):** `Schema.Statements` içinde **hiç `DELETE`, hiç `UPDATE` olmayacak.** Bunlar sürüme bağlı adımlara girer. Aksi hâlde bir onarım her açılışta çalışır ve doğruluğunu her açılış yeniden iddia eder — meşru bir çıkarım aynı anahtara düştüğü gün kanıt sessizce silinir.

### 1.4 Mevcut satırlar ne olacak

Ölçüt: **`segment` satırının varlığı** transkriptin başarısı için tek dürüst kanıttır; `call_summary` de çözümleme için.

| `call.state` | transkript aşaması | çözümleme aşaması |
|---|---|---|
| 0 Recorded / 1 Queued | Queued | Pending |
| 2 Transcribing | **Queued** (worker uygulama ile birlikte öldü, `JobObject` `KILL_ON_JOB_CLOSE`) | Pending |
| 3 Transcribed | Done | Queued (LLM varsa) / Pending |
| 4 Analysing | Done | **Queued** |
| 5 Analysed | Done | Done |
| 6 Failed, segment **var** | Done, `inferred=1` | Failed, `inferred=1`, `failure_code='legacy'` |
| 6 Failed, segment **yok** | Failed, `inferred=1`, `failure_code='legacy'` | Pending |
| 7 Skipped | Skipped | Skipped |

**`inferred=1`, eleştirmenin haklı olduğu yer.** Bir kullanıcı Haziran'da başarıyla çözümlenmiş bir çağrıyı Ağustos'ta “Tekrar dene” ile yeniden işlemiş ve **transkripsiyon** başarısız olmuş olabilir — `ReplaceSegments`'a hiç varılmadığı için Haziran'ın segmentleri hâlâ orada. O satır `state=6 AND EXISTS(segment)` görünür ve tablo ona “çözümleme başarısız” der. Bu yanlış olabilir. Bu yüzden ekran `inferred=1` satırlarda **“hangi adımda başarısız olduğu kaydedilmemiş”** rozetini gösterir ve **iki tekrarı da** sunar. Bilinmeyeni itiraf etmek, bilinemeyeni iddia etmekten iyidir.

Ayrıca `call_part` için tek satırlık eski kayıt doldurması: transkripti olan her çağrıya `status=Done, part_total=1, answer yok`. **Bu olmadan** yükseltmeden sonraki ilk kuyruk taraması bütün arşivi tekrar buluta yükler.

### 1.5 `call.state` kalıyor — türetilmiş ayna olarak

`ix_call_state`, `CallsAwaitingProcessing` (`Repository.cs:514`), `FailedCalls` (`:655`), `PendingWorkCount` (`:794`), `CallRow.ToModel()`, `OverviewViewModel.Status` (`:80`) ve ~15 test onu okuyor. Silmek çok geniş bir patlama alanı. Bunun yerine **her aşama geçişinin transaction'ı içinde** yeniden hesaplanır:

```
herhangi bir aşama Failed              -> 6 Failed
transkript Partial                     -> 3 Transcribed   (yarıklı; tekrar kartına DÜŞMEZ)
transkript Done & çözümleme Failed     -> 6 Failed
...Running -> 2/4, ...Queued -> 1, hepsi Skipped -> 7, hepsi Done -> 5, transkript Done -> 3
```

**`SetCallState` silinecek, `[Obsolete]` yapılmayacak.** İmzası `string? failureReason = null` (`Repository.cs:242`) ve koşulsuz yazıyor — yani `SetCallState(id, Queued)` diyen her çağıran (`:405, :553, :733`, `OverviewViewModel:128`, `HealthViewModel`) kayıtlı arıza nedenini **siliyor**. Türetilmiş bir aynanın yanında yaşayan kör bir `UPDATE`, iki yazarın tek sütuna yazması demektir. Yerine `FailStage(callId, stage, class, code, detail)` / `CompleteStage` / `QueueStage` gelir.

### 1.6 Zaman damgaları

`CompleteCall` `ended_at`'i `endedAt.ToString("o")` ile **yerel ofsetle** yazıyor (`Repository.cs:211-240`), `Iso()` ise `UtcDateTime.ToString("O")` ile UTC (`:1166`). Yeni sütunlarda `ORDER BY`/`MAX()` yapacağız. **Her yeni TEXT zaman damgası `Iso()`'dan geçecek**, ve göç `ended_at`'i kopyalamak yerine normalize edecek. Aksi hâlde hata, biçim hatası gibi değil zamanlama hatası gibi görünür ve aralıklı olur.

---

## 2. Kuyruk

### 2.1 İş birimi: aşama, çağrı değil, parça da değil

- **Çağrı çok kaba** — S2 tam olarak bu: başarısız bir çözümlemeyi tekrar denemek, zaten veritabanında duran bir transkripti yeniden üretmek için sesi tekrar yükleyip tekrar para ödemek demek.
- **Parça çok ince** — parçalar ayrı süreçlerde yaşamıyor. Bulut parçaları tek `vt_worker transcribe` çağrısı içindeki döngü (`cloud_engine.py:123-141`), çözümleme parçaları tek `AnalyseAsync` içindeki döngü (`AnalysisPipeline.cs:94-119`). Her parça bir kuyruk satırı olsaydı her 20 dakikalık ses için bir Python süreci (ve yerelde bir model yüklemesi) gerekirdi.

Yani: **kuyruk aşamaları zamanlar; parçalar aşamayı devam ettirilebilir kılar.**

### 2.2 Tek sahiplik — lane ayrımından ÖNCE

Bugün iki eşzamanlı işleyici zaten var ve aralarında hiçbir kontrol yok:

- `App.xaml.cs` → `Orchestrator.Start()` → `ProcessQueueAsync` (`:299`)
- `App.xaml.cs` → `Task.Run(ProcessBacklogAsync)` (`:1042`) → `Enqueue`
- `MainWindow` “Tekrar dene” → `ProcessBacklogAsync()` her basışta, beklenmeden

`Enqueue` (`:322`) çıplak `TryWrite`. Yeniden üretilebilir dizi: açılışta biriken 7, 8, 9 kuyruğa girer; kullanıcı "Tekrar dene"ye basar, `RequeueFailed` 3 ve 4'ü `Queued` yapar, `ProcessBacklogAsync` tekrar çalışır ve **hâlâ `Queued` olan 8 ve 9'u ikinci kez** kuyruğa basar. Bugün bunu ardışık kılan tek şey `_gpu` semaforu (`:670/751`) — sonuç iki kez işleme, tek seferde. **Lane ayrımı `_gpu`'yu kaldırdığı an bu iki eşzamanlı Python sürecine dönüşür**, ikisi de aynı `.cloudparts` dosyasına yazar, iki `ReplaceSegments` yarışır.

Çözüm: talebi tek koşullu ifade yap.

```sql
UPDATE call_stage
   SET status = 2, lease_owner = @owner, leased_until = @until,
       started_at = @now
 WHERE (call_id, stage) = (
       SELECT call_id, stage FROM call_stage
        WHERE status = 1 AND (lane = @lane OR lane IS NULL)
          AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
        ORDER BY priority, next_attempt_at, call_id LIMIT 1)
RETURNING *;
```

Sıfır satır dönerse **hiçbir şey yapılmaz**. `RETURNING` bu projede kanıtlı (`InsertCall` zaten kullanıyor). **Dört giriş noktasının hepsi** buradan geçecek: `ProcessQueueAsync`, `ProcessBacklogAsync`, `ReprocessAsync`, ve `MainWindow`'un beklenmeyen çağrısı. `ProcessAsync` artık dışarıdan doğrudan çağrılamayacak.

**Sıralama:** öncelik (küçük = önce; yeni biten çağrı 10, sıradan iş 50, kullanıcı tekrarı 0), sonra `next_attempt_at`, sonra `call_id`.

### 2.3 İki şerit — ve bulut işi neden GPU'yu beklememeli

Bugün `_gpu` semaforu GPU'dan çok daha fazlasını kapsıyor (`:670-751`): içinde `GpuCooldownSeconds` gecikmesi (varsayılan 60 sn, `:673-674`), `TranscribeAsync` — **tamamen uzak olan `TranscribeInCloudAsync` dahil** —, `AnalyseAsync` (uzak LLM dahil), Obsidian ve Notion dışa aktarmaları var. Yani buluta gidecek bir çağrı önce bir saatlik yerel transkripsiyonu bekliyor, sonra hiçbir şey yapmadan 60 saniye daha bekliyor, sonra yüklemeye başlıyor. Bu, karşılığında hiçbir şey alınmadan satın alınmış gecikme.

- **Yerel şerit, derece 1**, `GpuLease`'i tutar, cooldown'u öder.
- **Uzak şerit, derece 2**, GPU'ya hiç dokunmaz, cooldown ödemez.
  - Neden 1 değil: bir sağlayıcıda 60 saniyelik hız-limiti beklemesi, alakasız bir çağrıyı durdurmamalı.
  - Neden 8 değil: her uzak transkripsiyon yine WAV dilimleyip Opus kodlayan bir Python süreci başlatıyor (`cloud_engine.py:189, 211-237`) — kayıt da yapan bir dizüstünde gerçek CPU ve disk. Ayrıca tek endpoint'e paralel istek, kaçındığımız 429'u üretir.
  - Ek kapı: **endpoint başına en fazla bir iş uçuşta** (bellekte, `endpoint_id` anahtarlı). Şerit sınırı makine yükünü, bu kapı sağlayıcı yükünü sınırlar.

`_gpu`, `GpuLease` adlı paylaşılan bir servise dönüşür — çünkü bugün `HardwareProbe` ve `SettingsWindow`'un selftest'i VRAM için hiçbir dışlama olmadan yarışıyor.

**`lane` sütunu NULL olabilir.** `AppSettings.ResolveAsrModel`, `Automatic` modda `localTranscriptionUsable`'a bakıyor; o da `LocalTranscriptionUsableAsync`'in (`CallOrchestrator.cs:811-827`) sonucudur ve bir Python probe'u gerektirir. Satır yazılırken bilinemez. NULL şerit her iki şeritten de talep edilebilir; talep anında çözülür ve yazılır.

### 2.4 Yeniden deneme, geri çekilme ve dört başarısızlık sınıfı

**Üç iç içe döngü var ve kimse çarpmamıştı:** Python'da parça başına 5 POST (`cloud_engine.py:242`), C#'ta N endpoint (`:850-905`), ve önerilen iş seviyesi tekrarı. Bir saatlik bir çağrı (3 mic + 3 far parça), iki endpoint, sürekli hız limiti: `5 × 2 × 6 × 5 = 300 tam yükleme`. Her POST gövdeyi tamamen yazdıktan **sonra** durumu okur (`:290`), yani 429 ses zaten gittikten sonra gelir; ve başarısız POST hiçbir şey önbelleğe almaz.

Bu yüzden sınır **deneme sayısı değil, yükleme bütçesi**: `uploads_attempted >= 3 * parts_total` olduğunda yeni bir iş denemesi başlatılmaz. 6 parçalık bir çağrı için 18 yükleme — gerçek geçici bir kesintiyi karşılar, 300'lük fırtınayı durdurur.

| Sınıf | Kodlar | Davranış |
|---|---|---|
| **Geçici** | `timeout`, 5xx, `SQLITE_BUSY`, `device_invalidated` | `min(30·2^(n-1), 1800)` sn + yarım jitter; deneme harcar |
| **Hız limiti** | 429 | `Retry-After`'a **saygı**; `next_attempt_at`'e yazılır; kardeş endpoint denenmez |
| **Kalıcı** | `auth`\*, `bad_config`, `too_large`, `bad_request`, dosya yok | Anında Failed; deneme sayısı anlamsız |
| **Yapılandırma** | kullanılabilir endpoint yok, `cuda_runtime` + LocalOnly | Anında Failed; mesaj servisi değil ayarları işaret eder |
| **Çevrimdışı** | ağ ulaşılamıyor **ve** `GetIsNetworkAvailable()` de öyle diyor | 5 dk, **deneme harcamaz**, 7 gün tavanı |

\* **`auth` kritik ayrıntı:** sınıflandırma **tek endpoint'in hatasında değil, hepsi reddettikten sonra atılan toplu hatada** yapılır. `CallOrchestrator.cs:873-876`'daki yorum kararı açıkça yazıyor: süresi dolmuş bir anahtar, ikinci endpoint'in var olma sebebidir. Angle 1'in "auth = kalıcı, hemen öl" kuralı endpoint bazında uygulanırsa bu yedeklemeyi öldürür. Bunun için `TranscribeInCloudAsync`'in fırlattığı hata, birleştirilmiş Türkçe metin yerine `(endpoint_id, code)` listesi taşıyan tipli bir hata olmalı.

**`Retry-After` kırpılmayacak.** Bugün `_sleep(min(delay, MAX_BACKOFF_SECONDS))` (`cloud_engine.py:256`, `MAX_BACKOFF_SECONDS=60`) — sunucu `Retry-After: 3600` dediğinde 60 saniyede, dört kez daha, her seferinde tam yükleme ile deneniyor. Bu, yumuşak bir hız limitini askıya alınmış anahtara çeviren davranıştır. Tahmin edilen geri çekilme kırpılır; **sunucunun söylediği kırpılmaz** — `EngineError("rate_limited", retry_after=...)` fırlatılır ve `next_attempt_at`'e kalıcı olarak yazılır.

### 2.5 Kesinti ≠ başarısızlık

`Dispose` işlemciyi **kasten beklemiyor** ("bir saatlik transkripsiyonun ortasında olabilir, kapanışı bunun için tutmak uygulamanın donduğu izlenimini verir") ve `JobObject`'in `KILL_ON_JOB_CLOSE` bayrağı worker'ı her uygulama çıkışında öldürüyor. Yani **her normal kapanış**, çalışan bir işi öldürür.

Bu yüzden `interruptions` ayrı bir sütun ve **tek başına asla ölümcül değil**. Angle 1 (talep anında `attempts++`, tavan 5) ile Angle 4 (`attempt>=3` → `Failed`, ve `Failed` otomatik kuyruğa alınmaz) birleştirilirse, bir dizüstünün kapağını üç gün üst üste kapatmak 55 dakikalık bir görüşmeyi kalıcı olarak öldürür — hiçbir şey başarısız olmadığı hâlde ve ses diskte sağlamken. Kabul edilemez.

Uygulama: `Dispose` dönmeden önce çalışan aşamaları `interrupted_cleanly` işaretler. Açılışta `leased_until` geçmiş olan satırlar `Queued`'a döner; **temiz işaret varsa `attempts` artmaz, `interruptions` artar.** İşaret yoksa (gerçek çökme) `attempts` artar. Bir tavan istenirse duvar saatine göre olsun (“30 gündür hiç ilerleme yok”), sayıya göre değil.

### 2.6 K3

Zaten düzeltilmiş (`CallOrchestrator.cs:731-743`). Yeni modelde tek değişiklik: `catch` bloklarındaki `SetCallState` çağrıları **çalışan aşamaya** yazan `FailStage`/`QueueStage`'e dönüşecek, ve arıza nedeni asla varsayılan argümanla NULL'lanmayacak.

Ayrıca `PendingWorkCount` (`Repository.cs:794`, `state IN (0,1,2,3,4)`) düzeltilecek: `AnalyseAutomatically` kapalıyken her başarılı çağrı `Transcribed(3)`'te duruyor ve **sonsuza kadar bekleyen sayılıyor** — Genel Bakış'taki sayı asla sıfıra inemiyor. Aynı şekilde `:701`'de yazılan “Ayarlardan bir sağlayıcı seçtiğinde bu görüşme yeniden çözümlenebilir” vaadini **hiçbir kod yerine getirmiyor**: `CallsAwaitingProcessing` `state IN (0,1)` bakıyor. Yeni modelde o çağrının çözümleme aşaması `Pending` + `failure_code='no_llm'` olur; sağlayıcı yapılandırıldığında tek sorgu ile `Queued`'a çekilir.

---

## 3. Parça parça gönderme

### 3.1 Nerede duruyor, nasıl devam ediyor

Bugün plan da devam önbelleği de yalnızca Python'da: `plan_chunks` tek üretim çağıranı `cloud_engine.py:123`, önbellek `{wav}.cloudparts/{model}-{index}-{total}.json` (`:162, :175`). C# hiçbirini göremiyor.

Yeni akış:

1. C# ilk çalıştırmada Python'dan planı ister; Python `{"type":"plan","stream":"mic","chunks":[{index,start,end},...]}` yayar.
2. C# her giriş için bir `call_part` satırı yazar. **O çağrı için plan bir daha hesaplanmaz.**
3. Sonraki her denemede istek, sabitlenmiş sınırları **ve** C#'ın zaten tuttuğu cevapları taşır.
4. Python cevabı olan parçaları atlar; kalanları yapar; her parça için `{"type":"part", ...}` yayar.
5. Bir parça kalıcı olarak başarısız olursa **koşu iptal edilmez**; parça `failed` işaretlenir, sıradakine geçilir. `401` gelirse o endpoint'teki kalan parçalar aynı sebeple kısa devre yapılır (anahtarı beş kez daha denemek boşuna).
6. `merge_streams` yine tam çift üzerinde **bir kez** çalışır — echo bastırma ve difflib mantığını C#'a taşımak test edilmiş Python'u ikinci bir dilde kopyalamak olur.

**Yeniden birleştirme sırası:** `_to_segments(payload, chunk.start_seconds)` her zaman damgasına parçanın başlangıcını ekliyor, `resegment_on_gaps` mutlak zamanlarla çalışıyor. Yani parçalar mutlak zamana göre birleştirilir, indekse göre değil.

### 3.2 Sınırlar sabitlenir ve **doğrulanır** — bu maddeyi atlamayın

`input_fingerprint` (dosya yolu, boyut, model, endpoint) `plan_chunks`'ı kapsamaz. `chunking.py`'deki `QUIET_ENOUGH_RATIO` veya `SEARCH_WINDOW_SECONDS` bir güncellemede ayarlanırsa — ki `_quietest_near`'ın yorumu bunun bir kez zaten yapıldığını kaydediyor — parça **sayısı** değişmez, sınırlar 20 saniyeye kadar kayar. Saklanmış cevap yeni ofsetle uygulanır ve o parçadaki **her alıntı 20 saniyeye kadar yanlış yere damgalanır**. Alıntıya tıklandığında yanlış ses çalar. `QuoteVerifier` bunu yakalayamaz: kelimeler gerçek, yalnızca konum yanlış — ve konum, ürünün güvenilmesini istediği şeyin ta kendisi.

Bu yüzden: worker sabitlenmiş `start_ms`/`end_ms`'i geri alır, **yeni hesapladığı planla 1 ms toleransla karşılaştırır**, uyuşmazsa parçayı reddeder ve `plan_mismatch` bildirir. Dört satır kod, sessiz zaman çizelgesi bozulmasıyla görünür bir hata arasındaki fark.

### 3.3 Üç sessiz veri kaybı — hepsi bu bölümde kapanıyor

**(a) Yarım kalmış dilim tam sanılıyor.** `_chunk_segments` (`cloud_engine.py:186-189`) `if not os.path.exists(source): slice_wav(...)` yapıyor. `slice_wav` (`chunking.py:174-205`) hedefi doğrudan `wave.open(target,"wb")` ile açıyor — geçici dosya yok, atomik yeniden adlandırma yok. CPython her `writeframes` çağrısında RIFF başlığını yamalıyor, yani süreç dilimlemenin ortasında öldürülürse **geçerli ama kısa bir WAV** kalıyor. Sonraki denemede `os.path.exists` doğru döner, 8 dakikalık dosya yüklenir, cevabı o parçanın nihai cevabı olarak önbelleğe alınır, iş başarıyla biter — ve konuşmanın 12 dakikası, hiçbir şeyin eksik demediği bir transkriptten kalıcı olarak yok olur.
→ **Düzeltme:** `slice_wav` `f"{target}.partial"`'a yazar ve `os.replace` eder; `_chunk_segments` bayat `.partial`'ı önce siler.

**(b) İki akış aynı kazıma dosyasını paylaşabilir.** `cmd_transcribe` `engine.transcribe(mic_path)` ve `engine.transcribe(far_path)` çağrılarını **aynı süreçte aynı engine örneğiyle** yapıyor (`__main__.py:200-218`). Bugün çakışma imkânsız çünkü çalışma alanı WAV yolundan türetiliyor. Çalışma alanını iş başına bir dizine taşırsak (ki taşımalıyız, §3.5) ve dosya adları `part{index}.wav` kalırsa: devam sırasında mic'in 0. parçası önbellekten dönerken temizlik dalını (`:202-207`) atlar, dosyası kalır; far geçişi `os.path.exists` ile **mic'in dilimini bulur** ve onu transkribe eder. `merge_streams` far listesindeki her şeye koşulsuz `Speaker.THEM` damgalar. Kullanıcının kendi sesi, veritabanına **karşı tarafın konuşması olarak** yazılır, `QuoteVerifier` doğrular (kelimeler gerçek), ve `ExtractionPrompt` onu `KARSI:` diye etiketler.
→ **Düzeltme:** kazıma yolu akışı taşır: `work_dir/{stream}/part{index}.wav`.

**(c) Boş transkript iyi transkripti siliyor.** `ReplaceSegments` (`Repository.cs:524`) önce siler sonra verileni yazar. Boş sonuç zincirin her yerinde **başarı**: `_to_segments` boş payload için `[]` döner, `merge_streams([],[])` boş transkript döner, `cmd_transcribe` normal `result` yayar. `TranscribeAsync` (`CallOrchestrator.cs:935`) sayı kontrolü yapmadan yazıyor ve `:956`'da `Transcribed` işaretliyor. Yeni ekran “transkripti tekrar dene”yi 200 çağrılık bir listede satır başına düğme yaptığı an, `200 {"text":""}` cevabı veren bir vekil sunucu **tek tıkla 700 satırlık bir transkripti yok eder**.
→ **Düzeltme:** `ReplaceSegments`'a `allowEmpty` parametresi (varsayılan `false`, boşa düşürmeyi reddeder), ve `TranscribeAsync`'te yazmadan önce: mevcut segment varsa ve sonuç boşsa `WorkerException("empty_transcript")`. Gerçekten sessiz bir kayıt için ayrı bir kod (`ses bulunamadı`) kullanılır ki ekran servis arızası ima etmesin.

### 3.4 Kalıcı olarak başarısız bir parça: “yarık”

Bir parça her endpoint'te, her denemede kalıcı olarak reddediliyorsa (klasik örnek: 15 dakikalık bekleme müziği Opus'u sıkıştırmıyor, `too_large`), kullanıcı ekrandan **“bu bölümden vazgeç”** diyebilir → `call_part.status = 3 (vazgeçildi)`. Bu, transkriptin kalıcı bir yarığıdır ve **yalnızca insan kararıyla** oluşur; makine bir konuşmanın bir bölümünün eksik olduğunu kendi başına iddia etmez.

**Yarık `segment` tablosuna satır olarak yazılmaz.** İşaretleyici bir segment `segment_fts` tarafından indekslenir, `QuoteVerifier.Locate` tarafından doğrulanabilir (model işaretleyiciyi “alıntılayabilir”) ve `TranscriptChunker`'ın token bütçesini yer. Doğrulamayı geçen sahte kanıt, bu ürünün önlemek için var olduğu şeydir.

Bunun yerine:
- transkript aşaması `Partial` olur;
- **çözümleme yapılır** (Angle 2'nin “yarık varsa çözümleme yok” kuralını reddediyorum: 45 dakikalık bir görüşmenin 15 dakikası müzik diye kalan 30 dakikadan hiç defter kaydı çıkmaması, geri alınamayan bir çıkmaz yaratır), ama **rapor, özet ve her iki dışa aktarıcı yarığı açıkça taşır**;
- **dışa aktarma (Obsidian/Notion) reddedilir** ta ki kullanıcı “eksik olduğunu bilerek dışa aktar” diyene kadar;
- `ContactsViewModel`'in transkript görünümü segmentler arasına yarık satırı koyar.

**Eşleşmemiş akış kuralı (kritik):** Bir akışın parçası eksikse, **diğer akışın o zaman aralığındaki segmentleri de yazılmaz.** Sebep: echo bastırma (`merge.py`) mic segmentini zamanda örtüşen bir far segmentiyle difflib benzerliği ≥ 0.8 ile karşılaştırıyor. Far parçası yoksa karşılaştıracak bir şey yoktur, hoparlörle yapılan bir görüşmede karşı tarafın mikrofona sızan sesi `is_me=1, suspected_echo=false` olarak yazılır, prompt'ta `BEN:` diye etiketlenir ve **karşı tarafın sözü kullanıcının sözü olarak deftere geçer**. Üstelik `likely_no_headphones` oranı kesilmiş küme üzerinde hesaplandığı için temiz rapor verir ve uyarı da bastırılır. Yarık görünür bir eksikliktir; bu görünmez bir ikamedir.

### 3.5 Çalışma alanı ve silme

- Çalışma alanı `{wav}.cloudparts`'tan **uygulamanın adlandırdığı bir `work_dir`**'e taşınır. **`AppPaths.Root` altına körü körüne koymayın** — kök artık taşınabilir (`--data`, `AppSettings.DataRoot`) ve `AppPaths.DetectCloudSync` tam da kökün OneDrive içinde olabileceği için var. Senkronize bir kök altında düz metin transkript parçaları, konuşmanın üçüncü bir dışa aktarımıdır.
- **Yazma sırası (maliyet eleştirmeni haklı):** POST döner → worker cevabı `work_dir`'e yazar → `part` olayı yayılır → C# veritabanına yazar → dosya iş bitince silinir. Python önbelleğini “artık veritabanı var” diye aynı değişiklikte silmek, tek dosya yazımlık bir pencereyi (boru + kanal + SQLite transaction) beş adımlık bir pencereye çevirir; ve o pencerede kaybedilen şey **zaten ödenmiş ve zaten dışa aktarılmış** bir parçadır.
- `Repository.DeleteCall` (`:1030-1057`) `mic_path`, `far_path` ve `ConversationMix` kopyasını siliyor — `work_dir`'i de silecek. Ayrıca **tek seferlik süpürme**: `AppPaths.Recordings` altındaki artık `*.cloudparts` dizinleri. Bugün `shutil.rmtree` yalnızca **her parça başarılı olduğunda** çalışıyor (`cloud_engine.py:143`), yani dizin tam olarak onu üreten başarısızlık durumunda hayatta kalıyor — ve kullanıcı o çağrıyı silmiş olsa bile, konuşmasının kelime zamanlamalı düz metin transkripti sonsuza kadar diskte kalıyor.
- İlgili ve şu an tamamen boşta: `AudioRetentionDays` / `TranscriptRetentionDays` ayarlanabiliyor ama **hiçbir kod okumuyor**. Bu, arayüzün verdiği ve kodun tutmadığı bir gizlilik sözü. Bu planın kapsamında değil, ama sahibi belirlenmeli (§8).

### 3.6 K4 — defter çoğaltması

**Sorun sanılandan büyük.** `AnalysisPipeline.cs:123, 124, 148` çıplak `INSERT` döngüleri ve `Schema.cs`'te bu üç tabloda hiç benzersizlik kısıtı yok. Ama asıl kötüsü: `DeterministicChecks.OverdueCommitments` `CallId = commitment.CallId` üretiyor, `MovedDeadlines`/`ChangedAmounts`/`AdjudicateAsync` da öyle — girdileri `GetOpenCommitments(contactId)` ve `GetAllClaims(contactId)`, yani **o kişinin bütün çağrıları**. Ayşe'nin 3, 7, 9 numaralı çağrıları varsa, 9'u çözümlemek `call_id = 3` ile damgalı bir bayrak ekler. `DELETE WHERE call_id = @callId` bu satıra **asla ulaşmaz**.

Çözüm — iki kapsam:

| Bayrak türü | Kapsam |
|---|---|
| `EvadedQuestion`(4), `ScamPattern`(6) | çağrıya göre |
| `OverdueCommitment`(0), `MovedDeadline`(1), `ChangedAmount`(2) | **kişiye göre**, yeniden inşa (özetleri “3 gün geçti” gibi hesaplama anına bağlı) |
| `Contradiction`(3) | kişiye göre, **ama yalnızca adjudication taşıma hatası olmadan tamamlandıysa** — `AdjudicateAsync` `LlmException`'ı yutup devam ediyor, körü körüne yeniden inşa bir LLM kesintisinde sağlıklı bir koşunun bulduğu çelişkileri siler |
| `PressureTactic`(5) | **hiçbir kapsamda silinmez** — boru hattı bunu üretmiyor, yalnızca `SampleData` üretiyor ve o kullanıcının canlı tablolarına yazıyor |

**Kimlik anahtarları** (Angle 1'in önerdiği daha dar anahtarlar gerçek kanıt siler, aşağıda):

```
commitment: (call_id, by_me, quote_start_ms, obligation)
claim:      (call_id, by_me, quote_start_ms, entity, attribute, value)
flag:       (call_id, kind, quote_start_ms,
             COALESCE(counter_call_id,-1), COALESCE(counter_quote_start_ms,-1), summary)
```

- `obligation` anahtarda, çünkü tek alıntı iki söz taşıyabilir (“parayı cuma yollarım, evrakları da pazartesi”).
- `value` anahtarda, çünkü `QuoteVerifier.Locate` **segmentin** `StartMs`'ini döndürüyor; aynı segmentteki iki iddia `quote_start_ms`'i paylaşır ve `value` olmadan ikincisi reddedilir — ki bu tam olarak `ChangedAmounts`'un hesapladığı kanıttır.
- `summary` bayrak anahtarında, çünkü `ScamPatterns.Scan` her desen için `hits[0].segment`'i alıyor ve desen adı **yalnızca özet metninde** var; `MinimumMatches: 1` olan bir desenle başka bir desen aynı segmentte eşleşirse `(kind, quote_start_ms, quote)` özdeş olur ve iki dolandırıcılık tespitinden biri sessizce kaybolur — hem de dolandırıcılık için işaretlenen bir görüşmede.
- `COALESCE` şart: SQLite NULL'ları farklı sayar, yani nullable sayaç sütunları üzerindeki düz indeks tek alıntılı bayrakları (Overdue, EvadedQuestion, ScamPattern) hiç kısıtlamaz — K4'ün en çok çoğalttığı satırlar.

**Yazma yolu:** `ReplaceCallFindings(callId, contactId, ..., authoritative)`, tek transaction. Önce mevcut satırların kullanıcı kararlarını (`dismissed_by_user`, `status`, `fulfilled_by_call_id`) anahtara göre okur, siler, yeniden yazar, kararları anahtar eşleşmesinde taşır. `contactId` **transaction içinde `call`'dan yeniden okunur**, `:70`'te yüklenen bellekteki `Call` görüntüsünden değil.

`authoritative` koruması — **dört tasarımın en iyi fikri, yalnızca Angle 2'de var:** silme yalnızca her parça cevap taşıyorsa yapılır. Göç sırasında doldurulmuş eski bir çağrının (Done ama cevapsız) tek parçasını tekrar denemek, aksi hâlde defteri o tek parçadan yeniden inşa edip önceki sürümün yazdığı her şeyi silerdi.

**Benzersiz indeksler yalnızca ağ olarak** kurulur (`WHERE dedupe_key IS NOT NULL` kısmi indeks). Not: `Repository`'de `Database.Fts5Available` dışında hiç `catch (SqliteException)` yok, yani bir kısıt ihlali bir çağrının işlenmesini öldüren yakalanmamış bir istisna olur — sessiz bir kopyayı kaybolmuş bir konuşmayla takas etmek istemiyoruz. Asıl düzeltme sil-önce-yaz; indeks mantık hatasını sessiz kopya yerine gürültülü hataya çevirir.

**Zaten oluşmuş kopyaların temizliği** (göç adımı, sürüme bağlı, yedekten sonra, tek sefer):

```sql
-- Hayatta kalan: en düşük id. Ama kullanıcının dokunduğu HERHANGİ bir kopyadaki
-- karar ona taşınır -- silinmiş bir uyarıyı geri getirmek, kullanıcının zaten
-- hallettiği bir yanlış pozitifi yeniden üretmektir.
UPDATE commitment SET dismissed_by_user = (SELECT MAX(d.dismissed_by_user) FROM commitment d WHERE <anahtar>)
 WHERE id IN (SELECT MIN(id) FROM commitment GROUP BY <anahtar> HAVING COUNT(*) > 1);
DELETE FROM commitment WHERE id NOT IN (SELECT MIN(id) FROM commitment GROUP BY <anahtar>);
```

`claim`'de kullanıcı sütunu yok, sadece `DELETE`. `flag`'de `dismissed_by_user` ve `created_at` korunur (defter `created_at DESC` sıralı).

---

## 4. İşlem durumu ekranı

Tek ekran: **“İşlemler”**, gezinme çubuğunda “Durum”dan hemen önce. Üst kart “şu anda ne oluyor”u (istek A), liste “ne oldu ve ne eksik”i (istek B) yanıtlar.

### 4.1 Ne gösterir

**Üst kart (`HeroCard`):** Sırada / Çalışıyor / Başarısız / Metni yok. Altında, yalnızca bir şey çalışırken, **belirli** bir ilerleme çubuğu ve tek satır: `Ayşe · yazıya dökülüyor · 3/7 bölüm · %41 · ~4 dk`.

**Süzgeçler (`ChipButton`, `GroupName="IslemFiltresi"`):** Hepsi · Çalışıyor · Sırada · Başarısız · Metni yok · Çözümlenmemiş · Eksik bölümlü.

**Satır (`HoverCard`):** onay kutusu · avatar · isim + `RelativeDate · Length · AppName` · **iki aşama rozeti** · motor rozeti · eylemler.

- **Metin rozeti:** `Sırada (3.)` / `%41 · 3/7` / `Hazır` / `Eksik bölümlü` / `Başarısız` / `Atlandı`
- **Çözümleme rozeti:** aynı sözlük + `Eski metne ait` (transkript yeniden yazıldıysa) + `Yapay zekâ servisi yok`
- Başarısız rozetin altında ikinci satır: `FailureText.Summarise(failure_detail)` — asla ham traceback
- `inferred=1` ise: `Hangi adımda başarısız olduğu kaydedilmemiş`
- **Motor rozeti:** motor adı + `Bu makinede` (yeşil) / `Buluta gönderildi` (uyarı rengi) / `Hangi servise gittiği kaydedilmemiş` (`off_machine` NULL). NULL asla “yerel” diye okunmayacak.

### 4.2 Eylemler — S2 düzeltmesi burada

Her düğme uygulanabilir olduğunda görünür ve **maliyetini söyler**:

| Düğme | Ne yapar | Etiket / ipucu |
|---|---|---|
| `Çözümlemeyi tekrar yap` | yalnızca çözümleme aşamasını `Queued` yapar | “Mevcut metin yeniden kullanılır, ses tekrar gönderilmez.” |
| `Yeniden yükle · ~6 parça` | transkript aşaması; **parça sayısı düğmenin üstünde**, ipucunda değil | onay ister |
| `Eksik bölümü tekrar dene` | yalnızca o `call_part` | |
| `Bu bölümden vazgeç` | parçayı `vazgeçildi` yapar → yarık | onay ister, geri alınabilir |
| `Sıradan çıkar` | `Queued` → `Pending` (kalıcı) | |
| `Baştan yap` | plan + parça satırlarını siler | **onay**, ikon komşuluğu değil |
| `Metni aç` / `Sesi çal` | `ShellViewModel.OpenContact(contactId, callId)` | etiketsiz çağrıda pasif: “Önce isimlendir” |

Aşama bazlı tekrarın kilit kuralı: `ProcessAsync` **yalnızca `Queued` olan aşamayı çalıştırır.** Böylece S2, bir dal değil, tek bir kuraldan çıkan sonuç olur — ve talep diskte durduğu için çökmeden 5 saniye önce istenen tekrar, yeniden başlatmadan sonra da gerçekleşir.

### 4.3 Toplu eylemler ve pahalı koşu koruması

Bugünkü hâl kabul edilemez: `OverviewViewModel.RequeueFailed` (`:123-132`) `FailedCalls(limit: 100)`'ü tek tek `Queued` yapıyor, `MainWindow` `ProcessBacklogAsync()` çağırıyor, `ProcessAsync` transkripsiyonu koşulsuz çalıştırıyor. Bulut modunda bu **tek tıkla 100 konuşmanın yüklenmesi**, geri bildirim ise olay bittikten sonra bir snackbar. `HealthViewModel`'in kopyası daha da kötü: 100 çağrıyı kuyruğa alıyor ve `ProcessBacklogAsync`'i **hiç çağırmıyor**, yani görünür hiçbir şey olmuyor ve 100 yükleme bir sonraki açılışta gözetimsiz başlıyor.

Yerine:

- Her iki blanket düğme kaldırılır; Genel Bakış ve Durum kartları **bu ekrana bağlantı** olur.
- Toplu tekrar **seçim + fiyatlı onay** ister: “12 görüşme · toplam 4 sa 20 dk · **en fazla ~86 parça** · Groq'a yüklenecek. Devam?” Sayı bugünkü verilerle hesaplanabilir: `2 × ceil(duration_ms/1000/1200)` (`call.duration_ms` şemada, `MAX_CHUNK_SECONDS=1200`), eksi zaten önbellekte olan parçalar. Nokta tahmin değil **tavan** olarak söylenir.
- Varsayılan seçim **başarısız olan aşamadır**. Çözümleme düzeltmek için asla yeniden transkripsiyon yapılmaz.
- **`Partial` durumu `FailedCalls()`'a düşmez.** Aksi hâlde mükemmel bir transkripte sahip bir çağrı ana sayfadaki en görünür düğmenin arkasına düşer ve o düğme sesi yeniden yükler.

### 4.4 Türkçe anahtarlar

Yeni metinler `strings.tr.json` / `strings.en.json` içine (format: `"sayfa.turkce-slug"`; `LocalisationTests` iki dosyanın aynı anahtarları taşımasını zorunlu kılıyor):

```
mainwindow.islemler
islemlerpage.baslik                         islemlerpage.aciklama
islemlerpage.sirada                         islemlerpage.calisiyor
islemlerpage.basarisiz                      islemlerpage.metni-yok
islemlerpage.cozumlenmemis                  islemlerpage.eksik-bolumlu
islemlerpage.hepsi
islemlerpage.metin                          islemlerpage.cozumleme
islemlerpage.hazir                          islemlerpage.atlandi
islemlerpage.bolum-x-y                      islemlerpage.eski-metne-ait
islemlerpage.hangi-adim-kaydedilmemis
islemlerpage.bu-makinede                    islemlerpage.buluta-gonderildi
islemlerpage.hangi-servise-gittigi-kaydedilmemis
islemlerpage.cozumlemeyi-tekrar-yap         islemlerpage.mevcut-metin-yeniden-kullanilir
islemlerpage.yeniden-yukle                  islemlerpage.ses-tekrar-gonderilir-ucretlendirilir
islemlerpage.eksik-bolumu-tekrar-dene       islemlerpage.bu-bolumden-vazgec
islemlerpage.siradan-cikar                  islemlerpage.bastan-yap
islemlerpage.metni-ac                       islemlerpage.sesi-cal
islemlerpage.once-isimlendir
islemlerpage.toplu-tekrar-onayi             islemlerpage.en-fazla-parca
islemlerpage.n-dakikadir-ilerleme-yok       islemlerpage.n-dakika-sonra-tekrar-denenecek
islemlerpage.zaten-yuklenmisti              islemlerpage.n-servis-deneniyor
islemlerpage.bos-liste-baslik               islemlerpage.bos-liste-aciklama
```

### 4.5 Kabuğa bağlama (dört eşgüdümlü düzenleme)

1. `ShellPage` enum'una `Islemler` (`ShellViewModel.cs:10-35`, `Health`'ten önce)
2. `ShellViewModel`'de özellik + kurucu + `RefreshAll`
3. `MainWindow.xaml` gezinme `RadioButton`'ı (`CommandParameter="Islemler"`)
4. `MainWindow.xaml` sayfa barındırıcı + `PageVisibility` MultiBinding

Ayrıca `WindowSmokeTests`'e eklenmeli — bu, hatalı bir `Symbol="Foo24"` veya yeniden adlandırılmış `StaticResource` anahtarını yakalayan **tek** kontrol; ikisi de sorunsuz derlenir ve pencere yüklenirken patlar.

---

## 5. Canlı ilerleme

### 5.1 Üç `progress: null` → ekran

Üç hız, birbirini beklemez:

| | hız | iş parçacığı | olay başına maliyet |
|---|---|---|---|
| **Bildir** | sınırsız | işin bulunduğu iş parçacığı | bir sözlük araması + bir `Volatile.Write` (değişmez kayıt). Kilit yok, await yok, dosya yok, Dispatcher yok. |
| **Yayınla** | 4 Hz, yalnızca bir şey çalışırken | UI, **çekerek** | bir sürüm sayacı karşılaştırması |
| **Kalıcılaştır** | kilometre taşları (~2 yazma/dk) | özel arka plan görevi, `Channel` üzerinden | bildirim yolunda sadece `TryWrite` |

Bağlanacak yerler: `CallOrchestrator.cs:884` (bulut STT), `:933` (yerel STT), `:974` (çözümleme).

### 5.2 `System.Progress<T>` **kullanılmayacak**

`Progress<T>` her raporu kurulduğu andaki `SynchronizationContext`'e post eder. UI iş parçacığında kurulursa tek bir çağrı için binlerce dispatcher mesajı olur — etiket penceresinin de kullandığı mesaj kuyruğuna. Arka planda kurulursa bağlam yoktur, sırasız iş öğelerine dağılır ve **ilerleme sırasız gelir**. `PythonWorkerHost.RunAsync` olayları zaten kanaldan **eşzamanlı ve sırayla** teslim ediyor; bu sıra garantisini atmayalım. El yazımı `IProgress<T>`, tek satır: hub'a ilet.

### 5.3 Kısma — üç yerde, üç ayrı sebeple

1. **Python'da (`__main__.py`).** `faster_whisper_engine` her çözülen segmentte `progress(...)` çağırıyor — uzun bir çağrıda sürekli 10-20/sn, her biri bir `json.dumps` + flush + boru yazımı, hem de transkripsiyon döngüsünün üstünde. Kapı: 250 ms geçmediyse **ve** aşama/adım/parça değişmediyse **ve** yüzde <1.0 kımıldadıysa at. Aşama/adım/parça değişiminde **her zaman** yay; nihai raporu **her zaman** yay.
2. **Hub'da.** Rapor başına olay yayılmaz; çağrı başına son-yazan-kazanır. Kapı bozulsa bile UI'ye sıfır iş üretilir.
3. **UI'de.** `ShellViewModel`'in sahip olduğu tek `DispatcherTimer(250 ms)`, **çeker**, ve `hub.ActiveCount > 0` iken başlar, biterken durur. Haftalarca tepside duran bir uygulamada sonsuza kadar tıkırdayan 4 Hz'lik bir zamanlayıcı, kimsenin bildirmeyeceği ve kimsenin bulamayacağı bir pil hatasıdır.

`AppLog.Write` bir kilit alıp tamponsuz `File.AppendAllText` yapıyor — rapor yolunda **asla** olmayacak. Günlüğe: aşama başlangıcı 1 satır, parça sınırı başına 1, çalışırken 60 sn'de 1, aşama sonu 1. Bir saatlik iş için ~60 satır, 20.000 değil.

### 5.4 Gizlilik — ilerleme kaydında **hiç `string` özelliği olmayacak**

Bu, dört tasarımın en iyi gizlilik fikri ve aynen alınıyor. `JobProgress` üzerinde tek bir `string` özelliği yok; aşama/adım/şerit birer enum, gerisi `int`/`double`/zaman. Türkçe metin kenarda `ProgressText.Describe(JobProgress)` ile üretilir. Bir **yansıma testi** bunu doğrular, böylece kural gelecekteki düzenlemelerde yaşar.

Telde `step` bir dize olarak gelir ama **kapalı bir `switch`**'ten geçer; tanınmayan her şey `ProgressStep.None` olur. Yani hatalı veya kötü niyetli bir motor UI'ye veya günlüğe metin enjekte edemez.

**Neden bu kadar sıkı, teoriyle değil bu depodaki zincirle:** `cloud_engine.py:309` üçüncü bir tarafın HTTP gövdesinden 400 bayta kadarını `EngineError.message`'a koyuyor → `WorkerException.Message` → `CallOrchestrator` `failures.Add($"{endpoint.ResolvedName}: {e.Message}")` → toplu fırlatma → `:746` `SetCallState(Failed, e.Message)` ve `Notice?.Invoke` → `App.xaml.cs` `Orchestrator.Notice += (_, m) => AppLog.Write("kayıt", m)`. `AppLog` dosyanın kendi içine şunu yazıyor: *“Bu dosya paylaşılmak üzere yazılır: konuşma metni, kişi adı ve API anahtarı içermez.”* Rastgele bir sağlayıcı gövdesi oraya harfiyen yazılıyor. Serbest metinli bir ilerleme kanalı aynı kadere bir refactor uzaklıkta.

Düzeltmeler: `failure_code` (kapalı sözcük) günlüğe gidebilir; `failure_detail` (gövde) **yalnızca veritabanı ve ekran** — veritabanı zaten transkript tutuyor, günlük ise yabancıya gönderilecek dosya. Ayrıca `__main__.py`'nin `traceback.format_exc()`'ı stderr'e yazması (`PythonWorkerHost` her stderr satırını `AppLog`'a iletiyor) **yorum satırıyla değil kodla** düzeltilecek: istisna tipi + çerçeve listesi, `str(exc)` yok.

Ayrıca **`cloud_engine.py`'nin `_compress`'i, PyAV yoksa veya kodlayıcı patlarsa ham WAV döndürüyor** ve `_post` yalnızca 24 MB sınırına bakıyor. 12,5 dakikadan kısa her parça (her çağrının son parçası, kısa çağrıların tüm parçaları) **~16 kat baytla, sessizce** yükleniyor. Bu artık uyarı yayacak ve ekranda “sıkıştırma çalışmıyor, ham ses yükleniyor” diye görünecek.

### 5.5 Tepsiye küçültünce / kapanınca

- **Tepsiye küçültme hiçbir şeyi durdurmaz, hiçbir şey kaybolmaz.** Hub süreçte yaşıyor; pencere açılınca çekme zamanlayıcısı yeniden başlar ve güncel durum zaten oradadır.
- **Süreç ölünce** hub kaybolur — ki doğrudur, iş de ölmüştür (`JobObject` `KILL_ON_JOB_CLOSE`). Diskte kalanlar: hangi aşama, hangi deneme, ne zaman başladı, ilerleme en son ne zaman kımıldadı, kaç parça bitti. Parça içi yüzde, Python tekrar rapor eder etmez saniyeler içinde yeniden hesaplanır.
- **`parts_done` bir su seviyesidir: asla sıfırlanmaz.** Angle 4 bunu `BeginStage`'de sıfırlıyordu. Sonuç: 4 parçadan 3'ü biten bir iş öldürülür, ekran doğru şekilde “3'ü tamamlanmıştı” der; iş yeniden talep edilir, sayaç sıfırlanır, uygulama Python bir şey raporlamadan tekrar öldürülür — ve ekran artık hiçbir şey yapılmadığını söyler. Kullanıcı, üç adet ödenmiş ve dışa aktarılmış parçası olan bir iş için sağlayıcı değiştirir ve o üç parçayı ikinci bir şirkete boşuna yükler.
- `LastMovedAt` sayesinde ekran asıl gereken şeyi söyleyebilir: **“4 dakikadır ilerleme yok.”** Bu, K3'ün belirtisinin olurken görünmesidir; üç yeniden başlatma sonra sessizce kuyruğa dönmüş bir çağrı olarak keşfedilmesi değil.

### 5.6 A1 — yeni yeri

A1 tespit iş parçacığında düzeltildi, ama davetsiz misafir **bir iş parçacığı yana taşındı**: `FinishRecordingAsync` (`:494`) `recorder.Stop()`, `CompleteCall` (SQLite yazma, `busy_timeout=5000`), `SetCallState` ve `GetCall`'u **`WorkAsync` tüketicisinde eşzamanlı** çalıştırıyor — bir sonraki çağrının `Started` olayını işleyen aynı görev. `CompleteCall`'da 5 saniyelik bir `SQLITE_BUSY`, bir sonraki `BeginRecordingAsync`'i 5 saniye geciktirir. Bu planın her parçası o veritabanına yazar ekliyor (ikinci şerit, dakikada ~2 heartbeat), yani pencere uzuyor.

→ Veritabanı yazmaları o görevden çıkarılacak (veya ölçülüp sınırlanacak), ve **bunun için bir test yazılacak** (§7). Bu, plandaki ilk kod değişikliklerinden biri, sona bırakılan bir iyileştirme değil.

---

## 6. Sıra

Ön koşullar zincirleme. **Kuyruk, kullanıcının asıl istediği şey (şerit ayrımı) sonda** — çünkü ondan önceki her madde, şeritler olmadan çalışan bir çift ödeme hatasını engelliyor.

| # | İş | Boyut | Ön koşul | Not |
|---|---|---|---|---|
| 0 | Çalışma dizinindeki 28 dosyayı commit'le; dört tasarımdaki bütün `CallOrchestrator` satır numaralarını yeniden türet | S | — | A1 ve K3 zaten düzeltilmiş; onları “düzeltmeye” çalışan adımlar silinecek |
| 1 | `FinishRecordingAsync`'in DB yazmalarını `WorkAsync`'ten çıkar + gecikme testi | M | 0 | **A1'den sonra gelmeli**; bütün ölçümlerin zemini |
| 2 | Sürüme bağlı göç makinesi + `VACUUM INTO` yedeği + `AddColumnIfMissing`; şema değişikliği **yok** | S | 0 | Boş olarak indir, idempotanlığını kanıtla |
| 3 | `call_stage` + enum + `Repository` aşama API'si + türetilmiş `call.state`; `SetCallState` **silinir** | L | 2 | S1 |
| 4 | Göç: eski satırların doldurulması (`inferred=1` dahil), `ended_at` normalizasyonu, `call_part` eski kayıt satırları | M | 3 | §1.4 |
| 5 | **Tek sahiplik**: atomik `ClaimNext`; dört giriş noktası da buradan | M | 3 | **Şerit ayrımından önce olmalı** |
| 6 | `ReplaceCallFindings` + kimlik anahtarları + iki kapsam + `authoritative`; kopya temizliği göç adımı | L | 2, 3 | K4 |
| 7 | Boş transkript koruması; atomik `slice_wav`; akış bazlı kazıma dizini | S | — | Üçü de bağımsız, üçü de veri kaybı; **hemen yapılabilir** |
| 8 | Worker protokolü: kapalı `step` enum'u, `plan` ve `chunk` olayları, `completed_parts`, `work_dir`, `retry_after`; `failure_code`/`failure_detail` ayrımı; stderr redaksiyonu | L | 7 | §3, §5.4 |
| 9 | `call_part` + `call_part_answer` + devam etme + sınır doğrulaması + kısmi transkript + eşleşmemiş akış kuralı | L | 3, 8 | §3 |
| 10 | `.cloudparts` süpürmesi + `DeleteCall`'a `work_dir` | S | 9 | Gizlilik, gecikmemeli |
| 11 | Başarısızlık sınıflandırması + yükleme bütçesi + `Retry-After` saygısı + kesinti/başarısızlık ayrımı | M | 3, 8 | §2.4, §2.5 |
| 12 | `JobProgressHub` + el yazımı `IProgress` + üç `progress: null` bağlanması + çekme zamanlayıcısı + yansıma testi | M | 3, 8 | §5 |
| 13 | **İşlemler ekranı** + kabuk bağlantısı + string'ler + Genel Bakış/Durum kartlarının yönlendirilmesi | L | 3, 9, 12 | §4 |
| 14 | **Şerit ayrımı**: yerel(1)/uzak(2), `GpuLease` paylaşımı, cooldown yalnızca yerel şeritte, endpoint kapısı | M | 5, 11, 13 | Asıl istek (A) |
| 15 | `PendingWorkCount` + “LLM yok” çağrılarının kuyruğa dönüş yolu | S | 3 | |

Kabaca: 4 küçük, 6 orta, 5 büyük. 3, 6, 9, 13 en ağırları.

---

## 7. Testler

**Göç ve şema**
- `V2VeritabaniAsamaSatirlariniDogruUretir` — her eski `state` için bir çağrı; `Failed`'ın **ikisi**: biri segmentli biri segmentsiz. Segmentli olan `(Done, Failed, inferred=1)`, segmentsiz `(Failed, Pending)`; `Transcribing` → `Queued`.
- `MigrateUcKezCalisirVeHicbirSeyDegismez` — `call_stage`, `call_part`, `commitment`, `claim`, `flag` sayıları bayt bayt aynı.
- `SchemaStatementsIcindeDELETEveUPDATEYok` — kaynak seviyesinde iddia. Kaba, ama bu kuralı anlayanlar gittikten sonra ayakta tutan tek şey.
- `GocOncesiYedekWALIcindekiYazilariDaIcerir` — checkpoint'siz kapatıp yaz, göç et, `.pre-v3`'ü aç, satırların orada olduğunu doğrula. `File.Copy` bu testten kalır.
- `EskiCagrilarYenidenYuklenmez` — göçten sonra `TranscriptIsComplete` doğru; ilk kuyruk taraması arşivi buluta göndermez. **Fatura koruması.**

**Kuyruk ve sahiplik**
- `IkiKuyrukTaramasiAyniCagriyiBirKezIsler` — sahte worker `TranscribeAsync` sayar. Bugün başarısız oluyor.
- `TekrarDeneyeBesKezBasmakTekKosuBaslatir`.
- `ClaimNextAltiIsParcacigiAltindaAtomik` — her aşama tam bir kez talep edilir. Gerçek SQL ile, C# kilidiyle değil.
- `TemizKapanisDenemeHarcamaz` — `Dispose` ile iptal, yeniden başlat, `attempts` değişmemiş, `interruptions` artmış; sert öldürmede tersi. **Sıradan bir kapanış uzun bir görüşmeyi öldürmemeli.**
- `ZamanAsimiGorunurBirBasarisizligaDonusur` — iptal edilmemiş token ile `TaskCanceledException`; aşama `Failed`, kodu var, ve hiçbir noktada nedeni NULL olan bir `Queued`'a dönmüyor.
- `IlkEndpointtekiAuthIkincisiniYineDe Dener` — sonra ikisi de `auth`: iş `Yapılandırma` sınıfıyla terminal.
- `SurekliHizLimitiParcaBasinaUcYuklemeyiAsmaz` — 6 parça, sonsuz 429; toplam POST ≤ 18.
- `RetryAfter3600SaniyeBeklenirYenidenBaslatmaSonrasiDa` — tek POST, `next_attempt_at` bir saat sonra, simüle edilmiş yeniden başlatma erken denemez.

**Parça, devam, transkript bütünlüğü**
- `ParcaninOrtasindaOldurulenIsKaldiginYerdenDevamEder` — 4 parçalı iş, 3'ünden sonra öldür, devam et: yalnızca 4. parça POST edilir; parça 0-2 için POST çağrılırsa test patlar.
- `YarimKalmisDilimYenidenKullanilmaz` — plan 20 dk beklerken 6 dk'lık `part1.wav` bırak; `slice_wav` yeniden çağrılmalı. Ayrıca `writeframes` ortasında patlat: yalnızca `.partial` kalır.
- `MicVeFarAyniKazimaDosyasiniPaylasmaz` — mic 0. parçası önbellekten dönerken (temizliği atlayan dal) far 0. parçasının farklı bayt aralığı yüklediğini doğrula.
- `TasinmisSinirlarliKayitliCevapReddedilir` — kayıtlı `start_ms` 1200000 iken plan 1185000 dönerse parça reddedilir ve yeniden yüklenir, sessizce yeni ofsetle uygulanmaz.
- `BosSonucIyiTranskriptiSilmez` — 700 segmentli çağrıya sıfır segmentli normal `result`; 700 segment yerinde, aşama `Failed`. Segmentsiz çağrıda boş sonuç sorunsuz kabul edilir.
- `KaliciBasarisizParcaTranskripttteGorunur` — vazgeçilmiş parça: aşama `Partial`, `FailedCalls()` onu döndürmez, Obsidian dışa aktarımı yarığı zaman sırasında gösterir, çözümleme çalışır ama rapor ve özet yarığı taşır.
- `EslesmemisAkistakiSegmentlerYazilmaz` — far'ın parçası eksikken o penceredeki mic segmenti `is_me=1, suspected_echo=false` olarak yazılmaz ve prompt'a girmez.

**Defter (K4)**
- `AyniCagriyiIkiKezCozumlemekDefteriCogaltmaz` — sayılar aynı, ve anahtar eşleştiğinde satır id'leri korunuyor (böylece bayat listeye yapılan “yok say” tıklaması yine hedefe iner).
- `IkiCagriBirKisiFazladanGecikmisBayragiUretmez` — Ayşe'nin 3, 7, 9'u; 9'u, 7'yi, sonra tekrar 9'u çözümle; taahhüt başına tam bir `OverdueCommitment`. **`WHERE call_id = @callId` bu testten kalır — asıl mesele bu.**
- `YokSayilanBayrakYenidenIslemdeYokSayiliKalir`; `TamamlandiIsaretiKorunur`.
- `TekSegmenttekiIkiDolandiricilikDeseniIkisiDeKalir` — Angle 1'in bayrak anahtarı bu testten kalır.
- `TekSegmenttekiIkiIddiaIkisiDeKalir` — `value` anahtarda olmazsa kalmaz.
- `EskiKayitliCagrininDefteriTekParcaTekrarindaSilinmez` — `authoritative` koruması.
- `TekrarEdenSatirTemizligiKullanicininDokunduguSatiriSilmez` — üç özdeş taahhüt, `dismissed` ikincide, `fulfilled` üçüncüde: ikisi de hayatta.
- `TranskriptDegisinceCozumlemeParcalariGecersizlesir`.

**İş parçacığı, ilerleme, gizlilik**
- `IlerlemeRaporuVeritabaniAcmazVeSynchronizationContexteDokunmaz` — bildiren iş parçacığına patlayan bir `SynchronizationContext` kur, her çağrıda patlayan bir repository sahtesi ver, 1000 olay bildir.
- `EnqueueCagiraniBloklamaz` — sahte aşama uyurken `Enqueue` çok daha kısa sürede döner.
- `BirCagriKaydiBaslarkenCompleteCallYazmaKilidindeBekliyorsaBileGecikmez` — §5.6'nın koruması, A1 değişmezinin yeni yerdeki testi.
- `IlerlemeKaydindaHicStringOzelligiYok` — yansıma. Birisi `Detail` alanı eklediği an patlar; o düzenleme tam da paylaşılan günlüğe alıntı koyacak olan düzenlemedir.
- `SaglayiciHataGovdesiAppLoga Ulasmaz` — sahte worker `WorkerException("api_error", "429: {...GIZLI...}")` fırlatır; `AppLog`'a giden hiçbir şeyde `GIZLI` geçmez. Aynısı stderr traceback yolu için.
- `SaatlikIsIcin125tenAzKaliciYazma` — sayan repository sahtesi ile kilometre taşı sınırı doğrulanır.

**Ekran ve maliyet**
- `TopluTekrarHicbirSeyYuklenmedenOnceMaliyetiSoyler` — onay yanıtlanana kadar sıfır kuyruğa alma; metinde çağrı sayısı, süre ve parça tavanı var. Durum ekranındaki kopya için de aynı test.
- `CozumlemeTekrariTranscribeAsynciCagirmaz` — dokunulursa testi patlatan sahte worker. **S2, iddia olarak.**
- `BekleyenIsSayisiCozumlemeKapaliykenSifiraIner`.
- `IslemlerSayfasiKurulur` — `WindowSmokeTests`.

---

## 8. Riskler ve açık sorular

**Karara bağlananlar**

1. **Yarık varken çözümleme yapılır** (Angle 2'ye karşı), ama rapor/özet/dışa aktarma yarığı taşır ve Obsidian/Notion dışa aktarımı açık onay ister. Gerekçe: 45 dakikalık bir görüşmenin 15 dakikası bekleme müziği diye kalan 30 dakikadan hiç defter kaydı çıkmaması, çıkışı olmayan bir çıkmazdır.
2. **Yeniden transkripsiyon çözümlemeyi otomatik tetiklemez**, `Eski metne ait` işaretler ve teklif eder. Kullanıcının istemediği bir LLM koşusu bulutta para, yerelde zaman demek.
3. **`auth` sınıflandırması toplu hatada yapılır**, tek endpoint'te değil. Yedekleme öldürülmez.
4. **Kesinti asla ölümcül değil.** Tavan istenirse duvar saati (30 gün), sayı değil.
5. **`SetCallState` silinir**, `[Obsolete]` yapılmaz. `TreatWarningsAsErrors` kapalı bir projede uyarı sadece bir öneridir.
6. **`Partial` `FailedCalls()`'a düşmez.** Aksi hâlde en görünür düğme bir faturalama hatasına dönüşür.
7. **Toplu tekrar fiyat söylemeden çalışmaz**, ve fiyat **tavan** olarak söylenir.

**Kaldırılamayan riskler — açıkça söylüyorum**

- **Kopya temizliği göçü gerçek bir insanın arşivinden satır siler ve geri alınamaz.** Azaltmalar: `VACUUM INTO` yedeği, tek transaction, kullanıcının dokunduğu satırı tercih eden koruma, ve elle kurulmuş bir v2 veritabanı üzerinde çalışan test. Yine de bu, plandaki en tehlikeli tek ifade. **Güven yeterli değilse:** yalnızca sütunları ve kısmi indeksleri otomatik uygula, silmeyi Durum ekranındaki kullanıcı tetikli “tekrar eden kayıtları temizle” eylemine ayır. Kısmi indeks (`WHERE dedupe_key IS NOT NULL`) bu sıralamayı yasal kılar.
- **İki şerit + heartbeat yazmaları, bu veritabanının hiç görmediği bir çekişme demek.** `busy_timeout=5000`, `secure_delete=ON`, ve `Repository`'de `Database.Fts5Available` dışında hiç `catch (SqliteException)` yok. `SQLITE_BUSY` `Geçici` sınıfına konarak iş yeniden denenir, ama bu ölçüm gerektirir: uzun bir `ReplaceSegments` sürerken uzak şerit derecesi 2 ile bir dayanıklılık testi, şerit ayrımı **yayınlanmadan önce**.
- **Uzak şerit derecesi 2 bir yargı, ölçüm değil.** Doğru sayı Opus kodlamasının bu makinede ne kadar CPU yediğine ve kayıt sırasında çalışıp çalışmadığına bağlı. Ayar olsun, varsayılan 2, sert tavan 4 — ve gerçek bir görüşme sırasında biri izlesin.
- **`.cloudparts` kaldırılması yükseltme anında uçuşta olan bir işin devamını kaybeder** — bir çağrı bir kez yeniden yüklenir. Kabul edilebilir; sürüm notunda yazılsın, faturada keşfedilmesin.
- **Parça cevaplarını veritabanına almak veritabanını büyütür** — 20 dakikalık parça başına kabaca 150-200 KB. `.cloudparts`'ın düz metin dosyalarından kesinlikle iyi ve planlanan şifrelemenin kapsayacağı yer, ama transkript yazıldıktan sonra transkripsiyon cevapları budanmalı.
- **`inferred=1` bir çıkarım, kayıt değil.** Ekran onu kaydedilmiş bir başarısızlıkla aynı güvenle sunmayacak; rozet bunun için var. Yine de eski arşivde bazı satırlar yanlış aşamayı işaret edecek ve bunu tamamen düzeltmek mümkün değil — eski şema o bilgiyi hiç kaydetmedi.

**Açık sorular (sahibi gereken)**

1. **`AudioRetentionDays` / `TranscriptRetentionDays` hiç uygulanmıyor.** Arayüzün verdiği, kodun tutmadığı bir gizlilik sözü. §3.5'teki `.cloudparts` süpürmesi tek seferlik; genel eksiklik duruyor. Bu planın kapsamında değil — kimin?
2. **Parça önbelleği anahtarında `base_url` yok** (`cloud_engine.py:175`). İki endpoint aynı model adını kullanıyorsa birbirinin cevabını yeniden kullanır. Bu bugün bedava tasarruf; yasaklamak gerçek yükleme maliyeti demek. İki farklı sağlayıcıda aynı model adı anlamlı biçimde farklı mı?
3. **Bir harcama defteri (`cloud_upload(call_id, at, endpoint_id, bytes, seconds)`) olmalı mı?** Tamamen eklemeli, ve “bu bana ne kadara mal oldu, konuşmalarımı kim duydu” sorusunu iş satırları budandıktan sonra da yanıtlanabilir kılar. Kapsamı küçük tutmak için dışarıda bıraktım.
4. **Çözümleme bacağı da aynı bütçeyi hak ediyor mu?** `AdjudicateAsync` `GetAllClaims(contactId)`'den 10 çelişki adayı alıp **başka çağrılardan** birebir alıntılar gönderiyor. Yani tek bir çağrıyı yeniden çözümlemek, on başka görüşmeden alıntı dışa aktarır — ve toplu çözümleme tekrarı bunu arşiv boyunca çarpar. Dört tasarımın hiçbiri bunu fiyatlamıyor.
5. **Yerel transkripsiyon parçalanmalı mı?** `plan_chunks`'ın tek üretim çağıranı bulut motoru. 40 dakikanın 39'unda öldürülen yerel bir iş, 39 dakikalık GPU işini kaybediyor. Para maliyeti yok, dışa aktarım yok — ama §2.5'teki kesinti kuralının en çok cezalandırdığı durum bu. Şema değişmeden destekliyor; açmak tek bir `plan_chunks` çağrısı.
6. **Ölü bir işin ekranda ne kadar “ilk görünen şey” kalması gerekir?** Sekiz ay önce artık geçerli olmayan bir sebeple başarısız olmuş bir çağrı, durum ekranında gürültüdür. “Görüldü ama çözülmedi” gibi bir kavram muhtemelen gerekli ve muhtemelen bir sütun istiyor — şimdi eklemek sonra eklemekten ucuz.