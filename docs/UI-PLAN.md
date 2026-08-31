# UI durum analizi ve geliştirme planı — 31 Ağustos 2026 (v0.9.22)

Bu belge iki şeydir: ekranların bugünkü hâlinin sistematik bir denetimi ve bundan sonrasının
önceliklendirilmiş planı. "Tepkisel çalışma" eleştirisinin cevabı: bulunan her hata sınıfı burada
bütün ekranlara karşı tarandı, plan da tek tek isteklerden değil o taramadan çıkarıldı.

## 1. Denetim — yöntem ve sonuç

Bu oturumda gerçek kusur üretmiş dört hata sınıfı, bütün `src/VoiceTranscript.App` yüzeyine
karşı otomatik tarandı:

| Hata sınıfı | Bu oturumdaki örneği | Tarama sonucu (v0.9.22) |
|---|---|---|
| XAML'in var olmayan özelliğe bağlanması | `SendsAudioOffMachine` rozeti her satırda görünüyordu | **Temiz** — yeni eklenen 22 VM özelliği tek tek doğrulandı |
| Yazılmış ama hiçbir yerden erişilmeyen komut | `ClearCommand`, `DeleteContactCommand` | **Temiz** — tüm `[RelayCommand]`ler XAML ya da code-behind'dan erişiliyor |
| XAML'de tanımlı, code-behind'da olmayan handler | (bu oturumda yok, sınıf olarak riskli) | **Temiz** — tüm `Click=` hedefleri mevcut |
| Durum bildiren MessageBox ("pencereyi kapat-aç") | Çözümleme/yeniden çevirme diyalogları | **Kapandı** — kalan 22 çağrının tamamı onay ya da ölümcül hata diyaloğu, meşru kullanım |

Ekran ekran dolaşılarak bulunanların tamamı ya bu sürümde kapatıldı ya da aşağıda planlandı;
`docs/YAPILACAKLAR.md` §11–12 kayıt defteridir.

## 2. Ekran envanteri ve durumu

| Ekran | Durum | Not |
|---|---|---|
| Genel bakış | ✅ yenilendi | Önemli görüşmeler paneli (sürükle-bırak), Bugün, Son görüşmeler |
| Görüşme penceresi | ✅ yenilendi | Etiketler, canlı işleme şeridi, oynatıcı, kesit dışa aktarma |
| Kişi penceresi | ✅ yenilendi | Foto/doğum günü/Hakkında, ay gruplaması, tarihe atlama, etiket filtresi |
| İşlemler | ✅ iyileştirildi | Canlı şerit + Durdur + Bitenler; satır içi ilerleme çubuğu → §3.2 |
| Ayarlar | ⚠️ kısmi | Veriler bölümü eklendi; tam düzen turu → §3.1 |
| Arama / Sor / Defter / Kişiler / Durum | ✅ sağlam | Bu tur dokunulmadı; denetimden temiz çıktı |
| Kurulum sihirbazı | ✅ sağlam | Tamamlanma damgası bu tur gerçek oldu |

## 3. Plan — öncelik sırasıyla

Sıralamanın ölçütü: kullanıcının bir sonraki oturumda gerçekten karşılaşacağı şey önce.

### 3.1 Ayarlar tam düzen turu (bir sonraki tur, ~yarım gün)
Kullanıcının şikâyeti kayıtlı: "tab eksik, sıralama kötü." Yapılacaklar:
- Bölüm sırası kullanım sıklığına göre: Kayıt → Yazıya dökme → Çözümleme → Veriler → Dışa aktarma.
- Her bölümün içinde kartların "önce sık kullanılan" diye yeniden dizilmesi; Çözümleme'de sağlayıcı
  kartının anahtar/adres/model üçlüsünü tek akış hâlinde sunması.
- Kaydet/Vazgeç yerine anında uygulama + geri alma çubuğu değerlendirmesi (WinUI kalıbı).
- Pencere açılış boyutu ve kaydırma pozisyonunun bölüm değiştirirken sıfırlanması (görülen kusur).

### 3.2 İşlemler satır içi ilerleme (küçük)
Canlı şerit sayfa üstünde; işlenen SATIRIN kendisinde de minik bir çubuk/yüzde görünsün.
Veri hazır (`ActiveCallId`+`ActivePercent`); iş çok-değer dönüştürücüyle görünürlük bağlamak.

### 3.3 Panel cilası (küçük)
- Sürüklerken ekleme çizgisi (insertion indicator) — bugün kart görünür şekilde yerine oturuyor,
  çizgi konfor.
- Karta hatırlatma ekleme sağ tık menüsü (bugün yalnız eski kartların hatırlatması görünüyor).

### 3.4 Liste avatarlarında foto (küçük-orta)
Foto şimdilik yalnız kişi penceresinde. Kişiler sayfası ve panel kartlarında da gösterilmesi,
kaydırılan listede N foto yüklemenin maliyeti ölçülerek yapılmalı (DecodePixelWidth=56, arka
planda; donma olursa vazgeç).

### 3.5 Doğum günleri ana sayfada (küçük)
`UpcomingBirthdays(from, within)` sorgusu + yalnız doluyken görünen satır. Veri girildikçe değer
kazanır; boş kutu olarak asla görünmemeli.

### 3.6 Anlamsal arama (ayrı faz — YAPILACAKLAR 12.11)
UI etkisi: Arama sayfasına "benzer anlamlı sonuçlar" bölümü; Sor'un aday toplama katmanı.
Önce Core tarafı (embedding + sqlite-vec) kararlaştırılmalı.

## 4. Kapanış ölçütü

Bu turun tanımı gereği "bitti": bilinen bütün hatalar kapalı (688 test yeşil), bildirilen bütün
UI istekleri ya teslim edildi ya bu planda tarih/öncelikle duruyor, tek sürüm yayında (v0.9.22).
Tek açık soru kullanıcı logu bekliyor (bulut 404 — hata artık adresini söylüyor, ilk log kapatır).
