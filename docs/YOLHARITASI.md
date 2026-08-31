# Yol haritası — kalan her şeyin analizi ve sırası

*1 Eylül 2026. v0.9.24 yayında; bu belge ondan sonrasının tamamıdır ve önceki dağınık
listelerin (YAPILACAKLAR açık maddeleri, UI-PLAN kalanları) yerine geçer. Buradaki sıra
keyfî değil: her faz bir sonrakinin önkoşullarını üretir.*

## Bugünkü durumun özeti

Çalışan: kayıt (iki ayrı akış), yerel+bulut yazıya dökme (kendini iyileştiren model seçimi),
çözümleme (alıntı+zaman damgalı defter), etiketler, kişi profilleri, çalışma alanı, arama
(FTS5, Türkçe katlama), Sor, kullanım/jeton dökümü, güncelleme (SHA doğrulamalı), otomatik
başlama, saklama süpürmesi. 691 test, 0 hata. Bilinen açık hata: **yok** — bilinenlerin tümü
kapalı ya da aşağıda tasarımıyla duruyor.

---

## Faz 1 — Cila (küçük, bağımsız işler; herhangi bir sırada)

### 1.1 Sessizlik kırpma (eski §11.7'nin cevabı)
**Sorun:** 46 dk görüşme = 171 MB. **Karar:** kayıt biçimi DEĞİŞMEZ (WAV'ı 5 alt sistem okuyor,
bu VM'de ses donanımı yok, test edilemez). Bunun yerine: işleme bittikten sonra her akıştaki
uzun sessizlikler (>2 sn, eşik altı RMS) kısaltılır — iki akış tasarım gereği >%50 sessiz,
kayıpsız ~%50 kazanım. Zaman damgaları bozulmasın diye kırpma HARİTASI `call` yanında saklanır
ve oynatıcı/kesit dışa aktarma haritayı uygular. **Efor:** 2-3 gün; testler sentetik WAV'la
bu VM'de koşabilir. **Kabul:** eski kayıt açılır, alıntı zamanları şaşmaz, disk ölçülür küçülür.

### 1.2 Ayarlar tam düzen turu (12.9 kalanı) ✅ 1 Eylül: kaydırma sıfırlama + oturum içi pencere boyu yapıldı; bölüm sırası zaten doğruydu
Bölüm sırası kullanım sıklığına göre; Çözümleme bölümünde sağlayıcı-anahtar-model tek akış;
bölüm değiştirince kaydırma sıfırlanması düzeltilir; pencere boyu hatırlanır. **Efor:** yarım gün.

### 1.3 Panel ekleme çizgisi + liste fotoğrafları ✅ 1 Eylül yapıldı
Sürüklerken ekleme göstergesi; Kişiler sayfası ve panel kartlarında profil fotoğrafı
(DecodePixelWidth=56, donma ölçülürse vazgeç). **Efor:** yarım gün.

### 1.4 Yerel sunucu hata dili ✅ 1 Eylül yapıldı (testli)
"İnternet bağlantısı kurulamadı" 127.0.0.1 için yanlış sınıf — yerel adresler için
"Yerel sunucu çalışmıyor (adres)" densin. **Efor:** saatlik.

### 1.5 Model indirme görünürlüğü ✅ 1 Eylül yapıldı (HF "Fetching" satırları ilerleme olayına çevriliyor)
Kullanıcı logunda HF "Fetching files" çıktısı ham aktı. İndirme worker'dan yüzdeyle
raporlansın, İşlemler şeridinde "model indiriliyor %60" görünsün. **Efor:** 1 gün.

---

## Faz 2 — Altyapı (diğer her şeyin önkoşulu)

### 2.1 Şema migrasyon makinesi ✅ 1 Eylül yapıldı (Migrations.Steps + VACUUM INTO yedeği + 7 test) — kilit açıldı
**Sorun:** Migrate() yalnız CREATE TABLE IF NOT EXISTS; sütun eklenemez. Bugüne dek her özellik
yeni tabloyla çözüldü ama bu sonsuza dek süremez (şifreleme, kırpma haritası sütun ister).
**Tasarım:** `schema_version` tablosu + sıralı migrasyon listesi (her biri idempotent SQL ya da
C# delegesi), açılışta tek geçiş, migrasyon öncesi otomatik DB yedeği (`voicetranscript.db.bak-N`).
**Kabul:** v0 (bugünkü) → vN zinciri testte gerçek eski DB kopyasıyla koşar. **Efor:** 2 gün.

### 2.2 Dayanıklı işleme kuyruğu (§1b)
**Sorun:** kuyruk bellekte; çökme "Queued" satırlarını açılış taramasına bırakır (çalışıyor ama
kör). Bulut yükleme parça parça sürdürülebilir (`.cloudparts` var) — yerel değil.
**Tasarım:** kuyruk sırası DB'ye (`queue_position` — 2.1'e bağımlı); işleme aşaması per-call
kaydedilir; açılışta "kaldığı yerden". **Efor:** 2 gün. **Bağımlılık:** 2.1.

### 2.3 Yedekleme doğrulaması
Yedek al/geri yükle var; migrasyonla birleşince sürüm-atlamalı geri yükleme testi gerekir
(eski yedek yeni sürüme yüklenince 2.1 zinciri koşmalı). **Efor:** yarım gün. **Bağımlılık:** 2.1.

---

## Faz 3 — Güvenlik

### 3.1 Bekleyen veri şifrelemesi (eski §3)
**Kapsam:** DB (SQLCipher ya da sayfa düzeyi AES) + ses dosyaları (dosya başı AES-GCM, anahtar
DPAPI'de). **Neden şimdi değil:** anahtar kaybı = arşiv kaybı; migrasyon (2.1) ve doğrulanmış
yedek (2.3) olmadan sorumsuzluk. **Karar noktası (kullanıcıya sorulacak):** cihaz bağlı anahtar
(DPAPI, kolay ama makine ölünce riskli) mi, parola türevli mi (taşınabilir ama unutulursa gider).
**Efor:** 4-5 gün. **Bağımlılık:** 2.1, 2.3.

---

## Faz 4 — Zekâ

### 4.1 Anlamsal arama (12.11)
**Tasarım:** segment embedding'leri (yerel: bge-m3 ya da benzeri çok dilli küçük model —
Türkçe zorunlu; bulut seçeneği "metin makineden çıkar" uyarısıyla), sqlite-vec'te saklama,
Arama'da "benzer anlamlılar" bölümü, Sor'da aday toplama katmanı. Cevaplar yine alıntı+zaman
damgasıyla — ürün kuralı değişmez. **Önce:** 200 görüşmelik gerçek arşivde geri çağırma ölçümü;
FTS5 yetiyorsa ertelenir. **Efor:** 1 hafta. **Bağımlılık:** 2.1 (embedding tablosu büyük,
sürümlenmeli).

### 4.2 İlişki analitiği (§10)
Kim daha çok konuştu / kim dinledi / bilgi kimden kime — SES ve SÜRE istatistiklerinden
(iki ayrı akış bunu bedava verir: konuşma süresi oranı, kesme sayısı, soru/cevap oranı).
**Ürün kuralı sınırı:** yalnız sayılabilir davranış gösterilir ("konuşma sürenin %71'i sendeydi"),
asla nitelik yargısı ("baskındın" DENMEZ). **Efor:** 3 gün, çoğu gösterim tasarımı.

### 4.3 Maliyet tahmini
Kullanım ekranındaki jeton/dakika verisinin yanına sağlayıcı fiyat tablosuyla ₺/$ tahmini.
Fiyatlar katalogda elle tutulur (bayatlar — "tahmin" diye etiketlenir). **Efor:** 1 gün.

---

## Faz 5 — Platform (araştırma; kod yazılmadan karar)

### 5.1 FaceTime / macOS
Ön araştırmanın özeti: Phone Link FaceTime sesini TAŞIMAZ (yalnızca hücresel arama).
macOS portu = kayıt katmanının (WASAPI process loopback) CoreAudio karşılığı — büyük iş,
AVFoundation tap'leri sistem izinleriyle sınırlı. **Karar:** ayrı ürün kararı; bu depoda
yalnızca Core'un platform-bağımsız kalması gözetilir (bugün Capture katmanı zaten izole).

### 5.2 Çoklu makine
Kullanıcı iki PC'de çalışıyor (loglardan görüldü). Veri klasörü taşınabilir ama eşzamanlı
kullanım tasarlanmadı. Bulut eşitleme AÇIKÇA desteklenmiyor (DataRoot bulut klasörünü
reddediyor — doğru karar, SQLite+eşitleme bozulma üretir). Gerekirse: dışa aktar/içe al
birleştirmesi tasarlanır. **Durum:** ihtiyaç doğrulanana dek kapalı.

---

## Çalışma disiplini (bu oturumdan çıkarılan, kalıcı)

1. **Sürüm:** özellik yığını bitince TEK sürüm; her düzeltmede sürüm atılmaz. Eski sürüm ve
   paket silinir; SHA256SUMS her sürümde pakete eşlik eder.
2. **Her kullanıcı şikâyeti önce YAPILACAKLAR'a yazılır** (tepkisel kod yazılmaz), hata sınıfıysa
   tüm yüzeye taranır, düzeltme testiyle gelir.
3. **Kullanıcı verisi** (etiket, not, profil, pano) ayrı tabloda yaşar; boru hattı dokunamaz.
4. **Her hata mesajı adres/model/neden taşır** — log kendi teşhisini içermeli ("deneniyor: X @ Y").
5. Ürün kuralı her özellikte denetlenir: makine hatırlar, insan yargılar; her iddia alıntı +
   çalınabilir zaman damgası taşır.
