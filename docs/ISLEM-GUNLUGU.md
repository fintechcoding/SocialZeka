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

## 2026-08-31 — Geliştirme ortamı ve iki sessiz ayar kusuru

Kullanıcı hedef makinede kullanmaya devam etti ve üç şey bildirdi:

1. *"Görüşme yaptım, arkada sesleri işlemeye çalışıyordu, kaydedildi sanırım ama görüşme bitince
   kim aradı ne yaptı bu çıkmadı."*
2. *"WhatsApp'tan ve Telegram'dan isimleri doğru yakalayamıyor."*
3. *"İşleme kısmında bir hata oluşursa ve o sırada bir görüşme yaptıysam, o görüşme bitince kayıt
   ekranı çıkmadı."*
4. *"Serdal'la yaptığım bir görüşme sistemde Uliana'nın altına kaydedilmiş; bunu elle taşıyabilmem
   lazım."*

Bunların hepsi `YAPILACAKLAR.md` dosyasında numaralandırıldı — bu tur onları **düzeltmedi**,
zeminini kurdu ve yol üstünde çıkan iki ayrı kusuru kapattı.

### 0. Geliştirme ortamı kuruldu ve taban çizgisi alındı

Bu makinede .NET SDK yoktu; derleme ve test hiç çalıştırılamıyordu. Kuruldu:
.NET 10.0.400, Python 3.12.10 + pytest 9.1.1, Inno Setup 6.7.3. Ayrıntılar ve tuzaklar
`docs/GELISTIRME.md` içinde.

**Taban çizgisi:** derleme 0 hata, C# 439 test / 0 kırık, Python 56 test / 0 kırık.

Bu sayı önemli bir şey söylüyor: **kod tabanı sağlam, bildirilen hatalar derleme veya test
kırıklığından gelmiyor.** Ama daha önemlisi şu — 495 test yeşilken görüşme sonrası akış
çalışmıyor, yani **hiçbir test o akışı uçtan uca sürmüyor.** Bu, projenin daha önce bizzat
yaşadığı kör nokta:

> *"Bu dikişin varlığı süs değil: bitmiş bir kaydın dosya yollarını ve süresini satırına geri
> yazan adım tamamen eksikti ve birkaç yüz testlik bir takımdan sağ çıktı, çünkü hiçbiri bir
> kaydı ses kartı olmadan baştan sona sürükleyemiyordu."* — `CallOrchestrator.cs:78`

> **Kural:** §1 düzeltmesinin ayrılmaz parçası, kaydı tespit → kayıt → bitiş → kuyruk → işleme →
> özet → kayıt ekranı boyunca süren bir test olmak zorundadır. Altyapı zaten var ve
> kullanılmıyor: `CallOrchestrator` yapıcısı `captureBackend` enjeksiyon noktası taşıyor ve
> `FileAudioSource` mevcut.

### 1. `DataRoot` ayarı hiçbir şey yapmıyordu

**Belirti.** Geliştirme, uygulamanın gerçekten kullanıldığı makineye taşınıyor. Deneysel bir
derlemenin gerçek görüşme arşivinin üstünde çalışmaması gerekiyor. Bunu sağlayacak ayar
(`AppSettings.DataRoot`) tanımlıydı ve "veri dizinini geçersiz kılar" diyordu.

**Asıl sebep.** Ayar **projede hiçbir yerde okunmuyordu**; tek geçtiği yer kendi tanımıydı.
`AppPaths` yapıcısı `root` parametresini zaten kabul ediyordu ama `App.xaml.cs` onu argümansız
çağırıyordu. Bu, bu projede daha önce görülmüş bir kusur biçiminin **üçüncü tekrarı** —
`RecordAutomatically` de uzun süre tanımlıydı ama okunmuyordu (`CallOrchestrator.cs:157`).

Ayarla çözülemezdi: `settings.json` veri kökünün *içinde* yaşıyor, yani ayarı okumak kökü zaten
bilmeyi gerektiriyor. Geliştirme için ayrıca yetersiz — dev derlemesinin gerçek kuruluma
dokunmamak için gerçek kurulumun ayar dosyasını değiştirmesi saçma olurdu.

**Yapılan.** `--data <klasör>` komut satırı anahtarı. Öncelik: komut satırı > ayar > varsayılan.

- `AppPaths.ResolveRoot` / `DataDirectoryFrom` / `AsksForDataDirectory` — karar **saf**, Win32'ye
  ve WPF'e bağlı değil, dolayısıyla tamamen test edilebilir.
- `App.xaml.cs` başlangıcı yeniden sıralandı: veri klasörü **günlük açılmadan önce** çözülüyor.
  Aksi hâlde günlük yanlış klasöre yazardı — ve günlük, hedef makineden hata bildirmenin tek
  kanalı.
- Günlüğün ilk satırı artık hangi veri klasörünün kullanıldığını yazıyor, varsayılan değilse
  açıkça işaretliyor. `--data` var olduğu andan itibaren "hangi veritabanıydı" sorusu ortaya
  çıkıyor ve bu soru tam da bir görüşmenin nerede olduğu aranırken sorulur.
- Hatalı `--data` (arkasında klasör yok) **sessizce varsayılana düşmüyor**; uygulama açıklama
  verip kapanıyor. Sessiz geri düşüş, anahtarın var olma sebebini yok ederdi.
- Klasör oluşturulamazsa Türkçe açıklamayla kapanıyor. Daha önce bu, günlük henüz açılmadığı için
  kimseye hiçbir şey söylemeyen bir çökme olurdu.

### 2. Ayarları kaydetmek, ekranda görünmeyen ayarları siliyordu

**Belirti.** §1'i yaparken fark edildi: `DataRoot` canlandırılırsa, kullanıcı ayarları bir kez
kaydettiğinde taşınmış arşiv **görünmez** olacaktı.

**Asıl sebep.** `SettingsViewModel.ToSettings()` sıfırdan yeni bir `AppSettings` kuruyordu, yani
listelemediği her alan varsayılana dönüyordu. 34 alandan 5'i listede yoktu. Üçü çağrı yerinde
`with` ile elle kurtarılıyordu; **ikisi kurtarılmıyordu:**

| Alan | Sonuç |
|---|---|
| `TranscriptRetentionDays` | Her ayar kaydında sıfırlanıyordu — **bugün canlı bir kusur** |
| `DataRoot` | Canlandırıldığı anda her kayıtta silinecekti |

**Yapılan.** Yama değil, biçim değişikliği: `ToSettings()` artık pencerenin açıldığı kaydı
düzeltiyor — `_original with { ... }`. `MainWindow.OpenSettings` içindeki elle kurtarma listesi
kaldırıldı; artık gereksiz ve zaten yanlış biçimdi — her yeni ayarda elle güncellenmesi
gerekiyordu ve unutmak sessizdi. **İleride eklenen hiçbir alan artık sessizce düşemez.**

Kusur sınıfı için test yazıldı: yansımayla `AppSettings`'in bütün alanları geziliyor ve ayarlar
ekranının düzenlemediği her alanın aynı değerle döndüğü doğrulanıyor. Sıfırdan kurmaya geri
dönülürse anında kırılıyor; ekrana yeni bir alan eklenip listeye yazılmazsa da kırılıyor.

### 3. Düzeltilmeyen ama kayda geçen bulgular

Bunlar araştırma sırasında çıktı, `YAPILACAKLAR.md` içinde numaralandırıldı, bu turda
**düzeltilmedi**:

- **Saklama süresi hiç uygulanmıyor** (§8.1). `AudioRetentionDays` ayarlar ekranında
  düzenlenebiliyor ve "şu kadar gün sonra ses silinir" diyor, ama **silen kod yok**. `retention`
  geçen tek yerler iki yorum (`CallOrchestrator.cs:417`, `Models.cs:115`) ve `IsPinned` sütunu.
  **Kullanıcı kararı: arşiv zaten tutulmalı**, yani varsayılan davranış doğru ve veri kaybı riski
  yok. Kusur, tutulmayan sözün kendisi: ya gerçekten uygulansın ya ayar kaldırılsın.
- **`TranscriptRetentionDays` tamamen ölü** (§8.2) — tanımı dışında hiç geçmiyor.
- **Öksüz ses dosyaları birikiyor** (§8.1b). `Discard` silemediği dosyalar için "süpürme alır"
  diyor; süpürme yok. Veritabanında satırı olmayan görüşme sesleri kalıcı olarak kalıyor.
- **Yanlış kişiye kayıt zinciri** (§7). Kök sebep bulundu: `CallWindows` uygulamanın kendi adı
  olmayan her pencere başlığını kişi adı sayıyor; isimlendirme penceresindeki "bu başlığı bu
  kişiyle eşleştir" kutusu **varsayılan olarak işaretli** (`LabelCallWindow.xaml:86`); yanlış bir
  başlık `title_binding`'e yazılınca o başlıkla gelen **her** görüşme aynı yanlış kişiye gidiyor
  ve `NeedsLabel` false olduğu için **kayıt ekranı hiç çıkmıyor**. Hata sessiz, kalıcı ve kendini
  besleyen. Ayrıca `AssignContact` taşımada eski kişinin sayaçlarını güncellemiyor ve
  `commitment`/`claim`/`flag` satırları görüşmeyle birlikte taşınmıyor.

### Doğrulama

```
dotnet build VoiceTranscript.slnx -c Debug   →  0 hata, 22 uyarı
VoiceTranscript.Tests.exe                    →  454 test, 450 geçti, 0 kırık, 4 atlandı
pytest (worker/)                             →  56 test, 56 geçti
```

Yeni: `DataDirectoryTests` (15 test) — `--data` çözümlemesi ve ayar gidiş-dönüşü.

### Ders: önce belgeyi oku

`dotnet test`'in bu projede çalışmadığını araştırıp iki farklı çözüm denedim
(`dotnet.config`, `global.json`), ikisi de işe yaramadı ve ikisini de geri aldım. **Bunların
hepsi `docs/GELISTIRME.md` içinde zaten yazılıydı** — `test.ps1` de sebebini kendi başlığında
açıklıyordu.

> **Kural:** bir araç beklenmedik davrandığında ilk bakılacak yer `docs/GELISTIRME.md` ve bu
> günlüktür. Bu proje tam olarak bu yüzden belge tutuyor; okumadan araştırmak, yazılı olan bir
> cevabı ikinci kez satın almaktır.

Denemenin tek kazancı, bulgunun artık kanıtla yazılmış olması: hangi iki yolun denendiği ve her
birinin tam olarak ne döndürdüğü `docs/GELISTIRME.md` içinde tabloya alındı, bir daha denenmesin.

---

## 2026-08-31 (ikinci tur) — Kişi adı yakalama ve pencerelerin çağrı durumundan çıkarılması

### Kullanıcının verdiği bilgi, çözümün tamamını belirledi

> *"WhatsApp'ta, Telegram'da bir çağrı olunca **yeni pencerede** arayan kişinin, konuşulan kişinin
> ismi yazıyor."*

Bu, buradan doğrulanması mümkün olmayan tek şeydi — bu makine sanal, ses donanımı yok, messenger
oturumu yok. Ve tasarımı baştan değiştirdi.

**Neden bu kadar önemli:** tek bir anlık görüntüde çağrı paneli ile "o an açık sohbeti gösteren ana
pencere" **ayırt edilemiyor**. İkisi de "başlığı uygulamanın kendi adı olmayan bir pencere". Farkı
yaratan şey **belirme**: çağrı paneli bir saniye önce yoktu. Bu yalnızca ardışık iki anket
karşılaştırılarak görülüyor, ve tek bir anket üzerinde ne kadar akıllıca düşünülürse düşünülsün
bulunamazdı.

### 1. Pencereler artık çağrı durumunu belirlemiyor

**Asıl sebep — ve projenin kendi ilkesini ihlal etmesi.** `CallDetector`'ın kendi belgesi şunu
yazıyor: *"Neden pencere başlıkları yerine ses oturumları: kullanıcı Windows'u Türkçe çalıştırıyor
ve düğme veya pencere metnine dayanan her sezgisel yöntem patlamayı bekleyen bir yerelleştirme
tuzağı."* Sonra bir pencere bayrağı içeri sızmış ve **zilin başlamasını, çağrının bitmesini ve
kimle konuşulduğunu** birden sürmeye başlamış. Üstelik o bayrak "çağrı penceresi var" değil,
**"messenger'ın herhangi bir penceresi var"** demekti.

Üç ayrı kusur bundan doğuyordu:

- **Açık sohbet penceresi = sonsuz zil.** Detektör sürekli `Ringing` kalıyor, 3 dakikada bir zil
  zaman aşımına uğrayıp `Abandoned` üretiyordu — ve `Abandoned` **kaydı siler**. Elle başlatılmış
  bir kayıt, var olmayan bir çağrı yüzünden her üç dakikada bir sessizce yok oluyordu.
- **Tepsiye küçültmek = telefonu kapatmak.** Görüşme sırasında messenger'ı küçültmek kaydı tek bir
  örnekte bitiriyordu. Kalan konuşma ayrı bir görüşme olarak dosyalanıyor, ilk parça 5 saniyenin
  altındaysa **uyarısız siliniyordu**.
- **Çağrıdan önceki başlık çağrıya atfediliyordu.** Bayrak detektörü sürekli `Ringing`'de tuttuğu
  için başlık `Reset()` görmüyordu; görüşmeden dakikalar önce açık olan sohbetin adı kaydediliyordu.

**Yapılan.** Pencereler tek bir soruyu cevaplıyor: *bu görüşme kiminle.* Çağrı durumu **yalnızca
sesten** geliyor.

- `CallWindowPresent` → `AppWindowPresent`. Adı artık ne olduğunu söylüyor.
- Zil yalnızca render akışıyla başlıyor; cevapsız zil yalnızca sessizlikle kapanıyor.
- Pencerenin kaybolması çağrıyı **bitirmiyor**. `TrustWindowDisappearance` kaldırıldı.
- `InCall` için **4 saatlik tavan** eklendi. Sessizlik tek çıkıştı ve takılı bir ses oturumu onu
  etkisiz bırakıyordu; kayıt uygulama kapanana kadar sürüyordu. Tavana çarpmak **normal bir
  `Ended`** üretiyor — kayıt kayıp değil, bitmiş ve isimlendirmeye sunulmuş oluyor. Ses hâlâ
  akıyorsa yeni bir çağrı başlıyor, ki bu doğru: aksi hâlde takılı bir oturum sonraki gerçek
  aramayı da yutardı.
- Uygulama atfı yalnızca sesten. Pencere varlığı iki messenger için birden doğru olabildiğinden,
  çağrı sonundaki sessiz örneklerde atıf diğerine kayıyor ve bitmiş görüşme yanlış uygulama altında
  raporlanıyordu.

### 2. İsim yakalama

- **Önek rozeti.** `IsShellTitle` yalnızca `"Telegram (3)"` biçimini biliyordu; `"(3) WhatsApp"`
  biçimi kişi adı sayılıyordu. Arşiv **"(3) WhatsApp"** adlı bir kişi ediniyordu — ve her farklı
  okunmamış sayısı için ayrı bir tane, her biri bir kişinin geçmişinin bir dilimini tutan. İki
  biçim de eleniyor artık; yalnızca parantez içindeki **rakam dizisi** rozet sayılıyor, yani
  `"Ahmet (iş)"` bozulmuyor.
- **İlk eşleşmede durmak yok.** `Look` z-sırasındaki ilk pencerede duruyordu, yani cevap kullanıcının
  en son neye tıkladığına bağlıydı. Artık hepsi toplanıyor ve `Choose` karar veriyor.
- **Yeni beliren pencere kazanıyor** — yukarıdaki kullanıcı bilgisi. `Likely` güven.
- **Belirsizlikte susuyor.** Birden fazla aday varsa ve hiçbiri yeni değilse, ön plandaki `Possible`
  ile öneriliyor; o da yoksa **isim yazılmıyor.** Yanlış isim isimsizden kötü: isimlendirme
  penceresindeki "hatırla" kutusu varsayılan işaretli, yani bir yanlış tahmin kalıcı bir eşleşmeye
  dönüşüyor ve kişi "biliniyor" göründüğü için pencere bir daha hiç çıkmıyor.
- **Doğru uygulamadan okunuyor.** `Look` ses oturumunun suçladığı uygulamayı alıyor. Önceden ses
  Telegram derken başlık WhatsApp penceresinden gelebiliyordu; öğrenilen bağ `(başlık, app)` ile
  anahtarlandığı için o eşleşme bir daha asla tutmuyordu.
- **Daha iyi başlık üzerine yazabiliyor.** `TitleTrust` eklendi; `CallDetector` ilk gördüğünü
  kilitlemek yerine daha güvenilirini kabul ediyor.
- **Süreç listesi genişledi.** Store'dan kurulan Telegram'ın paket kimliği, Telegram çatalları
  (AyuGram, 64Gram, Kotatogram), WhatsApp Business ve **Signal Desktop** (kullanıcı isteği).

### 3. Tanı çıktısı — tahmini bitirmek için

`VoiceTranscript.exe --pencereler` çalıştırıldığında, izlenen uygulamaların **bütün** görünür
üst düzey pencereleri masaüstüne bir dosyaya yazılıyor: başlık, pencere sınıfı, boyut, ön planda
mı, her biri için "kişi adı olabilir mi" kararı, ve seçilecek isim.

Bu bilerek **ayrı** bir dosyaya yazılıyor, uygulama günlüğüne değil: çıktı kişi adı içerebilir ve
o günlük, kullanıcıya "içinde konuşma metni, kişi adı ve API anahtarı yoktur" sözüyle paylaşılmak
üzere veriliyor. Söz tutulmalı.

> **Kural:** bu makinede doğrulanamayan bir şey hakkında tahmin yürütmek yerine, doğrulanabildiği
> yerden veri getirecek bir araç yaz. "(3) WhatsApp" kişisi, bu kuralın yokluğunun bedeliydi.

### Doğrulama

```
dotnet build VoiceTranscript.slnx -c Debug   →  0 hata
VoiceTranscript.Tests.exe                    →  474 test, 470 geçti, 0 kırık, 4 atlandı
```

Önceki tur 454 testti; 20 yeni test eklendi. Karar `CallWindows.Choose` içinde ve **saftır** —
Win32'ye de WPF'e de bağlı değil, dolayısıyla gerçek bir arama olmadan tamamen sınanabiliyor.

**Hedef makinede sınanacak:** gerçek bir aramada isim doğru geliyor mu; gelmiyorsa `--pencereler`
çıktısı neyi gösteriyor.

---

## 2026-08-31 (üçüncü tur) — Görüşme sonrası akış ve kaydı doğru kişiye taşıma

Bu tur `YAPILACAKLAR` §1'i (görüşme sonrası akış) ve §7'yi (kişi onarımı) kapattı.

### 1. Tespit döngüsü artık hiçbir şeyi beklemiyor

**Asıl sebep.** Denetimin baş bulgusu, ilk teşhisimden ciddi biçimde kötüydü. Ben "isimlendirme
penceresi *işlemeyi* kilitliyor" demiştim; gerçek şu: **tespitin tamamını donduruyordu.**

Zincir: `Tick()` senkron ve tek bir arka plan döngüsünden çağrılıyor; `FinishRecordingAsync`
içindeki tek `await`, `CallFinished?.Invoke`'tan **sonra**; abone `Dispatcher.Invoke` +
`ShowDialog()` yapıyor. Sonuç: pencere ekranda kaldığı sürece döngü `Tick()` içinde park hâlinde,
`PeriodicTimer` kaçırılan tikleri düşürüyor, ve **o sırada yapılan görüşme hiç görülmüyor** —
satır yok, dosya yok, ses kalıcı olarak kayıp.

`LabelCallWindow.xaml:9-10`'daki `Topmost="True"` + `ShowInTaskbar="False"` bunu besliyordu:
pencere öne çıkmayı bıraktığında görev çubuğunda geri dönüş yolu yok.

**Yapılan — ve neden `InvokeAsync` yetmezdi.** Denetimin uyardığı gibi, sorun `Invoke` değil,
**örnekleme döngüsünün iş yapan iş parçacığıyla aynı olması**. `CompleteCall` senkron ve SQLite
kilidi başkasındaysa `busy_timeout` başına 5 saniye bekliyor; cihaz açmak da senkron. İkisi de
tespiti tek başına durdururdu.

- `Tick()` artık **yalnızca örnekliyor** ve olayı bir `Channel`'a yazıp dönüyor.
- Ayrı bir tüketici kayıt yaşam döngüsünü **sırayla** işliyor (tek kaydedici, tek geçerli çağrı
  olduğu için sıra artık tasarımın özelliği, zamanlamanın değil).
- İşleme **ayrı** bir kuyrukta. Yazıya dökme dakikalar sürüyor ve o sırada ne sonraki kaydın
  alınması ne de "bu kim" sorusu bekliyor.
- `FinishRecordingAsync`'in **tamamı** tek bir hata kapısında (kardeşi `BeginRecordingAsync` ile
  simetrik). Yalnızca `Stop()` sarılıydı ve `async Task` senkron fırlatmadığı için çağıranın
  `catch`'i sahte bir güvenlik ağıydı.
- `CallFinished` artık `GetInvocationList()` üzerinden abone abone çağrılıyor. Çok abonelikli
  delege ilk fırlatanda duruyordu: tek bir dinleyicinin hatası hem kayıt ekranını hem listeyi hem
  de yazıya dökmeyi birden götürüyordu.
- `Dispose` artık süren kaydı **düzgün bitiriyor**. Önceden yalnızca `Dispose` ediliyor, dönen
  sonuç atılıyordu — görüşme sırasında çıkmak o konuşmayı kaybettiriyordu.
- İsimlendirme penceresi tek örnek; ikisi üst üste açılıp alttaki ulaşılamaz kalamıyor.

### 2. Sıradan bir görüşme de özet alıyor

`AnalysisPipeline` özeti yalnızca taahhüt/iddia/bayrak bulunmuşsa yazıyordu. Söz verilmemiş,
rakam ya da tarih geçmemiş bir konuşma — yani **görüşmelerin çoğu** — hiç özet almıyordu.
Artık böyle bir görüşme metnin kendisinden özetleniyor. Bu yol alıntı doğrulamasından geçmediği
için istem "metinde geçmeyen hiçbir şey ekleme" konusunda ayrıca kesin.

### 3. İşleme bitince kullanıcıya söyleniyor

Özet aslında **gösteriliyordu** (`ContactsPage.xaml:267`), ama hazır olduğu hiç söylenmiyordu:
kullanıcının gidip araması gerekiyordu. `CallProcessed` olayı eklendi; işleme bitince tek satırlık
bir bildirim çıkıyor — kim, ne kadar sürdü, özetin ilk cümlesi. Başarısızsa sebebi.

### 4. Yapay zekâ servisi yoksa denenmiyor

Kullanıcının uyarısı: *"hiçbir API bağlanamamışsa denemesin."* Haklıydı ve tehlikesi
sanıldığından büyüktü — paylaşılan `HttpClient`'ın zaman aşımı **10 dakika** ve her metin parçası
için ayrı işliyor, yani 12 parçalık bir görüşme 2 saat asılı kalıp işleme yuvasını tutuyordu.

- `AppSettings.LlmReachableInPrinciple` — ağa dokunmadan, ayarlardan cevaplanıyor. Anahtarsız bir
  bulut sağlayıcı hiç denenmiyor; metin zaten yazılmış olduğu için kaybedilen özet, görüşme değil.
- Tek bir tamamlama isteğine **5 dakikalık** sınır kondu. Paylaşılan istemcinin 10 dakikası bir
  saatlik sesi yüklemek için doğru, bir sohbet tamamlaması için değil.
- **K3 düzeltildi:** `catch (OperationCanceledException)` süzgeçsizdi ve zaman aşımı tam olarak
  onu fırlatıyordu — görüşme sessizce `Queued`'a dönüyor, "işlenemedi" listesinde hiç çıkmıyor ve
  **her açılışta** yeniden deneniyordu. Artık yalnızca gerçek kapanış kuyruğa geri koyuyor.

### 5. Kaydı doğru kişiye taşıma

Kullanıcının bildirimi: *"Serdal'la yaptığım bir görüşme Uliana'nın altına kaydedilmiş."*

Bu bir düzen işi değil, **doğruluk** işi — ve otomatik atıf güvenilir yapılamayacağı için kalıcı
olarak gerekli. Messenger'ların sunduğu tek şey pencere başlığı; başlık bazen kişi, bazen o an
açık olan sohbet, bazen okunmamış sayacı.

- `AssignContact` artık **taşıma** yapabiliyor: görüşme + o görüşmeden çıkan `commitment`,
  `claim`, `flag` satırları **tek işlemde** birlikte gidiyor. Yarım taşıma en kötüsüydü: söz bir
  kişide, konuşma başkasında kalıyor ve iki geçmiş birden bozuluyordu — üstelik ikisi de eksiksiz
  görünerek.
- **İki kişinin de** sayaçları yeniden hesaplanıyor. Yalnızca hedef hesaplanıyordu; tek çağıran
  isimsiz kaydı ilk kez atadığı için görünmüyordu.
- `ForgetTitleBinding` + `TitleBindings` — yanlış öğrenilmiş başlık bağı çözülebiliyor ve
  listelenebiliyor. **Taşıma tek başına yarım onarım**: bağ kalırsa o başlıkla gelen her görüşme
  yine aynı yanlış kişiye gider, ve kişi "biliniyor" göründüğü için soru bir daha hiç sorulmaz.
- `MergeContacts` — bir insan iki kişi olmuşsa birleştiriliyor (görüşmeler, defter, mesajlar,
  başlık bağları; çakışan bağlarda hayatta kalanınki kalıyor).
- `RenameContact` — `UpsertContact` ada göre eşleştiği için yeniden adlandırma ayrı bir işlem
  olmak zorundaydı; aksi hâlde düzeltilmiş yazım ikinci bir kişi yaratıyordu.
- Arayüz: Kişiler sayfasındaki görüşme araç çubuğunda **"Kişiyi değiştir"**. Açılan pencere var
  olan kişiyi seçmeye **veya yenisini oluşturmaya** izin veriyor — taşınacak kişi çoğu zaman
  henüz yok, ki zaten yanlış atamanın sebebi de o. Kaç defter kaydının birlikte taşınacağını
  önden söylüyor ve "başlığı çöz" kutusu **varsayılan işaretli**.

### 6. Duman testi boşluğu

`LabelCallWindow` ve yeni `MoveCallWindow` hiçbir testte kurulmuyordu. Bu önemliydi: isimlendirme
penceresi her yeni kişide açılıyor, ve oradaki bir markup hatası (yeniden adlandırılmış kaynak
anahtarı, var olmayan simge) tam olarak **"kayıt ekranı çıkmadı"** belirtisini verir — kayıt
başarısızlığından ayırt edilemez. İkisi de artık `WindowSmokeTests` içinde gerçekten kuruluyor.

### Doğrulama

```
dotnet build VoiceTranscript.slnx -c Debug   →  0 hata
VoiceTranscript.Tests.exe                    →  495 test, 491 geçti, 0 kırık, 4 atlandı
pytest (worker/)                             →  56 test, 56 geçti
```

Önceki tur 474 testti. Yeni: `ContactRepairTests` (14), özet davranışı (3), LLM erişilebilirliği
(4), detektör tavanı ve pencere kuralları.

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

---

## 2026-08-31 — v0.9.7: oynatıcı, kesit çıkarma, Claude/OpenAI, kullanım istatistiği

Kullanıcı uygulamayı çalıştırırken bildirdiklerinin toplu turu. Hepsi tek sürümde toplandı.

### Arayüz

| Bulgu | Sebep | Ne yapıldı |
|---|---|---|
| "Durum" sekmeleri kaymış görünüyor | `TabControl` zaten `Margin="24"` veriyordu, gömülü sayfalar kendi `24`'ünü ekliyordu → içerik 48'den başlıyordu, sekme şeridi 24'ten | `ProcessingPage` kök `Grid` 0'a, `AiStatusPage` `PadPageScroll` yerine `0,4,18,24` |
| Sekmeler birbirinden ayırt edilemiyor | WPF-UI varsayılan şeridi seçili sekmeyi saç teli bir çizgi ve çok hafif bir zeminle işaretliyor | `SegmentedTabs` / `SegmentedTabItem` stilleri (`Theme.xaml`): seçili olan kenarlıklı dolu hap + yarı kalın yazı, altında ayırıcı çizgi |
| Görüşme penceresindeki oynatıcı yetersiz | Dalga formu yok, sürükleme yok, ▶ ikonu hiç ⏸ olmuyordu | Aynalı dalga formu, sürüklenebilir zaman çubuğu, durumu gösteren ikon, konuşmacı değiştirme düğmesi |
| "Kişiyi değiştir" düğmesi gereksiz | Sağ tık menüsüne taşınmıştı, araç çubuğunda hangi görüşmeye uygulanacağı belirsizdi | Araç çubuğundan kaldırıldı |

**Sürükleme** `Controls/Scrubbable.cs` ile eklendi — iliştirilmiş özellik, çünkü iki ekran çok
farklı şeyler çiziyor ve ortak olan yalnızca X koordinatını ana çevirme aritmetiği. `SeekTo`
sürükleme sırasında kullanılamaz: dosyayı yeniden açıp cihazı yeniden başlatıyor, fare hareketi
başına bir kez. `ScrubTo`/`EndScrub` konumu ayrı taşır, sesi bir kez yerleştirir.

### Sohbet kabarcığında sağ tık → ses kesiti

`ClipExporter.ExportExchange`. Birim bir satır **ve ardından gelen cevaplar** — çünkü insanların
tartıştığı birim bu. Cevabın tek başına kesilmesi "bağlamından koparmışsın" itirazını davet eder;
bunun cevabı sonradan tartışılmak yerine çıktının içine konur. Menü cevap sayar (0/1/3/5/10).

Sesin yanına tarih, kişi ve konuşulanları taşıyan bir `.txt` yazılır — bir yıl sonra dosya bir
klasördeki otuzdan biri olduğunda hangi görüşmeden geldiğini hatırlatan şey bu.

### Yapay zekâ sağlayıcıları

Anthropic **aynı protokol değil**: `/v1/messages`, `x-api-key`, sürüm başlığı, sistem istemi
üst düzey alan, yanıt içerik blokları dizisi. Mevcut istemciyle bunların hiçbirini anlatmayan bir
400 alınıyordu. Ayrı `AnthropicClient`. Yapılandırılmış çıktı zorlanmış bir *tool* ile alınıyor;
`response_format` yok ve nazikçe JSON istemek eşdeğer değil.

- `LlmProviderKind`: `Anthropic`, `OpenAi` eklendi (enum metin olarak saklanıyor, sıra güvenli)
- `LlmClientFactory` — beş çağrı yeri doğrudan `new OpenAiCompatibleClient` yapıyordu
- `ModelDirectory` — sağlayıcıdan **canlı** model listesi; OpenRouter fiyat ve bağlam uzunluğuyla
- `ModelPickerWindow` — arama kutulu seçici. OpenRouter tek başına birkaç yüz model yayımlıyor;
  filtresiz bir liste metin kutusundan daha kötü bir arayüz olurdu. Arama kimlik, ad ve fiyat
  satırında birden yürür: insanlar "haiku", "ucuz", "128k" diye arıyor ve bunların yalnızca biri
  kimlikte.

### Kullanım istatistiği (`processing_run`)

Uygulamanın bildiği ama hiç söylemediği iki sayı. **Gerçek zaman çarpanı** önemli olan: 1'in
altı, bir saatlik konuşmanın işlenmesinin bir saatten uzun sürdüğü, yani aramalar sürdükçe
birikmenin büyüdüğü anlamına gelir — ve bu sırada çalışan bir uygulama takılmış olandan ayırt
edilemez. Gözlenen en kötü değer 0,4× (47 dakikalık görüşme, 3,5 saat).

İkincisi jeton: bulut modeli sessizce gerçek para harcıyor, ilk haber aylık fatura oluyordu.

Satırlar konuşma içeriği, kişi veya başlık taşımaz — aşama, motor, süre, jeton. Yine de
`ON DELETE CASCADE`: "her şey silinecek" birebir doğru kalsın diye. Toplamlar küçülür, dürüst
olan bu.

**571 test, 0 hata** (28 yeni: 18 sağlayıcı, 10 kullanım).

### v0.9.8 — Windows açılışında başlatma uygulamaya alındı

Vardı, ama yalnızca kurulumdaki bir onay kutusu olarak: işaretlenince
`{userstartup}` altına bir kısayol yazılıyordu. Üç türlü bozuk:

- işaretlemeyen sonradan fikrini değiştiremiyordu,
- işaretleyen durdurmak için başlangıç klasörünü bulmak zorundaydı,
- **sessiz güncelleme kurulumu varsayılan görev seçimiyle yeniden çalıştırıyor**, yani bilinçli
  bir "hayır" tamamen ilgisiz bir sebeple onaylanan bir güncellemeyle geri alınabiliyordu.

Artık `AppSettings.StartWithWindows` niyeti tutuyor, `Services/AutoStart` her açılışta makineyi
ona uyduruyor. Bu aynı zamanda kimsenin seçmediği bir durumu da onarır: eski kurulumdan kalan
girdi, ya da bir temizlik aracının sildiği girdi.

Başlangıç klasörü kısayolu yerine `HKCU\...\Run`: kısayol COM ile yazılmak zorunda, ayrıştırmadan
okunamıyor ve her "PC'nizi hızlandırın" aracının ilk sildiği şey. Kayıt defteri değeri okumak bir
çağrı, yazmak bir çağrı, yokluğu da kesin.

Windows'un başlattığı kopya `--tray` alıyor ve pencere açmıyor — yoksa her açılışta karşılaşılan
ilk şey, tek amacı bir arama olana kadar sessizce beklemek olan bir uygulamanın istenmeyen
penceresi olurdu. Mantıklı bir varsayılan böyle kapatılan bir şeye dönüşür.

Kurulumdaki görev kaldırıldı, yerine yalnızca masaüstü kısayolu bırakıldı. İki yerden yönetilen
bir açma-kapama, çeliştiğinde hangisinin kazandığı belirsiz olur.

**576 test, 0 hata.**

### v0.9.9 — Bildirilen işin denetimi ve çıkan dört boşluk

Kullanıcı, kaldırıldığı söylenen "Kişiyi değiştir" düğmesinin hâlâ durduğunu bildirdi. Doğruydu:
düzenleme hiç uygulanmamıştı (kullanılan komut Python bulunamadığı için başarısız oldu, başka bir
yöntemle tekrar edilmedi) ve iş yapılmış diye raporlandı.

Bunun üzerine **bildirilen 14 iddianın tamamı** dosyaya karşı denetlendi; bozuk bulunanlar ikinci
bağımsız bir doğrulayıcıyla teyit edildi. 10'u tam çıktı, 4'ünde gerçek boşluk vardı.

| # | Boşluk | Kullanıcı ne görüyordu |
|---|---|---|
| 1 | Anthropic'te "Bağlantıyı sına" ASR problayıcısından geçiyordu | **Yanlış anahtar yeşil onaylanıyordu** |
| 2 | `succeeded:false` üretimde hiçbir yerden geçmiyordu | Hata sayacı kalıcı 0; tertemiz geçmiş |
| 2b | `ArchiveQuestions` ölçülmüyordu | "Sor" jetonları faturada var, ekranda yok |
| 3 | Adres boşken "Modellere gözat" | Sessiz hiçbir şey — mesaj gizli sayfaya yazılıyordu |
| 4 | `docs/YAPILACAKLAR.md:99` bağlantısı | Taşıma sonrası `docs/docs/...`'e çözülüyordu |

**1 en kötüsüydü**, çünkü düğme bozuk değil *yanlış cevabı doğru diye onaylıyordu*: probe yalnız
Bearer konuşuyor, Anthropic `x-api-key` + sürüm başlığı istiyor, gelen 400'ü de probe "401 değilse
yetkilidir" kuralıyla yeşile çeviriyordu. Artık `LlmClientFactory` + `ModelDirectory` üzerinden,
yani sağlayıcının kendi lehçesiyle.

**Yazdığım test, dokunmadığım eski bir hatayı da yakaladı:** `OpenAiCompatibleClient
.IsAvailableAsync` isteği yetkilendirme başlığı olmadan atıyordu (`LlmClient.cs:263`). Yani doğru
bir OpenAI/OpenRouter anahtarı hem ayarlarda hem Durum ekranında "ulaşılamıyor" görünüyordu. Test
yazmanın karşılığı tam olarak bu.

Ayrıca Signal desteği uçtan uca doğrulandı (süreç adları, `RecordSignal`'in gerçekten okunması,
kabuk başlıklarının kişi sanılmaması, ayar anahtarı) ve kaynak testi olmadığı için testleri
yazıldı.

**592 test, 0 hata** (16 yeni).

## 2026-09-02 — Denetimin minörleri, çökme sonrası kayıt kurtarma, görünümlerin kalan çevirisi

Üçüncü denetimin durdurucuları ve majörleri bir önceki turda kapanmıştı; bu tur kalan minörler ve
onların peşinden çıkan iki gerçek boşluk.

**Kayıt kurtarma.** Görüşme satırı kayıt başlarken açılır, ses yolunu kayıt düzgün bittiğinde
öğrenir. Uygulama çökerse, elektrik giderse ya da Görev Yöneticisi'nden öldürülürse iki WAV ay
klasöründe sağlam durur, satır ise bir sonraki açılışta "ses yok" diye Failed'a düşerdi — arşiv
kaybolmamış bir kaydı kayıp ilan ediyordu. Artık açılışta `ReclaimStrandedRecordings` ses bağlanmamış
bekleyen satırlar için kaydedicinin verdiği adla (`call-{id}-mic/far.wav`) dosyaları arar; başlığı
sıfırda kalmış WAV'ın gerçek uzunluğunu dosya boyundan geri yazar (`WavRepair`), satırı Queued'a alır.

**Karışım çakışması.** `ConversationMix` geçici dosyayı sabit `.partial` adıyla yazıyordu; aynı
görüşme için oynatma ve dışa aktarma üst üste gelince ikincisi birincinin yarım dosyasını kesiyor,
son biten yırtık dosyayı yerine taşıyordu. Ad koşuma özel; unutma bütün yarımları süpürüyor.

**Sınama başlığı.** `SttProbe.TestAsync` herkese Bearer gönderiyordu. Bakiye sorguları ElevenLabs'in
`xi-api-key`, Deepgram'ın `Token` beklediğini biliyordu, sınama bilmiyordu; geçerli anahtar
"reddedildi" görünüyordu.

**Çeviri.** Görünümlerde 382 Türkçe metin hâlâ XAML içinde duruyordu (275 anahtar vardı, 630 oldu).
İngilizce arayüz yarı Türkçeydi. Gizli görüşme sayısı satırı da artık çeviriden biçimleniyor.

Küçükler: bayat durdurma isteği, yeniden açılan pencerede saklanmayan gözlemlerin söylenmesi,
`HasCitations`, kullanılmayan tema kalınlıkları, KURULUM.bat betik adı, `ForgetAudio` akış
takibi, ayarlar CUDA satırı, `InstalledNormally`, çift OpenRouter, palet tıklaması, kaydedici arka
ucunun Dispose'u.

**788 test, 0 hata** (5 yeni: WAV onarımı ×4, sahipsiz satır sorgusu ×1) + 57 Python.

Hâlâ bu makinede doğrulanamayan: çökme sonrası gerçek bir kaydın kurtarılması (ses donanımı yok);
kod yolu sentetik WAV ile test edildi.

### Aynı gün, ikinci tur — eski dalda kalan iki istek

GitHub'a geçerken eski yerel dal (`yedek/oturum-30agustos`) kenara alınmıştı; iki istek orada
kalmış, yeni ağaca hiç gelmemişti. İkisi de kullanıcının açık isteğiydi.

**Ayrıntılı günlük.** "Tespit: Idle (hoparlör=False…)" satırı bir zamanlar bildirim olarak çıkıyor,
kullanıcı "bu bilgi bari admin panelden açılıp kapatılabilir olsun" demişti. Artık `VerboseLog`
ayarı: dedektörün her geçişi (hangi sinyal — pencere başlığı asla) ve her çevirinin hangi aşamaya
kadar geldiği yalnızca dosyaya yazılır. Durum sayfasındaki anahtar anında kaydeder (bu sayfada
Kaydet yok; anahtarı açanın sonraki işi sorunu tekrarlamaktır). Ayarlar penceresi anahtarı taşır,
başka bir ayarı kaydetmek onu sıfırlamaz.

**"Şu an kullanılan" kartı.** "Burada hâlâ neyin aktif olduğu belli değil" — tablo tek başına üç
soruyu da cevaplamıyordu: hangi model seçili, dosyaları burada mı, nerede çalışacak. Kart model
adını, indirilmiş olup olmadığını (ilk görüşme bekler mi), hangi kartta ya da işlemcide
çalışacağını (`Usable`'a göre, kart adıyla), özeti ve uyarıyı gösterir; seçim değişince anında
güncellenir.

**788 test, 0 hata** + 57 Python. Görsel doğrulama hedef makinede.

**Hedef makine için derleme.** `publish.ps1 -Version 2.1.6-beta1 -RequireInstaller` — kendi
kendine yeten Release yayını + Inno Setup paketi, `main @ 8a950ae` üzerinden.
`dist/VoiceTranscript-Setup-2.1.6-beta1-win-x64.exe` (66 MB),
SHA-256 `fbbd4cb6be858fd58c81ed30ae802e52e4e9cdead297b5908c18c758dc33ff51`. Sürüm etiketi
atılmadı; gerçek 2.1.6, hedef makinede iki yeni ekran ve kurtarma yolu görüldükten sonra.
