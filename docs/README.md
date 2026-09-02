# VoiceTranscript — belge dizini

Bu dosya bir içindekiler listesi değil, bir **yönlendirici**. Amacı tek bir soruya cevap vermek:
*elimdeki iş için hangi dosyayı açmalıyım?*

Bu proje belge tutuyor çünkü bir makinede geliştirilip başka bir makinede kullanılıyor ve
aradaki tek kanal yazılı olan şeyler. Yazılmayan her bulgu, ikinci kez satın alınıyor.

---

## Ne yapmak istiyorsun?

| İstediğin | Aç |
|---|---|
| Projeyi ilk kez kurmak, derlemek, test koşmak | [GELISTIRME.md](GELISTIRME.md) |
| Bir şeyin **neden** böyle yazıldığını anlamak | [MIMARI.md](MIMARI.md) |
| Geçmişte ne bozuktu, nasıl düzeltildi | [ISLEM-GUNLUGU.md](ISLEM-GUNLUGU.md) |
| Şu an ne yapılacak, sıra ne | [YAPILACAKLAR.md](YAPILACAKLAR.md) |
| Bilinen ama henüz düzeltilmemiş kusurların tamamı | [DENETIM-2026-08-31.md](DENETIM-2026-08-31.md) |
| Ürün ne vaat ediyor, kime | [../PRODUCT.md](../PRODUCT.md) |
| Kullanıcıya ne anlatılıyor | [../README.md](../README.md), [../OKUBENI.txt](../OKUBENI.txt) |
| Arayüzün tasarım gerekçeleri | [PLAN-UI.md](PLAN-UI.md) |
| Özgün tasarım planı (büyük ölçüde tamamlandı) | [PLAN.md](PLAN.md) |

---

## Dosyalar ne işe yarıyor

**[GELISTIRME.md](GELISTIRME.md)** — çalışma ortamı. İki makine kısıtı, doğrulanmış araç
sürümleri ve yolları, derleme ve test komutları, bilinen taban çizgisi, `--data` anahtarı, test
yazma kuralları, kod üslubu. **Bir araç beklenmedik davrandığında ilk bakılacak yer burası.**

**[MIMARI.md](MIMARI.md)** — sistemin şekli ve o şeklin gerekçeleri. Katmanlar, ses yolu, arama
tespiti, transkripsiyon, depo, çözümleme. "Sessizce bozulan yerler" ve "değiştirmeden önce
bakılacak yerler" başlıkları, bir değişikliğin nereyi kıracağını önceden söylüyor.

**[ISLEM-GUNLUGU.md](ISLEM-GUNLUGU.md)** — tarihli kayıt: *ne bozuktu → ne yapıldı → nasıl
doğrulandı.* Kod içindeki yorumlar **bir dosyanın** neden öyle olduğunu anlatır; bu dosya
**projenin** neden buraya geldiğini. Bir kusuru düzeltmeden önce buraya bak: daha önce
düzeltilmiş ve geri gelmiş olabilir.

**[DENETIM-2026-08-31.md](DENETIM-2026-08-31.md)** — çok ajanlı kod denetiminin ham raporu.
Altı mercek `CallOrchestrator` ve çevresini taradı, 74 bulgunun her biri çürütülmeye gönderildi,
59'u ayakta kaldı. **Değiştirilmez, tarihli bir kayıttır.** `CallOrchestrator`, `CallDetector`
veya etiketleme akışına dokunmadan önce buraya bak: dokunacağın satırın zaten bilinen bir kusuru
olabilir.

**[YAPILACAKLAR.md](YAPILACAKLAR.md)** — canlı liste. Öncelik sırası, açık kusurlar
dosya:satır ile, ve kullanıcının kullanırken bildirdiklerinin toplandığı yer (§9). Bir madde
bitince silinmiyor; `[x]` işaretlenip gerekçesiyle işlem günlüğüne taşınıyor.

---

## Bu projede pahalıya patlamış tuzaklar

Hepsinin ayrıntısı ilgili belgede; burada olmalarının sebebi **aynı tuzağa ikinci kez
düşülmemesi.**

| Tuzak | Belirtisi | Nerede yazılı |
|---|---|---|
| **Okunmayan ayar** | Ayar var, arayüzde görünüyor, hiçbir şey yapmıyor. En az üç kez oldu: `RecordAutomatically`, `DataRoot`, `TranscriptRetentionDays`. | ISLEM-GUNLUGU 2026-08-31 §1 |
| **`ToSettings` alan düşürme** | Ayarları kaydetmek, ekranda görünmeyen ayarları varsayılana çeviriyordu. | ISLEM-GUNLUGU 2026-08-31 §2 |
| **`dotnet test` yalan söylüyor** | "Zero tests ran" der, çıkış kodu 0 döner. Testler çalışmamış olur ve başarılı görünür. | GELISTIRME → Derleme ve test |
| **`--filter-class "A\|B"`** | Sessizce sıfır test çalıştırır ve başarılı der. | GELISTIRME → Derleme ve test |
| **Microsoft Store `python.exe`** | Sahte yorumlayıcı, çıkış kodu 9009, "Store'dan kur" mesajı. | GELISTIRME → Kurulum |
| **`dotnet` PATH'te yok** | winget kurulumundan sonra mevcut kabuk yeni PATH'i görmez; SDK yokmuş gibi görünür. | GELISTIRME → Kurulum |
| **cuBLAS / CUDA "hazır" yalanı** | Kurulum ekranı yeşil der, model ilk `encode()`'da ölür — hata **görüşme bittikten sonra** gelir. | ISLEM-GUNLUGU 2026-08-30 §1 |
| **Bildirilmeyen form alanı** | Sunucu 200 ve kusursuz bir metin döner, `words` dizisi hiç gelmez. FastAPI tanımlamadığı form alanlarını sessizce atar: `timestamp_granularities[]` yerine `timestamp_granularities` bekleyen bir sunucuya OpenAI'nin yazımını göndermek, her alıntının anını kaybettirir ve hiçbir yerde hata görünmez. | ISLEM-GUNLUGU 2026-09-02 (sekizinci tur) |
| **Öğrenilmiş yanlış başlık** | Bir kez yanlış kişiye bağlanan pencere başlığı, sonraki her görüşmeyi aynı yanlış kişiye yazar ve kayıt ekranını hiç göstermez. | YAPILACAKLAR §7 |

---

## Değişmez kurallar

Bunlar tercih değil; ihlal edildiğinde ürün sessizce bozuluyor.

1. **Görüşmeyi kaybetmek tek onarılamaz hatadır.** Yavaş olmak, çirkin olmak, eksik olmak
   onarılabilir. Kaydedilmemiş bir konuşma geri gelmez. Her tasarım kararı önce buna bakar.
2. **Sessiz başarısızlık, gürültülü başarısızlıktan kötüdür.** Yakalama sessizlik kaydettiyse,
   transkripsiyon başarısız olduysa, ayar uygulanmadıysa — kullanıcı **söylenmeden** öğrenemez.
3. **Yorum, kodun ne yaptığını değil neden öyle olduğunu anlatır.** Özellikle bir tuzağı
   önlüyorsa, tuzağın ne olduğunu yaz.
4. **Bir testin var oluş sebebi yorumunda yazılı olmalı.** "Ne kırıldığında bu test kırmızı olur"
   yazılmazsa, bir sonraki kişi onu gereksiz sanıp siler.
5. **`Core` katmanı Windows'a bağımlı değildir.** Bir şeyi `Core`'dan çıkarmak, onu bu makinede
   test edilemez yapmaktır — ve test edilemeyen şey, ancak kullanıcı fark ettiğinde bozulur.
6. **Kullanıcıya görünen bütün metin Türkçedir.**
7. **Günlük paylaşılmak üzere yazılır.** İçinde konuşma metni, kişi adı, kişi adı taşıyan dosya
   yolu veya API anahtarı **asla** olmaz.
8. **Geliştirme makinesindeki WhatsApp ve Telegram'a dokunulmaz** — ne süreçlerine, ne
   pencerelerine, ne verilerine.

---

## Bir tur bitince ne yapılır

1. `docs/ISLEM-GUNLUGU.md` dosyasına tarihli kayıt: **ne bozuktu → ne yapıldı → nasıl doğrulandı.**
   Kanıt yaz — hangi komut, hangi sayı.
2. `YAPILACAKLAR.md` içindeki maddeler `[x]` işaretlenir; silinmez.
3. Davranış değiştiyse `docs/MIMARI.md`, ortam değiştiyse `docs/GELISTIRME.md` güncellenir.
4. Yeni bir tuzak bulunduysa yukarıdaki tabloya bir satır eklenir.
5. Taban çizgisi sayıları değiştiyse `GELISTIRME.md` içindeki tablo güncellenir.
