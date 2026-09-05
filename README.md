# SocialZeka

VoiceTranscript'ten çatallandı (5 Eylül 2026). Kayıt ve döküm çekirdeği aynı; üstüne sosyal zekâ
koçu katmanı geliyor — plan ve gerekçe: [docs/PLAN-SOSYALZEKA.md](docs/PLAN-SOSYALZEKA.md).
Derlemeler, ad alanları ve `VoiceTranscript.exe` bilerek eski adı taşır; kullanıcıya görünen ad,
kurulum kimliği, sürüm adı ve veri klasörü (`%LOCALAPPDATA%\SocialZeka.Data`) yenidir.

WhatsApp ve Telegram sesli görüşmelerini otomatik kaydedip Türkçe yazıya döken, sonra yerel bir
yapay zekâ ile çözümleyen Windows uygulaması. **Hiçbir konuşma içeriği makineden çıkmaz.**

---

## Temel fikir

> Mikrofondan giden **biziz**, hoparlörden gelen **karşı taraf**.

Mikrofon ve uygulamanın hoparlöre gönderdiği ses **ayrı ayrı** kaydedilir. Böylece kimin ne
söylediği tahmin edilmez, **kesin bilinir** — konuşmacı ayrıştırma (diarization) modeline hiç
ihtiyaç yok. Bu hem 6 GB VRAM'de yer açar hem de iki kişi aynı anda konuşurken bile doğru çalışır;
diarization modellerinin en çok yanıldığı yer tam olarak orasıdır.

Grup aramalarında bu basitlik bozulur (karşı tarafın birden fazla kişisi tek akışta karışır), o
yüzden grup aramaları **sadece ses olarak** kaydedilir; yazıya dökülmez, çözümlenmez.

---

## Ne yapar

| | |
|---|---|
| **Kaydeder** | Arama başlayınca otomatik, ya da tek düğmeyle elle. İki ayrı akış, tek zaman çizelgesi üzerinde hizalı. Kayıt sırasında iki akışın seviyesi canlı görünür. |
| **Yazıya döker** | Görüşme bitince, ekran kartında ya da bulutta. Kelime bazlı zaman damgalarıyla. Birden fazla bulut servisi sırayla denenir. |
| **Çözümler** | Kim ne söz verdi, hangi rakam değişti, hangi soru cevapsız kaldı. |
| **Gösterir** | **Defter**: bütün kişilerde tutulmamış sözler, değişen rakamlar, cevapsız sorular tek ekranda. |
| **Dinletir** | İki akışın aynalı dalga formu, tıkla-dinle, çalan satır transkriptte vurgulanır. |
| **Buldurur** | Bütün arşivde tam metin arama; kişi, tarih ve konuşan süzgeçleriyle, eşleşme vurgulu. |
| **Durumunu söyler** | Yakalama, model, kuyruk, disk ve servisler tek sayfada — her birinin yanında onu düzelten düğmeyle. |
| **Dışa aktarır** | Obsidian markdown (yerel), yedekleme, her şeyi dışa aktarma. İsteğe bağlı Notion (bulut, varsayılan kapalı). |

Analiz katmanı **hüküm vermez, kanıt gösterir.** Her tespit birebir alıntı ve tıklanabilir zaman
damgası taşır; dinleyip kendin karar verirsin. Gerekçesi aşağıda.

### Ne yaptığını hemen görmek

İlk aramayı beklemeden: **Genel bakış → Örnek görüşmeleri yükle.** Altı haftaya yayılmış üç
görüşme gelir — fiyat iki kez değişir, bir söz vadesini geçirir, aynı soru iki kez cevapsız
kalır. Gerçek verinin yazıldığı tablolara yazılır ve tek tıkla kalkar.

Ayrıntılı ürün gerekçesi: [PRODUCT.md](PRODUCT.md).

---

## Durum

**Tamamlandı.** Yedi fazın tamamı yazıldı; ikisi hedef makinede ölçüm bekliyor.

| Faz | İçerik | Durum |
|---|---|---|
| 0 | Çözüm iskeleti, zaman çizelgesi hizalama, Türkçe metin normalizasyonu | ✅ |
| 1 | Python worker (çoklu motor), C# worker host, uçtan uca transkripsiyon | ✅ |
| 2 | Arama tespiti: WASAPI oturum durumu, durum makinesi, histerezis | ✅ |
| 3 | Ses yakalama: cihaz loopback, process loopback, yankı engelleme | ✅ kod · ⏳ hedefte ölçüm |
| 4 | Arayüz: kişi listesi, transkript, tıkla-dinle, defter, ayarlar | ✅ |
| 5 | Çözümleme: alıntı doğrulama, taahhüt/çelişki defteri, dolandırıcılık kalıpları | ✅ |
| 6 | Obsidian dışa aktarımı, Türkçe tam metin arama | ✅ |
| 7 | Hedef makineye kurulum, GPU ölçümleri, Türkçe doğruluk değerlendirmesi | ⏳ hedefte |

**Testler:** 250 C# + 27 Python, hepsi geçiyor. Üçü gerçek Python process'i başlatan entegrasyon testi.

---

## Kurulum

### Kaydı yapacak makinede (RTX 4050 notebook)

`dist\VoiceTranscript-Setup-1.0.0.exe` dosyasını çalıştır. Yönetici yetkisi istemez, kendini
`%LOCALAPPDATA%\Programs\VoiceTranscript` altına kurar.

Uygulama ilk açılışta **kurulum sihirbazını** gösterir: Python'un olup olmadığını denetler,
yoksa kurar, sabitlenmiş Whisper paketlerini yükler, ekran kartının gerçekten erişilebilir
olduğunu **varsaymak yerine doğrular**, ses yakalamayı sınar ve model dosyalarını indirir. Her
adımın tek bir düğmesi vardır ve neden gerektiği bir cümleyle yazılıdır.

Hiçbirini kurmak istemiyorsan: **Ayarlar → Yazıya dökme** bölümünden bulut seçeneğini seçmen
yeterli. O zaman Python da model de gerekmez, karşılığında görüşmenin sesi seçtiğin servise
yüklenir.

Sihirbaz yerine komut satırını tercih edersen aynı işi `setup.ps1` yapar:

```powershell
powershell -ExecutionPolicy Bypass -File setup.ps1
```

### Geliştirme makinesinde (GPU ve ses kartı olmadan)

```powershell
powershell -ExecutionPolicy Bypass -File test.ps1
```

C# ve Python takımlarını birlikte çalıştırır. `dotnet test` bilerek kullanılmıyor: .NET 10 SDK'sı
xUnit v3 modülünü Microsoft.Testing.Platform üzerinden çağırdığında `net10.0-windows` hedefi için
"Zero tests ran" diyor, oysa aynı modül doğrudan çalıştırıldığında bütün testleri buluyor.

```powershell
python -m vt_worker probe                           # bu makinede ne çalışıyor?
```

Sentetik iki akışlı test görüşmesi üretmek için (gerçek kaydın üreteceğinin aynısı, artı kimin ne
zaman ne dediğini bilen referans dosyası):

```powershell
powershell -File tools/synth_utterances.ps1 -ScriptPath tools/testcall.json -OutDir .work/utt
python tools/make_test_call.py --utterances .work/utt --script tools/testcall.json --out .work/call
```

Transkripsiyon entegrasyon testi Whisper ağırlıkları indirdiği için varsayılan olarak atlanır:

```powershell
$env:VT_RUN_ASR_TESTS = "1"; powershell -ExecutionPolicy Bypass -File test.ps1
```

### Dağıtım paketi üretmek

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

`dist\VoiceTranscript` klasörünü (kendi kendine yeten, .NET gerektirmeyen bir yayın) ve Inno
Setup kuruluysa `dist\VoiceTranscript-Setup-1.0.0.exe` kurulum paketini üretir. Derleyici yoksa
komutu söyler: `winget install JRSoftware.InnoSetup`.

Pakete Python, model ağırlıkları ve pip bağımlılıkları **bilerek konmuyor**. Bunları sihirbaz ilk
açılışta indiriyor; paketlemek 65 MB'lık kurulumu gigabaytlara çıkarır ve sihirbazın güncel
tutabildiği sürümleri dondururdu.

---

## Yapı

```
src/VoiceTranscript.Core/      net10.0             saf mantık, tamamı test edilebilir
  Audio/TimelineWriter.cs        iki akışı hizalama (aşağıya bakınız)
  Audio/CallRecorder.cs          iki WAV, tek ortak zaman çapası
  Audio/FileAudioSource.cs       ses donanımı olmadan geliştirme
  Detection/CallDetector.cs      arama durum makinesi (saf, histerezisli)
  Text/TurkishText.cs            Türkçe arama normalizasyonu
  Analysis/QuoteVerifier.cs      uydurma alıntıyı engelleyen koruma
  Analysis/DeterministicChecks.cs defterin asıl değeri — LLM yok, aritmetik var
  Analysis/AnalysisPipeline.cs   çıkarım → doğrulama → karşılaştırma → defter
  Storage/                       SQLite şeması + depo, FTS5 Türkçe arama
  Export/ObsidianExporter.cs     markdown çıktı
  Asr/ + Llm/                    seçilebilir motorlar, ölçülmüş rakamlarıyla

src/VoiceTranscript.Capture/   net10.0-windows     WASAPI
  WasapiCaptureBackend.cs        cihaz loopback + mikrofon + yankı engelleme
  ProcessLoopbackCaptureBackend.cs uygulama bazlı yakalama (opsiyonel)
  AudioSessionWatcher.cs         arama tespiti için oturum örnekleme
  TargetProcesses.cs             WhatsApp/Telegram process ağacı çözümleme

src/VoiceTranscript.Worker/    net10.0-windows     Python süpervizyonu
  JobObject.cs                   uygulama zorla kapansa bile worker'ı öldürür
  PythonWorkerHost.cs            başlat, ilerlemeyi oku, hataları sınıflandır

src/VoiceTranscript.App/       WPF (.NET 10)       arayüz
worker/                        Python              Whisper + CUDA
tools/                         geliştirme yardımcıları
tests/                         C# testleri
```

---

## Neden böyle yapıldı

Bu bölüm, kod okunurken "burası neden bu kadar karmaşık" diye sorulacak yerlerin cevabı.

### Zaman çizelgesi hizalama — projenin en sessiz tuzağı

WASAPI loopback, **hiçbir şey çalmıyorken hiç paket üretmez**. Bu Microsoft dokümantasyonunda
yazmıyor, NAudio dokümantasyonunda yazıyor. Paketleri arka arkaya eklerseniz, karşı tarafın
zamanın yarısında konuştuğu bir saatlik görüşmede loopback dosyası **yarım saat kısa** çıkar ve
ilk sessizlikten sonraki her konuşmacı atfı yanlış olur.

`TimelineWriter` her paketi QPC damgasına göre yerleştirir ve boşlukları sessizlikle doldurur.
Ayrıca **iki akış tek bir ortak başlangıca çapalanır**: her akışı kendi ilk paketine çapalamak,
geç konuşmaya başlayan tarafın tüm zaman damgalarını kaydırıyordu. İkisi de testle sabitlendi.

### Whisper'ın segment sınırları güvenilmez, kelime damgaları güvenilir

Gerçek bir çalıştırmada, aralarında 6.4 saniye sessizlik olan iki ayrı cümle tek bir 13 saniyelik
segmentte birleşti; içindeki fiyat, söylendiği andan **11 saniye önceye** damgalandı. Bu,
"alıntıya tıkla ve o anı dinle" özelliğini — yani analiz katmanının tüm doğrulanabilirliğini —
bozar.

`segmentation.py` segmentleri kelime damgalarındaki gerçek duraklamalardan yeniden böler, sonra
aynı konuşmacının bitişik parçalarını birleştirir. Referans görüşmede sonuç: **9 segment = 9
gerçek konuşma sırası**, zaman damgası hatası ortalama **0.07 saniye**.

### Alıntı doğrulama zorunlu

Modelin ürettiği her alıntının metinde gerçekten geçtiği kontrol edilir; geçmiyorsa kayıt
**reddedilir**. Ürün her şeyi "şu kişi şunu dedi, şu anda, tıkla dinle" diye sunuyor. Alıntı
uyduran bir model bunu gerçek bir insan hakkında sahte kanıta çevirir — birinin arkadaşı,
tedarikçisi, ailesi hakkında. İnsanlar hakkında alıntı uyduran bir sistem, hiç sistem
olmamasından kötüdür.

Karşılaştırma sunumda hoşgörülü, içerikte katıdır: Türkçe yazım farkları ve noktalama önemsenmez
(model "yapacağım" yerine "yapacagim" yazmış olabilir), farklı kelimeler reddedilir.

### Neden "yalan tespiti" yok

Bir LLM konuşma metninden yalan tespit edemez. *Beyond Liars' Bench* (arXiv:2607.20479)
çalışmasında, konuşanın iç aktivasyonlarına erişebilen beyaz-kutu problar bile koşullar arasında
ortalama 0.45–0.51 AUROC (şans = 0.50) alıyor; yalan tipi değişince **0.12–0.14'e**, yani
rastgeleden kötüye düşüyor. Bir insan konuşmacıda o erişim hiç yok.

Baz oran matematiği daha da net: cömert bir %82 duyarlılık/seçicilik ve gerçekçi %5 yaygınlıkla,
"bu kişi yalan söylüyor" bayraklarının **%81'i yanlış** çıkar — kullanıcının kendi ailesi ve iş
arkadaşları hakkında.

Onun yerine **sayılabilir olan** hesaplanır: ne söz verildi, hangi tarih kaç kez kaydı, hangi
fiyat kaç kere değişti, hangi doğrudan soru cevapsız kaldı. Hepsi aritmetik, hepsi açıklanabilir,
hiçbiri halüsinasyon üretemez. Model sadece bulur ve alıntılar; hüküm kodda kalır.

Türkçe'ye özgü iki tuzak ayrıca ele alındı: `"bakarız"`, `"inşallah"`, `"bir ara"` gibi ifadeler
kibar ret sayılır, taahhüt olarak kaydedilmez. Ve ses net değilse o segmentteki rakamlar otomatik
çelişki tespitinden çıkarılır — yanlış duyulmuş bir tutar, gerçek bir insan hakkında sahte fiyat
çelişkisi üretirdi.

### CUDA kurulumu: cuDNN artık gerekmiyor

CTranslate2 4.6.3 (Ocak 2026) cuDNN bağımlılığını kaldırdı. Gereken tek NVIDIA DLL'i
`cublas64_12.dll`. **Hem faster-whisper README'si hem CTranslate2 dokümanı hâlâ cuDNN 9 kurmanızı
söylüyor — ikisi de bayat.** İnternetteki yaygın "ctranslate2==4.4.0'a düş" tavsiyesi artık ters
yönde çalışır: o sürüm sert bir cuDNN 8 bağımlılığını geri getirir.

Bu, `setup.ps1` çalıştırılarak doğrulandı: kurulan paketler arasında **hiç cuDNN yok**.

Windows'ta ayrıca `pip install nvidia-cublas-cu12` tek başına yetmez; Python 3.8'den beri Windows
loader'ı C uzantılarının bağımlılıklarını `PATH`'te aramıyor. `sitecustomize.py`
`os.add_dll_directory()` ile bunu çözer. README'lerdeki `LD_LIBRARY_PATH` çözümü Linux'a özeldir
ve Windows'ta **sessizce hiçbir şey yapmaz** — bu yüzden hata genelde "CUDA bozuk" diye yanlış
teşhis edilir.

### Türkçe arama sessizce bozulur

SQLite FTS5'in `unicode61` tokenizer'ı standart Unicode küçültmesi yapar; Türkçe'nin noktalı ve
noktasız i'si için bu yanlıştır. `ışık` araması `IŞIK`'ı bulmaz — ve **hata vermez**, sadece boş
sonuç döner, siz de veri yok sanırsınız. Hem indeks hem sorgu aynı normalizasyondan geçirilir.
Türkçe sondan eklemeli olduğu için önek sorguları kullanılır: `kitap` araması `kitabı`,
`kitaptan` ve `kitabımı` sonuçlarına da ulaşır.

### Neden cihaz loopback varsayılan

Process bazlı yakalama sadece WhatsApp'ın sesini alır, arkada çalan müzik kayda girmez — kâğıt
üzerinde daha temiz. Ama hedef makinenin tam build'inde (26200) o sanal cihaz format görüşmesini
reddediyor (`E_NOTIMPL`), cihaz konumunu her zaman 0 bildiriyor ve QPC değerleri saat değil paket
sayacı. Yani **hizalama doğrulanamıyor**. Ayrıca bazı VoIP istemcilerinde doğru uzunlukta ama
tamamen sıfırlarla dolu tampon döndürdüğü raporlanmış; sessizlik başarıdan ayırt edilemez, bir
kaydedici için mümkün olan en kötü hata biçimi.

Cihaz loopback'te bu sorunların hiçbiri yok. Bedeli — o an çalan her şeyi alması — kayıt yalnızca
arama tespit edildiğinde çalıştığı için teoride kalıyor. Process loopback ayarlardan açılabilir
ve açılmadan önce gerçek sesle sınanır.

### Worker process başına tek iş

VRAM'i sürücüye tam olarak geri veren tek kesin mekanizma process çıkışıdır. `del model` +
`gc.collect()` + `torch.cuda.empty_cache()` CTranslate2'de işe yaramaz — CTranslate2 torch
kullanmaz ve kendi caching allocator'ı vardır. Aynı karar, uyku sonrası CUDA context'inin
geçersizleşmesi sorununu da kökten çözer.

`JobObject` ise uygulama Görev Yöneticisi'nden zorla kapatılsa bile Python process'inin yetim
kalmamasını garanti eder — aksi halde kullanıcının VRAM'ini yiyen, sebebi görünmeyen bir hayalet
process kalır.

### Hangi model, neden

Seçim ekranı **ölçülmüş Türkçe hata oranlarını** gösterir, çünkü isimlere bakarak seçim yapmak
yanıltıcı: en popüler Türkçe Whisper uyarlaması (`selimc/whisper-large-v3-turbo-turkish`),
dayandığı düz modelin neredeyse **iki katı** hata yapıyor (20.71 vs 12.17 WER).

Varsayılan `large-v3-turbo`: Türkçe'de `large-v3`'e göre bedeli sadece +0.24 WER, karşılığında
~3× hız ve %44 daha az VRAM. `medium`'a düşmek %37 bağıl hata artışı demek — üstelik turbo daha
az VRAM kullanıyor, yani VRAM için oraya düşmenin anlamı yok.

LLM tarafında Türkçe'ye özel eğitilmiş modeller kullanılmıyor: üç bağımsız 2026 kaynağı
(Cetvel/EACL, TurkBench, TUDUM) bunların genel amaçlı çok dilli modellerin gerisinde kaldığını
gösteriyor. Varsayılan `Qwen3.5-4B Q6_K` — 6 GB'a 32 bin token bağlamla sığıyor ve Türkçe'de
kelime başına en az token harcayan tokenizer'a sahip (LLaMA ailesinin ~%55 altında).

---

## İki makine

Bu depo **NVIDIA GPU'su ve ses donanımı olmayan** bir makinede geliştirildi; hedef makine 6 GB
VRAM'li bir RTX 4050 notebook. Bu yüzden ses kaynağı bir arayüzün arkasında, testler sentetik WAV
dosyalarıyla çalışıyor ve GPU'ya bağımlı her şeyin CPU karşılığı var.

**Hedef makinede yapılması zorunlu iki ölçüm** — ikisi de dokümantasyondan öğrenilemez:

1. **En az bir saatlik bir görüşme kaydet ve iki WAV dosyasının aynı uzunlukta çıktığını doğrula.**
   Ayrışıyorlarsa ilk sessizlikten sonraki konuşmacı atfı güvenilir değildir.

2. **Beş gerçek Türkçe aramayı elle düzelt ve hata oranını hesapla.** %15 altı kullanılabilir;
   belirgin şekilde üstü, başka model denemek gerektiği anlamına gelir.

Ayrıca bir gerçek WhatsApp ve bir gerçek Telegram araması sırasında pencere listesi ve
erişilebilirlik ağacı dökülmeli — WhatsApp'ın arama penceresine dair açık kalan tek soru bu tek
deneyle kapanır.

---

## Bilinen açık riskler

| Risk | Azaltma |
|---|---|
| Qwen3.5-4B'nin Türkçe kalitesi ölçülmemiş (karşılaştırmalar modelden eski) | Hedefte TR-MMLU + elle değerlendirme; yedek `Trendyol-LLM-8B-T1` |
| 60 dk boyunca iki akışın senkronu | QPC çapalı tampon + zorunlu bip testi |
| WhatsApp arama penceresi yapısı bilinmiyor | Yedek zaten var: görüşme sonrası tek dokunuşluk etiketleme, sonra hatırlanır |
| Kulaklık kullanılmazsa yankı | Windows yankı engelleme + tespit edilince uyarı |
| `faster-whisper` 9 aydır commit almamış | Sürümler sabit; whisper.cpp motoru alternatif olarak hazır |
| Process loopback build 26200'de kusurlu | Varsayılan değil; cihaz loopback ile çıkılıyor |

---

## Belgeler

| Belge | Ne için |
|---|---|
| [docs/MIMARI.md](docs/MIMARI.md) | Neyin nerede olduğu ve **neden orada olduğu**. Her kararın yanında neyi engellediği yazılı. |
| [docs/GELISTIRME.md](docs/GELISTIRME.md) | Derleme, test, iki makineli kısıt, hata bildirme. |
| [docs/ISLEM-GUNLUGU.md](docs/ISLEM-GUNLUGU.md) | Ne yapıldı, neden yapıldı, nasıl doğrulandı — tarih sırasıyla. |
| [PRODUCT.md](PRODUCT.md) | Ürün gerekçesi: ne olduğu ve ne olmadığı. |

Bir şeyi değiştirmeden önce `docs/MIMARI.md`'nin sonundaki **"Değiştirmeden önce bakılacak
yerler"** tablosuna bak. Buradaki kararların çoğu, *sessizce* bozulan şeylere karşı alınmış.

---

## Bir şey ters gittiğinde

Uygulama ne yaptığını diske yazar:

```
%LocalAppData%\VoiceTranscript.Data\logst-YYYY-MM-DD.log
```

**Durum → Günlük → Günlüğü kopyala** son üç günü panoya alır. Günlükte **konuşma metni, kişi adı
ve API anahtarı yoktur** — paylaşılmak üzere yazılır.

---

## Sorumluluk

Bu bir görüşme kaydedicisidir. Karşı tarafın sesini kaydetmenin hukuki durumu ülkeye ve duruma
göre değişir; bazı yerlerde her iki tarafın da rızası gerekir. Neyin kaydedildiğine ve kiminle
paylaşıldığına karar vermek uygulamayı çalıştıranın sorumluluğudur.

Uygulama bu konuda tarafını seçmiştir: kayıt sürerken ekranın üstünde bir şerit durur, otomatik
kayıt tek tıkla kapatılabilir, ve içerik makineden çıkmaz.

---

## Lisans

[MIT](LICENSE).
