# Yapılacaklar — kurulum, sınama ve arayüz

Bu dosya elde kalan işin listesi. Her madde neden yapıldığını da söylüyor, çünkü altı ay sonra
bu koda dönen kişi "bu neden böyle" sorusunu soracak.

---

## Durum

Aşağıdaki dalgaların 1, 1b ve 2'si tamamlandı; 3 (arayüz) ana ekranlar için tamamlandı.
Kalanlar Dalga 4 başlığı altında.

---

## Dalga 1 — Kurulum gerçekten otomatik olsun

Şu an ne oluyor: kurulum paketi yalnızca uygulamayı kopyalıyor. Python'u sihirbaz kuruyor ama
sihirbaz her adım için ayrı düğmeye basılmasını bekliyor, atlanabiliyor, ve atlandığında ayarlar
ekranında ham İngilizce hata çıkıyor:

> Worker durumu alınamadı: The worker exited with code 9009.
> Python was not found; run without arguments to install from the Microsoft Store…

Bu, kullanıcının çözemeyeceği bir hata. Yapılacaklar:

1. **Kurulum paketi bitince hazırlığı kendisi başlatsın.** Varsayılan olarak işaretli bir görev,
   kurulum biter bitmez uygulamayı hazırlık kipinde açar.
2. **Sihirbaz zinciri kendiliğinden çalıştırsın.** Python → paketler → model, tek tek düğmeye
   basmadan, canlı günlükle. Düğmeler yalnızca bir adım başarısız olursa tekrar denemek için.
3. **winget'e bağlı kalmasın.** winget her makinede yok ve kurumsal makinelerde kapalı olabiliyor.
   Yedek yol: python.org'dan resmi kurucuyu indirip sessiz kurmak
   (`/quiet InstallAllUsers=0 PrependPath=1`), SHA-256 doğrulamasıyla.
4. **Microsoft Store kısayolu tuzağı.** `python.exe` diye bir sahte dosya var, çalıştırılınca
   Store'u açıp 9009 döndürüyor. Tespit edilip yok sayılıyor; kullanıcıya Türkçe anlatılıyor.
5. **Ham hata metni gösterilmesin.** Her hata Türkçeye çevrilir ve yanında onu çözecek düğme olur.

## Dalga 1b — Donanım testi: bu makine Whisper'ı kaldırır mı

Sihirbazdaki "Ekran kartı" adımı yalnızca CUDA var mı diye bakıyor. Asıl soru bu değil. Asıl
soru: *bu makinede hangi model çalışır ve ne kadar sürer.* Bunun teknik özelliklerden okunması
mümkün değil — aynı 6 GB kart, sürücüsü eskiyse ya da başka bir uygulama belleği tuttuysa
tamamen farklı davranır. Ölçmek gerekir.

5b. **Donanım raporu**: ekran kartı adı ve belleği, işlemci ve çekirdek sayısı, RAM, boş disk.
5c. **Gerçek ölçüm**: kısa bir örnek ses gerçekten yazıya dökülür ve gerçek-zaman katsayısı
    ölçülür. "37x gerçek zaman" cümlesi tahmin değil, o makinede alınmış sonuç olur.
5d. **Karar cümlesi**: hangi model sığar, 60 dakikalık bir arama ne kadar sürer, yerel LLM için
    yer kalır mı. Uygun değilse ne yapılacağı da söylenir — küçük modele düşmek ya da buluta
    göndermek.
5e. Rapor hem sihirbazda hem ayarlarda, ve panoya kopyalanabilir olsun (destek istemek için).

## Dalga 2 — Adresler otomatik, her şey sınanabilir

Ekrandaki "Adres" alanı boş görünüyor; değer yalnızca soluk ipucu metni. Kullanıcı elle yazması
gerektiğini sanıyor. Ayrıca hiçbir yerde "bu anahtar çalışıyor mu" diye soracak bir düğme yok —
bunu bir görüşme kaybedilene kadar öğrenemiyorsun.

6. **Adres servis seçilince gerçekten dolsun**, ipucu olarak değil. Yanında "Varsayılana dön".
7. **Bağlantı sınaması**: bulut yazıya dökme, LLM sağlayıcısı ve Notion için ayrı ayrı. Sonuç
   erişilebilirlik, anahtar geçerliliği, modelin varlığı ve gecikmeyi söyler.
8. **Model listesi sağlayıcıdan çekilsin.** OpenRouter, Ollama, LM Studio ve llama-server hepsi
   model listesi veriyor. Elle model adı yazmak yerine gerçekten kurulu olanlar listelensin.
9. **Yerel sunucu otomatik bulunsun.** 11434 (Ollama), 8080 (llama-server), 1234 (LM Studio)
   yoklanır; çalışan varsa "Ollama bulundu, 3 model kurulu" denir.

## Dalga 3 — Arayüz baştan sona

10. **Genel bakış gerçek bir gösterge paneli olsun**: kayıt sırasında iki akışın canlı seviye
    çubukları (ses geliyor mu sorusunun cevabı görüşme bittikten sonra değil, o an), işlem
    kuyruğu ve ilerlemesi, bugün/bu hafta, disk kullanımı.
11. **Kişi ekranı**: satıra tıklayınca o ana atlayan transkript, dalga formu, transkript içinde
    arama, o görüşmenin defteri, konuşma süresi oranı.
12. **Arama**: kişi, tarih aralığı ve uygulama süzgeçleri; eşleşen kelimeler vurgulu.
13. **Her ekranda** boş durum, yükleniyor durumu ve hata durumu tutarlı olsun.

## Dalga 4 — Eksik özellikler

14. Elle kayıt başlat/durdur — tespit kaçırırsa.
15. Başka bir modelle yeniden yazıya dökme; başarısız işleri yeniden deneme.
16. İstenince dışa aktarma (tek görüşme, kişi, hepsi) — yalnızca otomatik değil.
17. Kişiyi tümüyle silme: ses, metin, indeks, olgular, dışa aktarılmış dosyalar.
18. **Konuşma payı** — iki ayrı akıştan bedavaya çıkan, başka hiçbir aracın güvenilir yapamadığı
    ölçüm: kim ne kadar konuştu, kim kimi kaç kez böldü.
19. Veri klasörü yönetimi, saklama süresi temizliği, yedekleme.
