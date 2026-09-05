# Yol haritası

*Güncelleme: 5 Eylül 2026 — SocialZeka çatalı. VoiceTranscript'in son sürümü v2.9.21; SocialZeka
sürümleri v3.0.0'dan başlar. Aktif program **sosyal zekâ koçu**: `docs/PLAN-SOSYALZEKA.md`
(paketler R0 → I, ölçüm kapıları, ekranlar). Aşağıdaki V2 programı ve sürüm numaraları tarihseldir;
Faz 3/4 maddeleri yeni plana şöyle yerleşti: 3.2 kulak teyidi → `verdict` tablosu (A2), 3.4 rakam
zaman çizelgesi → Kişi kartı "Rakam yolculuğu" (E), 4.1 kişi tutarlılık geçmişi → Kalıplar (E),
4.3 görüşme öncesi brifing → kayıt şeridi (H).*

## Bugünkü durumun özeti

Çalışan: kayıt (iki ayrı akış), yerel+bulut yazıya dökme (kendini iyileştiren model seçimi),
çözümleme (alıntı+zaman damgalı defter), tutarlılık denetimi (kanıt doğrulamalı, uyarı notlu),
iki taraflı söz takibi (SEN/O), aksiyon önerileri (onaylı kartlar), modelin okuması (imzalı
öznel panel), etiket sistemi (ikon+renk), kişi profilleri + Akış şeridi, çalışma alanı +
hatırlatma takvimi, arama (FTS5 + etiket sorgusu), Sor (alıntılı cevap), komut paleti (Ctrl+K)
+ klavye katmanı, bildirim merkezi (zil+geçmiş), tema seçimi (sistem/açık/koyu), kullanım/jeton
dökümü, güncelleme (SHA doğrulamalı), sessizlik kırpması, saklama süpürmesi; v2.1.3 ile:
Takvim sayfası (Outlook tarzı ay görünümü + ajanda), Çözümleme'de kategori alt-sekmeleri,
pencere üstü dikkat şeridi, opt-in yalan/manipülasyon değerlendirmesi (şema v7), çözümlemede
yerel/bulut rota seçimi, ayarlarda Genel+Tutarlılık kategorileri. 776 test, 0 hata.

---

## V2 PROGRAMI — AKTİF (onay 2026-09-01; faz başına tek sürüm; bu repoda)

**Tema:** V1 kaydetti/yazdı/defter tuttu; V2 kanıtı KULLANILIR yapar. Demir kural her fazda:
kullanıcı alanına otomatik yazma yok, her iddia çalınabilir alıntı; doğrulanamayan alıntı düşer.
*(Kural evrimi, 1 Eylül kullanıcı kararı: "hüküm yasağı" mutlak olmaktan çıktı — açık
yalan/manipülasyon değerlendirmesi opt-in ayardır (varsayılan kapalı), sözel düzeyle ve model
görüşü paketiyle gelir; sayısal skor hâlâ yok, alıntı-doğrulama yasası hâlâ mutlak.)*

### Faz 1 — İki taraflı defter → v2.0.0 ✅ BİTTİ (1 Eylül)
Söz ayrımı her yüzeyde: Defter'de "Verdiğim sözler" çipi + SEN rozeti + geciken kendi söz
listenin başında; Genel bakış'ta "SENİN N sözünün tarihi geçti" / "N sözün tarihi geçti" ayrık
kartları ("Sen → Uliana: …" satırlarıyla); mini takvimde mavi kendi-söz noktası (🤝 tooltip,
tıkla → görüşme); kişi penceresinde "Senin sözlerin"/"Onun sözleri" grupları; görüşme
penceresinde SEN/O rozetleri; vadesi-geçen bayrağı taraf söyler; tutarlılık denetimi kendi
önceki sözünle çelişkiyi de arar. Artı: bulgudan tek tıkla sebebi hazır hatırlatıcı; denetim
maliyet önizlemesi ("~N bin belirteç · kalan bakiye").

### Faz 2 — Okuma + Aksiyon + UI Kuşağı → v2.1.0 ✅ BİTTİ (1 Eylül; kullanıcı kararıyla öne alındı)
Kullanıcının üç seçimi işlendi: yorum TAM SERBEST (niyet/karakter izlenimi dahil — dürüst
paketlemeyle: ayrı zeminli panel, "öznel yorumdur, bulgu değildir" şapkası, model+tarih imzası,
kanıt katmanlarına asla sızmaz, ayarla kapanır); aksiyonlar ONAYLI ÖNERİ KARTLARI (her kart
alıntıya demirli, doğrulanamayan düşer; →Hatırlatıcı/→Önemliler/Yaptım/Reddet tek tık; reddedilen
asla dirilmez); UI DÖRT eksen birden. Şema v6 (action_item + reading_note). UI: bildirim
merkezi (tipli önem + zil + son 50 geçmiş + "Ne oldu?" tostu; onaylar ContentDialog), komut
paleti Ctrl+K (ActionRegistry + kişi araması, Türkçe katlamalı skor) + Ctrl+1..6/F5/Ctrl+F/
Ctrl+? klavye katmanı, kişi penceresinde "Akış" ilk sekme (görüşme/not/bulgu/söz/aksiyon/
hatırlatma tek şeritte, ay gruplu), tema seçimi (sistemi izle/açık/koyu — pin watcher'ı susturur).

### Faz 3 — Kanıt derinliği → (sürüm numarası eskidi; bkz. PLAN-SOSYALZEKA paketleri)
1. **Bağlam penceresi:** bulguya tıkla → iki panel; alıntı ±45 sn çevresiyle, karşı-alıntı
   kendi bağlamında; tek tuşla çal. Kırpılmış-alıntı riskini kapatır — V2'nin vicdanı.
2. **Kulak teyidi:** alıntıyı dinledikten sonra "Dinledim, metin doğru" (tarihli damga) /
   "Yanlış duyulmuş" (ASR hatası olarak kalıcı kapat). Şema v7: flag_verification.
3. **Kanıt paketi:** seçilen bulgular → klasör: alıntı başına WAV (ClipExporter) + kayit.md
   (alıntı, konuşan, tarih, zaman damgası; teyit varsa "kullanıcı dinleyip doğruladı"; sabit
   kapanış: "Bu bir kayıt dökümüdür; değerlendirme okuyana aittir"). Uyarı notu ve model
   metinleri varsayılan DIŞI.
4. **Rakam zaman çizelgesi:** aynı konu·niteliğin değer yolculuğu (15b→18b→20b) kronolojik
   şerit; düğümler tarihli ve çalınabilir; yön için renk yok.

### Faz 4 — Akış → v2.3.0
1. **Kişi tutarlılık geçmişi:** birikmiş bulguların zaman sıralı listesi ("Mart'tan beri kira
   3 kez değişti").
2. **Sessiz denetim vitrini:** otomatik koşumda Genel bakış'a nötr sayı kartı ("3 görüşmede
   denetim bitti, 2'sinde bulgu var"); isim/alıntı yok.
3. **Görüşme öncesi brifing:** kayıt başlarken overlay'de katlanır "Bu kişiyle açık konular"
   (açık sözler — seninkiler dahil, cevapsız sorular, son rakamlar; salt defter gerçekleri).

*(Söz–Yazı Telegram çaprazı kullanıcı kararıyla programdan çıkarıldı, 1 Eylül: "o gereksiz".
Message tablosu ve Telegram içe aktarma olduğu gibi duruyor; istenirse geri çağrılır.)*

### V3 havuzu (sıralanmamış)
İki model mutabakatı (düz cümle rozeti, asla sayı/renk) · denetim geçmişi (koşum anlık
görüntüleri, tekrara yalnız tarih notu) · uyarı→Önemliler önerisi · açık soru yaşam döngüsü
("cevaplandı" bağlama) · bekleyen veri şifrelemesi (anahtar stratejisi kullanıcıya sorulacak:
DPAPI mi parola türevli mi) · anlamsal arama (önce FTS5 yeterlilik ölçümü) · ilişki analitiği
(yalnız sayılabilir davranış; nitelik yargısı asla) · kullanım ekranında ₺/$ maliyet tahmini ·
Sabah Brifi (LLM'siz deterministik derleme; bildirim merkezini kullanır) · görüşme öncesi
hazırlık kartı (simetri kurallı) · çok turlu Sor (önceki turlar "doğrulanamaz bağlam" damgalı) ·
haftalık kişi özeti (yalnız sayılabilirler) · aksiyonlar takvimde (içi boş gri nokta = öneri) ·
kayıp iplikler ("açıldı, bir daha hiç dönülmedi") · arşiv denetimi (toplu tutarlılık, maliyet
önizlemeli) · kişi hızlı kartı (hover) · standart sağ-tık menüleri · yoğunluk modu
(Rahat/Sıkışık boşluk token takası) · 26 view'da sabit FontSize/Margin → Theme.xaml token
taraması · pencere içi Ctrl+1..n sekme kısayolları · RowItem klavye-odak görseli ·
ilk-çalıştırma kontrol listesi · loc:T tamamlama (ayar kartları + Ctrl+? örtüsü).

---

## Çalışma disiplini (kalıcı)

1. **Sürüm:** özellik yığını bitince TEK sürüm; her düzeltmede sürüm atılmaz. GitHub'da SON 10
   SÜRÜM TUTULUR (kullanıcı kararı 1 Eylül: "eskileri de tamamen silme, 10 tane kalsın");
   yalnız 10'u aşan en eskiler silinir. SHA256SUMS her sürümde pakete eşlik eder. Sürüm =
   yalnız tag push; CI kurar (yerel gh release create YASAK — CI ile yarışır).
2. **Plan önce, kod sonra** (kullanıcı kuralı 1 Eylül): "hızlıca ekleyip build alma" yasak;
   her değişiklik öncesi mekanizma yazılı düşünülür; özelliklerde plan+onay.
3. **Her kullanıcı şikâyeti önce YAPILACAKLAR'a yazılır**; hata sınıfıysa tüm yüzey taranır,
   düzeltme testiyle gelir.
4. **Kullanıcı verisi** (etiket, not, profil, pano) ayrı tabloda yaşar; boru hattı dokunamaz.
5. **Her hata mesajı adres/model/neden taşır**; LLM trafiğinin şekli loglanır, içeriği asla.
6. **Ürün kuralı her özellikte denetlenir:** makine hatırlar, insan yargılar; her iddia
   alıntı + çalınabilir zaman damgası taşır.
7. Özellik teslimlerinin sonunda kısa yaratıcı geliştirme fikirleri sunulur (kullanıcı isteği,
   1 Eylül); fikir uygulanmaz, menüye yazılır.

---

## ARŞİV — V2 öncesi yol haritası (referans; durumlarıyla)

### Eski Faz 1 — Cila
- 1.1 Sessizlik kırpma ✅ (SilenceTrimmer + kırpma haritası + v3 migrasyonu + 10 test)
- 1.2 Ayarlar düzen turu ✅ · 1.3 Panel çizgisi + liste fotoğrafları ✅
- 1.4 Yerel sunucu hata dili ✅ · 1.5 Model indirme görünürlüğü ✅

### Eski Faz 2 — Altyapı
- 2.1 Şema migrasyon makinesi ✅ (baseline+delta, VACUUM yedeği; bugün v5'te)
- 2.2 Dayanıklı kuyruk — C# tarafı ✅; kalan tek parça YEREL döküm parça önbelleği (Python
  işi; bu VM'de Python yok, uygulama makinesinde doğrulanacak)
- 2.3 Yedek geri yükleme + migrasyon zinciri ✅ (açılışta Migrate'ten önce; testli)

### Eski Faz 3-4 — Güvenlik/Zekâ → V3 havuzuna devredildi (yukarıda)

### Eski Faz 5 — Platform (araştırma notu; karar bekliyor)
- FaceTime/macOS: Phone Link FaceTime sesi taşımaz; macOS portu ayrı ürün kararı — Core
  platform-bağımsız tutuluyor (Capture katmanı izole).
- Çoklu makine: bulut eşitleme bilinçli reddediliyor (SQLite+sync bozar); ihtiyaç
  doğrulanırsa dışa aktar/içe al birleştirmesi tasarlanır.
