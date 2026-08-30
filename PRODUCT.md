# VoiceTranscript — ürün planı

Bu belge, projenin ne olduğunu ve neden para verilesi olduğunu anlatır. Yapılacaklar listesi
değil; yapılacakların *neden* o sırayla yapıldığının gerekçesi.

---

## 1. Ürün tek cümlede

**WhatsApp ve Telegram görüşmelerini kendiliğinden kaydeden, Türkçeye döken ve kimin ne söz
verdiğini alıntısıyla hatırlayan bir kişisel hafıza.**

Hedef kullanıcı: işini telefonla yürüten insan. Fiyat konuşur, tarih verir, söz alır — ve iki
hafta sonra "biz on iki bin demiştik" ile "hayır, on dört demiştik" arasında kalır.

---

## 2. Neden bu, başka bir kayıt uygulaması değil

Piyasadaki her araç görüşmeyi **tek karışık akış** olarak kaydeder ve kimin konuştuğunu bir
modele tahmin ettirir. O tahmin, en çok ihtiyaç duyulan yerde — iki kişi aynı anda konuşurken —
sistematik olarak yanılır.

Bu uygulama iki akışı **ayrı ayrı** kaydeder:

> Mikrofondan giden benim, hoparlörden gelen karşı taraf.

Bunun sonuçları teknik bir ayrıntı değil, ürünün tamamı:

| | Tek akış + diarization | İki ayrı akış |
|---|---|---|
| Kim konuştu | tahmin, %85-95 | **kesin, %100** |
| Üst üste konuşma | en çok yanıldığı yer | doğal olarak doğru |
| VRAM | diarization modeli de gerekir | gerekmez |
| Konuşma payı ölçümü | güvenilmez | **bedava ve kesin** |
| Söz kesme sayımı | mümkün değil | **bedava ve kesin** |

Rakiplerin dürüstçe söyleyemeyeceği tek cümle burada: **"bu alıntıyı kimin söylediği bir tahmin
değil."** Ürünün bütün güven zinciri buna dayanıyor.

İkinci ayırt edici karar: **skor yok.** Bir dil modeli konuşma metninden yalan tespit edemez;
beyaz kutu problar bile şans seviyesinde kalıyor, ve gerçekçi yaygınlıkla "bu kişi yalan
söylüyor" bayraklarının %81'i yanlış çıkar. Bu yüzden uygulama asla "güven puanı" vermez.
Sadece **şunlar tutmadı / şunlar değişti** der ve her satırın altına birebir alıntıyı,
dinlenebilir zaman damgasıyla koyar. Kararı insan verir.

---

## 3. Ürünün üç ekranı

Şu anki yapı teknik olarak doğru ama ürün olarak yanlış sıralanmış: en değerli şey — defter —
bir kişinin içinde, bir sekmenin arkasında duruyor. Doğru sıralama şu:

### 3.1 Defter (yeni, birinci sınıf)

**Bütün kişilerde**, "şunlar tutmadı / şunlar değişti" tek yerde:

- Vadesi geçmiş sözler, kaç gündür geçtiğiyle
- Görüşmeler arasında değişen fiyatlar, dizisiyle
- Kayan teslim tarihleri
- Cevapsız kalmış doğrudan sorular
- Baskı taktikleri, türü ve sayısıyla

Süzgeçler: kişi, tarih aralığı, tür, yalnızca açık olanlar. Her satır tıklanınca o ana atlar.
Reddedilebilir; reddedilen bir daha görünmez.

**Bu ekran ürünün kendisi.** Kullanıcı uygulamayı bunun için açar.

### 3.2 Kişi

Bir insanın bütün geçmişi: görüşmeler, transkript, defteri, istatistikleri.

- Konuşma payı, söz kesme sayısı, ortalama görüşme süresi, sıklık
- Dalga formu üzerinde oynatıcı; transkript satırına tıkla, oradan dinle
- Transkript içinde arama

### 3.3 Arama

Bütün arşivde tam metin. Türkçe harf kuralları uygulanır (`ışık` yazınca `IŞIK` bulunur).
Süzgeçler: kişi, tarih, kim söyledi. Sonuçta eşleşen kelimeler vurgulu, bağlamıyla.

Bunların yanında iki yardımcı ekran:

- **Genel bakış** — bugün ne oldu, ne bekliyor, neye bakmalı
- **Durum** — yakalama çalışıyor mu, model yüklü mü, kuyruk ne durumda, disk ne kadar

---

## 4. Güven, ürünün özelliği olarak

Bu uygulama insanlar hakkında kayıt tutuyor. Güvenilmezse hiçbir işe yaramaz; daha kötüsü,
zararlı olur. Güven şu dört kuralla kuruluyor ve hiçbiri pazarlama değil, kodda karşılığı olan
kısıtlar:

1. **Her iddia birebir alıntı taşır.** Modelin ürettiği her alıntının kaynak metinde gerçekten
   geçtiği doğrulanır; geçmiyorsa satır reddedilir. Uydurma kanıt üreten bir sistem, hiç sistem
   olmamasından kötüdür.
2. **Her alıntı dinlenebilir.** Zaman damgası tıklanır, ses o andan çalar.
3. **Sesi net olmayan satır işaretlenir** ve otomatik çelişki tespitinden çıkarılır. Bir ASR
   hatası ("on sekiz bin" → "on sekiz yüz") gerçek bir insan hakkında sahte bir suçlama üretmesin.
4. **Sezgisel kural, model çıkarımı gibi gösterilmez.** Dolandırıcılık kalıpları küratörlü bir
   listeden gelir ve arayüzde açıkça öyle etiketlenir.

Ve verinin sahibi kullanıcıdır: kişi bazlı tam silme (ses, metin, indeks, olgular, dışa
aktarılmış dosyalar), yedekleme, her şeyi dışa aktarma.

---

## 5. Ürünü satın alınabilir yapan şeyler

Teknik olarak doğru olmak yetmiyor. Para verilen bir yazılımın şu beşi olur:

| | Durum |
|---|---|
| **Kurulur ve çalışır.** Kurulum paketi gerekenleri kendisi indirir. | ✅ |
| **Görüşme kaybetmez.** Yedek servisler, yeniden deneme, kaldığı yerden devam. | ✅ |
| **İlk gün değer gösterir.** Gerçek bir arama beklemeden ne yaptığı görülür. | ✅ |
| **Aradığını bulur.** | ✅ |
| **Sorun çıkarsa nedenini söyler.** | ✅ |
| **Arşiv kullanıcınındır.** Yedek, dışa aktarma, kişi bazlı tam silme. | ✅ |

---

## 6. Dalgalar

### A — Defteri birinci sınıf yap ✅
Bütün kişilerde tek defter ekranı, süzgeçleriyle. Reddetme ve tutuldu işaretleme. Tıkla-dinle.
Bildirimler kalıcı çubuk yerine kendiliğinden kaybolan bildirim.

### B — Oynatıcı ✅
İki akışın aynalı dalga formu, tıklanabilir konum, transkriptle eşleşme: çalan satır vurgulanır.
Boşluk çal/duraklat, ok tuşları on saniye atlar. Konuşan tarafı değiştirmek konumu korur.

### C — Aramayı iyileştir ✅
Kişi, tarih aralığı ve konuşan süzgeçleri. Eşleşen kelimeler vurgulu — Türkçe harf kurallarıyla,
yani `ışık` araması `IŞIK`'ı da vurgular. Sonuçtan görüşmeye atlanır.

### D — İlk gün değeri ✅
Altı haftaya yayılmış üç örnek görüşme tek tıkla yüklenir: fiyat iki kez değişir, bir söz
vadesini geçirir, aynı soru iki kez cevapsız kalır. Aynı tablolara yazılır, aynı silme ile kalkar.

### E — Durum ekranı ve veri sahipliği ✅
Yakalama, model, kuyruk, disk ve bulut servisleri tek sayfada, her birinin yanında onu düzelten
düğmeyle. Yedekle (sesli/sessiz), her şeyi markdown olarak dışa aktar, yedekten geri yükle.

### F — Cila ✅
Sayfa geçiş animasyonları, klavye kısayolları, tutarlı boş durumlar.

---

## 7. Ne yapılmayacak

Bir ürünü tanımlayan, yapmadıklarıdır:

- **Güven / yalan skoru yok.** Ölçülemiyor; yanlış pozitifler gerçek insanlara zarar verir.
- **Grup aramalarında transkript yok.** Karşı taraftaki herkes tek akışta karışır, kimin
  konuştuğu tahmine döner. Ses saklanır, gerisi yapılmaz.
- **OCR yok.** Türkçe `ı/İ/ğ/ş/ç` OCR'ın en çok yanıldığı yer.
- **Anlamsal arama v1'de yok.** Söz ve fiyat takibi için yapısal SQL hem kesin hem bedava.
- **Bulut varsayılan değil.** Açıkça seçilir ve ne gönderildiği yazılı olarak gösterilir.
