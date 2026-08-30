# İşlem günlüğü

Bu dosya, projede **ne yapıldığını ve neden yapıldığını** tarih sırasıyla tutar. Amaç: başka bir
oturumda (veya başka biri tarafından) devam edilebilmesi. Her kayıt "ne bozuktu → ne yapıldı →
nasıl doğrulandı" biçiminde.

Kod içindeki yorumlar *bir dosyanın* neden öyle yazıldığını anlatır. Burası *projenin* neden
buraya geldiğini anlatır. İkisi farklı sorular.

---

## 2026-08-30 — Hedef makinedeki ilk gerçek kullanım turu

Uygulama ilk kez hedef makinede (RTX 4050, gerçek ses donanımı, gerçek WhatsApp/Telegram) çalıştı.
Geliştirme makinesinde görülmesi mümkün olmayan altı ayrı kusur ortaya çıktı. Hepsi giderildi.

### 1. `cublas64_12.dll` yüklenemiyor — transkripsiyon tamamen çalışmıyor

**Belirti.** Kayıt alınıyor, transkripsiyon başlıyor, sonra Python yığın izi:
`RuntimeError: Library cublas64_12.dll is not found or cannot be loaded`

**Asıl sebep — ve neden kimse fark etmedi.** İki kusur üst üste gelmişti:

- `ctranslate2.get_cuda_device_count()` **sürücüye** sorar. NVIDIA kartı olan her makinede 1
  döner, cuBLAS yüklenebiliyor olsun ya da olmasın. Kurulum ekranı bu sayıya bakıp yeşil
  "CUDA hazır ✅" gösteriyordu.
- Eksik DLL listesi `_cuda_report()` içinde **zaten hesaplanıyordu** ama yalnızca CUDA
  *bulunamadığında* gösteriliyordu. Yani tam da bu durumda hesaplanıp atılıyordu.

Sonuç: ekran "hazır" derken model karta yükleniyor, ilk `encode()` çağrısında ölüyordu. Hata
**görüşme bittikten sonra** geliyordu — kaydın tek kopyasının elde kaldığı an.

**Yapılan.**

- `CudaReport`'a `Usable` alanı eklendi: "kart var mı" ile "kart kullanılabilir mi" ayrıldı.
  (`src/VoiceTranscript.Core/Asr/WorkerProtocol.cs`)
- `EnvironmentSetup.DescribeGpu()` yazıldı. Dört ayrı durum ayırt ediliyor ve yalnızca biri
  çıkmaz sokak: kart+kütüphane var / kart var kütüphane yok (**kurulabilir**) / kart var CUDA
  görünmüyor (**kurulabilir**) / NVIDIA kartı yok (sorun değil).
- `EnvironmentSetup.InstallGpuRuntimeAsync()` eklendi. `pip install -r requirements.txt`
  çalıştırır, sonra cuBLAS'ı **gerçekten yükleyerek** doğrular. CUDA Toolkit gerekmez,
  yönetici yetkisi gerekmez.
- `_resolve_device()` artık cuBLAS yüklenemiyorsa GPU'yu **reddedip işlemciye düşüyor.**
  (`worker/vt_worker/engines/faster_whisper_engine.py`)
- `_start()` eklendi: iş sırasında GPU çökerse bir kez işlemcide yeniden denenir.
  faster-whisper tembel olduğu için hata `transcribe()` çağrısından değil **döngüden** gelir —
  yalnızca çağrıyı sarmak hiçbir şey yakalamıyordu.

> **Kural:** yavaş olmak başarısızlık değil. Görüşmeyi kaybetmek başarısızlıktır.

### 2. Hangi ekran kartının kullanıldığı belli değil

Kullanıcı sordu: iki kartlı makinelerde Intel/AMD tümleşik kart seçilmesin.

**Not:** bu risk aslında yok — CTranslate2 yalnızca CUDA cihazlarını sayar, tümleşik Intel/AMD
kartı zaten göremez. Ama **iki NVIDIA kartı** olabilir ve index 0, PCI sırasıdır, "daha iyi kart"
demek değildir.

**Yapılan.** `worker/vt_worker/gpu.py` yazıldı: `nvidia-smi` ile kartlar listelenir (sürücüyle
birlikte gelir, yeni bağımlılık yok), **en çok belleğe sahip olan** seçilir, `device_index` olarak
`WhisperModel`'e verilir. Kartın adı arayüze taşındı — artık "CUDA hazır" değil,
"RTX 4050 Laptop GPU (6 GB) kullanılacak" yazıyor.

### 3. Görüşmenin tamamı dinlenemiyor

**Belirti.** Oynatıcıda yalnızca "Karşı taraf" vardı. Tek taraf dinlemek, cevapları kesilmiş bir
insanı dinlemektir — konuşmanın nasıl geçtiğini öğrenmek için kullanılamaz.

**Yapılan.**

- `src/VoiceTranscript.Core/Audio/PcmReader.cs` — RIFF yürüyücüsü tek yere alındı
  (`WaveformPeaks` artık onu kullanıyor, ikinci kopya silindi).
- `src/VoiceTranscript.Core/Audio/ConversationMix.cs` — iki akış **talep üzerine** birleştirilip
  `<görüşme>-butun.wav` olarak yanına önbelleklenir.
- `PlaybackViewModel` üç kanallı oldu: **Tüm görüşme** (varsayılan) → Sen → Karşı taraf.

**Neden kayıt anında değil de sonradan:** görüşme sırasında makine meşguldür ve fark edilir; ayrıca
sonradan üretmek **diskte zaten duran eski kayıtların hepsine** de bu özelliği kazandırır. Türetilmiş
olduğu için serbestçe silinebilir.

**Neden yarıya bölmeden toplanıp kırpılıyor:** görüşmenin çoğunda tek kişi konuşur. Yarıya bölmek,
üst üste konuşulan birkaç saniye için **bütün konuşmayı 6 dB kısmak** demektir.

### 4. Ham Python yığın izi ana ekrana dökülüyordu

Genel bakış ekranı ve kişi paneli, başarısız işin `FailureReason` alanını **olduğu gibi**
yazdırıyordu: yirmi satır dosya yolu, asıl hata en altta, kıvrımın altında.

**Yapılan.** `src/VoiceTranscript.Core/Asr/FailureText.cs`. Bilinen arızalar Türkçe tek cümleye ve
**yapılacak işe** çevrilir ("cuBLAS yüklenemedi → Kurulum ekranından kurulabilir"). Tanınmayanlar
için yığın izinin son `XxxError:` satırı alınır. Sıra önemli: bellek yetersizliği de "cuda"
içerir, kütüphane kuralı önce eşleşse kullanıcı zaten sahip olduğu şeyi kurmaya gönderilirdi.

Ham metin **silinmiyor** — gerçek bir hatayı teşhis edilebilir kılan tek şey o.

### 5. Tek bir görüşme silinemiyordu

Bir kaydı silmenin tek yolu **kişinin tamamını** silmekti.

**Yapılan.** `Repository.DeleteCall(callId)`. Birleştirilmiş kopya da siliniyor — türetilmiş
olduğu için unutulması kolay, ama o dosya **konuşmanın tamamının çalınabilir bir kaydı.**
Bırakılırsa silme işlemi yalan olur.

Ayrıca `DeletionResult.FilesRemoved` artık **gerçekten silinen** dosyaları sayıyor. Önceden var
olmayan yollar da sayılıyordu: sesi çoktan gitmiş bir görüşme için "2 kayıt silindi" diyordu.

`LabelCallWindow`'daki "Bu kaydı sil" de gerçek silme yapıyor — önceden satırı "Atlandı" olarak
işaretleyip isimsiz bir hayalet bırakıyordu (ekran görüntüsündeki `İsimsiz · Atlandı` satırı).

### 6. `ObservedTitle` hiçbir zaman doldurulmuyordu

**En büyük sessiz kusur.** Tasarımın tamamı "Telegram arama penceresinin başlığı kişinin adıdır"
üzerine kuruluydu; `RememberTitle`, `ResolveTitle`, `ObservedTitle` hepsi yazılmıştı —
ama **başlığı okuyan kod hiç yazılmamıştı.** `WindowTitle` hiçbir yerde atanmıyordu.

Bu yüzden her görüşme "İsimsiz" olarak geliyordu, iki uygulamada da.

**Yapılan.** `src/VoiceTranscript.Capture/CallWindows.cs`:

- `EnumWindows` ile izlenen PID'lere ait üst düzey görünür pencereler taranır.
- Kural **şekle göre**, uygulamaya göre değil: bir pencerenin başlığı o uygulamanın kendi adı
  değilse, kişinin adı sayılır.
- Başlıklar temizlenir: bidi kontrol karakterleri atılır, NFKC uygulanır. Görünmez bir U+200E
  taşıyan ad, elle yazılan aynı adla eşleşmez — bu, **aynı kişi için ikinci bir kişi kaydı**
  yaratır ve geçmişi ikiye böler. İki yarı da tam görünür; defter sadece aradaki fiyat
  değişimini fark etmez olur.

`AudioSessionWatcher.Sample()` artık pencereleri de okuyor (parametreler nullable yapıldı; testler
sahte gözlem vermeye devam ediyor).

**WhatsApp hakkında dürüst durum.** WhatsApp'ın **ana** penceresinin başlığının sabit "WhatsApp"
olduğu doğrulandı. **Arama** penceresinin de öyle olup olmadığı doğrulanmadı — doğrulamak,
WhatsApp'ın oturum açtığı makinede gerçek bir arama açmayı gerektiriyor ve geliştirme makinesi
bunun dışında tutuluyor. O yüzden varsayım koda gömülmedi: WhatsApp'ta da "WhatsApp" olmayan bir
başlık çıkarsa isim olarak kullanılır. Çıkmazsa bir kez sorulur ve hatırlanır.

### Ayrıca

- **Kişi arama (isimlendirme penceresinde).** Son 5 kişi düğmesi yetmiyordu; 60 kişisi olan biri
  Mart'ta konuştuğu kişiyi orada bulamaz, adı elle yazar, ve **azıcık farklı yazılan bir ad ikinci
  bir kişi yaratır.** `Repository.SearchContacts()` Türkçe katlamayla arıyor (SQL'in `LIKE`'ı
  İ/ı için yanlış katlar ve *hata vermez*, sadece boş döner).
- **Ayarlar / model tablosu.** Beş model listeleniyor ama hangisinin indirilmiş olduğu
  yazmıyordu — yani seçimi yapmak için gereken tek bilgi eksikti. "Durum" sütunu eklendi.
- **Sol menü.** Rayla sayfa aynı renkteydi, ikisi tek yüzey gibi okunuyordu. Kendi zemini ve tek
  saç teli çizgisi verildi.
- **Kurulum sihirbazı iptal edilebilir oldu.** `SetupViewModel`'de `CancellationTokenSource` ve
  `Cancel` komutu; jeton beş çağrı noktasının hepsine geçiriliyor. Önceden hiçbirine
  geçmiyordu: pencere kapansa da pip, Python alt süreci ve iki saat zaman aşımlı indirme
  görünmeyen bir yerde devam ediyordu. `OperationCanceledException` genel `Exception`'dan
  **önce** yakalanıyor, yoksa iptal etmek sihirbazı kırmızıya boyuyordu.

### 7. Uygulama neyi yaptığını hiçbir yere yazmıyordu

Kullanıcı, hataları teşhis edebilmek için günlük dosyası istedi — haklı olarak: hedef makineyle
tek iletişim kanalı ekran görüntüsüydü ve eksik bir kütüphane bir gün boyunca "model inmiyor"
gibi göründü, çünkü ekranda sonucu vardı sebebi değil.

**Yapılan.** `src/VoiceTranscript.App/Services/AppLog.cs`.

- Gün başına bir dosya, 14 gün saklanır: `%LocalAppData%\VoiceTranscript.Data\logst-*.log`
- Her satır anında diske yazılır (tamponlanmaz) — işe yarayan satır neredeyse her zaman
  çökmeden önceki son satırdır, tampon da tam onu kaybeder.
- Yakalanmamış hatalar üç yerden toplanır: dispatcher, `AppDomain`, gözlemlenmemiş görevler.
  Dispatcher hatası `Handled` işaretlenir: bir sayfanın çizilememesi yüzünden ölen kaydedici,
  o sırada tuttuğu konuşmayı kaybeder.
- `PythonWorkerOptions.Diagnostic` eklendi; worker'ın stderr'i satır satır günlüğe akıyor.
  "gpu: RTX 4050 Laptop GPU (6 GB)" ve "cuda unusable: ... falling back to the processor"
  satırları buradan geliyor — başka hiçbir yerde görünmüyorlardı.
- Durum ekranına **Günlük** kartı: "Günlüğü kopyala" (son 3 gün) ve "Klasörü aç".

**Gizlilik.** Dosya *paylaşılmak üzere* yazıldığı için içine ne konduğu bir gizlilik kararı:
konuşma metni, kişi adı, API anahtarı ve içinde ad geçen dosya yolu **yazılmaz**. Dosyanın
başında bunu söyleyen bir blok var — göndermeye karar veren kişinin, hepsini okumak zorunda
kalmadan ne gönderdiğini bilmeye hakkı var.

### 8. Otomatik kayıt kapatılamıyordu · kayıt şeridi

`AppSettings.RecordAutomatically` ilk sürümden beri vardı ve **hiçbir yerde okunmuyordu.** Yani
otomatik kayıt hiçbir şekilde kapatılamıyordu — özel konuşmaları kaydeden bir uygulamada ciddi
bir eksiklik.

**Yapılan.**

- Orkestratör artık ayarı okuyor. Kapalıyken izleme sürüyor ve durum kartı "arama var,
  kaydedilmiyor" diyebiliyor — bu, kapatan kişinin istediği güvence.
- **Tepsi menüsüne işaretlenebilir "Otomatik kayıt" öğesi.** Ayarlar penceresine değil oraya,
  çünkü bu karar aramadan saniyeler önce veriliyor; dört tık derindeki bir pencere o anda
  ulaşılabilir değil.
- Ayarlar → Kayıt sayfasına iki kart: "Otomatik kayıt" ve "Kayıt şeridi".
- **`Views/RecordingOverlay`** — kayıt sürerken ekranın üst kenarında ince, yarı saydam bir
  şerit: yanıp sönen kırmızı nokta, süre, "Durdur" ve "bu görüşme boyunca gizle".

**Şeridin iki pencere stili load-bearing:**
`WS_EX_NOACTIVATE` olmadan şerit, arama bağlandığı anda klavye odağını arama penceresinden
çalar — yani uygulamanın her aramada yaptığı ilk şey aramayı bölmek olurdu.
`WS_EX_TOOLWINDOW` onu Alt+Tab dışında tutar.

**Neden varsayılan açık:** yalnızca tepsi simgesi varken, çalışan bir kaydediciyle kapalı olan
birbirinden ayırt edilemez. Konuşmalarının kaydedilip kaydedilmediğini bir bakışta anlayamayan
biri en kötüsünü varsayar — ve haklıdır.

### 9. Ayarlarda hangi modelin indirilmiş olduğu yazmıyordu

Beş model, hata oranları ve indirme boyutlarıyla listelenip seçim isteniyordu; seçimi yapmak için
gereken tek bilgi eksikti. "Durum" sütunu eklendi (`ModelPresenceConverter`).

### 10. Sol menü sayfadan ayrılmıyordu

Ray ve sayfa aynı zemindeydi, ikisi tek yüzey gibi okunuyordu. Kendi zemini ve tek saç teli
çizgisi verildi.

### Doğrulama

```
410 C# testi (409 geçti, 1 atlandı) + 56 Python testi — hepsi yeşil
```

Yeni testler: `ConversationMixTests` (9), `FailureTextTests` (7), `CallWindowsTests` (9),
`CallPersistenceTests` içine tek görüşme silme ve Türkçe kişi arama.

`RepositoryTests.DeletingAContactRemovesEveryTraceAndReportsItsAudioFiles` artık **gerçek dosyalar**
yazıp siliniyor mu diye bakıyor; önceden var olmayan yollara bakıyordu ve silme kodu ne yaparsa
yapsın geçiyordu.

---

## 2026-08-30 (ikinci tur) — Kalan istekler

### 11. Görüşmelerde arama ve tarih süzme

Bir kişiyle yüzlerce görüşme birikince liste işe yaramaz hâle geliyordu: hepsi bir tarih ve bir
süre. Kişi panelindeki görüşme listesine iki süzgeç eklendi.

- **Metin süzgeci** o kişinin görüşmelerinin *içinde* arar (`Repository.CallsMentioning`).
  `Search`'ten ayrı bir sorgu, çünkü sorular farklı: `Search` "bu nerede söylendi" der ve her
  eşleşen satırı ister; bu "hangisinde söylendi" der ve listeyi daraltır. Tam aramayı çalıştırıp
  gruplamak, bir kişinin görüşmelerini süzmek için bütün arşivi tarardı ve **sonuç sınırının
  ötesindeki görüşmeleri sessizce düşürürdü** — iki yüz görüşmesi olan biri hiç yokmuş gibi
  görünürdü.
- **Tarih süzgeci** `SearchPeriod`'u kullanıyor; `Bugün` ve `Dün` eklendi (`Until()` ile).
- Süzgeç açıkken **kaç görüşmenin gizlendiği** ve "Süzgeci kaldır" düğmesi gösteriliyor. Bu satır
  olmadan, on dakika önce açık unutulmuş bir süzgeç, iki görüşmesi olan bir kişiden ayırt
  edilemez — ve doğal sonuç "kayıtlarım silinmiş" olur.

### 12. Önemli anların ses kesiti

`src/VoiceTranscript.Core/Audio/AudioClip.cs` + `Services/ClipExporter.cs`. Defterdeki her kalemin
altında **"Sesi dışa aktar"**: o anın birkaç saniyesi ayrı bir WAV olarak yazılır.

- **Birleştirilmiş kayıttan** kesilir, tek taraftan değil. Kendisini doğuran soru silinmiş bir söz,
  başka bir sözdür.
- Alıntının **bittiği yer transkriptten** okunur, kelime sayısından tahmin edilmez. Tahmin iki
  yönde de yanlış olur: kısa kesilen kesit cümlenin ortasında biter, uzun kesilen kesit sonraki
  konuşmayı da taşır — yani o kişi hakkındaki bir alıntıya başkasının konuşması eklenir.
- İki ucuna **2 saniye pay** eklenir. Transkript parçası ilk hecede başlar; tam orada başlayan bir
  kesit kesik değil, **kurgulanmış** gibi duyulur — inandırıcı olması gereken bir şey için ölümcül.
- Dosya adında **kişi adı geçmez.** Bu dosyalar birine gönderilmek için üretiliyor; addaki bir isim,
  arşivde başka kimlerin olduğunu alıcıya söyler.

### 13. Soru sorma ekranı

`src/VoiceTranscript.Core/Analysis/ArchiveQuestions.cs` + `Sor` ekranı. Defter olduğu gibi duruyor.

Tasarım kısıtı defterinkiyle aynı: **model önüne konanı özetleyebilir, ona ekleme yapamaz.** Yani
bu, arama kutusu takılmış bir dil modeli değil; ucunda dil modeli olan bir getirme problemi:

1. Soru arama terimlerine çevrilir ve transkript dizininde aranır.
2. Eşleşen satırlar — *yalnızca onlar* — numaralanıp modele verilir.
3. Model kullandığı numaraları bildirmek zorundadır; **her dayanak listeye karşı doğrulanır.**

Üçüncü adım belirleyici olan. Birinin konuşmaları hakkında soru sorulan bir model, alıntılar cevabı
içermiyorsa akıcı, ikna edici ve **tamamen uydurma** bir anlatı üretir; okuyanın bunu gerçeğinden
ayırmasının yolu yoktur. Doğrulanmış dayanak, uydurma cevabın gösterecek bir şeyi olmaması demek —
ve hiçbir şey gösteremeyen cevap **gösterilmiyor.**

Arama hiçbir şey bulmazsa model **hiç çağrılmıyor.** "Bu konuda kayıt yok", bir paragraf
lafı dolandırmaktan iyi bir cevaptır ve bedava üretilir.

Türkçe soru sözcükleri (`ne`, `kim`, `hangi`, `mi`…) terimlerden ayıklanıyor: bırakılsa "ne
konuştuk" araması arşivin rastgele bir dilimini döndürür ve cevap soruyla ilgisi olmayan
satırlardan kurulur — bu da modelin halüsinasyon gördüğü gibi görünür, kötü sorgu gibi değil.

### 14. Dil desteği

Arayüzdeki **248 dizgenin tamamı** makineyle çıkarılıp `{loc:T anahtar}` biçimine alındı; Türkçe
sözlük markup'tan üretildiği için **birebir aynı**. İngilizce sözlük elle yazıldı. Ayarlar → Kayıt
bölümünden seçiliyor.

Türkçe temel dil, İngilizce çeviri — bu, alışılmışın tersi ve kasıtlı: bu uygulamanın hata
mesajları, defteri ve özellikle *neyin kaydedilip kaydedilmediğine* dair cümleleri Türkçe yazıldı
ve en çok emek isteyen kısım onlardı.

**İki tuzak, ikisi de sessiz:**

- WPF, nihai derlemesini `EmbeddedResource` öğelerini düşüren geçici bir projeyle üretiyor. Sözlük
  uygulama projesine gömülürse çıktıya **hiç ulaşmıyor** — derleme başarılı oluyor. Bu yüzden
  sözlükler `Core`'da.
- MSBuild, `strings.**tr**.json` adındaki `.tr.`'yi kültür etiketi sanıp dosyayı `tr\` altında bir
  uydu derlemesine koyuyor. csc'ye doğru `/resource:` argümanı bile geçiliyor, ama ana derlemede
  sözlük olmuyor. `WithCulture="false"` şart.

İkisinin de belirtisi aynı ve sinsi: **her ekrandaki her etiket kendi anahtarını gösterir**, hiçbir
yerde hata görünmez.

### 15. Hiç sınanmayan pencereler

Ayarlar (800+ satır markup), Kurulum ve kayıt şeridi hiçbir testte kurulmuyordu — ve dizge çıkarma
üçünün de markup'ını aynı anda yeniden yazdı. `WindowSmokeTests`'e eklendiler.

Bunu eklerken çıkan gerçek kusur: `SetupViewModel.LogFile` bir **alan başlatıcısında**
`App.Paths.Logs` okuyordu, yani bu görünüm modeli uygulama açılmadan **hiç kurulamıyordu.** İşi
kurulum yapmak olan tek ekran, bu yüzden hiç sınanamayan ekrandı. `EnvironmentSetup.Paths` eklendi,
global bağımlılık kaldırıldı.

### Doğrulama

```
439 C# testi (438 geçti, 1 atlandı) + 56 Python testi — hepsi yeşil
```

Yeni: `AudioClipTests` (8), `ArchiveQuestionsTests` (14), `LocalisationTests` (7).

---

## Bu makinede doğrulanamayanlar

Geliştirme makinesinde NVIDIA kartı ve ses donanımı **yok** (`Win32_SoundDevice` boş). Aşağıdakiler
yalnızca hedef makinede sınanabilir ve her turda yeniden bakılmalı:

| Konu | Nasıl sınanır |
|---|---|
| Gerçek ses yakalama | Kurulum → Ses yakalama adımı; iki akışta da seviye görünmeli |
| CUDA / cuBLAS | Kurulum → Ekran kartı satırı yeşil ve kart adı yazıyor olmalı |
| 60 dakikalık senkron | Her iki yola 60 sn'de bir 1 kHz bip, QPC çapalı fark ölçülür |
| WhatsApp arama penceresi başlığı | Gerçek arama sırasında görüşme "İsimsiz" mi geliyor |
| Türkçe doğruluk (WER) | 5 gerçek arama elle düzeltilip karşılaştırılır; hedef %15 altı |

---

## Sıradaki işler

Kullanıcının istediği, henüz yapılmamış olanlar:

İstenen her şey yapıldı. Sırada bekleyen, **hedef makinede ölçülmesi gerekenler**
(yukarıdaki tabloya bakın) ve ileride değerlendirilebilecek olanlar:

- **C# tarafındaki dizgeler** henüz sözlüğe alınmadı; arayüzün tamamı (248 dizge) alındı, ama
  görünüm modellerindeki bildirim ve durum metinleri Türkçe sabit duruyor. Mekanizma hazır:
  `Localisation.T()` çağırmak yeterli.
- **Telegram sohbet dökümü içe aktarma** — kullanıcı isteğiyle *opsiyonel* olarak planlandı,
  birincil değil. `Import/TelegramExport.cs` ayrıştırıcı hazır, arayüze bağlanmadı.
