# VoiceTranscript — Sıralı Uygulama Planı

Bu plan, doğrulanmış bulguların tamamını birleştirir (aynı kusurun farklı yerlerden görülmüş hâlleri tek maddede toplanmıştır) ve altı dalgaya böler. Her dalga kendi başına bitirilip sürülebilir. Sıra rastgele değil: önce **ürünün söylediği şeyin doğru olması**, sonra **hangi ekranın var olduğu ve ne işe yaradığı**, sonra **kontrollerin gerçekten çalışması**, en sonda **dil ve görsel sistem**. Kozmetik iş en sona bırakılmıştır çünkü silinecek bir ekranı güzelleştirmek boşa emektir.

---

## Yön veren üç karar

**1. Sihirbaz soru sorar, panolar durum bildirir.** Bugün `SetupWindow` bir sihirbaz değil, kendi kendine çalışan bir kurulum logudur; `HealthPage` de aynı ön koşulları ikinci kez denetler. Bundan sonra: `SetupWindow` yalnızca ilk çalıştırmada açılır ve yalnızca **karar** ister (rıza, yerel mi bulut mu, hangi model, ses testi). "Python var mı, paketler kurulu mu, model indi mi" sorusunun tek adresi **Durum** sayfasıdır. Aynı olgu iki yerde saklanmaz.

**2. Bir olgu ekranda bir kez söylenir.** Genel bakış bugün beş iş birden yapıyor (istatistik panosu, uyarı merkezi, Defter'in kopyası, çağrı arşivi, örnek veri yöneticisi). İki işe indiriliyor: *karar bekleyen ne var* (Dikkat) ve *son ne kaydedildi* (Son görüşmeler).

**3. Tıklanabilir görünen her şey tıklanır.** `HoverCard`, `TimeLink`, `Primary` buton — bunların hepsi kullanıcıya bir söz verir. Söz tutulmayacaksa görsel dil geri alınır.

### Ekran envanteri (sonrası)

| Ekran | Tek işi |
|---|---|
| MainWindow rayı | Durum kartı + seviye ölçerler + 5 gezinme hedefi + Ayarlar |
| Genel bakış | Karar bekleyenler ve son görüşmeler |
| Defter | Sözler, değişenler, dikkat — tek yetkili liste, eylemleriyle |
| Kişiler | Kişi, görüşmeleri, dökümü, oynatıcı |
| Arama | Tam metin arama |
| Durum | Ön koşullar, donanım raporu, yedek/dışa aktarma — kurulumun tek sahibi |
| SetupWindow | Yalnızca ilk çalıştırma kararları |
| SettingsWindow / LabelCallWindow | Değişmiyor |

---

## Dalga 0 — Kaydedilen görüşme veritabanına yazılmıyor

**Neden:** Bu, listedeki en ağır kusurdur ve tek başına ürünün "çalışmıyor" görünmesinin sebebidir. `call` satırı `CallOrchestrator.cs:245`'te kayıt **başlamadan önce** INSERT ediliyor (`duration_ms = 0`, `mic_path = NULL`, `far_path = NULL`) ve bu değerleri hiçbir kod güncellemiyor — `FinishRecordingAsync` yalnızca `SetCallState(callId, Queued)` çağırıyor. Sonuç zinciri: Genel bakış'taki toplam kayıt süresi sonsuza dek "0 dk"; her satırın uzunluğu "00:00"; `PlaybackViewModel.LoadAsync(null, null, TimeSpan.Zero)` erken dönüyor, dolayısıyla **gerçek** her kayıtta dalga formu paneli hiç görünmüyor (yalnızca demo verisinde çalışıyor, kusur bu yüzden fark edilmemiş); `TranscribeAsync` worker'a `MicPath = null` veriyor; ve `Repository.cs:732-734` kişi silerken ses dosyalarını `mic_path IS NOT NULL` koşuluyla aradığı için **kişi silme işlemi WAV dosyalarını diskte bırakıyor** — mahremiyet vaadi tutulmuyor.

**İşler**
1. `src/VoiceTranscript.Core/Storage/Repository.cs` — `SetCallState`'in yanına `CompleteCall(long callId, string? micPath, string? farPath, TimeSpan duration, DateTimeOffset endedAt, string? captureStats = null)` ekle; `mic_path`, `far_path`, `duration_ms`, `ended_at` alanlarını UPDATE etsin.
2. `src/VoiceTranscript.App/Services/CallOrchestrator.cs`, `FinishRecordingAsync` — `CompleteCall(...)` çağrısını **5 saniyeden kısa kayıtları silen `Discard` korumasından sonra**, `SetCallState(..., Queued)` satırından hemen önce koy. Aksi hâlde satır silinmiş dosyaları gösterir.
3. `src/VoiceTranscript.App/ViewModels/OverviewViewModel.cs:71` — `Length` sıfır süreyi artık "00:00" diye göstermesin: `Call.State is Recorded ? "kaydediliyor" : "—"`. Ölçülmemiş bir şey ölçülmüş gibi yazılmaz.
4. `PlaybackViewModel.LoadAsync` — yükleme ve hata durumları (bkz. Dalga 3, madde 3.6) bu dalgada birlikte gidebilir; en azından "ses dosyası bulunamadı" mesajı bu dalgada eklenmeli, çünkü bu düzeltmeden **önce** kaydedilmiş satırlar kalıcı olarak yolsuzdur ve kendini açıklamalıdır.

**Bitti sayılma ölçütü:** Elle başlatılan bir kayıt bittikten sonra Kişiler'de o görüşme seçildiğinde dalga formu ve transport görünüyor, süre gerçek, ve kişi silindiğinde WAV dosyaları diskten gidiyor.

---

## Dalga 1 — Ekran ve bölüm silme (yapısal)

**Neden:** Sahibinin "saçma ekranlar" dediği şeyin somut karşılığı, aynı olguyu iki yerde gösteren yüzeyler. Bu dalga hiçbir yeni özellik eklemez; **yüzey sayısını azaltır**. Ürünü en çok iyileştiren tek hamle budur.

### 1.1 Ön koşul denetimi tek sahibe: Durum sayfası
`SetupViewModel.HardwareReport` (SetupViewModel.cs:176) ile `HealthViewModel.HardwareReport` (HealthViewModel.cs:152) iki ayrı kopyadır ve ortak bir depo yoktur: sihirbazda yapılan çok dakikalı ölçüm Durum'a hiç ulaşmaz, oradaki rapor kartı `NullToCollapsed` yüzünden kapalı kalır ve kullanıcı aynı ölçümü ikinci kez bekler. Üstelik `App.ShowSetup` her açılışta yeni bir `SetupViewModel` kurduğu için log, log dosyası ve rapor her kapanışta çöpe gider.

- Ön koşul durumu (`Steps`, `Log`, `LogFile`, `ShowLog`, `IsBusy`, `Problem`, `IsReady`, `RunAllAsync`, `RefreshAsync`, `ExecuteAsync`) uygulama ömrü boyunca tek örnek olan `HealthViewModel`'e taşınır (`App.OnStartup`'ta zaten bir kez kuruluyor). İstenirse `HealthViewModel.Setup` adlı bir alt view model olarak.
- **`SetupViewModel.HardwareReport` silinir.** Tek kopya `HealthViewModel.HardwareReport` olur.
- Rapor `Path.Combine(App.Paths.Root, "hardware.json")` dosyasına yazılır ve `HealthViewModel` kurucusunda geri okunur. Böylece ölçüm süreç yeniden başlayınca da hayatta kalır ve "her açılışta yeniden ölç" kuralı "kayıtlı rapor yoksa ya da kullanıcı Ölç'e basarsa ölç" olur.
- `HealthPage.xaml`'a, mevcut denetim kartlarının üstüne bir `HeroCard` bandı: adım satırları + günlük + var olan donanım rapor kartı (HealthPage.xaml:229-260 olduğu gibi taşınır). Her adım `Present`/`Working` iken bant tek yeşil satıra çöker: "Kurulum tamam — Python, paketler, model hazır" + "Yeniden denetle".
- **Silinen:** `MainWindow.xaml:223-229` "Kurulum ve testler" ray düğmesi ve `MainWindow.xaml.cs:68-72` `Setup_Click`. Gerekçe: navigasyondan bir tık uzakta, gigabaytlarca indirme başlatan bir düğme ticari bir üründe bulunmaz. Sihirbaza dönüş yolu Durum sayfasındaki "Kurulumu yeniden çalıştır" bağlantısıdır.

### 1.2 Genel bakış'tan "Vadesi geçen sözler" bölümü siliniyor
Bu blok (OverviewPage.xaml:113-153) Defter'in "Vadesi geçti" çipinin **zayıflatılmış kopyasıdır**: Aç, "Tutuldu olarak işaretle" ve Kaldır düğmeleri yok, alıntıdaki zaman damgası tıklanmıyor. Yani ana sayfa "bu söz on bir gün gecikti" diyebiliyor ama hiçbir şey yaptırmıyor; kullanıcı aynı satırı Defter'de tekrar bulmak zorunda. Ayrıca liste sınırsız: `Recent` bilinçli olarak 12 ile sınırlanmışken her vadesi geçmiş söz karta dönüşüyor (~110-140 px), altmış sözde sayfa sekiz bin piksel oluyor ve "Son görüşmeler" ekrandan tamamen çıkıyor.

- **Silinen:** OverviewPage.xaml:113-153'ün tamamı (`GroupHeader`, alt başlık, `ItemsControl ItemsSource="{Binding Overdue}"`). `Overdue` koleksiyonu view model'de kalır — `RebuildAttention` sayıyı okuyor.
- **Silinen:** `OverviewViewModel.RebuildAttention`'daki `string.Join(" · ", Overdue.Take(2).Select(o => o.Line))` detayı. Bu, kırk piksel aşağıda kart olarak zaten basılan iki cümlenin birebir tekrarıydı — "sayfanın kendini tekrarlayarak yer doldurması"nın tam örneği. Yerine özet: `$"En eskisi {oldest} gün gecikmiş · {people} kişi"`.
- `AttentionAction` enum'una `ShowOverdue` eklenir; overdue Dikkat satırı `ActionLabel = "Deftere git"` alır (buton ve görünürlüğü zaten `HasAction`'a bağlı, XAML değişmez).
- **Sayı uzlaştırması:** `Repository.OverdueCommitments` (Repository.cs:372-386) `by_me` sözleri dahil edip koşullu olanları hariç tutuyor; `LedgerViewModel` tam tersini yapıyor. Defter'inki kanonik kabul edilir: sorguya `AND cm.by_me = 0` eklenir, `AND cm.is_conditional = 0` kaldırılır. Aksi hâlde Dikkat satırındaki sayı ile Defter çipindeki sayı birbirini tutmaz ve "Deftere git" sözünü verdiği satırlara gitmez.

### 1.3 Raydaki "Yenile" siliniyor, yenileme sayfalara iniyor
`ShellViewModel.RefreshAll` yalnızca Overview, Ledger ve Contacts'ı yeniliyor. Yani Durum sayfasındayken ray düğmesi hiçbir şey yapmıyor — üstelik o sayfanın kendi çalışan "Hepsini denetle" düğmesinin sekiz piksel yanında duruyor — Arama'da da hiçbir şey yapmıyor. Pencere kromunda duran, beş hedefin üçünde işleyen bir fiil öğretilemez ve güvenilemez.

- **Silinen:** `MainWindow.xaml:239-246` "Yenile" düğmesi; `ShellViewModel.cs:203`'teki `[RelayCommand]` özniteliği (metot kalır, dört yerden çağrılıyor). Ayarlar düğmesindeki artık `Margin="0,0,0,2"` kaldırılır.
- **Rozet düzeltmesi (zorunlu):** `OpenFlagCount` yalnızca `RefreshAll` içinde hesaplanıyor, `LedgerViewModel.Dismiss`/`Fulfil` ise satırı silip sayacı güncellemiyor — bugün bayat rozetin tek çaresi o ray düğmesi. Kurucuda `Ledger.PropertyChanged` dinlenip `OverdueCount`/`FlagCount` değiştikçe `OpenFlagCount` yeniden hesaplanır.
- Yerine sayfa başına aynı görünümde tek düğme (`ui:Button Appearance="Transparent"`, `Padding="8"`, yalnız `ArrowClockwise24` 16px, `ToolTip="Yenile"`): OverviewPage başlığının sağına, LedgerPage çip satırının Auto sütununa, ContactsPage'deki "Kişi ara" kutusunun sağına. Durum ve Arama'ya eklenmez — "Hepsini denetle" ve "Ara" zaten o sayfaların yenilemesidir.

---

## Dalga 2 — İlk çalıştırma: gerçekten sihirbaz

**Neden:** Bugünkü ilk çalıştırma bir müşteriye gösterilebilecek durumda değil: her açılışta geri geliyor, hiçbir şey sormadan gigabaytlarca indiriyor, durdurulamıyor, ilerlemesi ölçülemiyor ve hata çıkarsa hatayı kimseye gönderme yolu yok.

### 2.1 Sihirbaz bir kez açılır (durdurucu)
`App.xaml.cs:130` `!File.Exists(Paths.SettingsFile)` koşuluna bakıyor, ama `settings.json`'ı yazan tek yer `MainWindow.xaml.cs:82` — yani Ayarlar'ı açıp Kaydet'e basmak. Sihirbazın kendisi hiçbir şey yazmıyor, `SetupWindow.Completed` (SetupWindow.xaml.cs:26) ayarlanıp hiç okunmuyor, `ShowDialog()`'un dönüşü atılıyor. Sonuç: müşteri kurulumu tamamlıyor, "Bitir"e basıyor, çıkıyor, açıyor — modal yine karşısında ve `Loaded` üzerinden tüm zincir yeniden koşuyor. Girişte oturum açıldığında çalışan bir uygulama için bu, sahibinin sonsuza dek gördüğü ilk şey.

- `AppSettings`'e `public DateTimeOffset? SetupCompletedAt { get; init; }` eklenir. Mevcut JSON seçenekleri (`WhenWritingNull`) sayesinde eski dosyalar bozulmadan yüklenir ve doğru şekilde null döner.
- Kapı: `if (wantsSetup || Settings.SetupCompletedAt is null)`. Kurulumdaki `--setup` yine zorlar.
- Damga **`Finish_Click`/`Skip_Click` içine değil**, `ShowSetup` içine, pencere kapandığında konur — `SetupWindow.xaml:26`'daki `ui:TitleBar` yalnızca `ShowMaximize="False"` diyor, yani X ve Alt+F4 iki click handler'ı da atlar ve nag geri gelir. "Şimdilik atla" da damgalanır: atlamak bulut kullanıcısı için meşru bir cevaptır; her açılışta tekrar sormak sihirbaz değil dırdırdır.
- **Silinen:** `SetupWindow.Completed` özelliği ve atamaları.
- **Zorunlu yan düzeltme:** `SettingsViewModel.ToSettings()` `AppSettings`'i sıfırdan kuruyor; listelenmeyen alan sessizce sıfırlanır. `MainWindow.xaml.cs:82` şu hâle gelir: `App.Settings = viewModel.ToSettings() with { SetupCompletedAt = App.Settings.SetupCompletedAt };` Aksi hâlde Ayarlar'da Kaydet'e basmak sihirbazı geri getirir. (Aynı initializer'ın `RecordAutomatically` ve `TranscribeGroupCalls`'u da düşürdüğü ayrı bir kusurdur; burada not edilir, Dalga 3'te kapatılır.)

### 2.2 Kurulum durdurulabilir ve modal değil (durdurucu)
Bugün "Şimdilik atla"/"Bitir"/X pencereyi kapatıyor ama kurulum çalışmaya devam ediyor: pip, python alt süreci ve iki saatlik timeout'la kurulmuş `PythonWorkerHost` gözlenemeyen ve durdurulamayan bir arka planda kalıyor. Daha kötüsü, `App.xaml.cs:147` `ShowDialog()` döner dönmez worker'ı yarım kurulmuş venv'e bağlıyor. Ve pencere modal olduğu için, kurulum süresince durum kartı, seviye ölçerler ve "Kaydı başlat" erişilemez — yani ilk kurulum sırasında gelen bir görüşme elle bile kaydedilemez; o düğmenin var oluş sebebi tam olarak bu durumdur.

- `SetupViewModel`'e `CancellationTokenSource` eklenir ve token **beş çağrı noktasının hepsine** geçirilir: `CheckAsync` (:304), `InstallPythonAsync` (:390), `CreateEnvironmentAsync` (:391), `host.DownloadModelAsync` (:463 — 1,6 GB'lık olan) ve `_hardware.MeasureAsync` (:496). Hepsi zaten token parametresi alıyor; hiçbirine geçilmiyor.
- `ExecuteAsync`'in catch'inde `OperationCanceledException` genel `Exception`'dan **önce** yakalanır: `State = Unknown`, "İptal edildi", `Problem` set edilmez, `ShowLog` açılmaz. Kullanıcının durdurması hata değildir, sihirbazı kırmızıya boyamamalıdır.
- `EnvironmentSetup.cs:405-409` iptal ile timeout'u ayırır: iptalde süreç ağacı öldürülüp `ThrowIfCancellationRequested()`, yalnızca gerçek timeout "Zaman aşımı." der.
- `SetupWindow.xaml.cs`'e `OnClosing`: `IsBusy` iken iptal edilir ve `e.Cancel = true`; iş çözüldükten sonra pencere kendini kapatır. X deliği kapanır.
- `App.xaml.cs`: `wizard.ShowDialog()` → `wizard.Show()` (Owner korunur). Bunun iki zorunlu sonucu var: (a) `DialogResult` atamaları silinir — `Show()` ile atama `InvalidOperationException` atar; (b) `PythonWorkerHost` yeniden kurma bloğu `wizard.Closed` handler'ına taşınır, yoksa yarım venv'e bağlanır. Ayrıca `Orchestrator.Start()` `ShowSetup` çağrısının **üstüne** alınır: modalin ekranda durması bir görüşmenin kaydedilmemesinin sebebi olamaz.

### 2.3 Sihirbaz beş satırlık bir build logu değil, altı adımlık bir akış olur
Bugün beş ön koşul satırı aynı anda görünüyor, hepsi kendi kendine koşuyor ve müşterinin işi izlemek. Hiçbir şey sorulmuyor: Geri yok, İleri yok, adım sayacı yok. Sözcükler de alıcının değil geliştiricinin: "Python", "Whisper paketleri", "CUDA kitaplık yolu", "Ayrı bir ortama kurulur".

Adımlar:
1. **Hoş geldin** — SetupWindow.xaml:36'daki mevcut cümle olduğu gibi kullanılır (yeniden yazılmaz) + taahhüt: "Kurulum yaklaşık 6 dakika sürer ve ~{model.DownloadGb} GB indirir." (GB `_settings().AsrModel.DownloadGb`'den bağlanır, sabit yazılmaz.)
2. **Neyi kaydediyor** — kayıt açıklaması. Kod tabanında bugün böyle bir metin **yok** (rıza/onam/yasal araması boş dönüyor). `IsChecked` İleri'yi kapılar.
3. **Nerede yazıya dökülsün** — yerel/bulut iki radyo kartı, `AsrMode`'u yazar. Ürünün tek gerçek kararı budur ve bugün kurulum başladıktan **sonra** açılan, kapalı bir `CardExpander`'a (xaml:163-167) gömülü. **Silinen:** o CardExpander; metni bulut kartının açıklaması olur. Bulut seçilirse 4. ve 5. adım tamamen atlanır.
4. **Bu bilgisayar** — ölçüm ve mevcut HeroCard (xaml:111-157) + önerilen modeli kabul/değiştirme.
5. **Bileşenler indiriliyor** — tek toplu ilerleme satırı.
6. **Ses testi** — mevcut `TestAudioAsync`, kendi sayfasında, canlı ölçerlerle.

- **Silinen:** `SetupWindow.xaml.cs:22`'deki `Loaded += RunAllAsync`. İş, kullanıcı 5. adımda İleri'ye bastığında başlar.
- **Silinen:** footer'daki "Baştan çalıştır". Yeniden deneme, hata veren adımın kendi sayfasına aittir; global bir kontrol değildir.
- Footer: `[Geri]` … `[İptal]` `[İleri/Bitir]`. "Şimdilik atla" yalnızca 1-3. adımlarda (hiçbir şey indirilmeden önce) görünür.
- Beş `Title`/`Purpose` cümlesi ekran metni olmaktan çıkıp **log satırı** olur; 5. adımda müşteri tek satır görür.

### 2.4 Doğru modeli bir kez indir
Bugün `RunAllAsync` sırası Python → Paketler → **Model** → Donanım. Yani hangi modelin kullanılacağına karar veren ölçümden **önce** katalog varsayılanı (1,6 GB) indiriliyor; sonra `HardwareProbe` `PickAsr` ile başka bir model öneriyor ve `SelfTestAsync` onu da indiriyor; `MeasureHardwareAsync` ise öneriyi `AsrModelId`'ye hiç yazmıyor. CPU-only bir dizüstünde sonuç: iki indirme ve yine kullanılmayacak modele işaret eden bir yapılandırma.

- Sıra `Python, Packages, Hardware, Model` olur (bu aynı zamanda kurucudaki satır sırasıyla da uyuşur).
- Donanım adımı `AsrModelId`'yi değiştirdiyse Model adımı öncesi `RefreshAsync(alreadyBusy: true)` çağrılır, yoksa "model var mı" sorusu eski modele sorulur.
- Öneri **uygulanır**: `MeasureHardwareAsync` içinde, `report.RecommendedAsr` null değilse, `RepositoryUnconfirmed` değilse ve `SendsAudioOffMachine` değilse `App.Settings = App.Settings with { AsrModelId = ... }` + `Save`. Bulut modeli sessizce seçilemez — kaydı makinenin dışına çıkarır.
- **Guard:** `PickAsr` 2 GB'lık bir kartta `cloud-openai-whisper` döndürebiliyor; `HardwareProbe.MeasureAsync:183-190` bunu `faster-whisper`'a `whisper-1` yükletmeye çalışıp çöküyor. `PickAsr` yerel motorlarla sınırlanır.
- `EnvironmentSetup.cs:248`'deki "Yaklaşık 300 MB" gerçek yükü olduğundan küçük gösteriyor; ölçülen değere göre düzeltilir.
- İndirmeden önce disk alanı: `DriveInfo.AvailableFreeSpace` **`RunAllAsync`'in başında** okunur (bugün yalnızca Donanım adımında, yani model indirildikten sonra okunuyor — hiçbir işe yaramıyor) ve `model.DownloadGb * 2 + 2` GB altındaysa Model adımı başlamaz, sebebi açıkça yazılır.

### 2.5 İlerleme ölçülür, günlük ulaşılabilir olur
Penceredeki her gösterge belirsiz (`IsIndeterminate="True"`): yüzde yok, bayt yok, adım sayacı yok. Tip sistemi de engelliyor — servislerle view model arasındaki tek kanal `IProgress<string>`. Ayrıca `ShowLog` bayrağı üç yerde `true` yapılıyor ama `CardExpander`'da `IsExpanded` bağlaması olmadığı için günlük **hiç açılmıyor**; `LogText` panoya kopyalanmak için üretiliyor ama ona bağlı düğme yok; log dosyasının yolu yalnızca ilk `Say` çağrısında geçiyor ve `Log.Insert(0, …)` onu anında listenin dibine itiyor. Kurulumu patlayan bir müşterinin hatayı gönderebileceği desteklenen bir yol yok.

- `worker/vt_worker/models.py` → `snapshot_download`'a `tqdm_class` verilerek gerçek kesir yayınlanır (500 ms'de en fazla bir rapor).
- `WorkerProgress`'e `BytesDone`/`BytesTotal`; `SetupStep`'e `Fraction`; `SetupViewModel`'e `BusyFraction` ve `StepCounter` ("Adım 3 / 4" — `Steps.Count` değil, zincir uzunluğu; `Audio` otomatik koşmuyor).
- `HardwareProbe.MeasureAsync`'e `IProgress<WorkerProgress>?` parametresi; `progress: null` kaldırılır — bu tek başına donmuş görünen donanım satırını canlandırır.
- Sabit strip'e `ui:ProgressBar`; satırlara 3px'lik ince bar. 20px'lik halka belirsiz kalır (o boyutta değer okunmaz). Paketler adımı dürüstçe belirsiz kalır — pip yönlendirilmiş pipe'a yüzde basmıyor. **Uydurma ETA yazılmaz**; yanlış ETA hiç ETA'dan kötüdür.
- Günlük: `CardExpander`'a `IsExpanded="{Binding ShowLog, Mode=TwoWay}"`; içine "Günlüğü kopyala" ve "Günlük klasörünü aç" (Explorer `/select,` ile — `HealthViewModel.OpenDataFolderAsync`'teki desen) ve `LogFile` yolunu gösteren bir `Caption`. Hata InfoBar'ının hemen altına "Hata ayrıntısını kopyala" (`Problem`'e `NullToCollapsed` ile bağlı). Bunlar donanım kartına konmaz — o kart hata anlarında zaten `Collapsed`.

---

## Dalga 3 — Ölü kontroller ve dürüst durumlar

**Neden:** Bu dalgadaki her madde, kullanıcıya bir şey vaat edip tutmayan bir kontrol. Bir üründe en pahalı hata türü budur: kullanıcı bir kez tıklar, hiçbir şey olmaz, bir daha güvenmez.

**3.1 Genel bakış'taki üç birincil düğme ölü (durdurucu).** `OverviewViewModel.ActionRequested` (:112) `RunAction` (:180) tarafından tetikleniyor ve çözümde **hiçbir abonesi yok**; `ShellViewModel` kurucusu Ledger, Contacts ve Search'ü bağlıyor, Overview'u atlıyor. "İsimlendir", "Tekrar dene", "Ayarlar" — ana ekranın en dikkat çekici üç `Primary` düğmesi — hiçbir şey yapmıyor. `Unlabelled()` ve `RequeueFailed()` çağrılmıyor. (Aynı "tekrar dene" fiili Durum sayfasında **çalışıyor**; ürün aynı komutun bir çalışan bir bozuk kopyasını sevk ediyor.)
Bağlama **`MainWindow.xaml.cs`'te** yapılır, `ShellViewModel`'de değil — view model'in penceresi yoktur, `LabelCallWindow` sahipsiz açılır ve ana pencerenin arkasına düşer. Mevcut `DataContextChanged` lambda'sına abone/abonelikten çık çifti eklenir; `Settings_Click`'in gövdesi `OpenSettings()`'e çıkarılır. `ShowUnlabelled` isimlendirme döngüsünde kullanıcı "Sonra" derse döngü **kırılır** (aksi hâlde on iki isimsiz kayıtta çıkılamayan bir modal zinciri olur). `RetryFailed` bildiriminde sayı yer alır (`$"{n} kayıt yeniden kuyruğa alındı."`) — hem bilgi verir hem `Notice` dizesi ardışık tıklamalarda değiştiği için snackbar tekrar görünür.
Ayrıca `AttentionItem.HasAction` sertleştirilir: `ActionLabel is not null && Action != AttentionAction.None`.

**3.2 "Son görüşmeler" satırları tıklanabilir görünüyor ama değil.** `HoverCard` (`Components.xaml:23-37`) `Cursor="Hand"` ve hover arka planı veriyor, stilin kendi yorumu "Used wherever a card is actually clickable" diyor; satırlarda `InputBindings` yok, `OverviewPage.xaml.cs` altı satır. Aynı görünüm `SearchPage.xaml:101`'de görüşmeyi **açıyor** — kullanıcı hareketi orada öğrenip burada başarısız oluyor. `OverviewViewModel.Open(RecentCall)` + `OpenRequested` eklenir, `ShellViewModel.OpenContact(contactId, callId)`'ye bağlanır; `ContactId` null ise isimlendirme akışına yönlendirilir. Tooltip `NeedsLabel`'a göre değişir.

**3.3 Kişiler > Defter sekmesindeki zaman damgası yalan söylüyor.** `ContactsPage.xaml:471`'deki `TimeLink` stilinin kendi yorumu "Timestamps are clickable everywhere: they seek the recording to that moment" diyor; bu örnekte `InputBindings` ve `ToolTip` yok. `PlayFlagCommand` ve `DismissFlagCommand` (ContactsViewModel.cs:442, 453) hiçbir XAML'de geçmiyor. `MouseBinding` + `ToolTip="Bu andan itibaren dinle"` eklenir ve karta bir "Bu satırı kaldır" hayalet düğmesi konur.
**Kritik detay:** `PlayFlag` önce doğru görüşmeyi seçmeli. `Flags` kişinin bütün görüşmelerini kapsıyor ama `PlayFrom` `SelectedCall`'dan yol çözüyor ve seçili çağrı varsayılan olarak **en yenisi**; bugünkü hâliyle eski bir bayrağa tıklamak yanlış kaydı o saniyeden çalar. (Aynı ölü `TimeLink` `SearchPage.xaml:134`'te de var; aynı düzeltme uygulanır.)

**3.4 Defter'de yanlış satırda duran onay düğmesi.** "Tutuldu olarak işaretle" (LedgerPage.xaml:179-184) her satırda etkin, ama `LedgerViewModel.Fulfil` `Kind is not (Overdue or Promises)` ise sessizce dönüyor — "Değişenler" ve "Dikkat" satırlarında sessiz no-op. Sayfanın satırlarının yaklaşık yarısı basınca hiçbir şey yapmayan bir düğme taşıyor. `LedgerEntry.CanFulfil` eklenir, düğmeye `Visibility` bağlanır (devre dışı değil, **gizlenir**; sağ kenar hizası korunur). Guard savunma amaçlı kalır. Dismiss düğmesi koşulsuz kalır — o, Değişenler satırında en azından kendini açıklıyor.

**3.5 Defter'de geri alınamaz tek tık.** "Bu satırı kaldır" (LedgerPage.xaml:186-191) 30x28 px kenarlıksız bir ikonla veritabanına kalıcı bir dismissal yazıyor; onay yok, geri alma yok, "kaldırılanlar" çipi de olmadığı için yanlış satıra basmak arayüzden telafi edilemez. Üstelik anlamı bambaşka olan Checkmark düğmesinin 2 px yanında. `RestoreCommitment` / `ReopenCommitment` / `RestoreFlag` eklenir; Dismiss ve Fulfil bir `PendingUndo` bırakır ve listenin altındaki `ui:InfoBar` "Geri al" sunar (zamanlayıcı yok: bar bir sonraki işleme ya da filtre değişimine kadar durur). Dismiss düğmesine `Margin="12,0,0,0"` ve yeni `DangerGhostButton` stili (`GhostButton`'a dokunulmaz — dokuz yerde kullanılıyor).
Aynı desen `ContactsViewModel.DismissFlag`'te de var; aynı dalgada kapatılır. `Refresh()` çağrısı ayrıca bayat çip sayaçlarını da düzeltir.

**3.6 Oynatıcının yükleme ve hata durumu yok.** `IsLoaded` false olduğu sürece panel **ekranda yok**, sonra aniden beliriyor; dosya silinmişse `WaveformPeaks.Read` sıfır dizi döndürüyor, `LoadAsync` yine de `IsLoaded = true` yapıyor ve 0,6 px taban yüzünden **normal görünen düz bir dalga formu** çiziliyor; ardından Play/Seek `File.Exists` üzerinden sessizce return ediyor. Kullanıcıya hiçbir şey söylenmiyor. `IsBusy` + `AudioProblem` + `ShowPlayer` eklenir; dosya varlığı arka planda yoklanır (kopmuş sürücüde `File.Exists` bloklar); `_loadToken` ile yarış önlenir; tek akış varsa `ListeningToMe` ona ayarlanır ve durum yazılır; transport `IsLoaded`'a bağlanır. **Silinen:** `ContactsPage.xaml` Grid.Row=5'teki `PlaybackMessage` TextBlock'u ve `ContactsViewModel.PlaybackMessage` — mesaj yüzeyi tek olur (bu aynı zamanda bir sonraki sağlıklı görüşmeye taşınan bayat mesaj hatasını da kapatır).

**3.7 Durum sayfası ilk açılışta yalan söylüyor.** `ProblemCount` yalnızca Warning|Bad sayıyor; hiçbir denetim koşmadan önce 0 olduğu için başlık "Her şey çalışıyor" diyor, yanındaki ikon ise `IsHealthy`'ye bağlı olduğu için sarı uyarı üçgeni gösteriyor. Kullanıcının bu sayfayı ilk görüşü: uyarı üçgeninin yanında "Her şey çalışıyor". Dönüşü de aynı derecede yanlış: `RefreshAsync` yakalama testini hiç çalıştırmadığı için tertemiz bir makinede bile üçgen kalıyor.
Tek bir `Overall` durumu (Bad > Warning > Unknown > Good) hem glif hem cümleyi sürer; ölçülmemiş şey "iyi" değil "henüz bilinmiyor"dur ve nötr gri gösterilir. Metin: "Denetleniyor…", "Henüz denetlenmedi", "Denetlenenler çalışıyor. Sınanmadı: Ses yakalama.", "Her şey çalışıyor". **Silinen:** `HealthHeadlineConverter` (PresentationConverters.cs:331-346) ve `App.xaml:38`'deki kaynak girdisi — ulaşılamaz `"Denetlenmedi"` dalı dahil. Ayrıca `RefreshAsync` içinde `UpdateCloud()` sonrası bir kez `Announce()` çağrılır ki kuyruk/disk/bulut sonuçları Python yoklaması boyunca saklanmasın.

**3.8 Sihirbazın bitiş durumu.** `IsReady` hesaplanıyor ve hiçbir yere bağlanmıyor; "Bitir" ilk karede etkin. `SetupStep.NeedsUser` ile "Ses yakalama" satırı "beklemede" değil "sıra sende" görünümüne geçer (aksan rengi + `Primary` düğme), ve footer'a bir özet `ui:InfoBar` konur: engelleyici varsa Error + "Yine de kapat", bulut seçiliyse Informational, ses sınanmadıysa Warning, temizse Success. "Bitir" **koşulsuz devre dışı bırakılmaz** — çevrimdışı ya da proxy arkasındaki bir makinede kullanıcı diyalogdan hiç çıkamaz.

**3.9 `LabelCallWindow`'daki iki sızıntı.** (a) `LabelCallWindow.xaml.cs:48` `CallApp` enum'unu Türkçe cümleye enterpole ediyor; elle başlatılan her kayıt `CallApp.Unknown` geçtiği için "Kaydı başlat"tan sonra görülen ilk cümle: *"04:31 uzunluğunda bir Unknown görüşmesi kaydedildi."* Aynı sızıntı `OverviewViewModel:66`'daki "Son görüşmeler" rozetinde. `CallAppText.TurkishName()` eklenir ve **null** döner; `Unknown` için ayrı bir cümle kurulur ("… uzunluğunda bir kayıt tamamlandı."), rozet ise `HasApp` ile tamamen gizlenir — yer tutucu kelime basılmaz.
(b) `RememberBox` varsayılan olarak işaretli ve "Bir dahaki sefere sormadan tanınır." diyor, ama `CallOrchestrator.Tick` `Sample`'ı pencere başlığı **olmadan** çağırdığı için `_observedTitle` daima null ve `RememberTitle`'a hiç ulaşılmıyor: diyalog her görüşmeden sonra sonsuza dek açılıyor. Tercih edilen çözüm başlık yakalamayı gerçekten uygulamaktır (`user32` `EnumWindows` ile hedef PID'lerin görünür pencereleri; `Process.MainWindowTitle` **kullanılmaz**, sohbet penceresini döndürür), artı "whatsapp"/"telegram" gibi kimseyi tanımlamayan başlıkların eşleştirilmemesi — aksi hâlde ilk isimlendirilen kişi bütün WhatsApp görüşmelerini kendine toplar. Uygulanmayacaksa onay kutusu **silinir** ve `LabelCallWindow.xaml.cs:17-24` ile `MainWindow.xaml.cs:40-46`'daki, sahip olunmayan davranışı anlatan yorumlar düzeltilir.

**3.10 Klavye kaçırması.** `MainWindow.OnPreviewKeyDown` (158-191) Contacts sayfasında Space/Left/Right'ı pencere düzeyinde yutup `e.Handled = true` yapıyor: sekme şeridi ("Görüşmeler"/"Defter"/"Açık sözler") klavyeyle hiç kullanılamıyor, raydaki "Kaydı başlat"a sekip Space'e basmak kayıt başlatmak yerine sesi oynatıyor. Kısayollar Ctrl'e taşınır (`if (Keyboard.Modifiers != ModifierKeys.Control) return;`) ve çıplak tuşlar oynatıcı paneline `InputBindings` olarak verilir (panel `Focusable`, dalga formu tab-stop, tıklayınca odak alır, `IsKeyboardFocusWithin` için görünür bir odak halkası). Tooltip'lere gesture yazılır: "10 sn geri (Ctrl+←)", "Oynat / duraklat (Ctrl+Boşluk)".

---

## Dalga 4 — Tek dil

**Neden:** Ürün kullanıcıya kendisi için üç ayrı isim veriyor, bir ayara beş ayrı isim veriyor ve aynı kelimeyi iki farklı anlamda 248 px'lik rayın içinde aynı anda kullanıyor. Bunlar tek tek küçük, toplamda "ticari ürün disiplini yok" izleniminin ana kaynağı.

**4.1 Hitap tek: "sen".** `LabelCallWindow` baştan sona resmi "siz" kullanıyor — üstelik kullanıcının ilk görüşmesinden hemen sonra gördüğü **ilk pencere** o. Değişecek yedi dize: `LabelCallWindow.xaml:24` "Kiminle görüştünüz?" → **"Kiminle görüştün?"**; `:43` "veya adı yazın" → **"veya adını yaz"**; `LabelCallWindow.xaml.cs:101` "…Emin misiniz?" → **"…Emin misin?"**; `App/Services/EnvironmentSetup.cs:201` "…yeniden açın." → **"…yeniden aç."**; `SettingsWindow.xaml.cs:52` "…seçin" → **"…seç"**; `Core/Configuration/AppSettings.cs:287` "Başka bir konum seçin." → **"…seç."**; `Capture/CaptureSelfTest.cs:57` "…tekrar deneyin." → **"…tekrar dene."**
**Dokunulmaz:** `ScamPatterns.cs`'teki dolandırıcılık kalıpları (değiştirmek tespiti bozar) ve `SampleData.cs`'teki karşı taraf replikleri. Global find/replace **yapılmaz**.

**4.2 Konuşmacı adları tek.** Kişiler > Görüşmeler sekmesinde aynı anda üç isim görünüyor: kart "Sen … karşı taraf", döküm "Ben", oynatıcı "Sen". Kural: kendisi her yerde **"Sen"**, karşı taraf biliniyorsa **kişinin adı**, bilinmiyorsa "karşı taraf". `ContactsViewModel.cs:259` `"Ben"` → `"Sen"`; `ComputeTalkStats` içinde `var them = SelectedContact?.Name ?? "karşı taraf"` (grup görüşmesinde ad değil "karşı taraf" — o kanalda birden fazla kişi var); `SearchViewModel.cs:76` `"Ben"` → `"Sen"`. `ObsidianExporter` **dokunulmaz** — dışa aktarılan belgeler ayrı bir editoryal karardır ve değiştirmek kullanıcının kasasındaki eski dosyalarla çelişir.

**4.3 "arama" yalnızca arama demek.** Telefon çalarken durum kartı "Arama çalıyor" derken sekiz satır aşağıdaki gezinme öğesi de "Arama" diyor ve tam metin aramayı kastediyor. Kural: kaydedilen konuşma **"görüşme"**, tam metin arama **"arama"**, çalan olay **"çağrı"**.
Başlıcaları: `ShellViewModel.cs:163` "Arama çalıyor" → **"Gelen çağrı"** ("Görüşme çalıyor" olmaz — görüşme çalmaz); `:89` ve `:166` "Arama başlayınca otomatik kaydedilecek" → **"Görüşme başlayınca…"** (her zaman görünen dinlenme metni, en kötü örnek); `HealthPage.xaml:255` ve `SetupWindow.xaml:143` "60 dakikalık arama için" → "…görüşme için"; `SettingsWindow.xaml:539` "Uzun aramalar" → "Uzun görüşmeler"; `:96`, `:116`; `SettingsViewModel.cs:154`; `OverviewPage.xaml:248`; `HardwareProbe.cs:78, 275`; `CallRecorder.cs:46`; `AsrCatalog.cs:223`. "Grup araması" → **"Grup görüşmesi"** beş yerde birden: `ContactsViewModel.cs:280`, `CallOrchestrator.cs:413`, `AnalysisPipeline.cs:78`, `ObsidianExporter.cs:127`, `NotionExporter.cs:93`.
**Dokunulmaz:** `ScamPatterns.cs:26` "Sahte banka araması", `SettingsWindow.xaml:304` "görüntülü aramayla", `AudioDeviceCatalog.cs:43` "aramalar için varsayılan" (Windows'un kendi ifadesi), `AsrCatalog.cs:203` "Arama sonuçlarında" (arama anlamı), `MainWindow.xaml:206` ve `SearchPage.xaml:19` "Arama" (artık tek anlamlı).

**4.4 Obsidian ayarının tek adı: "kasa".** Aynı ayar beş isimle anılıyor; "Obsidian kasası ayarlanmamış" hatasını okuyup Ayarlar'a giden kullanıcı orada "Vault klasörü" etiketli bir alan buluyor ve aynı şey olduğunu tahmin etmek zorunda kalıyor. `SettingsWindow.xaml:787` → "Obsidian kasa klasörü"; `:771` → "Seçtiğin Obsidian kasasına markdown dosyaları yazar…"; `SettingsWindow.xaml.cs:52` → "Obsidian kasa klasörünü seç"; `AppSettings.cs:291` → "…kasa klasörü seçilmemiş."; `:294` → "Seçilen Obsidian kasası bulunamadı."; `OverviewViewModel.cs:271` → "Obsidian kasası seçilmemiş".

**4.5 Ön ödemeli bakiyenin tek adı: "bakiye".** Düğme "Krediyi sor" diyor, on cevabın sekizi "bakiye" diyor, aynı kart iki kelimeyi bir arada kullanıyor. `SettingsWindow.xaml:472` → **"Bakiyeyi sor"**; `:369` "kredisi biterse" → "bakiyesi biterse"; `SttProviders.cs:127` → "Kalan bakiyeyi API'den bildirir."; `SttProbe.cs:194, 201` "Kota alınamadı/Kota bilgisi gelmedi" → "Bakiye alınamadı/Bakiye bilgisi gelmedi"; `HealthPage.xaml:16` "kredisi biter" → "bakiyesi biter"; `SttEndpointViewModel.cs:10`'daki yorumdaki düğme adları gerçekle eşitlenir. Kod tarafındaki `Balance*` isimleri değişmez. `:208`'deki "Kalan {n} / {m} karakter." kalır — birim değere aittir.

---

## Dalga 5 — Görsel sistem

**Neden:** Bu dalga en sona bırakılır çünkü öncekiler ekran ve kontrol siliyor; ama içindeki ilk iki madde "kozmetik" değil, **okunabilirlik** kusuru.

**5.1 Semantik renkler karanlık temada erişilebilir değil.** `Theme.xaml:38-40`'taki `MeBrush #0F6CBD`, `ThemBrush #7A7574`, `GoodBrush #0F7B0F` sabit hex; uygulama sistem temasını takip ediyor (`ApplySystemTheme`, `SystemThemeWatcher.Watch`) ve bu üç renk karanlık kartta (~#2B2B2B) sırasıyla 2,63 / 3,12 / 2,60 kontrast veriyor — metin dışı grafik nesneler için gereken 3:1'in altında. Bunlar dekoratif değil: `GoodBrush`, "kaydedici yaşıyor mu" sorusunu yanıtlayan 10 px'lik durum noktası. Üstelik satır 35-36'daki yorum bu renklerin "hem açık hem koyu kartta okunaklı" olduğunu iddia ediyor — bu doğru değil, yorum düzeltilir.
Mekanizma önemli: tüketicilerin hepsi `StaticResource` kullanıyor, yani sözlük değiştirmek ya da `DynamicResource` takma adı işe **yaramaz**. Altı `Color` kaynağı (light/dark) tanımlanır ve `SemanticBrushes.Apply(theme)` var olan fırça nesnelerinin `.Color`'ını yerinde değiştirir; 17 tüketici aynı nesneye referans tuttuğu için değişiklik canlı yayılır. `ApplicationThemeManager.Changed`'e abone olunur. `MainWindow.xaml:66`'daki satır içi kayıt kırmızısı `#C42B1C` de aynı mekanizmaya alınır. `ContactsPage.xaml:341,343`'teki `Opacity="0.85"` **1.0 yapılır** — yoksa dalga formu düzeltmeden sonra bile 3:1'i geçmez ve orası yedek metin etiketi olmayan tek yer.

**5.2 Avatar paleti "ben" rengiyle çakışıyor.** `PresentationConverters.cs:50` `#0F6CBD` — `MeBrush` ile bayt bayt aynı. Sekiz kişiden biri, ürünün her yerde "benim sesim" anlamına gelen maviyi avatar olarak alıyor; Kişiler sayfasında 56 px'lik başlık avatarı ile 40 px altındaki döküm şeritleri aynı görüntüde çarpışıyor. `[0]` → `#4B3F9E` (menekşe), `[6] #1F498B` → `#5C6300` (zeytin). Dizi **8 elemanda kalır**, yoksa `sum % Palette.Length` bütün mevcut kişilerin rengini kaydırır. Çalışma zamanında filtreleme yapılmaz (aynı sebep); bunun yerine hue'ları `MeHue = 207.9`'a 30 dereceden yakın olan girdiyi düşüren bir test/`Debug.Assert` eklenir ve paletin üstüne mavi bandın neden yasak olduğu yazılır.

**5.3 Arama sonuçlarında isimler kırpılıyor.** `SearchPage.xaml:130` `Width="76"` sabit ve `TextTrimming` yok: "Mehmet Yılmaz" ~92 px, "Ahmet Kahraman" ~99 px ölçüyor, isim üç nokta olmadan harfin ortasından kesiliyor. `Width="92"` (ContactsPage.xaml:280 ile aynı) + `TextTrimming="CharacterEllipsis"`. Sütun **Auto yapılmaz** — her satır ayrı Grid olduğu için eşleşme metni her satırda farklı x'ten başlar.

**5.4 Defter çipleri dar pencerede kırpılıyor.** Beş çip sarmayan yatay `StackPanel`'de; `MinWidth="1060"`'ta içerik sütunu 748 px, çipler + 220 px'lik filtre kutusu 827 px istiyor — "Dikkat" çipi ve "Değişenler"in bir kısmı, var oldukları belli olmadan sağdan taşıyor. `StackPanel` → `WrapPanel`; `Components.xaml:245` `ChipButton` margin'i `0,0,8,8`; satır Grid margin'i `0,0,0,6`'ya iner (tek satır hâlinde eski 14 px korunur); filtre kutusuna `Margin="8,0,0,0" VerticalAlignment="Top"` (Stretch olursa iki satır yüksekliğine uzar).

**5.5 Sayfa iç boşlukları ve ölçüsü tek token'a.** Beş sayfada dört farklı inset var (`24,4,32,24` / `24,4,32,16` / `16,4,24,16` …): Genel bakış'tan Kişiler'e geçince içerik aynı anda 8 px sola ve 8 px sağa kayıyor — çerçeve gözle görülür şekilde zıplıyor. `Theme.xaml:25`'teki hiç kullanılmayan `PadPage` token'ı `32,4,32,24` yapılır ve kaydırılan sayfalar için `PadPageScroll` `32,4,18,24` eklenir (ScrollBar padding'in dışında gerçek genişlik yiyor). Üst değer **4'te bırakılır**: rayın durum kartı y=0'dan başlıyor, yalnızca sayfa üstünü artırmak başlıkları kartın 20-32 px altına düşürür; nefes payı istenirse iki taraf birlikte değiştirilir.
Ölçü de üç farklı: 1000 / 860 / 760 px. `PageMeasure = 900` token'ı Overview, Health ve Settings'e uygulanır; Ledger ve Search'te **dış Grid**'e konur (yalnızca liste kapatılırsa filtre kutusu kart sütununun dışında kalır) ve `HorizontalAlignment="Left"` zorunludur, yoksa ortalanıp ortak sol kenar bozulur. `ContactsPage` **dokunulmaz** — o bir master/detail bölünmesi.

**5.6 Kayıt göstergesi kıpırdamıyor.** `Components.xaml:336`'daki `RecordingPulse` storyboard'u tanımlı, uzun uzun belgelenmiş ve **hiçbir yerden referans verilmiyor**; `StatusDot` kayıt sırasında yalnızca renk değiştiren hareketsiz bir elips. Bugün boşta #0F7B0F, kayıtta #C42B1C — 10 px'lik bir noktada yedek kodlaması olmayan bir kırmızı-yeşil çifti. `IsRecording` DataTrigger'ına `EnterActions`/`ExitActions` eklenir; `StopStoryboard` **zorunludur** (`RepeatBehavior="Forever"` kendiliğinden bitmez, aksi hâlde nokta yarı sönük kalır). Nabız `Fill`'e değil `IsRecording`'e bağlanır, çünkü `HasProblem` görüşme ortasında rengi sarıya çevirebilir ve hareket devam etmelidir.
**Silinen:** `Components.xaml:324`'teki `PageEnter` storyboard'u ve yorum bloğu — `PageHost` stili (satır 289) aynı animasyonu zaten satır içinde yapıyor ve yorum bloğu birebir kopya. `PageHost`'a dokunulmaz.

---

## Silinenlerin toplu listesi

| # | Silinen | Dalga |
|---|---|---|
| 1 | `SetupWindow`'un beş satırlık ön koşul kontrol listesi (rolü Durum'a taşınır) | 1 |
| 2 | `MainWindow.xaml:223-229` "Kurulum ve testler" ray düğmesi + `Setup_Click` | 1 |
| 3 | `SetupViewModel.HardwareReport` (ikinci kopya) | 1 |
| 4 | `OverviewPage.xaml:113-153` "Vadesi geçen sözler" bölümünün tamamı | 1 |
| 5 | Dikkat satırındaki, alttaki kartları birebir tekrarlayan `Overdue.Take(2)` detayı | 1 |
| 6 | `MainWindow.xaml:239-246` "Yenile" ray düğmesi + `RefreshAllCommand` | 1 |
| 7 | `SetupWindow.Completed` ölü özelliği | 2 |
| 8 | `SetupWindow.xaml.cs:22` `Loaded += RunAllAsync` | 2 |
| 9 | Footer'daki "Baştan çalıştır" | 2 |
| 10 | "Hiçbirini kurmadan da kullanabilirsin" `CardExpander` (metni 3. adıma taşınır) | 2 |
| 11 | `HealthHeadlineConverter` + `App.xaml:38` kaynağı | 3 |
| 12 | `ContactsPage.xaml` Grid.Row=5 `PlaybackMessage` + `ContactsViewModel.PlaybackMessage` | 3 |
| 13 | Defter'de söz olmayan satırlardaki "Tutuldu olarak işaretle" düğmesi | 3 |
| 14 | `LabelCallWindow`'daki "Bu pencere başlığını eşleştir" onay kutusu — *başlık yakalama uygulanmazsa* | 3 |
| 15 | `Components.xaml:324` `PageEnter` storyboard'u | 5 |

---

## Sıralamanın gerekçesi, tek cümlede

Dalga 0 olmadan oynatıcı, süreler, dışa aktarma ve kişi silme yanlıştır — üstüne konan hiçbir iş doğru görünmez. Dalga 1 ekran sayısını azaltır, bu yüzden Dalga 2-5'te yapılacak iş de azalır. Dalga 2 müşterinin ilk on dakikasıdır. Dalga 3 tıklanan her şeyin çalışmasını sağlar. Dalga 4 ve 5 ürünün tek bir elden çıkmış gibi okunmasını sağlar — ama silinecek bir ekranın metnini düzeltmek ya da rengini ayarlamak boşa emek olduğu için en sona bırakılmıştır.