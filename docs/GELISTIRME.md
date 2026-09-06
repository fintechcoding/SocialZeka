# Geliştirme

## İki makine — mimariyi belirleyen kısıt

Bu proje **iki ayrı makinede** yaşıyor ve bunu bilmeden yapılan her tahmin yanlış çıkıyor.

| | Geliştirme makinesi | Hedef makine |
|---|---|---|
| Kimlik | `DESKTOP-4LOD265` — **Hyper-V sanal makinesi** | Kullanıcının günlük makinesi |
| GPU | `Microsoft Hyper-V Video` — **NVIDIA yok, `nvidia-smi` yok** | RTX 4050 Laptop, 6 GB |
| Ses donanımı | **Yok** — `Win32_SoundDevice` gerçekten boş | Gerçek mikrofon + hoparlör |
| CPU / RAM | Ryzen 7 PRO 8700GE (konak), 7,8 GB | — |
| WhatsApp / Telegram | Çalışmıyor, **dokunulmuyor** | Oturum açık, gerçek aramalar |
| Uygulama verisi | Yok — uygulama burada hiç çalışmadı | Gerçek görüşme arşivi |

*(Yukarıdakiler 2026-08-31'de bu makinede ölçüldü, tahmin değil.)*

Sanal makine olması ayrıntı değil, kısıtın kendisi: **ses donanımı yok**, yani WASAPI yakalama
burada hiçbir koşulda denenemez. Ekran kartı da sanal, dolayısıyla CUDA yolu da öyle.

**Sonuçları:**

1. Ses kaynağı `IAudioCaptureBackend` arkasında; geliştirmede WAV besleyen `FileAudioSource`.
   Bu yüzden bütün kayıt zinciri ses kartı olmadan uçtan uca test edilebiliyor.
2. GPU'ya bağımlı her şeyin işlemci karşılığı var.
3. `Core` katmanı Windows'a bağımlı değil. Bir şeyi `Core`'dan çıkarmak, onu test edilemez
   yapmaktır.

> **Kural:** geliştirme makinesindeki WhatsApp ve Telegram'a dokunulmaz — ne süreçlerine, ne
> pencerelerine, ne verilerine. Telegram'ın şifreli `tdata` klasörü ve WhatsApp'ın yerel deposu
> ürünün kendisi tarafından da **hiçbir zaman** okunmaz.

---

## Kurulum

### Doğrulanmış ortam (2026-08-31, `DESKTOP-4LOD265`)

| Araç | Sürüm | Nerede | winget kimliği |
|---|---|---|---|
| .NET SDK | 10.0.400 | `C:\Program Files\dotnet\dotnet.exe` | `Microsoft.DotNet.SDK.10` |
| Python | 3.12.10 | `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` | `Python.Python.3.12` |
| pytest | 9.1.1 | aynı yorumlayıcı | — |
| Inno Setup | 6.7.3 | `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` | `JRSoftware.InnoSetup` |
| GitHub CLI | 2.96.0 | PATH'te | `GitHub.cli` |

> ⚠️ **`dotnet` PATH'te değil.** winget ile kurulduğunda mevcut kabuk oturumu yeni PATH'i
> görmüyor. Tam yolla çağır ya da yeni bir terminal aç. Bu, "SDK kurulu değil" gibi görünen ama
> olmayan bir sorundur — beş dakika kaybettirdi, bir daha kaybettirmesin.

> ⚠️ **`python` komutu Microsoft Store kısayoluna düşer.** Kurulum yapılmadan `python` yazılırsa
> *"Python was not found; run without arguments to install from the Microsoft Store"* çıkar ve
> çıkış kodu 9009 olur. Bu sahte bir yorumlayıcıdır. Tam yolu kullan.

```bash
dotnet restore
```

Python worker'ı için (isteğe bağlı, transkripsiyon denemek istersen):

```bash
py -3.12 -m venv worker/.venv
worker/.venv/Scripts/python -m pip install -r worker/requirements.txt
```

> **Yalnızca `worker/tests` koşacaksan bu venv gerekmez.** O testler standart kütüphane, `numpy`
> (`vt_worker.speaker` filtre bankasını onunla hesaplar) ve `pytest` kullanıyor;
> `requirements.txt`'teki ağır bağımlılıklar (ctranslate2, faster-whisper, onnxruntime,
> nvidia-cublas) **gerekmiyor**. `pip install pytest numpy` yeter.

---

## Geliştirme verisini gerçek arşivden ayırmak

Uygulama, kullanıldığı makinede geliştirildiğinde deneysel bir derleme **gerçek görüşme
arşivinin üstünde** çalışır. Bunu önlemek dikkatle değil, anahtarla olur:

```bash
VoiceTranscript.exe --data C:\vt-dev
```

- Öncelik: `--data` > `AppSettings.DataRoot` ayarı > varsayılan
  (`%LOCALAPPDATA%\VoiceTranscript.Data`).
- Veri klasörü **günlük açılmadan önce** çözülür; günlüğün ilk satırı hangi klasörün
  kullanıldığını yazar ve varsayılan değilse işaretler.
- `--data` verilip arkasına klasör yazılmazsa uygulama **açılmaz**. Sessizce varsayılana düşmek,
  anahtarın var olma sebebini ortadan kaldırırdı.
- Bulut klasörü denetimi (`DetectCloudSync`) verilen klasöre de uygulanır.

Karar mantığı `AppPaths.ResolveRoot` içinde ve saftır — Win32'ye de WPF'e de bağlı değil, bu
yüzden tamamen test edilebilir (`tests/VoiceTranscript.Tests/DataDirectoryTests.cs`).

---

## Derleme ve test

```bash
./test.ps1
```

Bu betik hem C# hem Python testlerini çalıştırır. **PowerShell'den çalıştır** — Bash'ten değil.

> **Neden `dotnet test` değil:** `net10.0-windows` hedefinde SDK ile xUnit v3 /
> Microsoft.Testing.Platform arasındaki uyumsuzluk yüzünden `dotnet test` **"Zero tests ran"**
> der ve sıfır çıkış kodu döndürür. Yani testler çalışmamış olur ve başarılı görünür.
> `test.ps1` test modülünü doğrudan çalıştırarak bunu atlar.
>
> **Bu 2026-08-31'de yeniden denendi; çözülmedi.** Denenen iki resmî yol da işe yaramadı:
>
> | Denenen | Sonuç |
> |---|---|
> | Hiçbiri | `error : Testing with VSTest target is no longer supported ... on .NET 10 SDK` |
> | Kökte `dotnet.config` → `[dotnet.test.runner] name = "Microsoft.Testing.Platform"` | Yok sayıldı, aynı VSTest hatası |
> | Kökte `global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }` | VSTest hatası gitti, yerine **"Zero tests ran"** geldi |
>
> Test projesi zaten `UseMicrosoftTestingPlatformRunner` ve `TestingPlatformDotnetTestSupport`
> ayarlarını taşıyor; eksik olan SDK tarafı ve bu sürümde bir çözümü yok. Her iki dosya da geri
> alındı — depoda yoklar ve **tekrar eklenmemeli.**
>
> ⚠️ `VoiceTranscript.Tests.csproj` içindeki yorum "global.json bu işi tamamlar" diyor. **Yanlış.**
> Öyle bir dosya depoda yok, olsa da çalışmıyor. Yorum düzeltilecek (`YAPILACAKLAR.md` §5.3).

### Bilinen taban çizgisi

2026-09-06, hedef makinede (`C:\Voice\SocialZeka`, SocialZeka programı tamamlandıktan sonra),
temiz bir depo üzerinde:

| Takım | Sonuç |
|---|---|
| `dotnet build VoiceTranscript.slnx -c Debug` | **0 hata**, 86 uyarı, ~8 sn (artımlı) |
| `VoiceTranscript.Tests.exe` | **1338 test · 1333 geçti · 0 kırık · 5 atlandı**, ~9 sn |
| `pytest` (`worker/`) | **179 test · 179 geçti**, ~24 sn |

Önceki kayıtlar: 2026-09-05 (çatalın ilk derlemesi) 1080/1075/5 ve 156/156; 2026-09-02
846/841/5 ve 89/89. Aradaki fark A1'den I'ya kadarki paketlerin testleridir; sayı düşerse
sebep senin değişikliğindir.

Atlanan 5 testin dördü `OpenRouterLiveTests` içinde (`VT_OPENROUTER_KEY` tanımlı değil), biri
`PythonWorkerHostTests` içinde (Whisper ağırlıkları indirilmeden çalışmaz). İkisi de beklenen
davranış, kusur değil.

Uyarıların hepsi zararsız: NAudio'nun kullanımdan kaldırılmış `MMDevice.AudioClient` özelliği ve
xUnit'in `TestContext.Current.CancellationToken` önerisi.

**Bir değişiklikten sonra bu sayılar düşerse sebep senin değişikliğindir.** Taban çizgisi bunun
için yazılı.

Tek bir sınıfı çalıştırmak:

```bash
tests/VoiceTranscript.Tests/bin/Debug/net10.0-windows10.0.19041.0/VoiceTranscript.Tests.exe --filter-class VoiceTranscript.Tests.ConversationMixTests
```

> `--filter-class "A|B|C"` **desteklenmiyor** — sessizce sıfır test çalıştırır ve başarılı der.
> Birden fazla sınıf için bayrağı tekrarla.

Yalnızca Python:

```bash
worker/.venv/Scripts/python -m pytest worker/tests -q
```

Yayınlama:

```bash
./publish.ps1
```

---

## Test yazma kuralları

**Bir testin var oluş sebebi yorumda yazılı olmalı.** Bu projedeki testlerin çoğu, gerçekten
yaşanmış ve *sessiz* bir kusurun kaydıdır — "ne kırıldığında bu test kırmızı olur" sorusunun
cevabı yazılmazsa, o test bir sonraki kişi tarafından gereksiz sanılıp silinir.

**Sahte veriyle geçen testten kaçın.** Örnek: kişi silme testi, var olmayan dosya yollarına
bakıyordu ve silme kodu ne yaparsa yapsın geçiyordu. Şimdi gerçek dosya yazıp gerçekten silinmiş
mi diye bakıyor.

**Testler paralel çalışır** (sınıf düzeyinde). Süreç genelinde durum değiştiren hiçbir şey
kullanma — özellikle `SqliteConnection.ClearAllPools()`. Kapsamlı `Database.ClearPool()` var.

**Ağır varsayılanları testte kıs.** `GpuCooldownSeconds = 60` gerçek bir gerekçeyle var (güç
bütçesi), ama testte her vaka için bir dakika bekleme demek. Test ayarlarında `0`.

---

## Bu makinede doğrulanamayanlar

Aşağıdakiler **yalnızca hedef makinede** sınanabilir. Bir şey "çalışıyor" denmeden önce orada
görülmüş olmalı:

| Konu | Nasıl sınanır |
|---|---|
| Gerçek ses yakalama | Kurulum → Ses yakalama; iki akışta da seviye görünmeli |
| CUDA / cuBLAS | Kurulum → Ekran kartı satırı yeşil **ve kart adı yazıyor** olmalı |
| 60 dakikalık senkron | İki yola 60 sn'de bir 1 kHz bip; QPC çapalı fark düz olmalı |
| WhatsApp arama penceresi başlığı | Gerçek arama sırasında görüşme isimsiz mi geliyor |
| Türkçe doğruluk (WER) | 5 gerçek arama elle düzeltilip karşılaştırılır; hedef %15 altı |

### Hedef makineden hata bildirmek

Uygulama günlüğü diske yazıyor:

```
%LocalAppData%\VoiceTranscript.Data\logs\vt-YYYY-MM-DD.log
```

**Durum → Günlük → Günlüğü kopyala** son üç günü panoya alır. Günlükte konuşma metni, kişi adı ve
API anahtarı **yoktur**; paylaşılmak üzere yazılır.

Ekran görüntüsü tek başına yetmez: bir eksik kütüphane bir gün boyunca "model inmiyor" gibi
göründü, çünkü ekranda yalnızca sonucu vardı, sebebi değil.

---

## Kod üslubu

- **Yorum, kodun ne yaptığını değil neden öyle olduğunu anlatır.** Özellikle bir tuzağı önlüyorsa,
  o tuzağın ne olduğunu yaz. Bu projedeki en pahalı hatalar sessiz olanlardı.
- Kullanıcıya görünen bütün metin **Türkçe**.
- Sınıf yorumları neyi güvence altına aldıklarını söyler; "load-bearing" olan satırlar açıkça
  öyle işaretlenir.
- XML yorumlarında `--` kullanma — XAML ayrıştırmasını bozar.

---

## Yapı

```
src/VoiceTranscript.Core       alan modeli, SQLite, çözümleme, ses dosyası işleri (Windows'suz)
src/VoiceTranscript.Capture    WASAPI, cihaz kataloğu, pencere gözlemi
src/VoiceTranscript.Worker     Python alt sürecinin C# tarafı (Job Object, IPC)
src/VoiceTranscript.App        WPF, tepsi, bütün ekranlar, orkestrasyon
worker/vt_worker               Python: faster-whisper, CUDA, parçalama, birleştirme
tests/VoiceTranscript.Tests    C# testleri
worker/tests                   Python testleri
docs/                          bu belgeler
installer/                     Inno Setup betiği
```
