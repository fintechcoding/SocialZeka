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

## 2026-09-02 (ikinci tur) — WAV arşivi Opus'a: yirmi kat küçük

Kullanıcı: *"WAV dosyaları çok fazla konuşma olursa aşırı yer kaplar, buna bir çözüm bulur musun?"*

**Hesap.** İki 16 kHz mono PCM akışı saatte 230 MB; karışım kopyasıyla 345 MB. Günde iki saat
görüşme, ayda 14-20 GB. Dinlenecek toplam süre belki birkaç dakika — bir söz ya da rakam kayda
karşı denetlenirken.

**Seçenekler.** Saklama süresi (ses silinir — kanıt gider), sessizlik kırpma (var, %20-30),
ADPCM (4×, kalite düşük), AAC/Media Foundation (16 kHz girişi desteklemez, yeniden örnekleme
gerekir), Opus (tam bu sinyal için tasarlanmış: 16 kHz konuşma, 24 kbit/s şeffaf, 20×). Opus;
`Concentus` + `Concentus.Oggfile` ile saf C#, yerel bağımlılık ve ffmpeg yok.

**Tasarım.**
- Görüşme tamamen işlendikten sonra (döküm arşivde, sessizlik kırpılmış) iki akış ayrı ayrı
  Ogg/Opus'a çevrilir. Kimin ne söylediği karışmaz.
- Her akış yan dosyaya kodlanır, geri çözülüp örnek sayısı orijinalle karşılaştırılır, ancak
  ondan sonra WAV silinip satır yeni yolu öğrenir. Çökme ya orijinali ya doğrulanmış kopyayı bırakır.
- Dökümü olmayan kayıt sıkıştırılmaz (saklama süpürmesiyle aynı kural): o görüşme için ses tek kayıttır.
- `AudioMaterialiser`: okuyan herkes PCM ister — `PcmReader.Open`, oynatıcı, kesit, dalga formu,
  worker'a giden yol. `.wav` olduğu gibi geçer, `.ogg` bir kez `cache/audio/`'ya çözülür; 2 GB
  üstünde en eskisi silinir, görüşme unutulunca kopyası gider. Görüşme penceresi çözmeyi dalga
  formuyla aynı işçi iş parçacığında yapar, tıklandığında önbellek hazırdır.
- Eski kayıtlar açılışta en düşük öncelikli ayrı bir iş parçacığında, kuyruğun dışında, birer
  birer sıkıştırılır. Diskin gerçekten geri geldiği yer burası.
- Uzantı `.ogg` (Obsidian ve Windows kabuğu söylenmeden çalar). Ayarlarda "Sesi sıkıştır"
  anahtarı, varsayılan açık.

**792 test, 0 hata** (4 yeni: kodek gidiş-dönüş ve içerik doğrulaması, önbellek tek çözme ve
unutma, yarım dosya bırakmama, sıkıştırma birikimi sorgusu) + 57 Python.

Bu makinede doğrulanamayan: gerçek bir saatlik kaydın kodlama süresi (Concentus saf C#; tahmin
hedef makinede akış başına 1-2 dakika, en düşük öncelikte) ve oynatıcının `.ogg` görüşmede ilk
tıklama gecikmesi.

**Hedef makine için derleme (beta2).** `publish.ps1 -Version 2.1.6-beta2 -RequireInstaller`,
`main @ 76f1966`. `dist/VoiceTranscript-Setup-2.1.6-beta2-win-x64.exe`, SHA-256 `5ae230a272d575365880c535c15e997809cb8d1e200dd7dc8bcf223a399002a2`.
İlk açılışta eski WAV'lar arka planda sıkıştırılmaya başlar; günlükte "sıkıştırma birikimi" satırı
ve görüşme başına "sıkıştırıldı: … MB → … MB" görülür.

## 2026-09-02 (üçüncü tur) — Notion listesi, ElevenLabs, yapılacaklar, Telegram tespiti, Opus denetimi

Kullanıcı Notion'daki sorun listesini yapıştırdı; aynı sırada "Telegram'da görüşme yakalanmadı" dedi
ve "bu yaptıkların speech-to-text'i bozmaz, değil mi?" diye sordu. Üç ajan denetimi başlatıldı;
oturum limiti yüzünden yalnızca ElevenLabs araştırması ve Opus denetiminin bulucuları tamamlandı,
hakemler ve Telegram bulucuları düştü. Kalan iş elle yapıldı.

**Speech-to-text bozulmadı mı?** İlk yazıya dökme her zaman sıkıştırılmamış PCM'i okur; sıkıştırma
döküm ve çözümleme bitip sessizlik kırpıldıktan sonra çalışır. Bulucuların bulduğu gerçek riskler
yarış durumlarıydı, hepsi kapatıldı: görüşme başına ses kilidi (`_audioBusy`), tek birikim iş
parçacığı, önbellek süpürmesinde üç saatlik taze-dosya koruması, saklama süpürmesinde durum
koruması. Deney (OpusAlignmentTests) Concentus'un pre-skip yazmadığını ölçtü: çözülen ses 104
örnek (6,5 ms) geçti, bir çerçeve dolguluydu. Lookahead ve örnek sayısı Ogg etiketine yazılıp
çözerken düşülüyor; artık örnek örneğine aynı saat.

**Notion maddeleri.** Başlıktan kişi atama varsayılan kapalı, her görüşmeden sonra sorulur, genel
başlıklar ("Voice call") hiç bağlanmaz · çift tıklama tepsideki pencereyi öne getirir · Sözlük
(hotwords + initial_prompt + bulut prompt/keyterm/keywords) ve Karışık dil anahtarı · worker
BelowNormal, işlemcide iki çekirdek boş · Yapılacaklar sayfası (todo tablosu, göç v9; öneriler ve
hatırlatmalarla tek liste, Ctrl+8) · Opus arşivi (önceki tur).

**ElevenLabs neden hata verdi.** Katalog onu OpenAI biçimli sayıyordu; worker
`/audio/transcriptions`'a Bearer ile gidiyor, servis 404 döndürüyordu — sınama ise model listesini
okuyup yeşil gösteriyordu. Kendi motoru yazıldı (`cloud-elevenlabs`: `/speech-to-text`,
`xi-api-key`, `model_id`, `language_code`, kelime listesi → segment), varsayılan `scribe_v2`.
Deepgram için de (`cloud-deepgram`: ham gövde, `Token`, `/listen`, `keywords`/`keyterm`).
Katalog motoru adlandırıyor (`WorkerEngine`), orkestratör soruyor.

**Telegram.** Kod okumasında süreç eşleme (Telegram.exe + Store paketi) ve bütün uç noktaların
taranması sağlam. En olası kaçırma: hoparlör oturumunun kesik kesik gelmesi — ardışık iki örnek
hiç oluşmuyor, mikrofon açık kalıyor, dedektör boşta kalıyor. Dedektör artık mikrofon üç örnek
açık ve hoparlör (ardışık olmasa da) iki kez duyulmuşsa aramayı başlatıyor; tek bildirimli sesli
mesaj yine arama değil. Unigram eklendi. Ayrıntılı günlükte "boşta ama ses veriyor" satırı on beş
saniyede bir dört sinyali yazıyor: bir sonraki kaçırma günlükten okunur.

**807 test, 0 hata** (15 yeni) + 68 Python (11 yeni).

Bu makinede doğrulanamayan: gerçek ElevenLabs/Deepgram yüklemesi (anahtar yok; istek biçimi
belgeye ve canlı 404/401 sondasına göre), Telegram'ın gerçek oturum davranışı (günlük gerekli).

**Hedef makine için derleme (beta3).** `publish.ps1 -Version 2.1.6-beta3 -RequireInstaller`,
`main @ 7adc6f7`. `dist/VoiceTranscript-Setup-2.1.6-beta3-win-x64.exe`, SHA-256 `95d206e01baecd45204ba6656e81ebdea9eacf7d5b455a8173bab90886269f14`.
Denemede: Durum → Ayrıntılı günlük açıkken bir Telegram görüşmesi yap; günlükteki "tespit" satırları
kaçırmanın nedenini gösterir. Ayarlar → Sözlük'e Sumsub, KYC gibi terimleri yaz.

## 2026-09-02 (dördüncü tur) — Sözlük kendini büyütüyor, model kutusu dürüst

Kullanıcı: *"Sözlük vs. oluşturamayız, bir sürü kelime var, bunun başka yolu olabilir"* ve
*"ElevenLabs'ten model listesini çekemiyordu; anahtar girildiyse modele tıklanınca çekmeli,
çekebiliyorsa doldurmalı, çekemiyorsa 'anahtar hatalı' demeli, servis desteklemiyorsa elle giriş."*
Ayrıca sohbete bir API anahtarı yapıştırıldı; kullanılmadı, kaydedilmedi, iptal edilmesi söylendi.

**Otomatik sözlük.** `VocabularyMiner`: Whisper'ın cümle ortasında büyük harfle yazdığı ve arşivde
en az iki kez geçen sözcükler (Türkçe ek kesme işaretinden kırpılır); `Repository.VocabularyNames`
kişi adları ve kişi bilgileri; `Vocabulary.Compose` yazılanı önde, adları, madenlenenleri sırayla
birleştirir (300 hotword, 40 terimlik prompt — uzun prompt sessizliğe yankılanır). Orkestratör on
dakikada bir toplar; "Sözlüğü arşivden kendiliğinden büyüt" varsayılan açık.

**Model kutusu.** `SttProbe.ListModelsAsync` üç cevabı ayırır: liste geldi / anahtar reddedildi
(401-403) / servis liste vermiyor (katalog + elle giriş). ElevenLabs `/v1/models` ses modellerini
döndürür — 200 anahtarı kanıtlar, adlar katalogdan; Deepgram `stt[].canonical_name`; OpenAI
biçimliler `data[].id`. Kutu açılınca anahtar başına bir kez sorar, durum satırına yazar.
Bağlantı sınaması da aynı yordamı kullanır; "scribe_v2 listede yok" yanlış uyarısı bitti.

**822 test, 0 hata** (15 yeni: sözlük madenciliği ×6, sonda ×8, depo ×1) + 68 Python.

**Hedef makine için derleme (beta4).** `publish.ps1 -Version 2.1.6-beta4 -RequireInstaller`,
`main @ c64a4e7`. `dist/VoiceTranscript-Setup-2.1.6-beta4-win-x64.exe`, SHA-256 `01776fbc7ebdee108d5f0c9df12bfec049381d5514a752f9f2d73be8ecda2882`.

**Görüşme eylemleri her listede aynı (beşinci tur).** Kullanıcı: "Burada başarısız görüşmeleri
silme gibi özellikler de olmalı." Genel Bakış'ın satır menüsü üç eylem, Kişiler'inki altı; silme
hiçbirinde yoktu. `Services/CallActions` tek yer: sil (onay + kalan dosya uyarısı), taşı, yeniden
çevir/çözümle, ses dosyasının yeri. Genel Bakış menüsü sekiz eylem; Kişiler'de menüde ve satırda
görünür çöp kutusu. 822 test yeşil.

**Hedef makine için derleme (beta5).** `main @ d262bae`,
`dist/VoiceTranscript-Setup-2.1.6-beta5-win-x64.exe`, SHA-256 `0b8f175b37bbd9aec1a5a18fce01e5a3ec90ef1871d2d66b7cbe9fd07fda2247`.

## 2026-09-02 (altıncı tur) — Arayüz değerlendirmesi ve onaylanan düzeltmeler

Kullanıcı ilk ekrandaki görüşme menüsünün ekran görüntüsüyle "başarısız görüşmeleri silme gibi
özellikler de olmalı; UI'ı incele, önerilerini söyle" dedi. Dört gözlü ajan değerlendirmesi
(ilk ekran, görüşme yaşam döngüsü, bilgi mimarisi, tutarlılık — 54 öneri, hakem sıralaması)
yapıldı; öneriler sunuldu, kullanıcı "bunlar mantıklı, düzelt" dedi.

**Yapılanlar.** Görüşme eylemleri tek yerde (`Services/CallActions`: sil, taşı, yeniden yazıya dök,
yeniden çözümle, dosya) ve her listede aynı: Genel Bakış, Kişiler (satırda görünür çöp kutusu), kişi
penceresi, görüşme penceresi ("…" menüsü + hata şeridinde "Tekrar dene…"), İşlemler, etiketleme
penceresi. `CallActions.Changed` kabuğu yeniler. Menü maddeleri duruma göre kapanıp sebebini söyler.
Başarısız satır kırmızı, sebepli, satır üstünde tekrar dene/sil; Dikkat kartı sebepleri gruplar ve
kör toplu tekrar yerine listeye ya da ayarlara gider. `CallStateText` tek durum sözlüğü, `SpeakerText`
tek konuşmacı adı, tek ses→metin fiili. İsimsiz satır "?" avatar ve satırdan "Kim olduğunu söyle…".
Tek sayfa boşluğu, Windows 11 içerik katmanı, koyu temaya göre değişen anlam renkleri, pencere
başlığı/tepsi ipucu durumu söylüyor, işleme sürerken ilk ekran yenileniyor, ölü fiiller ve ray
fazlalıkları kaldırıldı.

**836 test, 0 hata** (14 yeni) + 68 Python. Görsel doğrulama hedef makinede.

Hakem sıralamasında kalanlar (yapılmadı, kullanıcının kararına bırakıldı): rayı 7'ye indirme
(Takvim + Yapılacaklar + Bugün → Ajanda, Ara + Sor → tek sayfa, İşlemler'e kapı), üst kartın
"bu sabah beni bekleyen" sayılara dönmesi, tek görüşme satırı şablonu, Kişiler sayfası ile kişi
penceresinin birleşmesi, klavye yolu (Del/Enter/Shift+F10), İşlemler'de toplu seçim.

**Hedef makine için derleme (beta6).** `main @ bd6da3d`,
`dist/VoiceTranscript-Setup-2.1.6-beta6-win-x64.exe`, SHA-256 `e347015d475c11b3cec257f8dce6e0a9559a4b4af55114c4c06047e75ef8d9b5`. İki metin düzeltmesi
(Krediyi sor → Bakiyeyi göster; llama-server özetindeki "uygulama başlatır" iddiası) pakete girmedi,
bir sonraki derlemede.

## 2026-09-02 (yedinci tur) — Servis ekleme akışı ve Görüşmeler arşivi

Kullanıcı: "kullanım kolaylığı açısından da AI servisleri ekleme/denemeyi ve görüşmeler ekranını
değerlendir." İki gözlü ajan değerlendirmesi (30 öneri, kuşkucu doğrulama) sunuldu; "tamam bitir"
üzerine önerilen sırayla uygulandı: (A) görüşme ekranı hızlı kazanımlar `9b0e4a8`, (B) servis
akışı, (C) Görüşmeler sayfası.

**Servis ekleme ve sınama.** Servisler bloğu her zaman görünür — varsayılan "yalnızca bu makinede"
modunda gizli olduğu için servis eklenecek yer bulunamıyordu; servis eklemek modu Otomatik'e
çevirir. "Servis ekle" önce sağlayıcıyı sorar (özetiyle). Kart sırası ihtiyaca göre: "Anahtar al ↗"
+ anahtar → model → sına/bakiye; adres "Gelişmiş" altında. Anahtar yapıştırılınca bir saniye sonra
kendiliğinden sınanır; kart başlığında hazır / anahtar reddedildi / ulaşılamıyor / model bulunamadı /
anahtar eksik rozeti. LLM tarafında "Anahtar al" bağlantısı, `ILlmClient.ProbeAsync` ile 401 ve DNS
hatası ayrı sebeplerle, model kutusu açılınca liste çekiliyor. Çözümleme varsayılanı "Seçilmedi":
eski varsayılan llama-server hiç başlatılmayan bir sunucuydu ve ilk kullanıcı sessizce özetsiz
kalıyordu; ayarlar, görüşme satırı ("çözümleme servisi seçilmedi") ve Durum bunu söylüyor. Kurulum
sihirbazına yedinci adım "Çözümleme servisi".

**Görüşmeler.** Yeni sayfa (rayda Genel Bakış'ın altında, Ctrl+9, palette): bütün görüşmeler güne
göre; kişi / dönem / uygulama / durum / etiket süzgeçleri ve ad kutusu (`CallsViewModel.Filter`
saf, testli); ilk ekrandaki satır ve sekiz fiillik menüyle aynı; ilk ekran "Tümü →" ile buraya
gönderiyor. Beta6'ya girmeyen iki metin düzeltmesi de bu pakette.

**842 test, 0 hata** (5 atlanan: ASR ağırlığı ister) + 68 Python. Görsel doğrulama hedef makinede.

Kalanlar (kullanıcının kararına bırakıldı, yapılmadı): rayı 7'ye indirme, üst kartın sayılara
dönmesi, tek görüşme satırı şablonu, Kişiler sayfası ile kişi penceresinin birleşmesi, klavye yolu,
İşlemler'de toplu seçim, defter tekrarlarının birleşmesi, görüşme penceresinin 7 sekmeden 5'e inmesi.

**Hedef makine için derleme (beta7).** `main @ eca3507`,
`dist/VoiceTranscript-Setup-2.1.6-beta7-win-x64.exe`, SHA-256 `13a767076e28fd2c6895d0f97c92f630f2c61c7ecaa1591b45c4c002cbab6b38`.

---

## 2026-09-02 (sekizinci tur) — Kendi Whisper sunucumuz servis listesine

Kullanıcı: "bu servisi de entegre edelim, api key alanı sadece ayarlarda doldurulsun, kullanıcı
elle doldursun, bulut servisler listesine ekle." Elimize bir de düz metin API anahtarı geldi;
anahtar **hiçbir yere yazılmadı** — ne koda, ne teste, ne belgeye, ne varsayılana. Kart açılır,
kullanıcı yapıştırır, bir saniye sonra kendiliğinden sınanır. (Sohbete yapıştırıldığı için o
anahtarın iptal edilip yenilenmesi gerekiyor.)

### Sunucunun kendi şeması, kendisine anlatılandan farklı çıktı

Servis `GET /openapi.json` adresini **anahtarsız** yayınlıyor. Prompt'taki tarifle iki yerde
çelişiyor ve iki çelişki de sessiz:

**1. Alan adı `timestamp_granularities` — köşeli parantezsiz, ve düz bir metin.** OpenAI'nin
yazımı `timestamp_granularities[]`. Sunucu FastAPI ve FastAPI **tanımlamadığı form alanlarını
sessizce atar**. Yani ortak bulut motoru bu sunucuya olduğu gibi bağlansaydı: 200, kusursuz bir
Türkçe metin, ve `words` dizisi hiç yok. Defterdeki her alıntı söylendiği anı kaybederdi ve
hiçbir ekranda hata görünmezdi. Bu, uygulamanın dayandığı tek şeyi — "alıntıya tıkla, o anı
dinle" — sessizce silen bir kusur. Motorun ayrı yazılmasının asıl sebebi bu.

**2. `/v1/jobs` başka bir gövde istiyor.** Yalnızca `file`, `language`, `prompt`,
`word_timestamps`. `model` yok, `response_format` yok. Sunucunun kendi açıklaması da bu ucun ne
için var olduğunu yazıyor: *"Cloudflare'in 100 saniyelik origin timeout'unu bu şekilde aşarız."*
Prompt "25 dakikaya kadar senkron çalışır" diyordu; sunucunun kendi açıklaması bunu yalanlıyor.
Kuyruk aynı anda tek iş tuttuğu için, **kaydın uzunluğu senkron isteği güvenli yapmaya yetmez** —
üç dakikalık bir parça bile başkasının bir saatlik işinin arkasında bekleyebilir.

### Yapılan

**Yeni motor** `worker/vt_worker/engines/ex5_engine.py` (`cloud-ex5`). Üç sınır koda geçti:

- *Boyut.* `max_upload_bytes` artık motor başına. Ortak motor OpenAI'nin 24 MiB'ında kalıyor
  (kataloğun en dar sınırı), ex5 90 MiB'a çıkıyor. PyAV'ın kurulu olmadığı makinede bu fark
  gerçek: ses ham WAV olarak dakikada 1.92 MB, yirmi dakikalık parça 38 MB — OpenAI sınırında
  reddedilen görüşme, burada sadece yavaş olanı.
- *Süre.* 180 saniyenin altındaki parçalar senkron uçtan (tek gidiş-dönüş, `model` ve
  `response_format` destekli), üstündekiler `/v1/jobs`'tan gidiyor: gönder, beş saniyede bir sor,
  `completed` olunca `result`. Uzunluğu bilinmeyen parça da işe gidiyor — güvenli varsayılan.
  **Senkron istek yine de 524 alırsa parça işe düşüyor**, hata vermiyor: ses o noktada zaten
  yüklenmiş ve kaybedilecek tek şey görüşmenin kendisi.
- *Eşzamanlılık.* Parçalar zaten sırayla yükleniyordu; iş beklerken 502/kopuk bağlantı
  **yeniden yüklemeye değil beklemeye** yol açıyor — iş sunucuda duruyor, ikinci kopya göndermek
  tek GPU'lu bir kuyrukta cevaba giden en yavaş yol.

**Ortak motorda iki durum artık okunabilir.** 413 ve 524 emeklilikte `api_error` idi ve mesaj,
Cloudflare'in HTML hata sayfasının ilk 400 karakteriydi — görüşme satırında "neden başarısız
oldu" diye görünen şey buydu. İkisi de artık cümle: hangi sınır, hangi adres. 524 yeniden
denenmiyor (aynı istek yüz saniye sonra aynı yere varır), 413 zaten kalıcı.

**Katalog** `SttProviders.cs`: `ex5`, adres `https://stt.ex5.ai/v1`, motor `cloud-ex5`, bakiye
ucu yok, `SignupUrl` yok — anahtar elden veriliyor, olmayan bir kayıt sayfasına link koymaktansa
kartta link hiç görünmüyor. **Listedeki yeri davranıştır:** `Find` tanımadığı bir tür için
`All[^1]`'e düşer ve o son eleman "Özel adres" olmalı; gerçek bir sağlayıcıyı sona eklemek eski
ayar dosyalarındaki her bilinmeyen türü bizim sunucumuza yönlendirirdi. İlk sıra da yüklü:
düz "Servis ekle" düğmesi `All[0]`'dan kart açar.

C# tarafında **hiçbir tür-özel dal gerekmedi**: Bearer, standart `/v1/models`, bakiye yok —
`SttProbe.Authorise`'ın default dalı zaten doğru olanı yapıyor.

### Testler

`worker/tests/test_ex5.py` (19 test): iki isteğin alan adları birebir (`timestamp_granularities`
var, `timestamp_granularities[]` yok; iş gövdesinde `model` yok), kısa parça senkron / uzun parça
iş / bilinmeyen parça iş, 524'ten işe düşme, yanlış anahtarın ikinci kapıyı denememesi, iş
yoklama döngüsü, 404/başarısız/bitmeyen iş, 90 MiB tavanı. `test_cloud_retry.py`'a 413/524
sınıflandırması ve "ortak motor en dar sınırda kalıyor" güvencesi. C# tarafında lehçe teorisine
`ex5 → cloud-ex5` satırı, bakiyesiz sağlayıcı listesine `ex5`, ve iki yeni koruma: katalogdaki her
motor adının worker'ın kaydettiği bir ad olması (iki program arasında bunu bağlayan hiçbir şey
yok — uyuşmazlık derleme hatası değil, görüşmeden *sonra* gelen "Unknown engine" hatasıdır) ve
listenin "custom" ile bitip "openai" ile başlaması.

**846 test · 841 geçti · 0 kırık · 5 atlandı** + **89 Python**. `docs/GELISTIRME.md` taban çizgisi
454/56'da kalmıştı, gerçek sayılara güncellendi.

### Bu makinede doğrulanamayan

Gerçek bir anahtarla tek bir yükleme yapılmadı — anahtar sohbetten geldiği için kullanılmadı.
Şema ile doğrulanan her şey doğrulandı (`/health` 200, `model_loaded: true`, `max_upload_mb: 95`;
`/openapi.json` ile bütün alan adları). **Anahtar girildiğinde bakılacak iki şey:**
`GET /v1/models` hangi model adını döndürüyor — katalogdaki `whisper-1` sunucunun kendi şema
varsayılanı, ama liste başka bir ad veriyorsa kart "model bulunamadı" der ve açılır kutu doğru
adı zaten gösterir; ve işin `result` gövdesinin `segments`/`words` taşıdığı (taşımazsa metin yine
gelir, sözcük zamanları gelmez).

**Paket.** `v2.1.6` tam sürüm olarak etiketlendi — beta1'den beta7'ye kadar biriken her şey ve
ex5 servisi tek pakette. Tam sürüm olduğu için `/releases/latest` bunu döndürür ve **kurulu her
kopya otomatik güncellemede bunu görür.** Sürüm numarası etiketten geliyor; installer'ı ve
sağlama toplamını GitHub Actions üretiyor, iki test takımı da yayından önce orada koşuyor.

---

## 2026-09-02 (dokuzuncu tur) — 403'ün anahtarla ilgisi yoktu

v2.1.6 hedef makinede denendi. Günlük üç ayrı kusuru aynı anda gösterdi.

### 1. Worker'ın adı yok, Cloudflare de adsızları banlıyor

```
22:32:36.694  deneniyor: ex5 Whisper (kendi sunucumuz) @ https://stt.ex5.ai/v1 · model whisper-1
22:33:00.571  İşleme başarısız: ... API anahtarı kabul edilmedi (403). Ayarlardan anahtarı denetle.
```

**Anahtar doğruydu.** Ayarlar ekranı aynı anahtarla "hazır · 1 model listelendi" diyordu — C#
tarafı `HttpClient` kullanıyor, worker `urllib`. `urllib` kendini `Python-urllib/3.12` diye
tanıtıyor ve Cloudflare bunu doğrudan reddediyor: **403, gövde "error code: 1010"** ("site sahibi
tarayıcını engelledi"). Anahtar gerektirmeyen `/health` ucuyla doğrulandı — varsayılan başlıkla
403, herhangi bir gerçek isimle 200.

Yani mesaj kullanıcıyı çalışan bir anahtarı denetlemeye gönderiyordu. **Yanlış talimat, ham hata
kodundan kötüdür** — çünkü uygulanır, sonra anahtar yeniden girilir, sonra yine başarısız olur.

**Yapılan.** `USER_AGENT` sabiti; `_send` her isteğe ekliyor (bir lehçenin unutması görünür bir
hata değil, iki hafta sonra başkasının 403'ü olarak dönerdi), ex5'in yoklama GET'i de taşıyor.
Ayrıca 403 artık ikiye ayrılıyor: gövdede Cloudflare'in numaralı reddi varsa kod `blocked` ve
mesaj "anahtarla ilgili değil" diyor; servisin kendi JSON reddi ise `auth` olarak kalıyor.

### 2. Uyarı yanlış şirketi söylüyordu

```
22:32:36.692  Bu görüşme yazıya dökülmek üzere OpenAI Whisper API servisine yükleniyor.
22:32:36.694  deneniyor: ex5 Whisper (kendi sunucumuz) @ https://stt.ex5.ai/v1
```

İki milisaniye arayla. Yönlendirme doğruydu, etiket yanlış: uyarı `AsrCatalog`'un model satırının
adını yazıyordu, oysa yükleme sırayla denenen **uç noktaya** gider ve model satırının adı orada
sadece süs. Sesin makineden çıktığını söyleyen uyarı, kullanıcının güvenmek zorunda olduğu tek
satır; yanlış şirketi söylemesi uyarı olmamasından kötü. `CallOrchestrator` ve Genel Bakış artık
`UsableSttEndpoints.FirstOrDefault()?.ResolvedName` yazıyor.

### 3. Kayıtlı anahtar "anahtar eksik" görünüyordu

`SttEndpointViewModel` kurucusunda `_apiKey` alanı doğrudan atanıyor, bu da `OnApiKeyChanged`'i
çalıştırmıyor; rozetin alan başlangıcı ise `KeyMissing`. Sonuç: yapılandırılmış her servis, anahtar
yeniden yazılana kadar turuncu "anahtar eksik" rozetiyle açılıyordu. Artık kayıtlı anahtarı olan
kart "sınanmadı" ile açılıyor — dürüst olan bu: anahtar var, henüz kimse servise sormadı.

**846 test · 841 geçti · 0 kırık · 5 atlandı** + **92 Python** (üçü de testli: her isteğin ad
taşıması, yoklamanın da taşıması, 1010'un `auth` değil `blocked` olması).

**Doğrulanan.** Anahtar geçerli, `/v1/models` tek model listeliyor ve adı `whisper-1` — kataloğun
varsayılanı doğru çıktı. Kalan tek belirsizlik işin `result` gövdesinin `words` taşıyıp taşımadığı.

**Paket.** `v2.1.7`, tam sürüm.

---

## 2026-09-02 (onuncu tur) — Aynı iki cümlenin duvarı

v2.1.7 hedef makinede doğrulandı: uyarı artık "Ses ex5 Whisper (kendi sunucumuz) servisine
yükleniyor" diyor ve görüşme gerçekten oraya gidiyor. Cloudflare teşhisi de kullanıcı tarafından
doğrulandı — sunucu "bana hiç istek gelmedi" dedi, çünkü 403 kenarda veriliyordu ve istek Mac
mini'ye hiç ulaşmıyordu. Anahtar denenmemişti bile.

**Kalan kusur: aynı cümlenin duvarı.** Servisi düşük bir birikimde her kayıt bir çift üretiyor —
"…yükleniyor", "…başarısız" — dakikada bir, ve ekran aynı iki cümlenin sütunu oluyor. Yirmi kayıt,
kırk toast.

Bariz kural — "bir öncekiyle aynıysa atla" — **hiçbirini yakalamıyor**, çünkü çift dönüşümlü
geliyor ve arka arkaya iki uyarı hiçbir zaman eşit olmuyor. Yakalanması gereken şey her cümlenin
en son ne zaman söylendiği. `NoticeRepeatGuard`: saf, saat enjekte edilmiş, beş dakikalık pencere,
tam metin üzerinden anahtarlanıyor. Aynı sebeple düşen iki kayıt bir kez söylenir — satırların
kendisi kayıt başına ayrıntıyı zaten taşıyor; başka türlü ifade edilen her şey tanımı gereği yeni
bilgidir ve gelmeye devam eder. Pencere kısa: birikimi toplamak için var, oturumu susturmak için
değil — hele sesin makineden çıktığını söyleyen uyarıyı hiç.

Bastırılan bir hata yine de oturumu "sorunlu" işaretliyor: o bayrak işlerin durumuyla ilgili, bu
cümlenin daha önce söylenip söylenmediğiyle değil.

**851 test · 846 geçti · 0 kırık · 5 atlandı** + **92 Python**.

**Paket.** `v2.1.8`, tam sürüm.

---

## 2026-09-02 (on birinci tur) — ElevenLabs'ın iki hatası tek hataymış

Kullanıcı defterdeki eski başarısızlıkları gösterdi. İki farklı hata görünüyordu:

```
ElevenLabs Scribe: 404 (https://api.elevenlabs.io/v1/audio/transcriptions): {"detail":"Not Found"}
ElevenLabs Scribe: Sunucuya ulaşılamadı (…/audio/transcriptions): EOF occurred in violation of protocol (_ssl.c:2406)
```

**İkisi de aynı sebep, ve sebep zaten düzelmişti.** URL `/audio/transcriptions` — ElevenLabs
`/v1/speech-to-text` sunuyor. Lehçe yönlendirmesi (`endpoint.Provider.WorkerEngine`) `7adc6f7` ile
geldi ve `v2.1.6`'da; `git merge-base --is-ancestor 7adc6f7 v2.1.5` **hayır** diyor. O satırlar
2 Eylül 17:08–17:14'te, yani makine `2.1.5` koşarken oluşmuş.

**Neden bazıları 404 bazıları EOF?** Canlı sunucuya karşı üretildi: olmayan rotaya **1 MB** gövde
göndermek `EOF occurred in violation of protocol (_ssl.c:2406)` veriyor, aynı rotaya birkaç bayt
göndermek temiz 404. Ağ geçidi, koyacak yeri olmayan bir gövdeyi okumak yerine bağlantıyı
sıfırlıyor. Günlükteki dağılım da tam bu: kısa görüşmeler (00:15, 00:26) 404, uzunlar (02:36,
07:57, 11:08) EOF. **Tek yanlış adres, bir akşamda alakasız görünen iki hata.**

Doğru uç bugün doğrulandı: `POST /v1/speech-to-text` → `422 model_id gerekli`, yani motorumuzun
gönderdiği şekli kabul ediyor. Geçersiz anahtar da düzgün `401` dönüyor — orada kusur yok.

**Yapılan.** Kod tarafında düzeltilecek bir şey kalmamıştı; düzeltilen mesajın kendisi. Bir TLS
kütüphanesinin dosya adı ve satır numarası (`_ssl.c:2406`) görüşme satırına olduğu gibi
düşüyordu ve okuyan için hiçbir anlamı yok. Artık: "Sunucu yükleme sırasında bağlantıyı kapattı
(adres). Çoğunlukla adresin bu servise ait olmadığı anlamına gelir; ağ kesintisi de olabilir."
Yeniden denenebilir kalıyor — ağ değiştiren bir dizüstü de aynı hatayı verir ve o gerçekten
düzelir. Sıradan çözümleme hataları kendi sözlerini koruyor.

Eski satırlar için "Tekrar dene" yeterli: 2.1.8 onları `/v1/speech-to-text`'e gönderecek.

**851 test · 846 geçti · 0 kırık · 5 atlandı** + **93 Python**.

**Paket.** `v2.1.9`, tam sürüm.

---

## 2026-09-02 (on ikinci tur) — 24 kbps bir görüşmenin dörtte üçünü yedi

Sunucu tarafındaki ölçüm, sıkıştırmanın maliyetini sayıyla gösterdi. **Aynı kayıt, dört kez:**

| bitrate | çıkan kelime |
|---|---|
| 21.5 kbps | **1624** |
| 20.6 kbps | iyi |
| 19.1 kbps | felaket |
| 18.2 kbps | **330** |

Eşik ~20 kbps ve `OPUS_BITRATE = 24_000` tam o uçurumun kenarındaydı — Opus, aralıklı konuşmada
hedefinin altına düştüğü için gerçekte 18-21 kbps çıkıyordu. Kayıp **%80**. Üstelik çöp segment 0,
zaman tutarsızlığı 0: model **uydurmuyor, duymuyor** — 659 saniyenin 520'sinde konuşma bulamamış.
Bir insan için 18 kbps hâlâ gayet anlaşılır; modelin dinlediği ince ayrıntı çoktan atılmış.

### Sayı yanlış değildi, soru yanlıştı

Sıkıştırmanın tek işi başkasının tavanının altına inmek. İkinci bir işi varmış gibi — kimsenin
istemediği yerden yer kazanmak — sabit bir sayıyla uygulanıyordu. Oysa sınıra sığması gereken birim
görüşme değil, **20 dakikalık parça**:

| | ham (kayıtta olduğu gibi) | sınırın payı |
|---|---|---|
| 20 dk parça | 38.4 MB | — |
| OpenAI / Groq 24 MiB | sığmaz | → Opus **100 kbps** (15.1 MB) |
| ex5 90 MiB | **sığar** | → **kayıpsız**, olduğu gibi |

Artık soru ters soruluyor: *bu sınıra sığan en iyisi ne?* Kayıt zaten 16 kHz mono 16-bit, yani
256 kbps; üstünde "kalite" diye bir şey yok, o yüzden kendi sunucumuza dosya hiç kodlanmadan
gidiyor ve kodlayıcı denklemden tamamen çıkıyor. Sığmadığı yerde bitrate kaynaktan ölçekleniyor —
sağlayıcı başına elle tutulan bir tablo yok, sınır kendisi karar veriyor. İki uçtan sınırlı:
üstte 128 kbps (16 kHz mono kodlayıcının söyleyecek bir şeyi kalmıyor), altta 32 kbps — bu kadar
dar bir sınır, sessizce duyulmayan bir yükleme yapmak yerine boyut denetiminden bir cümleyle
dönmeli.

`Ex5WhisperEngine`'e yazdığım özel kural kaldırıldı: temel kural onu zaten kapsıyor ve aynı kazancı
sınırı geniş olan her sağlayıcıya veriyor.

**851 test · 846 geçti · 0 kırık · 5 atlandı** + **96 Python** (bitrate'in uçurumun üstünde
kalması, tavan/taban sınırlaması, sığan kaydın hiç kodlanmaması testli).

**Paket.** `v2.2.0` — davranış değişikliği: aynı görüşme artık ölçülebilir biçimde daha çok kelime
çıkarmalı.

---

## 2026-09-02 (on üçüncü tur) — ex5 çalıştı; sessiz kayıtlar ve dinlerken takip

**ex5 doğrulandı.** Görüşme penceresi: `stt.ex5.ai · whisper-1 · gerçek zamanın 1,8 katı`,
96 satır, zaman damgaları yerinde ve konuşmacı ayrımı doğru. Son belirsizlik kapandı — işin
`result` gövdesi `words` taşıyor, alıntılar söylendiği ana açılıyor.

### Hiç ses yakalamamış kayıt "bekleyen iş" sayılıyordu

İşlemler ekranının başında iki satır kalıcı olarak duruyordu: `00:00`, *"The audio device has been
disconnected or the audio hardware has been reconfigured."* Yakalama hiç başlamamış, diskte dosya
yok, ikinci bir denemenin değiştirebileceği bir şey de yok.

Uygulama bunu **zaten biliyordu** — "Yeniden yazıya dök" düğmesi bu satırlarda kapalı ve toplu
kuyruğa alma onları atlıyor ("7 görüşme yeniden kuyruğa alındı. **2 tanesi atlandı**"). Söylenmemiş
tek yer bekleyenler süzgeciydi. Sonuç: iki gece önceki iki satır listenin tepesinde temelli
oturuyor ve yanındaki kırmızı sayaç 2 diyor — hiçbir çalışmayla sıfırlanamayacak bir birikim
rakamı. Bu, bekleyen sayacının kendisinin düzeltmek için yazıldığı kusurun başka bir yönden
gelmiş hali.

`ProcessingRow.NeedsTranscription` eklendi ve süzgeç ile kırmızı sayaç ona soruyor. Satırlar
"Hepsi"nde duruyor ve oradan silinebiliyor — veri silinmedi, yalnızca iş listesi dürüstleşti.
Durum sayfasının "hepsini tekrar dene" düğmesi de artık sesi olmayanı kuyruğa geri koymuyor;
eskiden bir yuvayı aynı cümleye varmak için harcıyor ve yapmadığı işi bildiriyordu.

### Dinlerken metin artık kendisi kayıyor

Konuşulan satır işaretleniyordu ama hiçbir şey ona gitmiyordu. On dakikalık bir görüşmede bu,
işaretin ömrünün neredeyse tamamını ekranın altında geçirmesi demek — yani işaretlemenin tek
amacı (sesin hangi cümlede olduğunu görerek okumak) sadece ilk ekran boyu çalışıyordu.

`CurrentTurnChanged` yalnızca satır **değiştiğinde** haber veriyor; oynatıcı saniyede birkaç kez
konum bildiriyor ve bir satır saniyelerce sürüyor, her tikte kaydırmak sürekli hafif bir titreme
olurdu. Kaydırma görünüm tarafında, çünkü baloncuğun ekranda nereye düştüğünü yalnızca o bilir.

**Takip geri çekilmeyi biliyor.** Bir dakika önce ne söylendiğine bakmak için geri kaydıran kişi
bu pencerenin var oluş sebebini yapıyor; saniyede iki kez onu ileri sürükleyen bir görünüm bunu
imkânsız kılar ve özellik olmamasından kötü olur. Elle kaydırma takibi durduruyor; oynata basmak
ya da bir satıra tıklamak — ikisi de "beni sese götür" demek — yeniden başlatıyor. Kendi
kaydırmamız bayrakla işaretli, yoksa kendi hareketimizi kullanıcının hareketi sanardık.

**856 test · 851 geçti · 0 kırık · 5 atlandı** + **96 Python**.

**Paket.** `v2.2.1`.

---

## 2026-09-02 (on dördüncü tur) — Bayt sayısından süre tahmin etmek

Sunucu tarafı yedi yüklemeyi özetledi ve alarma geçti: *"5 MB'lık dosyadan 170 kelime çıkıyor,
veri başına verim yirmide bir."* Kıyas noktası olarak *"1.7 MB → 1405 kelime"* verdi.

**Aritmetik hatası.** O kıyas 20 kbps Opus döneminden; bugünküler kayıpsız. Kayıt 16 kHz mono
16-bit, yani 256 kbps — saniyede 32 KB. Aynı ses, on üç kat daha fazla bayt:

| boyut | 20 kbps varsayılırsa | gerçekte (kayıpsız) | kelime | kelime/dk |
|---|---|---|---|---|
| 2.1 MB | ~14 dk | **66 sn** | 71 | 65 |
| 5.0 MB | ~33 dk | **156 sn** | 170 | 65 |
| 1.7 MB | 11 dk → 1405 kelime makul | **53 sn** | 1405 | **1587 — imkânsız** |

Boyuta bölünen her oran on üç kat kayıyor. Kelime/dakika olarak bakınca 65, 65, 60, 28, 23, 17 —
sıradan konuşma temposu.

**Dosyaların tam çift olması da kanıt.** 1.1/1.1, 2.1/2.1, 510KB/510KB. Opus değişken bit hızıyla
çalışır; iki farklı içerik asla aynı bayta çıkmaz. Aynı bayt aynı süre demektir: bir görüşmenin
mikrofon ve karşı taraf akışları. Günlük de aynını söylüyor (`mic %50 → far %51`). Yani sunucunun
"ikili kötü/iyi deseni" dediği şey, konuşmacı ayrımının çalışıyor olması.

**Yapılan.** Düzeltilecek kusur yoktu; düzeltilen, bunu tahmin etmek zorunda kalmak. Yükleme artık
kendini söylüyor: `1/1 yükleniyor · 1,5 MB · kayıpsız`. Bir satır, ve kimse bir daha bayt
sayısından süre çıkarmaya çalışmıyor.

Ayrıca doğrulandı: `2.2.0` hedef makinede kurulu (23:31:52) ve o günden sonraki her görüşme
`mic → far → merge` ile hatasız tamamlanıyor.

**856 test · 851 geçti · 0 kırık · 5 atlandı** + **97 Python**.

**Paket.** `v2.2.2`.

---

## 2026-09-02 (on beşinci tur) — Sunucu ekibinin soruları; senkron yol kalktı

Sunucu tarafı yapılandırılmış bir soru listesi gönderdi. İkisi gerçek değişiklik gerektirdi.

### Senkron uç tamamen kalktı

Üç dakikanın altındaki parçalar `/v1/audio/transcriptions`'a gidiyordu; gerekçe "yüz saniye kısa
bir parça için cömert"ti. Sunucu ekibinin verdiği tek cümle bu gerekçeyi çürüttü: **makine aynı
anda tek iş yapıyor.** Başkasının bir saatlik kaydı işlenirken gönderilen on beş saniyelik bir
parça onun arkasında bekler — ve bu bekleme **bizim isteğimizin içinde** geçer. Yani zaman aşımını
parçanın uzunluğu değil kuyruk belirliyor. 524 sonrası işe düşme görüşmeyi kurtarıyordu ama
yüklemeyi iki kez ödeyerek.

Vazgeçilen bir şey yok: iş ucu gereken alanları alıyor, `word_timestamps` zaten varsayılan olarak
açık, maliyeti bir yoklama aralığı. `_sync_request` ölü kod olarak silindi.

### Uzun işte ilerleme

Sunucu `progress_percent` bildiriyormuş. Yirmi dakikalık bir parça beş dakika boyunca kıpırdamayan
bir çubuk demekti; bu çalışmak gibi değil, donmak gibi görünür. Yoklama artık geldiği yeri
bildiriyor: `1/1 yazıya dökülüyor · %40`.

### Cevaplanan, değişiklik gerektirmeyenler

Kanalları **birleştirmiyoruz ve birleştirmeyeceğiz** — `merge_streams` iki akışı zaman damgasına
göre zaten iç içe diziyor, konuşmacıyı atıyor, üst üste binmeyi ve yankıyı işaretliyor. Sunucuda
birleştirme ucu istemiyoruz. Diarization da istemiyoruz: ayrı yakalama onu ücretsiz veriyor.
Dört hata durumu (401 · 413 · 524 · 403+1010) ayrı ayrı ele alınıyor, sonuncusu `blocked` koduyla
ve "anahtarla ilgili değil" cümlesiyle.

Onlara iletilen iki düzeltme: `timestamp_granularities` kendi şemalarında **düz metin**, liste
değil — önerdikleri `["segment","word"]` biçimi bugünkü uçla çalışmaz. Ve `filter_noise` bizde
kapalı olmalı: silinen segment silinen alıntıdır, biz belirsiz satırı silmeyip işaretliyoruz.

**856 test · 851 geçti · 0 kırık · 5 atlandı** + **94 Python**.

### Yeniden yazıya dökmede servis seçimi hiçbir şeye bağlı değilmiş

Kullanıcı "Yeniden yazıya dök → buluttan OpenAI" seçti, toast "ex5 Whisper servisine yükleniyor"
dedi. **Yönlendirme hatası değil: seçim hiçbir zaman bir yere bağlanmamıştı.**

İki katalog bir bulut transkripsiyonunu tarif ediyor ve yalnızca biri karar veriyor.
`AsrCatalog`'un satırları ("OpenAI Whisper API", "Groq") sesin makineden *çıktığını* söyler;
*nereye gittiğini* `SttEndpoints` söyler. Diyalog birinci türü sunuyordu, `TranscribeInCloudAsync`
ise her zaman yapılandırılmış ilk servise gidiyordu. Bu, uyarı model adını tekrarlamayı bırakıp
gerçek uç noktayı söyleyene kadar **görünmezdi** — on üçüncü turdaki düzeltme kusuru yaratmadı,
ortaya çıkardı.

Artık liste ayarlardaki kartlardan geliyor ve seçilen servis **yalnız başına** kullanılıyor.
Yedekleme zinciri kendiliğinden gelen bir kayıt için doğru — bir sağlayıcının kesintisi yüzünden
akşamın görüşmesi kaybolmasın diye var. "Bu servisle dök" için yanlış: iki servisi aynı sesle
karşılaştıran ya da bir anahtarın çalışıp çalışmadığına bakan biri, ötekinin cevabını berikinin
adı altında almamalı. Ses zaten diskte, yeniden denenebilir.

### Her gece bir saat düşen test

`CallsFilterTests.ByPeriodUsesTheCallsOwnClock` bu turda kırmızı geldi ve sebebi değişiklik
değildi: örnek satır "bir saat önce" diye kuruluyordu, ki gece yarısı ile bir arası **dün** olur.
Yani test her gece 00:00–01:00 arasında düşüyordu ve kimse o saatte bakmıyordu. Sürüm beklerken
saat 00:0x'te yakalandı. Satır artık "bugünün içinde kalan bir an".

**860 test · 855 geçti · 0 kırık · 5 atlandı** + **94 Python**.

**Paket.** `v2.2.3`.

---

## 2026-09-03 — `import os` yok: işlemcide yazıya dökme hiç çalışmıyormuş

Kullanıcı işlemciyi seçti ve worker çöktü:

```
faster_whisper_engine.py, line 138, in load
    cpu_threads = max(1, (os.cpu_count() or 4) - 2)
NameError: name 'os' is not defined
```

`os` kullanılıyor, hiç import edilmemiş. `7adc6f7` ile geldi, yani **v2.1.6'dan beri işlemcide
yerel yazıya dökme hiç çalışmıyor** — ve kimse fark etmedi.

**Neden hiçbir şey yakalamadı.** Üç şey üst üste geldi: satır yalnızca cihaz `cpu`'ya çözümlenince
çalışıyor, çalışan bir ekran kartı olan makine oraya hiç varmıyor; motorun kendi testleri Whisper
ağırlıkları olmadan atlanıyor; ve projede linter yok. Bir linter'ın anında bulacağı bir kusur,
kaydı alındıktan **sonra** kullanıcının ekranına düştü.

**Yapılan.** İmport eklendi. Ama asıl mesele sınıfın kendisi, o yüzden `worker/tests/test_imports.py`
yazıldı: her modülün AST'sini gezip `ad.özellik` biçiminde kullanılan standart kütüphane adlarının
gerçekten import edildiğini doğruluyor. Bağımlılık yok, yirmi satır, ve tam olarak olan hataya
nişan alıyor. Doğrulandı: import geri alınınca test kırmızı oluyor —
*"faster_whisper_engine.py uses os without importing it"*.

**860 test · 855 geçti · 0 kırık · 5 atlandı** + **109 Python** (biri modül başına, 15 modül).

**Paket.** `v2.2.4`.

---

## 2026-09-03 (ikinci tur) — Sunucunun attığı kısa yanıtlar geri geliyor

Sunucu ekibi dokümanı v3'e çıkardı ve üç şeyi düzeltti: `timestamp_granularities[]` artık kabul
ediliyor (bizim bulduğumuz kusur — OpenAI SDK'sını kullanan herkes sessizce kelime damgasız
gidiyormuş), `filtered_out` alanı eklendi, ve iki yanlış iddialarını geri çektiler.

**Ama bizi ilgilendiren delik kapanmadı.** Şema yeniden çekildi:

| Uç | `filter_noise` |
|---|---|
| `/v1/audio/transcriptions` | var |
| **`/v1/jobs`** | **yok** |
| `/v1/conversations` | yok |

Yani artık kendilerinin de "önerilen yol" dediği uçta halüsinasyon filtresi **kapatılamıyor**, ve
varsayılan açık. `filter_noise=false` göndermek işe yaramaz: FastAPI tanımsız alanı sessizce atar
ve biz kapattığımızı sanırız — `timestamp_granularities`'te yaşananın aynısı, ters yönde.

**Yapılan.** Kapatamıyorsak attığını geri koyalım. `filtered_out` üç gerekçe bildiriyor ve üçü aynı
şey değil:

- `konusma_degil(no_speech=X)` → **geri konuyor**, sunucunun kendi şüphe puanını taşıyarak. Bizim
  kuralımız oradan devralıyor: 0.6 üstü belirsiz işaretlenir ve otomatik çelişki denetiminin
  dışında tutulur. Risk altındaki satırlar tam da bunlar — kısık bir "hı", "tamam", "aynen".
- `bos` → hiçbir şey yok, bırakılıyor.
- `tekrar_dongusu` → sessizlik üzerine yirmi kez "abone ol" modelin bilinen bir artefaktı, birinin
  söylediği bir şey değil. Geri koymak deftere kanıt kurallarıyla gürültü sokardı.

Defterdeki her söz birebir alıntı ve tıklanabilir bir an taşıyor; yukarıda silinen bir cümle
kimsenin hesabını veremeyeceği bir boşluk bırakır. Kural yine aynı: **işaretle, silme.**

Onlara sorulan da açık: `filter_noise` (ve tutarlılık için `vad`, `normalize`) `/v1/jobs` gövdesine
eklensin. Eklendiği an `false` göndeririz ve bu geri koyma katmanı gereksizleşir — o güne kadar
duruyor, zararsız: alan gelmezse hiçbir şey değişmiyor.

**860 test · 855 geçti · 0 kırık · 5 atlandı** + **113 Python**.

**Paket.** `v2.2.5`.

---

## 2026-09-03 (üçüncü tur) — Filtre artık kapatılabiliyor; açık bırakıldı

Sunucu ekibi boşluğu kapattı: `vad`, `normalize`, `filter_noise` artık üç ucun da gövdesinde.
Şemadan doğrulandı.

**Ama `false` göndermiyoruz, ve bu bilinçli.** İki seçenek de mevcut olduğunda hangisinin daha çok
bilgi verdiğine bakmak gerekiyor:

- `filter_noise=false` → model ne ürettiyse geliyor, **tekrar döngüleri dahil**: sessizlik üzerine
  yirmi kez "abone ol". Bizim bunları yakalayan bir filtremiz yok, dolayısıyla deftere kanıt
  kurallarıyla girerlerdi.
- `filter_noise=true` → temiz transkript **artı** neyin niçin atıldığının etiketli listesi. Yani
  filtreyi reddetmekten *daha fazla* bilgi: on ikinci turda yazılan katman, gerçek konuşma olabilecek
  olanları belirsiz işaretiyle geri koyuyor, bilinen artefaktlar dışarıda kalıyor.

İkincisi hem daha temiz hem daha dürüst. Katman gereksizleşmedi, doğru seçim olduğu anlaşıldı.

Ayrıca artık **açıkça** gönderiliyor, varsayılana bırakılmıyor. Bugün varsayılan `true`; sunucuda
bunun değişmesi kimsenin görüşmesine sessizce halüsinasyon sokardı ve ne istediğini söyleyen bir
istek kayamaz.

**860 test · 855 geçti · 0 kırık · 5 atlandı** + **113 Python**.

**Paket.** `v2.2.6`.

---

## 2026-09-03 (dördüncü tur) — Satıra tıklamak görüşmeyi çalsın; üst üste konuşma görünsün

Kullanıcı: "bir konuşmayı tıkladım, çalma devam ediyor ama sadece o tarafın sesleri geliyor,
öyle olmaz."

**Haklı, ve bu bilinçli bir karardı — yanlış olduğu kullanınca anlaşıldı.** `PlayFrom` tıklanan
satırın tarafına geçiyordu; gerekçe "tam o kelimeleri denetlemek için tek ses en nettir" idi.
Kullanımda tersi çıkıyor: çalma tıklanan satırdan sonra devam ediyor ve tek kanalda karşı tarafın
söylediği her şey **sessizlik**. Bir görüşmenin yarısını dinliyorsun, metin ise altında ses olmayan
cümleler boyunca kayıyor. On üçüncü turda eklenen takip özelliğinin bozuk görünmesinin sebebi de
buydu: işaret, seçili kanalın hiç konuşmadığı satırlara gidiyordu.

Artık karışım varsa tıklama **tüm görüşmeyi** o andan çalıyor. Tek tarafı yalıtmak hoparlör
düğmesinde duruyor — orada bilinçli bir eylem. Karışımı olmayan eski kayıtta o tarafın kanalına
düşülüyor; sessizlik çalmaktan iyidir.

### Üst üste konuşma ve yankı artık baloncukta

Veri en baştan beri vardı (`merge_streams` işaretliyor, veritabanı saklıyor, Kişiler ekranı
kullanıyor) ama görüşme penceresine hiç çıkmıyordu. İki küçük rozet eklendi:

- **`üst üste`** — bu satır başlarken karşı taraf zaten konuşuyordu. Görünmesi gerekiyor çünkü
  satırın **anlamını** değiştiriyor: boşluğa söylenen "tamam" onaydır, birinin üstüne söylenen aynı
  kelime sözünü kesmektir. Defterde bunu ayırt etmeden alıntılamak yarım bir olguyu alıntılamaktır.
- **`yankı`** — aynı sözler aynı anda iki kanalda. İki kişinin aynı cümleyi kurması değil,
  hoparlörden mikrofona sızan tek ses. Yankı üst üste gelmeyi bastırıyor: "bu gerçekten burada
  söylendi mi" sorusu, "nasıl söylendi"den önce gelir.

İkisi de işaretleniyor, hiçbiri gizlenmiyor — aynı anda söylenmiş gerçek bir "aynen" ile sızıntı
metin düzeyinde ayırt edilemez ve silmek gerçekten söylenmiş bir sözü yok etme riski taşır.

**869 test · 864 geçti · 0 kırık · 5 atlandı** + **113 Python**.

**Paket.** `v2.3.0`.

---

## 2026-09-03 (beşinci tur) — "Durdur" durdurmuyordu

Ekranda "5 sırada / işleniyor" yazarken kullanıcı: **"durdurma yok."** Düğme oradaydı ama
yalnızca **o anki işi** kesiyordu; bir saniye sonra beştekinin ikincisi başlıyordu. Yani durdurma
değil, araya girmeydi. Makinenin yüklemeyi bırakmasını isteyen ya da yanlış kırk görüşmeyi kuyruğa
almış biri için tek çıkış uygulamayı kapatmaktı.

`StopEverything()` eklendi: önce kuyruk boşaltılıyor, **sonra** çalışan iş kesiliyor — ters sırada
yapılırsa döngü bir sonraki numarayı hemen alır ve bir tane daha başlar. Kuyruktan çıkanlar tek tek
durdurmanın yaptığının aynısıyla park ediliyor: `Skipped` ve üstünde sebebi. Hiçbiri silinmiyor,
her biri bir "Yeniden işle" uzağında.

Düğme yalnızca arkada bekleyen varken görünüyor, ve sonucu sayıyla söylüyor — *"sıradaki 39 kayıt
da beklemeye alındı, hiçbiri silinmedi"*. Bir listeden otuz dokuz kaydı sessizce çıkaran düğme,
onları silen düğmeden ayırt edilemez; buradaki güvencenin tamamı hiçbir şeyin kaybolmadığı.

**Ölü kod temizliği.** Senkron yol kalkarken `SYNC_MAX_SECONDS` ve modül açıklamasının yarısı
geride kalmıştı — dosya hâlâ "kısa parçalar senkron uca gider" diye anlatıyordu. Bu projede yorum
belgedir; yanlış anlatan yorum, yanlış kod kadar zararlıdır.

**869 test · 864 geçti · 0 kırık · 5 atlandı** + **113 Python**.

**Paket.** `v2.3.1`.

---

## 2026-09-03 (altıncı tur) — Her servis ekranda, anahtarı olsun olmasın

Kullanıcı: "yeniden çevirde bulutta tek sağlayıcı çıkıyor, anahtarı girilmiş ve aktif olanların
hepsi görünmeli."

Kod zaten öyle çalışıyordu — `UsableSttEndpoints` anahtarı olan **her** kartı döndürüyor. Tek
çıkmasının sebebi listede gerçekten tek kart olmasıydı, ve asıl istek cümlenin ikinci yarısındaydı:
*"ayarlanmamış sağlayıcıların api keyleri boş olsun by default."* Yani kartların **var olması**
bekleniyor.

**Doğru beklenti.** Liste yalnızca elle "Servis ekle"den eklenenleri tutuyordu; o ekrandan Groq'un
ya da OpenAI'nin seçenek olduğunu öğrenmenin bir yolu yok. OpenAI anahtarı olan biri servisin
var olduğunu **tahmin edip** Ayarlar'a gidip menüyü bulup seçmek zorundaydı.

Artık katalogdaki her servis boş kartla geliyor. Boş anahtar zaten kullanılabilir değil
(`IsUsable`), dolayısıyla yönlendirme değişmiyor ve hiçbir servise bağlanılmıyor — kart, anahtar
bekleyen etiketli bir kutu, ve anahtarı yapıştırmak kurulumun tamamı. Sona ekleniyorlar: kişinin
kendi sıralaması, ki denenme sırasıdır, olduğu gibi kalıyor.

"Özel adres" bilerek eklenmiyor. Kendi adresi olmadığı için boş hali ne işe yaradığını
söyleyemeyen bir karttır; o menüde kalıyor, orada seçmek bilinçli bir eylem.

`CloudKeyValidationTests` iki yerde `SttEndpoints.Single()` diyordu — davranış değil erişim biçimi
eskidi, kart artık adıyla bulunuyor.

**873 test · 868 geçti · 0 kırık · 5 atlandı** + **113 Python**.

**Paket.** `v2.3.2`.

---

## 2026-09-03 (yedinci tur) — Bulut çevirisi neden yerelden kötü: tek bir bayrak

Kullanıcı: "yerelde GPU'da güzel çeviriyor, bulutta saçma sapan çıkıyor, hem kayıyor hem sıralama
bozuk." Ve haklı olarak sordu: dosyaları başka türlü mü göndersek, kendi sunucumuza upload arayüzü
mü yazsak?

**Gerek yok — fark aktarımda değil, çözümleme ayarında.** Aynı ağırlıklar, farklı bayraklar.

Yerel motorun kodunda tam bu kusura karşı yazılmış bir yorum duruyor:

> *"Whisper invents text when fed silence. Both of these suppress that, and the recorder produces
> a lot of silence because it captures the whole call rather than just speech."*
> `vad_filter = True` · `condition_on_previous_text = False`

Sunucunun kütüphanesinin kaynağından doğrulandı (`mlx_whisper/transcribe.py:71`):
`condition_on_previous_text: bool = True`. Yani sunucu bu bayrağı **açık** çalıştırıyor ve uçlarında
kapatacak bir alan yok.

Açıkken model kendi ürettiği metni bağlam olarak geri besliyor. Bir yanlış tahmin bir sonrakini
bozuyor, ve kaydedici görüşmenin tamamını yakaladığı için tek kanalda dakikalarca süren sessizlik
uydurma metinle doluyor. Üç şikâyetin üçü de bundan çıkıyor: uydurma cümleler, sürüklenen zaman
damgaları (bağlam penceresi modelin kendi çıktısına göre kayıyor), ve kayan damgalar birleştirmede
yanlış sıraya oturduğu için bozulan konuşma sırası.

**Kalan parametreler zaten aynı.** `no_speech_threshold=0.6`, `logprob_threshold=-1.0`,
`compression_ratio_threshold=2.4` — mlx varsayılanları bizim yerelde kullandığımızla birebir. Fark
tek bir bool.

`hotwords` istenmedi: mlx-whisper'da yok, o CTranslate2'ye özgü. Yerelde 209 terim her pencereyi
yönlendiriyor, bulutta yalnızca `prompt`'un ilk 40 terimi var ve Whisper prompt'u bir kez okuyup
çoğu zaman göz ardı ediyor. Bu fark kapatılamaz ama bayrak düzeltilince etkisi çok azalır.

Sunucudan `condition_on_previous_text` alanı istendi. Kod değişikliği yok — **yerel motora
dokunulmadı**, zaten doğru olan taraf o.

---

## 2026-09-03 (sekizinci tur) — Yanlış teşhis, ve doğru düzeltme

Kullanıcı "OpenAI'de de aynı sorun var" dedi, ve sunucu ekibi teşhisimi çürüttü. İkisi de önemli.

### Teşhis yanlıştı

`condition_on_previous_text`'in sunucuda açık olduğunu söylemiştim. Değilmiş — kurulumdan beri
`False`, ve ekip bunu `transcribe()` çağrısını canlı yakalayarak gösterdi. **Kütüphanenin
varsayılanına bakıp çağıranın ne geçirdiğini varsaymışım**; ikisi farklı şeyler ve burada
karıştırıldı. `speech_only.py` içindeki gerekçe düzeltildi: bu projede yorum belgedir, çürütülmüş
bir sebebi anlatan yorum yanlış kod kadar zararlıdır.

Ekip ayrıca dört çözümleme alanını `/v1/jobs`'a açtı ve `/health`'e `decode_defaults` ekledi —
artık kaynak koda bakmadan tek `curl` ile sunucunun ne yaptığı görülüyor. Kalıcı çözüm bu, ve
teşhisimin yanlış olmasının sebebi de zaten görünmez olmasıydı.

### Ama düzeltme yine de doğru yerde

Kullanıcının "OpenAI'de de aynı" gözlemi asıl ipucuydu: sorun **tek bir sağlayıcıda** değil,
barındırılan her Whisper'da. Kalan tek yapısal fark VAD:

- Yerelde `vad_filter=True` — faster-whisper konuşma dışını **modele hiç göstermeden** atıyor.
- ex5'te `vad=false`. OpenAI'de böyle bir parametre **hiç yok**.

Ve bu uygulama görüşmenin tamamını iki ayrı kanala kaydediyor: biri konuşurken öteki dakikalarca
sessiz. Whisper sessizliğe "hiçbir şey" dönmez — otuz saniyelik pencerelerde eğitildiği için,
konuşma olmayan pencerede eğitim verisinde en çok ne varsa onu üretir; Türkçede "abone ol". Sunucunun
halüsinasyon filtresinin var olması bunun ne sıklıkta olduğunun ölçüsü.

**Yani sessizlik istemcide, yüklemeden önce atılıyor** — sağlayıcıdan bayrak istemek yerine, çünkü
o zaman hepsinde çalışıyor. `speech_only.py`: kare kare seviye taraması, konuşma aralıkları,
cömert dolgu (kırpılmış ünsüz Whisper'ın uydurduğu bir kelimeye dönüşür), yakın aralıkların
birleştirilmesi, ve **zamanların geri haritalanması.**

Haritalama bu işin kritik yeri. Defterdeki her satır tıklanıp dinlenebilen bir an taşıyor; bir
saniye kayan damga, içermediği sesi gösteren bir alıntıdır. Aralıklar açık bir liste olarak
tutuluyor, zamanlar koşan toplam üzerinden değil o liste üzerinden çevriliyor, ve modelin son
aralığın ötesinde bildirdiği her şey uzatılmıyor **kırpılıyor** — attığımız sessizliğin içine
kelime koymak, o kelimenin kanıtlanabilir biçimde söylenmediği bir yere koymaktır.

Kendini sınırlıyor: kayıt çoğunlukla konuşmaysa dokunmuyor (haritalama yanlış olabilecek bir şey
daha, bir şey kazandırmalı), hiç konuşma yoksa dokunmuyor (sessiz kanal transkriptin göstermesi
gereken bir olgu), okunamayan dosyaya dokunmuyor (daha iyi transkript uğruna hiç transkript
olmaması takas edilmez).

**Yerel motora dokunulmadı.**

**873 test · 868 geçti · 0 kırık · 5 atlandı** + **123 Python** (dokuzu haritalama ve sınırlar).

**Paket.** `v2.4.0`.

---

## 2026-09-03 (dokuzuncu tur) — Kaybolan OpenAI anahtarı

Kullanıcı: "yeniden çevirde sadece bizimki var ama OpenAI keyi de var."

**Vardı, ve iki yerde birden görünmez olmuştu.** Tek anahtar bir zamanlar tek alanda (`AsrApiKey`)
dururdu; servisler listeye dönünce o alan "başka bir şey yapılandırılmamışsa" yedeği olarak
bırakılmıştı. Koşul her iki yerde de `Count == 0`:

- `AppSettings.UsableSttEndpoints` — liste boş değilse eski anahtara hiç bakmıyordu.
- `SettingsViewModel` — göçü yalnızca liste boşken yapıyordu.

Yani ex5 kartını ekledikleri an OpenAI anahtarı hâlâ dosyada, hâlâ geçerli, ve bir daha ne
gösteriliyor ne kullanılıyordu. **Hata vermedi, kayboldu** — ikisinin kötüsü bu.

Artık eski anahtar listenin **sonuna ekleniyor**, yerine geçmiyor. Sona, çünkü sıralama denenme
sırasıdır ve o kişinin kendi kararı; eski dosyadan taşınan bir anahtar sessizce kuyruğun başına
geçmemeli. Aynı anahtarı taşıyan bir kart varsa eklenmiyor — aynı servis iki adla iki kez
denenmesin. Ayarlar ekranında da anahtar artık kendi kartına taşınıyor, ve kartı boş olana; ekrana
yazılmış bir anahtar daha yeni bir karardır, eski dosyadan gelenle ezilmemeli.

`SttProviderTests.TheListWinsOverTheOlderSingleKeyOnceItIsConfigured` tam da bu davranışı
savunuyordu. Niyeti yanlıştı ve gerçek bir kullanıcıya anahtarını kaybettirdi; test yeniden yazıldı
ve sebebi içine kondu.

**880 test · 875 geçti · 0 kırık · 5 atlandı** + **123 Python**.

**Paket.** `v2.4.1`.

---

## 2026-09-03 (onuncu tur) — Tahmin etmeyi bırak, yazdır

Kullanıcı aynı görüşmenin iki çıktısını yan yana gösterdi. Yerelde karşı tarafın Rusçası **Kiril
harfleriyle** doğru geliyor: *"Сейчас тебя заберут, договариваюсь, Готов чемодан."* ex5'te aynı
sözler Türkçe hecelere dönüşmüş: *"Nesil? Mide. Mide."*, *"Dacimound!"* — ve uygulama kendisi
satırların %38'ini belirsiz işaretlemiş.

**Ve ben iki turdur tahmin ediyordum.** Biri (`condition_on_previous_text`) yanlış çıktı. Sebep her
seferinde aynı: buluta ne gönderdiğimiz hiçbir yerde yazılı değildi, dolayısıyla yerelle
karşılaştırmanın tek yolu tahmin etmekti.

Artık her bulut isteği iki satır bırakıyor:

```
1/1 yükleniyor · 1,5 MB · kayıpsız · dil tr · sözlük 40 terim (ipucu)
1/1 geldi · dil ru · 8 satır · 41 kelime
```

İkinci satırdaki **servisin kendi bulduğu dil**, tartışmayı bitiren sayı: biz `tr` dayatırken servis
`ru` duyuyorsa, zorlamanın kendisi kusurdur. Bu satırlar "Ayrıntılı günlük" ayarına bakmadan
yazılıyor — worker onları orta noktayla işaretliyor, yüzde bildiren satırlar eskisi gibi ayara bağlı.
Kayıt başına birkaç satır, o ayarın bastırmak için var olduğu gürültü değil.

### Ve zaten oradaki anahtar

`Karışık dil (Türkçe–İngilizce)` ayarı kapalıyken **her kayıt Türkçe kabul ediliyor**. Karşı taraf
Rusça konuşuyorsa söyledikleri Türkçe hecelere çevrilir — ekrandaki tam olarak bu. Ayar açıkken
bulut yoluna dil hiç gönderilmiyor ve servis kendi buluyor.

Yani çare zaten vardı, adı yanlıştı: "Türkçe–İngilizce" diyen bir anahtarın Rusça konuşan biri için
aranacağı akla gelmez. Yeniden adlandırıldı — **"Dili görüşmeden bul"** — ve açıklaması ne olduğunu
değil, kapalıyken ne olacağını anlatıyor: *"Kapalıyken her kayıt Türkçe kabul edilir. Karşı taraf
başka bir dil konuşuyorsa söyledikleri Türkçe hecelere çevrilir ve anlamsız çıkar."*

**880 test · 875 geçti · 0 kırık · 5 atlandı** + **123 Python**.

**Paket.** `v2.4.2`.

---

## 2026-09-03 (on birinci tur) — Asıl sebep: arşiv 24 kbps'ti

Kullanıcı üç çıktıyı yan yana koydu — yerel, OpenAI, ex5 — ve "GPU'da hiçbir sorun yok, buluta
gönderince çıkıyor" dedi. Sonra doğru soruyu sordu: **"bu özellikler bozuyor olabilir mi?"**

Evet. Ve sebep bulutta değil, bizde.

```
OpusArchive.Bitrate = 24_000
```

Bugün ölçtüğümüz uçurumun kenarındaki sayının aynısı, ikinci bir yerde. Zincir şu:

1. Görüşme WAV olarak kaydedilir.
2. **İlk çeviri orijinal PCM'i okur** — karşılaştırılan iyi çıktı bu.
3. "Sesi sıkıştır" arşivi **24 kbps Opus**'a çevirir (günlükte ölçüldü: 251,1 MB → 20,5 MB, 12:1).
4. "Yeniden yazıya dök" `EnsurePcm` ile o Opus'u geri açar.
5. Buluta giden ses **zaten bozulmuş** sestir.

Yani bulut-yerel farkı değil, **orijinal-yeniden sıkıştırılmış** farkı. Dosyanın kendi yorumu bunu
varsayıyordu — *"transcription reads the PCM original, and nothing here runs before it has"* — ve
ilk çeviri için doğru; ikinci çeviri için değil. Varsayım yazıldığında "yeniden yazıya dök" yoktu.

Bütün gün yüklemeyi kayıpsız yapmakla uğraştık; kaynak çoktan bozulmuştu.

**Arşiv 64 kbps'e çıktı**, ve bu bir taban: *"asla 64 kbit altına inme"*. Kural teste yazıldı, çünkü
bunu ileride düşürmenin bariz gerekçesi disk şikâyetidir ve bedeli diskte değil — ilk çeviriden
sonraki her transkriptte, sessizce ödenir. Dosya beş kat küçük yerine yirmi kat küçük değil artık;
aradaki fark kelimelerle ödeniyordu.

**Zaten 24'te sıkıştırılmış kayıtlar geri gelmez.** Atılan atılmıştır; yalnızca bundan sonrakiler
iyi. Elde orijinali kalan görüşme varsa yeniden çevirmeye değer.

`OpusArchiveTests.ARecordingShrinksTwentyFoldAndDecodesWhole` eski takası savunuyordu; adı ve eşiği
yeni takasa göre düzeltildi.

**883 test · 878 geçti · 0 kırık · 5 atlandı** + **123 Python**.

**Paket.** `v2.5.0` — davranış değişikliği.

---

## 2026-09-03 (on ikinci tur) — "Takıldı mı çalışıyor mu"

Kullanıcı bir işin uzun süre %23'te durduğunu gösterdi. Sunucu meşguldü ya da sayı bildirmiyordu;
ikisi de normal. Sorun bu değildi — **hangisi olduğunu söyleyecek bir şey yoktu.**

Yoklama yalnızca `progress_percent` geldiğinde bir şey yazıyordu. Sunucu eşzamanlılığı 1 olduğu
için başkasının işinin arkasında beklemek olağan ve uzun sürebilir; o süre boyunca çubuk hiç
kıpırdamıyordu. **Dört dakikadır kıpırdamayan bir çubuk, donmuş bir uygulamadan ayırt edilemez** —
ve bakan kişi Durdur'a bastığında tıkanmış bir işi değil, kuyruktaki yerini atmış oluyor.

Artık her beş saniyede bir, sunucu ne söylerse söylesin, bekleyişin kendisi mesaj:

```
1/1 yazıya dökülüyor · sunucuda sırada · 2 dk
1/1 yazıya dökülüyor · sunucuda işleniyor · 40 sn
1/1 yazıya dökülüyor · %60, ~3 dk kaldı
```

Sırada beklemek en çok adlandırılması gereken durum, çünkü durdurmanın en pahalı olduğu durum o.

Döngü de düzeltildi: bitmiş bir yoklama için artık "bekliyor" satırı yazılmıyor, ve düşen bir
yoklamada saat duruyordu — geçen süre geçmiştir, kullanıcının okuduğu sayaç kendi saatiyle
uyuşmalı.

Ayrıca `.partial` artıkları süpürülmeye başlandı: gerçek bir arşivde iki haftalık 1,8 MB'lık bir
tane duruyordu. Her biri benzersiz adla yazılıp tamamlanınca yeniden adlandırılıyor, yani adı hâlâ
duran hiç tamamlanmamış demektir. `.cloudparts` süpürülüyordu, bunlar hiç.

**883 test · 878 geçti · 0 kırık · 5 atlandı** + **125 Python**.

**Paket.** `v2.5.1`.

---

## 2026-09-03 (on üçüncü tur) — Bozuk sesli kayıtları bulup kaldırma

Kullanıcı: "eski ogg'leri silelim, eğer transcript edemeyeceksek onlara ihtiyacımız yok, Durum'a
bir kaldır düğmesi koyalım."

**Tarihe göre değil, ölçerek.** Zaman damgasıyla kesmek hangi sürümün hangi dosyayı yazdığı
hakkında bir tahmindir, geç güncelleyen için yanlıştır ve denetlenemez. Dosyanın boyutu ile
görüşmenin süresi işin kendisidir. Gerçek arşivde eskiler 19–24 kbps ölçüyor, yeniler 55 civarı;
eşik 40 kbps'e konuldu, ikisine de uzak.

Ölçemediğine dokunmuyor. Bu cevap kayıtları kalıcı olarak siliyor: kötü bir kaydı tutmanın bedeli
disk, iyi bir kaydı silmenin bedeli bir görüşme.

**İki basış, bilerek.** İlki yalnızca ölçüyor ve ne bulduğunu söylüyor — **kaçının metni olduğu
dahil.** Bunlar önemli, çünkü kaldırmanın bedeli orada: ses kurtarılamaz ama kelimeler o ses
bozulmadan **önce** çıkarıldı ve hâlâ iyiler. Metni olan bir kaydı silmek iyi bir metni silmektir.
Hiçbir şey, bu cümle okunmadan ve ne yaptığını söyleyen ikinci bir düğmeye basılmadan silinmiyor.
Onaydan sonra ses, döküm, defter kayıtları ve notlar birlikte gidiyor.

**894 test · 889 geçti · 0 kırık · 5 atlandı** + **125 Python**.

**Paket.** `v2.6.0`.

---

## 2026-09-03 (on dördüncü tur) — Sessizlik kesme geri alındı

Kullanıcı: **"benim sesim, karşının sesi ayrı ayrı gidiyor; karşı taraf konuşurken benim wav'ımda
kesinti olacak, bu normal, bunlara dokunursan bozarsın."**

Haklı, ve on ikinci turda eklediğim `speech_only` tam da buna dokunuyordu. Gerekçesi sağlam
görünüyordu — hiçbir barındırılan servis VAD çalıştırmıyor, ve Whisper uzun sessizliğe yazıyor.
Kaçırdığım şey **o sessizliğin ne olduğuydu**: bir kanaldaki boşluk öteki kişinin konuşmasıdır.
Görüşmenin yapısı, ölü hava değil; bir kanalın çağrının çoğunda sessiz olması **beklenen** durum.

Sunucu ekibi bedelini ölçtü: kesme yapılan işler **compression_ratio 8.35** ile döndü (eşik 2.4) —
Whisper'ın kendi "bu çıktı kendini tekrar ediyor" ölçüsü — ve çözücü aynı pencereyi sıcaklık
merdiveninde 1.0'a kadar altı kez yeniden çalıştırıp yine boş üretti. Kesme noktaları, bir cümlenin
sonunu dakikalar sonra kaydedilmiş başka bir cümlenin başına yapıştırıyordu. **Çare hastalıktan
kötüydü.**

Ekleme yerlerine sessizlik koyarak yumuşatmayı denedim; ama doğru cevap yumuşatmak değil,
dokunmamak. `speech_only` ve testleri kaldırıldı. Yerine `cloud_engine`'de sebebini anlatan bir
yorum kaldı — bu, tekrar denenmeye çok müsait bir fikir ve neden çalışmadığının yazılı olması gerek.

**Sessizlik halüsinasyonu sese dokunmayan yerde ele alınıyor:** servisin kendi filtresi tekrar
döngülerini yakalıyor, neyi attığını bildiriyor, ve gerçek konuşma olabilecekler belirsiz
işaretiyle geri konuyor (on ikinci tur).

**904 test · 899 geçti · 0 kırık · 5 atlandı** + **115 Python**.

**Paket.** `v2.6.1`.

---

## 2026-09-03 (on beşinci tur) — VAD, doğru yerde

Kullanıcı: "yerel CPU modelleri de çözdü." Bu belirleyici. Aynı sesi işlemcideki küçük model de
doğru çözüyorsa **fark model değil.** Geriye yerel motorun yaptığı ve bulutta yapılmayan tek şey
kalıyor: `vad_filter=True`.

Ve on ikinci turda düştüğüm tuzak tam buradaydı. VAD'ı ben **dosyayı keserek** taklit etmeye
çalıştım; faster-whisper öyle yapmıyor. VAD çözücünün **içinde**, pencere pencere çalışıyor:
konuşma olmayan pencereleri atlıyor, alakasız sesleri birbirine yapıştırmıyor, ve **hiçbir dikiş
yeri oluşturmuyor.** Halüsinasyon edecek bir ek yeri yok. Aynı fikir, iki farklı yerde, biri
işe yarıyor öteki bozuyor.

O yüzden istekte gidiyor artık: `vad=true`. Servis bunu kapalı gönderiyordu ve gerekçesi olan
ölçümü operatörü kendisi geri çekti — iki farklı kanal karşılaştırılmıştı, yani "kaybolan"
kelimeler sayıldıkları kanalda zaten yoktu. Bizim yerel motorumuzda açık ve bu kaydı hem ekran
kartında hem işlemcide doğru çözüyor.

Yorumda bırakılan not: aynı kayıtta bulut çıktısı yerelinkinden ince gelirse ilk kapatılacak şey bu.

**904 test · 899 geçti · 0 kırık · 5 atlandı** + **115 Python**.

**Paket.** `v2.6.2`.

---

## 2026-09-03 (on altıncı tur) — Yedekler parolayla korunabiliyor

Kullanıcı: "yedekle sistemi var ama şifreleme sistemi yok."

Ayrı bir dışa aktarma yazmak yanlış olurdu — yedekleme zaten var, eksik olan tek şey kilidiydi.

**Şifreli ZIP değil, bilerek.** ZIP'in otuz yıldır taşıdığı şifreleme ZipCrypto ve süs
sayılacak kadar kırık; AES uzantısı ise araçlar arasında o kadar tutarsız destekleniyor ki
"benim programımda açıldı" bir güvence sayılmaz. Bu dosyada bütün görüşmelerin metni ve
isteğe bağlı olarak sesleri var. Bu yüzden kap sade: **AES-256-GCM**, anahtar paroladan
PBKDF2 ile (600 bin tur). 7-Zip açamaz. Takas bu, ve doğru yönde.

**Çerçeveli, tek blok değil.** Sesle birlikte yedek gigabaytlarca; tek bir AES-GCM işlemi
hepsini birden bellekte ister. Yük 1 MB'lık çerçevelere bölünüyor, her biri kendi etiketiyle.
Üç ayrıntı, "şifreli görünen" ile şifreli olan arasındaki farkı yapan:

- **Her çerçevenin nonce'ı sayaçtan türüyor**, hiç tekrarlamıyor. Aynı anahtarla aynı nonce iki
  çerçevenin içeriğini birbirine sızdırır.
- **Her çerçeve kendi sırasına ve başlığa bağlı**, yani çerçeveler yer değiştiremez,
  çoğaltılamaz, atılamaz.
- **Son çerçeve son olduğunu söylüyor.** Bu olmasaydı dosyayı ortadan kesmek, kusursuz doğrulanan
  **daha kısa bir yedek** üretirdi — arşivin yarısını sessizce kaybettiren bir geri yükleme.

Dört hata ayrı ayrı söyleniyor, çünkü her birinin cevabı farklı: bizim dosyamız değil, daha yeni
sürümle yazılmış, parola yanlış ya da bozulmuş, yarım kalmış. Parola yanlış mı dosya mı bozuk
ayırt **edilmiyor** — edilebiliyormuş gibi yapmak, hiçbir şeyin denetlemediği bir parolayı
"doğru" ilan etmek olurdu.

**Parola isteğe bağlı.** Verisini kaybetmiş ve bir çubuğa kopya almak isteyen birinin önüne parola
kutusu koymak yardım değil engel. Boş bırakmak şifrelemiyor, ve mesaj hangisinin olduğunu söylüyor.
Geri yüklemede parola **yalnızca gerekince** soruluyor: dosya ilk sekiz baytında kendisi söylüyor,
kimsenin nasıl yazdığını hatırlaması gerekmiyor.

Çözülmüş kopya nasıl biterse bitsin siliniyor — şifrelinin yanında duran okunabilir bir kopya
parolayı anlamsız kılardı.

**913 test · 908 geçti · 0 kırık · 5 atlandı** + **115 Python**.

**Paket.** `v2.7.0`.

---

## 2026-09-03 (on altıncı tur) — Sebep bizim gönderdiğimiz bir alandı: `initial_prompt`

Kullanıcı, dördüncü kez, aynı şeyi söyledi: "yerelde CPU'daki küçük model bile
güzel çeviriyor, bulutta saçma sapan çıkıyor." Bu turda ilk kez **aynı ses aynı
anda iki yoldan** geçirildi, ve sebep bulutta değil bizim istekte çıktı.

### Ölçüm zemini değişti, ve asıl mesele buydu

Dört gündür kelime sayısı ve satır sayısı karşılaştırılıyordu. İkisi de uyduran
motoru ödüllendiriyor. Yeni ölçüt **kapsama**: sesin duyulabilir konuşma taşıyan
saniyelerinin kaçı metinle örtüldü. Bir görüşme kaydında susmak uydurmaktan
tehlikelidir, çünkü hiçbir şey yanlış görünmez — duraklamalı bir konuşmadan
ayırt edilemez.

O ölçüt konunca, aranan şey bir turda çıktı.

### Sebep

Sözlüğün ilk 40 terimi her isteğe `prompt` olarak gidiyordu:

    "Uliana, Serdal, Gurhan Abi, Sinan, (1) Bozkurt , Maydin, Avukat Polonya,
     ... Yani, Ben, Tamam, Ama, Evet, Bir, Sen, Kadir, Bak, Şimdi, Çok, ..."

Aynı kayıt, aynı 180 saniye, aynı servis, tek fark bu alan:

    prompt VAR:  "Yani, Uzun, Bir, Süre, Tabii, İşin, Yücün, Rast gelsin,
                  Yapıyor, Bunu, Ama, Sonuçta, Bu, Paraları, Senin, Ödem..."
    prompt YOK:  "Bu paraları senin ödemen gerekiyordu. O kendisi üstleniyor.
                  Neden? Çünkü senin sorumluluğunda."

Hotwords ile prompt aynı özelliğin iki yazılışı sanılmıştı. Değil. Hotwords bir
**ağırlıklandırma** — yanlış terim yalnızca kazanamaz. Prompt ise decoder'a
"bunu az önce sen yazdın" demek, ve modelin devam ettirdiği şey içerik kadar
**üslup**. Virgülle ayrılmış büyük harfli terim listesi bir üsluptur.

Yerelde de oluyordu, aynı prompt `small`'a `"Ben, O, O, O, O, O..."` yazdırdı.
Yerelin sağlam görünmesi bağışıklık değil doz: orada prompt yalnız ilk pencereyi
tohumluyor. Sunucu `/health`'te `prompt_persists_across_windows: true` diyor.

### Ve kendini besliyordu

`VocabularyMiner` isimleri "cümle ortasında büyük harfli kelime" diye buluyordu.
Kural yerel çıktıda çalışıyor (253 aday → 34 gerçek isim). Bulut çıktısı cümle
ortasında rastgele büyük harf üretip nokta atlayınca aynı kural 2036 aday → 230
"isim" topladı, ve en sıktakiler dilin en yaygın kelimeleriydi: "Yani", "Ben",
"Tamam", "Ama", "Evet", "Bak", "Abi". Onlar prompt'a gidiyor, çıktıyı daha çok
bozuyor, madenci daha çoğunu buluyordu.

Tarihler birebir tutuyor. `InitialPrompt` 02-09 04:13'te (7adc6f7),
`AutoVocabulary` 04:41'de (c64a4e7) girdi. Call 32 (01-09 18:18): 100-160
kelime/dk, sıfır uydurma satır. Call 36 (02-09 13:37) ve sonrası: neredeyse
hepsinde uydurma.

### İkinci sebep, sunucu tarafında: `vad=false`

Prompt kalktıktan sonra kalan fark VAD. 180 saniyenin 157'si konuşma:

    ex5 sunucu varsayılanı (vad=false)  108/157  %69  20 satır  1 uydurma
    ex5 + normalize=false               108/157  %69  20 satır  1 uydurma
    ex5 + filter_noise=false            108/157  %69  20 satır  1 uydurma
    ex5 + vad=true                      151/157  %96  43 satır  0 uydurma
    OpenAI whisper-1                    149/157  %95            0 uydurma
    yerel faster-whisper small (CPU)    150/157  %96   8 satır  0 uydurma
    yerel large-v3-turbo                151/157  %96  72 satır  0 uydurma

Kaybolan 42 saniye sessizlik değildi: 32 saniyesi konuşma, ve seviyesi
kapsanan bölgelerle aynı (-20.2 dBFS). `filtered_out` boş geliyordu, yani
sunucunun filtresi de yemiyordu — decoder o pencerelerde metin üretmiyordu.

Dört çalışan yapılandırma birbirinden iki puan içinde. Tek sapan, bizim
üretimde çalıştırdığımız ayardı.

### İki kendi hatam, ölçümle düzeltildi

**`normalize=false` eklendim, geri aldım.** 60 saniyelik sentetik oda tonunda
uydurmayı ikiye bölüyordu. Gerçek konuşmada 151'i 128'e düşürüyor ve uydurmayı
geri getiriyor. Gerekçe sağlamdı, zemin yanlıştı.

**"`vad=true` hiçbir şey yapmıyor" dedim, yanlıştı.** Sentetik oda tonunda
gerçekten hiçbir şey yapmıyor — bayraklı ve bayraksız çıktı birebir aynı, aynı
zaman damgalarıyla. Neredeyse bu yüzden elenecekti. Sessizlik, VAD'ı ölçmek için
yanlış zemin.

> **Kural:** bir bayrağı sentetik sessizlikte ölçme. Bu uygulamanın kayıtlarında
> sessizlik bol, ama bayrakların çoğu konuşmaya bakar.

### Yapılan

- `initial_prompt` istemciden, protokolden ve worker'dan tamamen kaldırıldı.
- `AutoVocabulary`, `VocabularyMiner`, ayardaki arayüz öğesi, iki dil kaynağı ve
  yalnız madenci için var olan iki depo sorgusu silindi. Elle yazılan liste
  hotwords olarak kalıyor.
- ex5 isteğine `vad=true`. `normalize` hiç gönderilmiyor; sunucunun varsayılanı
  zaten doğru.
- `.cloudparts` önbellek anahtarı isteğin tamamını görüyor. Önceden model ve
  parça numarasından ibaretti: bir bayrak değiştirip yeniden çevirince
  değişiklikten önceki yanıt birebir geri geliyordu — düzeltme işe yaramamış
  görünür, insan başka bir düzeltme aramaya gider. Bu tur boyunca en az bir kez
  yanılttı.
- `Segment.compression_ratio` — Whisper'ın kendi ölçüsü, metinden hesaplanıyor.
  Servis segment başına `avg_logprob`/`no_speech_prob` dönmüyor ve
  `is_low_confidence` testlerinin ikisi de None-korumalı, yani skor yokluğu
  "güvenilir" okunuyordu: 2241 segmentin 1321'i, tam olarak servisten gelenlerin
  hepsi. Arşivde beş satır yakaladı, sıfır yanlış alarmla.
- `chunking.speech_coverage` + `processing_run.speech_coverage` (göç 10) +
  kalite satırı: "konuşmanın %69'u yazıya döküldü". Bu turun aradığı sayı buydu
  ve hiçbir yerde tutulmuyordu.

### Yeniden çevrilmesi gerekenler

Prompt girdikten sonra çevrilen 11 görüşme: 36, 37, 38, 39, 40, 41, 42, 43, 44,
45, 51. On birinde de gözle görülür belirti var.

### Sunucudan istenenler

`vad` varsayılanının `true` olması, ve segment başına `avg_logprob` /
`no_speech_prob`. İkincisi olmadan düşük güveni yalnız tekrar döngülerinden
tahmin edebiliyoruz.

---

## 2026-09-04 — Geriye dönük kayıt: on dokuz commit ve iki ölçüm

Bu turun günlüğü o gün yazılmamıştı; 5 Eylül'de plan denetimi sırasında geri kazanıldı.

**Commit'ler (özet):** hangi dökümün ekranda olduğu `call.transcript_version_id`'de tutuluyor,
geri yükleme kopya yazmıyor (76d3564); zaman çizgisi yoğunluğu süreye değil söylenene göre
(694555c); bulutta bozulan kelime sırası bitişten çıpalanarak onarılıyor (5535672, dc3f8b6);
öneriler reddedilebiliyor, dökümler karşılaştırılabiliyor (b8a828a); "4 görüşme işlenemedi ·
Göster" gerçekten gösteriyor (7a700c6, d8026a6, 503c1e9); yedek üzerine yazmak zorunlu değil
(a22ca95); kayıt şeridi sürüklenebilir ve konumunu hatırlıyor (a0b9d9a); sessizlik kırpma
kaldırıldı (53ce631).

**Ölçüm — kısa görüşmelerde OpenAI (EK-2).** #57/#58/#61 aynı çözülmüş WAV'lar; kapsama mic/far:
#57 yerel 0,292/0,354 · OpenAI 0,167/0,524; #58 yerel 0,090/0,674 · OpenAI 0,602/0,136;
#61 yerel 0,818/0,383 · OpenAI 0,860/0,095. **Sistematik değil**: OpenAI bir kanalda yerelden iyi,
ötekinde kötü. #56 (14 sn) ölçülmedi. Karar yok; "kapsama düşükse yerelle bir daha dene" fikri
ölçüsüz kaldı (PLAN-SOSYALZEKA §9).

**Ölçüm — VAD ilk sözü düşürüyor (EK-4).** `vad_filter=False` ile: #61 far 0,383 → 0,473 ("Alo"
geri geldi); #58 far 0,674 → 0,742; **#57 far 0,354 → 0,000** (kanal tamamen kayıp). VAD'i
kapatmak elendi. `min_speech_duration_ms` hiç denenmedi; `faster_whisper_engine.py`
`vad_parameters` geçirmiyor — açık iş.

---

## 2026-09-05 — SocialZeka: çatal, kimlik, arşiv devralma (Paket R0 + P0)

**Karar.** Sosyal zekâ koçu programı için kullanıcı ayrı repo istedi: VoiceTranscript
çatallandı. Plan, ikinci görüş ve ekran taslakları `docs/PLAN-SOSYALZEKA.md`.

**Ne yapıldı.**
- Yerel çatal `C:\Voice\SocialZeka` (`git clone`, tam geçmiş, 54 etiket). GitHub reposunu
  kullanıcı elle açtı (`gh` bu makinede kurulu değil); `9cc3b64` + bütün etiketler + dallar
  `fintechcoding/SocialZeka`'ya itildi, yalnız `main` için tek CI koşumu tetiklendi ve geçti.
- Kimlik: `AppPaths.ApplicationName = "SocialZeka"` (veri kökü `%LOCALAPPDATA%\SocialZeka.Data`),
  `LegacyApplicationName`, `DatabaseFileName`, `LegacyArchiveToTakeOver`; `App.xaml.cs` tek-örnek
  kilidi ve ikinci-açılış sinyali yeni adla, ilk açılışta VoiceTranscript arşivini **taşıma**
  teklifi (Evet taşı / Hayır boş başla / İptal çık; yalnız `--data` verilmemişken ve bu kökte
  veritabanı yokken); `AutoStart` değer adı; pencere başlıkları; `UpdateService` repo yolu ve
  UserAgent; `ReleaseAssets` `SocialZeka-Setup-*`; `installer/SocialZeka.iss` yeni AppId
  `{A867C415-…}`, `AppMutex`, `DataDir`; `publish.ps1`, `release.yml`; `strings.tr/en.json` üç
  değer; README / OKUBENI / PRODUCT / docs başlıkları; MIMARI "Ad ve çatal".
  **Ad alanları, csproj adları ve `VoiceTranscript.exe` bilerek aynı kaldı.**
- Testler: `LegacyArchiveTests` (5 yeni: teklif yalnız eski DB varken ve yenisi yokken; aynı
  klasör asla; sabitler); `UpdateTests`, `ConfigurationTests`, `WindowSmokeTests` yeni adlara.
- P0: başka oturumun geçici klasöründeki `kisa/oai-57/58/61.json` düz metin OpenAI anahtarı
  taşıyordu (164 karakter) — üçü silindi. **Anahtar OpenAI panelinden döndürülmeli.** Ölçüm
  tezgâhı WAV'sız `tools/olcum/` altına; taban çizgisi ve eskimiş belge satırları düzeltildi.

**Nasıl doğrulandı.** `./test.ps1`: derleme 0 hata / 74 uyarı (~20 sn); C# **1067 test · 1062
geçti · 0 kırık · 5 atlandı** (4 `OpenRouterLiveTests` anahtar yok, 1 `PythonWorkerHostTests`
ağırlık yok); Python **156 geçti**. Devralma diyaloğu gerçek makinede henüz denenmedi: ilk
`SocialZeka.exe` açılışında VoiceTranscript.Data varken görülecek (VoiceTranscript kapalı olmalı).

**Bekleyen.** VoiceTranscript'in dondurulması (README işareti); §18'e VoiceTranscript2 iptal
gerekçesi (kullanıcıdan).

## 2026-09-05 — Paket A1: arayüz borçları (şikâyet 2/3/4/5/6, dil)

**Ne bozuktu.** Kullanıcının sekiz şikâyetinden beşi şema istemeyen arayüz borcuydu
(`PLAN-SOSYALZEKA.md` §4.11): "Yaptım" bir yüzeyde işaretlenince öbürleri eski kalıyordu
(`ShellViewModel.RefreshAll` Yapılacaklar'ı hiç okumuyordu, üç `SetActionStatus` olay
yaymıyordu); Bitenler kutusu listenin dibindeydi ve kapalıyken sayı bilmiyordu; "N görüşme
yeniden kuyruğa alındı" yazılıp aynı satırda `Refresh()` tarafından siliniyordu; "Gizlendi:"
sabit metni ve "kaldır" dili reddi gizleme gibi anlatıyordu; Ayarlar'da Kaydet pencerenin sağ
kenarındaydı (1920 px'te son alandan ~870 px uzakta), Yenile kutunun üstünde havada, Sına/Bakiye
kartın en altında, bulut modunda yerel blok soluk ama yerinde; motor kutusu OpenAI'nin yüz modelini
(gpt-3.5-turbo, babbage-002) listeliyor, üstelik `SttEndpointViewModel.TestAsync` probe'un
sırasını alfabetik ezerek bozuyordu.

**Ne yapıldı.**
- Aksiyon ↔ Yapılacaklar: `CallWindowViewModel`/`OverviewViewModel`/`ContactsViewModel`
  `SetActionStatus` sonrası `Services.CallActions.NotifyChanged()`; `RefreshAll` → `Todo.Refresh()`;
  `TodoViewModel` Toggle/Dismiss/UndoDismiss de yayar. Bitenler kutusu süzgeç satırına, "Bitenler
  ({0})" sayısıyla; biten satırlar hep okunur, yalnız açıkken gösterilir (`DoneCount`);
  `AppSettings.TodoShowDone` (`TodoPage.xaml.cs`, `ConversationTimeline` kalıbı).
- `ProcessingViewModel.Requeue`: `Refresh()` önce, bildirim sonra.
- Dil: `todopage.reddedildi-n`; `callwindow.bu-oneri-bir-daha-gosterilmez` → "Reddedersen bir
  daha önerilmez."; `ledgerpage.bu-satiri-kaldir` ve `contactspage.bu-kaydi-defterden-kaldir` →
  "Bu bulguyu reddet"; `settingswindow.uzun-aramalar` → "Uzun görüşmeler"; `healthpage.60-…`
  "görüşme"; `settingswindow.krediyi-sor` → "Bakiyeyi sor"; `todopage.oneriyi-gizle` →
  `todopage.oneriyi-reddet`. `.cs`/XAML sabit metinleri sözlüğe: `ShellViewModel` durum satırları
  ("Gelen çağrı", "Görüşme başlayınca…"), `LedgerViewModel` KindLabel/LateText/beş bildirim,
  `MainWindow.xaml` bildirim paneli, `ProcessingPage.xaml` "Hepsini durdur", `CallWindow.xaml`
  dört Setter, `RecordingOverlay.xaml.cs`, `CallerOverlay.xaml` başlığı — 35 yeni anahtar, iki
  dilde. `MainWindow.xaml.cs` ölü `Setup_Click` silindi. `YOLHARITASI`/`YAPILACAKLAR` "Gizle" →
  "Reddet".
- Ayarlar: alt bar `232` ray boşluğu + `MaxWidth=760` yıldız sütunu (sola hizalı `MaxWidth`
  Grid **olmaz**: sorun listesi boşken içeriğe büzülüp düğmeleri sola kaydırıyor — ilk denemede
  öyle yazılmıştı, düzeltildi); Yenile `VerticalAlignment=Bottom`; model kutusunun altına
  "Tümünü göster ({0} model daha)" bağlantısı, Sına/Bakiye/Durum kutunun hemen altına, "Gelişmiş
  adres" en alta; `UsesLocalAsr=false` iken yerel blok `Collapsed`.
- Motor listesi: `SttProbe.TranscriptionCandidates(models, catalogue)` — `whisper|transcribe|
  scribe|stt|speech|asr` ∪ katalog, boş kalırsa tam liste; `SttModelList`/`SttTestResult`
  `AllModels` + `HiddenCount`; `TestAsync` model varlığını tam listeye göre yargılar (kutu gizledi
  diye "listede yok" denmez); Message "N modelden M tanesi…". `SttEndpointViewModel`: `OrderBy`
  kalktı, `Offer/Fill`, `ShowAllModels` geçişi, `HiddenModelCount`.
- Testler (+13): `LocalisationTests` üç kural (`.cs` içindeki `Localisation.T("…")` anahtarları
  sözlükte; `{0}` eşliği; "gizle" sözü yalnız `calleroverlay.`/`recordingoverlay.` değerlerinde ve
  `"Gizlendi` sabiti kodda yok); `SttProbeTests` dört (daraltma + sıra, tanınmayan liste bütün
  gelir, katalog daraltmadan sağ çıkar, yazılan model tam listeye göre); `SttEndpointViewModelTests`
  yeni (sıra korunur, geçiş, katalogdan kurulan kart gizlemez); `SuggestionsOnTheTodoPageTests`
  üç (`DoneCount` kapalıyken, `showDone:true` başlangıç, "Reddedildi");
  `FailedCallsAreReachableTests` Requeue bildirimi.

**Nasıl doğrulandı.** `./test.ps1`: derleme 0 hata / 68 uyarı; C# **1080 test · 1075 geçti · 0
kırık · 5 atlandı** (taban 1067/1062 → +13); Python **156 geçti**. İlk koşumda
`EveryKeyUsedInCodeExists` `HealthPage.xaml.cs:54`'teki önek+değer birleştirmesini yarım anahtar
sandı; tarama yalnız `T("…")` biçimindeki bütün anahtarlara daraltıldı. Kaydet hizası (≤ 32 px
@1920) ve motor kutusunun canlı OpenAI anahtarıyla ilk beş satırı **elle bakılacak**; kurgu
gereği ikisi de sütun sayılarından çıkıyor (256 + 760 = 1016 iki yüzeyde de).

**Sürüm.** A1 bitince ilk etiket `v3.0.0` (YOLHARITASI "yığın bitince tek sürüm"); etiket
kullanıcı onayıyla atılır.

## 2026-09-05 — Paket A2, ara durum: ön koşul ve bağımsız yarılar ana dalda

Dört iş ayrı çalışma ağaçlarında paralel yürütüldü, her biri kendi commit'iyle ana dala
birleştirildi (worktree ajanları yanlışlıkla dondurulmuş VoiceTranscript deposunda açıldı; ikisi
kendi SocialZeka ağacını açtı, ikisi SocialZeka `main`'i çekip ilerledi — hepsi `05871a4` üstüne).

- **Tarih çözümü görüşme gününe göre** (`85aa95f`): `TurkishDates.TryResolve(phrase, DateOnly spokenOn)`
  zorunlu; `DateTime.Now` düşüşü kalktı; `AnalysisPipeline.Absorb` ve `ActionExtraction` görüşmenin
  yerel başlangıç gününü verir; "N gün/hafta sonra" sayaç kuralı eklendi. 7 test. Ajanın bulduğu
  ek hata (`2154f4f`→`65d74b7`): `TryWeekday` tablodaki ilk alt dizeyi alıyordu, "cumartesi" cumaya
  çözülüyordu; en uzun ad kazanır, 1 test.
- **Worker C+J** (`cdea035`): ElevenLabs `probability = exp(logprob)`; `tag_audio_events:"true"`;
  olaylar segmentlerin dışında `audio_events:[{channel,start_ms,end_ms,kind}]` (kanal alanı plana ek:
  motor hangi tarafı aldığını bilmez, Aynam "3 kez güldün" için gerekir); istek imzasına bayrak
  girdi (eski önbellek parçaları olaysız dönmesin). 7 test.
- **Worker G** (`5d1a663`): `prosody.py` salt numpy — 25/10 ms çerçeve, −40 dBFS konuşma kapısı
  `speaker.py`'den içe alınır, YIN/CMND 60–400 Hz eşik 0,15 (plan sabitleri, **ölçülmedi**),
  0,5 sn kutular `[t, dbfs, f0|null, voiced]`; `cmd_prosody`. Ölçüm: 20 dk sentetik, %90 konuşma:
  tek kanal 2,96 s, iki kanal 5,98 s, 161 MiB (hedef ≤ 15 s). 16 test.
- **Dikey alan** (`ba443f6`, şikâyet 8): 880×720'de dökümün üstündeki bantlar **297 → 199 px**,
  döküm sessiz görüşmede **351 → 515 px**, oynatıcı açıkken **309 → 436 px**; oynatıcı satırı
  ses yokken 62 → 0 px, açıkken 104 → 79 px (dalga katlanınca 47). Sekme şeridi 44 px kaldı
  (`SegmentedTabItem` dolgusu ortak); etiketli görüşmede başlık bandı 880'de iki satıra sarıyor
  (Hatırlat/Önemli etiketleri simgeye inmedi — bandın söylediği değişmesin diye). `LayoutTests`
  ölçümü **çocuk süreçte** alır: WPF süreç başına tek `Application`, tema fırçaları
  dondurulamıyor, `WindowSmokeTests` o tek iş parçacığının sahibi; ikisi aynı süreçte
  koşunca önce başlayan kazanıyordu. Ortak UI iş parçacığı fikstürü ileride; şimdilik +2 sn.
  `CallWindowViewModel.TranscriptVersionCount` "N döküm" rozeti için; "Yeniden çevir" düğmesi
  ⋯ menüsüne, `callwindow.yeniden-cevir` anahtarı XAML'de kullanılmıyor (sonraki temizlikte).

**Doğrulama.** `./test.ps1` ana dalda: C# 1090 test (1085 geçti, 5 atlandı); Python 179.

## 2026-09-05 — Paket A2 çekirdeği (şema v15), bayatlık arayüzü, Paket C C# yarısı, Paket B

**Şema v15** (`45db320`). `reading_note / deception_note / consistency_note / call_summary /
action_item` → `transcript_version_id`; `Repository.DerivedFreshness` görüşmenin gösterdiği
dökümle karşılaştırır: yok / taze / **bilinmiyor** (NULL — asla "bayat" denmez) / bayat.
`commitment`: `created_at, fulfilled_at, decided_at` + kullanıcı sütunları `user_deadline_date,
user_obligation, edited_at`; `flag`/`action_item`: `decided_at`; `verdict` (KULLANICI, kulak
teyidi; anahtar katlanmış alıntı + ms, `target_id`'ye FK yok — birleştirmede id'ler değişir).
Fiiller: `Reopen/Restore/Abandon`, `DismissCommitments/DismissFlags`, `RestoreFlag`,
`SetUserDeadline/SetUserObligation` (söylenen tarih/söz makine sütununda kalır),
`PromiseLedger` (tek sorgu, dört yüzey buradan), `Dismissed*`, `SaveVerdict/Verdicts/VerdictTally`.
Koruma: `ClearAnalysis` ve `SweepLedger` düzenlenmiş satıra dokunmaz; boru hattı
`SurvivingCommitmentKeys` + `DismissedFlagKeys` ile aynı sözü/işareti ikinci kez yazmaz (K4 —
daha önce her yeniden çözümleme tutulmuş sözü açık, reddedilmişi reddedilmemiş geri getiriyordu).
Kullanıcının vadesi her sayımda ve takvimde kazanır (`COALESCE`); `MovedDeadlines` yalnız
söyleneni okur — erteleme kişiye bayrak olmaz. `MergeArchive`: `map_version` ile döküm
işaretçileri yeniden eşlenir (önceden ham kopyalanıyordu; FK açıkken ithalatı düşürebilirdi),
`verdict` taşınır. Testler: `MigrationTests` v15 + eksik v8/v9/v10/v14 blokları + **taze/yükseltilmiş
sütun kümesi karşılaştırması** (her tablo, her sütun); `DerivedFreshnessTests`, `LedgerUndoTests`,
`PromiseLedgerTests`, `VerdictTests`, `ArchiveMergeTests` v15, `AnalysisPipelineTests` ikinci
çözümleme. Not: göç testi eskiden yalnız `call` tablosunu eski biçimde ekiyordu, ALTER'lar hiç
koşmuyordu; `commitment` ve `reading_note` v14 biçiminde ekilince v15 ALTER'ları gerçekten koşuyor.

**Bayatlık arayüzü** (`e9a8721`, şikâyet 7): görüşme penceresinde Defter/Aksiyonlar/Tutarlılık/
Değerlendirme/Okuma sekmelerinde "önceki dökümden" uyarısı + yeniden üret / **Sil**
(`DeleteReading/DeleteDeception` ilk kez çağrılıyor); bilinmiyor için çubuk yok; yeniden dökümden
sonra çözümleme koşmayacaksa düzenleyici tek bildirim; sürümler penceresi geri yüklemede uyarır.

**Paket C, C# yarısı** (`f47c584`): `SpokenWord.Probability`, `SegmentWords` dörtlü satır (3
ondalık; üçlü eski satırlar aynen), `CallOrchestrator` artık düşürmüyor. Eşik yok — motor
başına kulak teyidiyle ölçülecek (D).

**Paket B** (`6e18ebd`): Sözler sayfası (Ctrl+5), ray grupları (GÖRÜŞMELER / HAFIZA / BUL, Durum
altta), Ctrl rakamları ray sırasında ve **tek listeden** (`ActionRegistry` → tuş bağları, palet,
Ctrl+? listesi; `ActionRegistryTests` her sayfaya bir eylem + tekil tuş), arayan şeridi iki yön
(`PromiseLedger`), genel bakışta liste tek satıra indi (Dikkat kartları duruyor). "Tutuldu mu?"
yalnız öneri (`SuggestFulfilment`: sonraki ≤5 görüşmede ≥2 ortak anlamlı kelime, kabul oranı
ölçülecek: 30 öneride < %30 → kapatılır); "açık kaldı" yalnız vade sonrası görüşme olduysa
(`CountCallsSince`); oran yok, üç sayı. Kalan B işi: Defter'deki söz çipleri ve `Fulfil`
(defter fiilleri dalıyla birlikte), takvimin `PromiseLedger`'dan beslenmesi.

**Doğrulama.** `VoiceTranscript.Tests.exe`: 1134 test (1129 geçti, 5 atlandı); Python 179.

**Defter fiilleri** (`81f1fcb`, şikâyet 1'in arayüz yarısı; ayrı çalışma ağacında yazıldı, iki
kesintiden sonra tamamlandı): `Services/LedgerActions` tek fiil kümesi (Dismiss/DismissMany/
Restore/Fulfil/Reopen/Abandon/SetUserDeadline/SetUserObligation/Edit, her biri `PendingUndo`
döndürür; `Changed` → `RefreshAll`); Defter'de satır başına Reddet, Reddedilenler çipi + Geri
getir, Seç kipi + "Seçilenleri reddet (N)", Sırala (Tarih/Kişi — Türkçe alfabe/Tür), Kaynak
(Kural/Denetim), sayfa içi Geri al kartı; görüşme ve kişi penceresindeki söz kartlarında
Tutuldu / Tutulmadı / Ertele / ✎ (`EditPromiseWindow`: söylenen sözler ve tarih durur, "senin
düzeltmen" rozeti); ContactsPage'in Reddet'i aynı yoldan. **Sonra:** söz çipleri ve satırları
Defter'den çıktı — Sözler sayfasında; Defter yalnız değişen rakamlar + işaretler + reddedilenler
(`LedgerPageUndoTests` işaretlerle yeniden yazıldı; `PromiseSideTests`'in iki defter testi Sözler
testlerine devredildi). Sözler kartına ✎ bağlandı. Testler: 1137 C# (1132 geçti, 5 atlandı).

## 2026-09-06 — Aynam çekirdeği (şema v16) ve şive ön-ölçümü: dedektör yazılmadı

**Paket D, çekirdek yarısı** (`ab9e272`, ayrı çalışma ağacında). Şema v16: `speech_habit`
(makine önbelleği; `transcript_version_id` + `lexicon_version` taşır, böylece bir yeniden
döküm ile bir yeniden sayım birbirinden ayrılır), `habit_lexicon` ve `call_intent` (ikisi de
KULLANICI tablosu; `ClearAnalysis` üçüne de dokunmaz). `habits.tr.json` gömülü tohum sözlük
(27 küfür gövdesi + ek listeleri, 13 dolgu, boş "haric"); eşleşme **token sınırlı**: gövde
kelimenin başında ve kalan ya boş ya izinli bir ek — "klasik" ve "şikayet" eşleşmez, "siktir"
eşleşir. `HabitLexicon` (yükleme, ekleme, `LexiconVersion` = satırların sırasız FNV-1a özeti),
`TalkStats` (konuşma payı ve söz kesme kuralı `CallWindowViewModel`'den birebir alındı, yankılı
satırlar dışarıda), `SpeechHabits` (yalnız `IsMe && !SuspectedEcho`; kelime güveni eşiğin
altındaysa ya da satır belirsizse **"belirsiz" kovası** — listelenir, sayılmaz; kulak teyidi
"yanlış duyulmuş"/"bu o değil" derse sayımdan düşer; "verilen bilgi" yalnız **tür + zaman**,
değer asla), `HabitTrend` (aylar **havuzlanır**, ortalanmaz: iki dakikalık bir görüşmedeki tek
küfür, saatlik bir görüşmenin yanında ortalamaya girerse ayı olmadığı bir yere çeker) ve
`HabitTrendLayout` (saf, testli). Payda görüşme değil: **kendi konuşma dakikan / 100 kelimen**.
66 yeni test; toplam 1203 (1198 geçti, 5 atlandı).

**Şive ön-ölçümü** (plan §6.1, `tools/olcum/sive-onolcum.py`, gerçek arşiv: 51 görüşmede
kullanıcının kendi satırları): 40 eşleşme, 13 görüşmede, **görüşme başına 0,78**. Kapı ≥ 1
eşleşme/görüşme idi — **kaldı**. Dedektör yazılmadı; Aynam "şive: ölçülmüyor (neden ▸)" der ve
gerekçeyi gösterir. Örüntü dağılımı: `-yon` 24, `-yom` 5, "napıyo…" 3, "hele" 2, "gari" 2,
"valla" 2, `-cem/-cez` 2. Kesinlik kapısı (≥ 0,6) hiç ölçülmedi: sayı zaten eşiğin altında, ve
Whisper'ın yazı diline normalize ettiği bir şeyi saymak konuşmacıyı değil motoru ölçer. Bu
sonuç plandaki §7-7 kararını doğruluyor.

**Ölçüm tezgâhı**: `arsiv.py` (SocialZeka.Data yoksa devralınmamış VoiceTranscript.Data'ya
düşer — çatal sonrası arşiv iki yerden birinde), `sive-onolcum.py`, `aynam-kesinlik.py`
(`verdict` tablosundan tür ve **motor** başına doğru/dinlenmiş; kapılar küfür %90, dolgu %85,
en az 30 dinleme; v15'ten eski arşivde yığın izi yerine tek cümle). `esik.py` dondurulmuş
depoya bakıyordu, düzeltildi. Dinleme örnekleri konuşma içeriği taşıdığı için `.gitignore`
`tools/olcum/*.jsonl|*.wav|*.txt` satırlarıyla depo dışında.

## 2026-09-06 — Kişi kartı çekirdeği (şema v17) ve bir kural değişikliği

**Kural değişikliği — yazılı olarak.** `deception_note` bugüne kadar çıkmaz sokaktı: model
şüphe düzeyini ve değerlendirme paragrafını yazar, hiçbir tablo o satıra bakmaz, hiçbir isteme
geri beslenmezdi. Bu, düzey ve değerlendirme için **aynen duruyor**. Gevşetilen tek şey alıntı:
sözleri döküme karşı **doğrulanmış** bir taktik satırı artık `tactic_evidence` tablosuna
kopyalanır, böylece aynı cümle kişinin kartında sayılabilir. Neden `flag` değil: dokuz tüketici
ve iki dışa aktarım o tabloyu kanıt sayıyor, oysa bu satırlar şüphe arayan bir görev tanımından
çıkıyor — kartta ayrı kaynak süzgecinde ve "model etiketi" rozetiyle durur, hiçbir isteme
girmez (test bunu bir işaret dizesiyle kanıtlıyor). Kullanıcının 5 Eylül'deki ikinci kararı
("doğrulanmış alıntı biriksin") bunun onayıdır; plan §2 bu kaydı istiyordu.

**Şema v17** (`6d6284d`): `tactic_evidence` (beyaz liste; bilinmeyen taktik **düşer**, "diger"
yazılmaz; `dismissed_by_user` tombstone, `DismissedTacticKeys` yeniden yazılmayı engeller —
kural iki yazıcı için de `ReplaceTacticEvidence`'ın içinde) ve `speech_act` (`sorular` artık
kalıcı; `EvasionRate` eskisi gibi çalışıyor). İkisi de `LedgerTables`'a girdi; yeni
`CountedLedgerTables` `speech_act`'i saymaz (bir soru defter satırı değildir). `ClearAnalysis`
soruları ve boru hattının kendi taktik satırlarını siler, değerlendirmeninkilere dokunmaz.
`baski_isaretleri` yazımı **`AnalysisOptions.WritePressureSigns` kapısının arkasında, varsayılan
kapalı** — plan önce kesinlik ölçümü istiyor; doğrulanamayan alıntılar artık
`QuotesRejected`'a sayılıyor, ölçüm bu yüzden yapılabilir.

**Kart sorguları**: `ContactPatterns` (tür × kaynak; dinlenmiş/doğru `verdict`'ten, katlanmış
alıntı + ms ile eşlenir — SQLite Türkçe katlama yapamaz, o yüzden tek sorgu + C# tarafında
toplama), `PatternRows`, `FigureJourney` (yalnız karşı tarafın iddiaları, ≥ 2 farklı değer;
düşük güvenli duraklar işaretiyle listelenir, atılmaz), `OwnWords`, `SpeechActs` ("N/M
görüşmede ölçüldü" paydası), `ContactSeries`; `ContactTrend` (ay kovaları; Unknown yön paydada
değil; konuşma payı ölçülen görüşmelerin **ortalaması**, söz kesme ölçülen dakikalar üzerinden
**havuzlanır**; son 3 ay ↔ önceki 3 ay). 41 yeni test.

**Doğrulama.** `VoiceTranscript.Tests.exe`: 1255 test (1250 geçti, 5 atlandı).

## 2026-09-06 — Aynam arayüzü: sayfa, görüşme sekmesi, üç sayılık tost

**Sayfa** (`dcc6bf7`, Ctrl+8, rayda yeni KOÇLUK grubu). Altı kart: küfür/dk, dolgu/100 kelime,
hız k/dk, konuşma payın, söz kesme/10 dk, istemeden verilen bilgi. Her kartın altında **aynı
uzunluktaki önceki dönem** ve ▲/▼/— oku — renk yok, yargı sözcüğü yok. Pencere içindeki sayılar
**havuzlanır** (pay ve payda görüşmeler boyunca toplanır), ortalamaların ortalaması alınmaz.
Paydası olmayan kart tire gösterir ve nedenini yazar — bugünkü arşivde "hız" böyle: hiçbir döküm
kelime zamanı taşımıyor. Eğri `HabitTrend` + `HabitTrendLayout`'tan geliyor; motor değişimi
kesik çizgi, kulaklıksız görüşme içi boş nokta, noktaya tıklayınca o görüşme açılıyor. Anlar
listesinde üç kulak teyidi düğmesi (Doğru / Yanlış duyulmuş / Bu o değil) `verdict`'e yazıyor ve
sayım anında düşüyor; "belirsiz" anlar listelenip sayılmıyor. Kesinlik satırı dinlenen ve doğru
çıkan sayısını söylüyor. **Şive kart olarak yok** — ölçüldü, kapıdan kalktı, sayfa nedenini
"neden ▸" ile açıklıyor (dün yazılan ölçüm sonucu).

**Görüşme penceresinde Aynam sekmesi**: bu görüşmenin altı sayısı, anlar, niyet satırı (yalnız
kullanıcı yazdıysa), "hesaplandı · döküm" damgası, ve döküm yenilenmişse bayatlık uyarısı +
"Yeniden say". Karşı taraf sayılmıyor; ekran bunu yazıyor.

**"Ne oldu?" tostu**: "sen %61 · küfür 3 · 2 açık söz" — sayılmamış görüşmede parça atlanıyor,
"sen %0" yazılmıyor. Tıklayınca görüşme Aynam sekmesinde açılıyor; bildirim mekanizmasına bu
yüzden tıklama eylemi eklendi (`Post(..., onClick)`), `NoticeSeverity` ile aynı yoldan.

**Temizlik**: konuşma payı/söz kesme hesabının iki elle yazılmış kopyası (`CallWindowViewModel`,
`ContactsViewModel`) silindi, ikisi de `Core.Analysis.TalkStats`'ı çağırıyor. Davranış farkı:
yankı şüpheli satırlar artık ikisinde de sayılmıyor.

**Doğrulama.** 1277 test (1272 geçti, 5 atlandı). Ajanın notu: bağlamaların gerçekten çözüldüğü
elle bir "render" testiyle doğrulandı ama test ağaca alınmadı — ikinci bir STA iş parçacığı
`WindowSmokeTests`'in tek `Application`'ıyla çakışıyor (aynı sebeple `LayoutTests` çocuk süreçte
ölçüyor). İstenirse `WindowSmokeTests`'in kendi iş parçacığının içine konur.

## 2026-09-06 — Ses: canlı sessiz ölçer (H), ölçüm ve kelime olmayan sesler (G, J)

**Canlı ölçer** (`605eea6`, Paket H, varsayılan **kapalı**). Kayıt şeridinde "sen %64", 6 px
pay çubuğu ve kendi 120 saniyelik medyanına göre ▲/—/▼ oku. **Desibel sayısı ekrana çıkmıyor**
(Windows iletişim hattı sinyali işliyor, sayı kurgu olurdu), uyarı yok, alarm yok. Mekanizma:
yakalama olayına üçüncü abone (`SpeakerIdentifier` kalıbı), paket başına `Interlocked`
sayaçlar ve 120 saniyelik halka; **paket başına tahsis sıfır** (ölçüldü: 10 000 pakette 0 bayt),
kilit yok, gövde try/catch — hata ölçeri karartır, kayıt yoluna çıkmaz. Kulaklık kapısı: aynı
10 saniyelik pencerede far kanalı da eşiğin üstündeyse o pencere sayılmaz; atılan pencere
tutulandan çoksa pay hiç gösterilmez. Ölçülen bedel: 20 dk iki kanal (240 000 paket) **0,24 sn
işlemci — bir çekirdeğin %0,02'si**, paket kaybı **sıfır**. `SpeakerIdentifier.IsSpeech`
gövdesi `Dbfs`'e ayrıldı ve `IsSpeech` onu çağırıyor (kısa paket dahil davranış birebir aynı) —
iki ekranın aynı saniye hakkında farklı şey söylememesi için tek formül, tek eşik.

**Ses ölçümü** (`d13ecda`, `cf54407`, bu commit; Paket G'nin C# yarısı). Şema v18: `prosody`
**sese göre** anahtarlı (`audio_key` = dosya adı + uzunluk) — yeniden döküm ölçümü geçersiz
kılmaz, kırpma/yeniden kodlama kılar; `audio_event` dökümüne bağlı. `ProsodySeries` saf ve
**kanal içi**: medyan + MAD (standart sapma değil — bağırmalar kendi ölçeğini şişirip kendini
gizlerdi; MAD sıfıra düşerse — kutuların yarısından çoğu aynıysa, ki tam da ölçümün var olduğu
durum — ortalama mutlak sapmaya düşülür), perde yarım ton, zirve z > 2 ve ≥ 4 ardışık kutu
(iki saniye), üst üste konuşma ve yankı bölgeleri **ölçülür ama sayılmaz** — başkasının sesi
üzerinden ölçülen düzey o başkasının sesidir. Orkestratör en sonda, döküm güvendeyken, GPU
kapısı olmadan çağırıyor; hata bir ölçümü kaybettirir, görüşmeyi değil. Ayarlarda **açık**
varsayılan, çünkü şeridin işe yarayıp yaramadığı ancak 60 zirve dinlenerek ölçülebilir ve
dinlenecek bir şey olması için sayıların var olması gerekiyor. **Şerit çizilmiyor** ve ayar
kartı bunu kapısıyla birlikte yazıyor.

**Kelime olmayan sesler** (Paket J'nin C# yarısı). `WorkerResult.AudioEvents` → `audio_event`;
kahkaha dökümün **yanında**, içinde değil — satır olsaydı kimsenin söylemediği bir cümle
alıntılanırdı. Etiketlemeyen motor boş liste gönderir ve bu da yazılır (önceki motorun okuması
kalmasın diye sil-yaz). `ClearAnalysis` dokunmaz: sesle birlikte geldiler, defterin akıl
yürütmesiyle değil.

**Doğrulama.** 1310 test (1305 geçti, 5 atlandı).

## 2026-09-06 — Kişi kartı arayüzü (Paket E tamam)

`888ecda`. Tek `ContactCardView`, iki yerde: kişi penceresinde "Kişi kartı" sekmesi (Defter
sekmesi kalktı — 261 satır XAML ve dört fiil onunla birlikte; kart onun işini de yapıyor) ve
kabuktaki Kişiler sayfasının ayrıntı bölmesinde aynı denetim.

**Gidişat**: görüşme sıklığı, kim aradı (bilinmeyen yön paydada değil), konuşma payın, karşı
tarafın söz kesmesi, cevapsız kalan sorular — her satırda "N/M görüşmede ölçüldü" paydası ve
son dönem ↔ önceki dönem oku, renksiz. **Grup görüşmeleri hem Gidişat'tan hem sorulardan
dışarıda** ve kart "N grup görüşmesi sayılmadı" diyor. **Kalıplar**: tür × kaynak; model
etiketli satırlar (`tactic_evidence`) kendi rozetiyle, kendi sayısıyla ve **yalnız
"Değerlendirme" süzgecinde** — Kural ve Denetim seçildiğinde ekranda tek bir model etiketi
yok. Rozeti belirleyen test, alıntıları getiren sorgunun kullandığı testin aynısı, böylece ikisi
ayrışamıyor. Ret oranı %30'u geçen tür **çubuğunu kaybediyor**, alıntıları kalıyor; düşük
güvenli satırlar gri ve ayrı sayılıyor. Her alıntıda üç kulak teyidi ve Reddet — hepsi
`LedgerActions` üzerinden, geri alınabilir. **Rakam yolculuğu** ve **Elindeki kayıtlar** kanıt
zemininde, tarihli ve çalınabilir; ikincisi "kullanabileceğim argümanlar" isteğinin kanıt
karşılığı olduğunu ve uygulamanın argüman yazmadığını yazıyor. **Modelin görüşü** paneli tek
katlanmış satır: henüz yok, varsayılan kapalı, ayrı zemin.

Bir test kartın bütün genel yüzeyini ve bütün `contactcard.*` metinlerini tarayıp skor/puan/
güven/risk/tehlike sözcüklerini reddediyor — kural sözcükten de sızamasın diye.

**Doğrulama.** 1321 test (1316 geçti, 5 atlandı).

## 2026-09-06 — Kişi kartında modelin görüşü (Paket I tamam; program bitti)

`22cef12` + `c1379cf`. Şema v19 `contact_reading` — kişi başına tek satır değil, tarihli geçmiş:
her okuma bir ücretli istektir ve üstüne bir [Katılmıyorum] işareti binmiş olabilir, o yüzden
eskisi silinmiyor. Tablo ölü uç: hiçbir sorgu ona join atmıyor, hiçbir isteme geri beslenmiyor.

**Pakete ne giriyor.** Sayılabilir bir özet satırı, `[B#]` defter çıpaları (iddia 20 + söz 20 +
`flag` 20) ve `[A#]` görüşme satırları (40). **Ne girmiyor: `deception_note`, `tactic_evidence`,
`call_summary`.** İlk ikisi modelin kendi eski şüphesini geri okuması olurdu; üçüncüsü döküme
karşı hiç doğrulanmamış tek saklı metin. Bunu tutan test üç ayrı imleci yalnız o üç tablonun
içine yazıp ne kullanıcı ne sistem isteminde geçmediğini iddia ediyor — `TacticEvidenceTests`'in
tekniği, üç tabloya genişletilmiş hâli. Grup görüşmeleri paketin iki yarısından da, `calls_covered`
sayısından da düşüyor.

**Çıpa kuralı.** Verilmemiş bir numaraya dayanan madde eleniyor ve **sayılıyor**; `genel_izlenim`
dayanaksız yazılamıyor; elenen oranı %40'ı geçince kart "bu model bu iş için uygun olmayabilir"
diyor. Saklanan şey ham cevap değil, **zorlanmış şekil**: kart yeniden açıldığında görülen,
gösterilenin aynısı; düşen madde düşmüş kalıyor.

**Tavan.** Bulut 400 bin, yerel 24 bin karakter. Aşarsa paket küçültülüp (B30 + A20) yeniden
kuruluyor — aynı sorunun kırpılmışı değil, daha küçüğü dürüstçe sorulmuş hâli. O da sığmazsa
karakter sayısıyla reddediyor ve tek kuruş harcamıyor.

**İki sınır panelin kendi metninde.** Psikolojik durum ve duygu durumu verilmiyor (gerekçesiyle:
metinden ya da sesten duygu okuması Türkçede doğrulanmadı, yanlışı zararlı); "kullanabileceğin
argümanlar" yazılmıyor ve karşılığının yukarıdaki **Elindeki kayıtlar** olduğu söyleniyor. İstemde
ayrıca ses tonu iddiası, skor/yüzde/güvenilirlik derecesi, yağcılık ve kesinlik dili yasak;
`baska_okuma` ile `ben_icin_notlar` (simetri) zorunlu.

**Geri alma kuralı işliyor.** Son üç **kişinin** en yeni okumasının üçünde de [Katılmıyorum]
varsa özellik kendini kapatıyor, ayar kartına "ölçüm olumsuz" rozeti ve gerekçesi düşüyor,
günlüğe satır yazılıyor. Anahtarı elle açmak rozeti kaldırıyor.

**`IGpuGate` yazılmadı, bilerek.** `_gpu` semaforunu bugün yalnız `CallOrchestrator.ProcessAsync`
alıyor; elle çalıştırılan Okuma, Değerlendirme ve Tutarlılık zaten doğrudan modele gidiyor. Yani
kişi okuması bulut kuyruğunun arkasına hiç düşmüyor — planın vaat ettiği fayda halihazırda var.
Yazmak, "koşan iş GPU'yu kullanıyor mu" bilgisini kayıt/döküm yolundan dışarı açmak demekti;
davranışta hiçbir kazanç yokken o yola dokunmak kayıt maliyeti taşır.

**Ayrılık:** `MergeContacts` iki kişi birleşince okumaları silmiyor, hayatta kalan kişiye taşıyor;
`LatestContactReading` en yenisini gösteriyor. Silmek, özelliğin açık kalıp kalmayacağına karar
veren [Katılmıyorum] işaretlerini sessizce yok etmek olurdu.

**Doğrulama.** 1338 C# testi (1333 geçti, 5 atlandı) ve 179 Python testi. Yeni sınıf
`ContactReadingAnalysisTests` (9 test), `ContactCardTests` +4, `MigrationTests` v19,
`ArchiveMergeTests` +1, `SchemaStrictnessTests` şemayı kendiliğinden yakaladı (+2).

## 2026-09-06 — Bitiş denetimi: ertelenen tarihsiz söz çözümlemeyi çökertiyordu

Program bitti denildikten sonra dokuz denetçi bütün paketleri planın kabul ölçütlerine karşı
koda bakarak sınadı; 45 ciddi bulgunun her biri onu çürütmekle görevli ayrı bir ajana verildi.
19'u çürütüldü, **26'sı doğrulandı**. İkisi kırık, kalanı eksik ya da tutarsızlık.

**Kırık 1, bugün düzeltildi.** `DeterministicChecks.OverdueCommitments` kapıyı
`commitment.IsOverdue(today)` ile açıyor; o da `EffectiveDeadline`'ı, yani
`UserDeadlineDate ?? DeadlineDate`'i okuyor. Bir satır aşağıda gün sayısı ham `DeadlineDate`'ten
hesaplanıyordu. Konuşmadan tarih çıkmamış ama kullanıcının ertelediği bir söz kapıdan geçiyor ve
o satır **null'u açıyordu**: `InvalidOperationException`, ve o kişiyle yapılan her yeni
görüşmenin çözümlemesi ölüyordu.

Bu teorik değildi. Gerçek arşivde on üç sözün on ikisinde konuşmadan çıkmış tarih yok ve Sözler
sayfası hepsinde Ertele düğmesini gösteriyor. Düzeltme tek satır: sayım artık kapının yargıladığı
tarihi okuyor. `MovedDeadlines` bilerek dokunulmadı — o, konuşmada söylenen tarihi okur, çünkü
kullanıcının kendi ertelemesi karşı tarafa kaydırılmış vade diye yazılamaz; kapısı zaten
`DeadlineDate: not null` süzüyor.

Üç test: tarihsiz ertelenmiş söz çökmeden sayılıyor; kullanıcının tarihi gün sayısını belirliyor;
erteleme karşı tarafa kaydırılmış vade olarak geçmiyor.

**Kırık 2, sıraya alındı.** Kişi kartında "yetersiz kayıt" reddi ekrana hiç ulaşmıyor:
`ContactReadingAnalysis` üç görüşmeden azını dürüstçe reddediyor ama `ContactCardViewModel`
`report.Insufficient` dalını okumadığı için [Yeniden sor] sessiz kalıyor. Dokuz kişinin çoğunda
üçten az görüşme var, yani düğme çoğu kartta hiçbir şey yapmıyor gibi görünüyor.

Kalan 24 bulgu `PLAN-IKINCI-TUR.md`'nin arkasına, paket paket kapatılmak üzere yazıldı. En
görünür olanları: `ReprocessWindow`'da "indirildi" rozeti hiç yazılmamış; şikâyet 2'nin kendi
ölçüsü ("Yaptım" sonrası Yapılacaklar aynı anda güncel) hiçbir testle korunmuyor, yani üç
`NotifyChanged` çağrısından biri silinse bütün takım yeşil kalır; `fulfilled_by_call_id` hâlâ
her yolda null; Sözler sayfası A2'de "tek fiil kümesi" diye kaydedilen `LedgerActions`'ı
atlıyor; Kalıplar'ın üçüncü kaynağı (`tutarli_gozlemler`) ne saklanıyor ne sayılıyor.

**Doğrulama.** 1341 C# testi (1336 geçti, 5 atlandı; taban 1338'di) ve 179 Python testi.

## 2026-09-06 — İkinci tur planı

`docs/PLAN-IKINCI-TUR.md`. Kullanıcının üç isteği üç pakete ayrıldı: **Ç** çevreler (kişiye bir
kez yazılan aidiyet, şema v20), **S** sözün tabanı (dört yüzey, şema yok), **B** arayüz bütünlük
sözleşmesi (12 kural, 12 test). Üç ayrı çok ajanlı turdan sentezlendi; her sayı canlı arşivden
ölçüldü.

Planın dayandığı iki ölçüm. Birincisi: kullanıcı 6 Eylül 12:23'te **#99 ve #100'ü yedi saniye
arayla "tutuldu" işaretledi**; ikisi aynı görüşmenin aynı milisaniyesinden, tek cümleden çıkmış
ve #100 ("Dur Whatsapp'tan ayırayım seni bekle") bir söz bile değil. İkincisi: bu üründe elle
sınıflandırma sunan beş yüzeyin beşi de bugüne kadar hiç kullanılmamış (`call_tag` 0,
`board_card` 0, `contact_field` 0, `is_pinned` 0, `todo` 0). İlki S paketinin sırasını, ikincisi
Ç paketinin risk cümlesini ve kapsama ölçüsünü belirledi.

Plan §4.6'nın "Sözlerim çipi" maddesi **iptal edildi**: tasarlandı, 7,5 ile turun en yüksek
puanını aldı, kullanıcı reddetti ("yok düşsün demiyorum"). Gerekçesiyle birlikte
PLAN-IKINCI-TUR §0.1'de duruyor ki yeniden önerilmesin.

## 2026-09-06 — Paket S: sözün tabanı (ikinci turun ilk paketi)

`d73669e` + `1813c0c`. Sözler sayfası değişmedi, üstüne beş yüzey geldi. Şema değişikliği yok,
model çağrısı yok.

**Sözün etrafı.** Alıntının altında, aynı görüşmenin öncesindeki ve sonrasındaki ikişer döküm
satırı; her satır sen/o damgalı ve tıklanınca o anı çalıyor. Varsayılan katlı, çünkü on üç kartın
her birine dört satır eklemek defteri yeniden döküme çevirirdi. Tek yeni sorgu (`SegmentsAround`),
`ix_segment_call` üzerinde iki LIMIT'li yürüyüş, satır başına 140 karakter kırpma. Yorum yok:
uygulama "bu söz değil" DEMİYOR, satırları gösteriyor, kararı kullanıcı veriyor.

**Tek cümle, iki söz.** Aynı `(call_id, by_me, quote_start_ms, katlanmış alıntı)` dörtlüsüne düşen
satırlar tek kart oluyor ve kart tek soru soruyor. Adaylar kartın içinde, kullanıcının seçimi
kartın altında ayrı rozette — zemin sınırı çerçeveyle çizili. Bugünkü arşivde tam bir grup var ve
o grup kullanıcının kendi sözlerinde: #99 ve #100.

**"Ne zamana?"** Tarihsiz, açık, koşulsuz her kartın altında beş düğme. İlk dördü yalnız
`user_deadline_date`'e yazıyor. Beşincisi — "tarihsiz kalsın" — yeni bir `verdict` türüne
(`vade`) yazılıyor. `user_deadline_date`'in NULL'u zaten "kullanıcı söylemedi" demek; aynı hücre
"kullanıcı yok dedi" anlamına da gelemez, yoksa şerit sormaya devam ederdi. `kind='soz'` de
olamazdı: `SaveVerdict` `(call, kind, quote, ms)` ile üstüne yazdığı için iki yargı birbirini
silerdi.

**"Bu söz değildi".** Kulak teyidi sözlere genişledi: Doğru · Yanlış duyulmuş · Bu söz değil.

**Dürüstlük satırı.** Her sütunun altında "Bu sütun N görüşmeden çıkarıldı." Fark eşiği geçince
ikinci cümle çıkıyor. Eşik `karşı >= benim * 2 + 3`: yalnız oran 0'a karşı 2'de de ateşlerdi,
yalnız fark 40'a karşı 43'te ateşlerdi. Bugünkü 3'e karşı 10 geçiyor, eşitlik sessiz.

**Denetimden gelen dört düzeltme de indi.** "Açık" çipi artık yalnız açık satırları sayıyor ve
toplam "Hepsi"ye taşındı; ✎ düzenlemesinin geri alması artık sunuluyor (sayfanın tek geri
alınamayan fiiliydi); `fulfilled_by_call_id` yazılıyor; sayfanın bütün fiilleri `LedgerActions`
üzerinden geçiyor.

**Bilerek düzeltilmeyen bir kusur, testle sabitlendi.** `SurvivingCommitmentKeys` hayatta kalan
satırı `(ByMe, katlanmış alıntı)` ile tanıyor, yükümlülüğüyle değil. Bu yüzden seçilmeyen adayın
mezar taşı, kullanıcının SEÇTİĞİ okumaya da uyuyor. Kusur S2'den önce de vardı ama S2 onu
nadirlikten olağan yola çeviriyor. Anahtarı yükümlülükle daraltmak, model bir sonraki koşumda
cümleyi yeniden yazdığında reddedilmiş bir satırı diriltirdi — ve reddi diriltmek daha kötü
başarısızlık, mekanizma zaten onun için var. Doğru çözüm eşleşmeyi anahtardan çıkarıp hatta
taşımak; bu paketin sınırının dışında. Ayrı iş olarak YAPILACAKLAR §26'ya yazıldı.

**Doğrulama.** 1357 C# testi (1352 geçti, 5 atlandı; taban 1341'di) ve 179 Python testi.

## 2026-09-06 — Eski sürümün yedeği: söz vardı, testi yoktu

`cf13753`. Kullanıcı eski VoiceTranscript yedeklerinin bu sisteme alınıp alınamayacağını sordu.
Alınabiliyor, ve iki ayrı mekanizmayla: `BackupService.ImportAsync` açtığı KOPYAYA göç
çalıştırıyor, `Repository.Copy` ise sütun listesini iki veritabanının kesişiminden kuruyor, yani
yalnız bir tarafın bildiği sütun ya da tablo içe aktarmayı düşürmek yerine geride kalıyor.

Ama içe aktarmanın on üç testinin hepsi gelen arşivi GÜNCEL şemayla kuruyordu; yani "eski yedek
de açılır" sözünü hiçbir şey tutmuyordu. Yeni test arşivi önce dolduruyor, sonra v14'e düşürüyor.

Testin gücü ölçüldü: `archive.Migrate()` satırı geçici olarak kapatıldığında test **yeşil
kalıyor**, çünkü ikinci mekanizma tek başına yetiyor. Bu, kemer ve askı olarak çalışıyor demek;
testin yorumu bunu açıkça söylüyor ki yeşili birinin tek başına çalıştığının kanıtı sayılmasın.

## 2026-09-06 — Sorulan soru ve alınan cevap artık saklanıyor (şema v20)

`3a9d017` + `1ea4d5a`. Kullanıcı görüşme detaylarındaki sonuçların saklanmadığını fark etti ve
haklıydı: Sor sekmesinde ve Sor sayfasında cevap yalnız bellekteki koleksiyona giriyordu.
Pencereyi kapatınca cevap da alıntıları da gidiyordu; aynı soruyu yarın sormak aynı faturayı
ikinci kez ödemek demekti. `ArchiveQuestions` önbelleğe de bakmıyor, yani aynı oturumda ikinci
kez sormak da para harcıyordu.

**Tek tablo, `ask_exchange`.** İki soru arasında bağ yok, çünkü `ArchiveQuestions` durumsuz:
modele önceki tur hiç gösterilmiyor. İplik tablosu var olmayan bir sohbeti modellerdi ve "şu tek
alışverişi kaldır" bir DELETE yerine bir çağlayana dönerdi.

**`call_id` boş bırakılabilir.** SQLite CASCADE'i yalnız NULL olmayan anahtarda tetikliyor, yani
bir görüşme kendi sorularını götürüyor, arşiv geneline sorulanlar ayakta kalıyor. `contact_id`
ise SET NULL: kişiyi kaybetmek, alıntıları hâlâ çözülen soruları silmemeli. Dönem süzgeci ad
olarak değil **çözülmüş an** olarak saklanıyor — martta sorulan "son 7 gün" eylülün haftası
değildir.

**Alıntılar JSON sütununda**, ev usulü. Bir cevabın denetlenebilmesi için gereken her alan
duruyor: numara, görüşme, kişi adı, görüşmenin başlangıcı, `start_ms`, konuşan, metin. Saklanmış
bir cevabın çıpaları hâlâ gösterdiği anı çalıyor. Okunamayan bir yük, çıpasız cevap sayılıyor,
istisna fırlatmıyor.

**Zemin: ≈ modelin görüşü.** Soru kullanıcının, alıntılar kanıt, ama satırın bütünü modelin o
günkü okuması. Ölü uç: hiçbir sorgu join atmıyor, hiçbir isteme satır gösterilmiyor. Kaldırma
fiili **Kaldır**, Reddet değil — kullanıcının kendi malzemesi.

**Bayatlık yalnız görüşmeye bağlı cevaplarda.** Tek bir görüşme, yeniden dökümün iki yanında
yazılmış cevaplar taşıyabilir, o yüzden yargı `DerivedFreshness` yerine alışveriş başına
veriliyor. Arşiv geneline sorulan bir cevapta bayatlık **iddia edilmiyor**: kırk görüşmeden
birinin yeniden dökülmesi neredeyse her cevaba uyarı koyardı ve karşılığında hiçbir şey
söylemezdi.

**`RecordRun` zaten vardı** — hem başarı hem `LlmException` yolunda. Kullanım ekranı bu aşamayı
okuyor.

**Yol boyunca bir yarış düzeltildi.** Yeni test sınıfı xunit'in sıralamasını kaydırınca
`OpusArchiveTests` tutarlı biçimde kırılmaya başladı. Sebep önceden vardı: iki sınıf da süreç
genelindeki `AudioMaterialiser.CacheDirectory`'yi kurucuda kurup `Dispose`'da null'lıyordu, yani
paralelde birinin yıkımı ötekinin değerini testin ortasında siliyordu. İkisi tek koleksiyona
alındı.

**Doğrulama.** 1373 C# testi (1368 geçti, 5 atlandı; taban 1358'di) ve 179 Python testi.

**Sıra notu:** şema **v20**'yi bu iş aldı. `PLAN-IKINCI-TUR`'da çevreler için yazılan v20
**v21**'e kaydırıldı; şema sürümleri tek sıradır ve sevk edilen adım düzenlenmez.
