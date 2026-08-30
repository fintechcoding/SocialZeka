# Geliştirme

## İki makine — mimariyi belirleyen kısıt

Bu proje **iki ayrı makinede** yaşıyor ve bunu bilmeden yapılan her tahmin yanlış çıkıyor.

| | Geliştirme makinesi | Hedef makine |
|---|---|---|
| GPU | Radeon 780M (iGPU) — **NVIDIA yok** | RTX 4050 Laptop, 6 GB |
| Ses donanımı | **Yok** (`Win32_SoundDevice` boş) | Gerçek mikrofon + hoparlör |
| WhatsApp / Telegram | Kurulu ama **dokunulmuyor** | Oturum açık, gerçek aramalar |

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

```bash
dotnet restore
```

Python worker'ı için (isteğe bağlı, transkripsiyon denemek istersen):

```bash
py -3.12 -m venv worker/.venv
worker/.venv/Scripts/python -m pip install -r worker/requirements.txt
```

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
