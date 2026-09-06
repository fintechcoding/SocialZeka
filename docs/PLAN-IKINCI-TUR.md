# SocialZeka — ikinci tur: çevreler, sözün tabanı, arayüz bütünlüğü

*6 Eylül 2026 · HEAD `2ac4f97` · Şema sürümü 19 · Birinci turun bütün paketleri (R0, P0, A1, A2, B, C+D, E, G+H+J, I) bitti; taban 1338 C# / 1333 geçti / 5 atlandı ve 179 Python.*

Birinci tur `docs/PLAN-SOSYALZEKA.md`. Bu belge onun devamı, yerine geçeni değil: oradaki ürün
kuralları (§2), üç zemin (§3.1) ve yapılmayacaklar (§7) burada da aynen bağlar.

Bu plan; üç ayrı çok ajanlı turdan sentezlendi. On üç keşif ajanı bugünkü ekranları, veri
modelini ve **kullanıcının gerçek arşivini** okudu; dokuz tasarım üç ayrı açıdan kuruldu; her
tasarım iki ya da üç bağımsız mercekten puanlandı. Aşağıdaki her sayı canlı veritabanından
ölçüldü, tahmin değil. Yargıçların çürüttüğü maddeler plana girmedi; çürütemedikleri, kendi
düzeltmeleriyle birlikte girdi.

---

## 0. Kullanıcının üç isteği ve ne anlaşıldı

| # | Kullanıcının cümlesi | Ne anlaşıldı |
|---|---|---|
| 1 | "sürekli görüştüğüm kişiler var mesela aile bir de işle alakalı; aile ile alakalı görüşmeler Görüşmeler ekranını çok dolduruyor, diğer önemli görüşmeleri çok rahat bulamıyorum" · "aile için kişi seçimi olmalı, UI'de de bütünlüğü olmalı bunun" | **Paket Ç — Çevreler.** Kişiye bir kez atanan, adlandırılmış bir aidiyet; kişileri sen seçersin; kavram bütün ekranlarda aynı biçimde görünür. |
| 2 | "verdiğim sözleri nasıl hesaplamalıyız? şu anki hâli güzel ama başka nasıl bir özellik olabilir" | **Paket S — Sözün tabanı.** Sözler sayfası olduğu gibi kalır; üstüne, satırın kendisini düzelten ve çevresini gösteren yüzeyler gelir. |
| 3 | "UI'de gördüğüm böyle bir bütünlük koyabiliriz" | **Paket B — Bütünlük sözleşmesi.** Testle zorlanan, sayfadan sayfaya değişmeyen bir arayüz kuralları kümesi. |

### 0.1 İPTAL EDİLEN: sözlerin Yapılacaklar'a düşmesi

Plan §4.6 bir "Sözlerim çipi" (`commitment ByMe=1 status=0`, Toggle → `FulfilCommitment`)
öngörüyordu ve bu tur onu tasarlayıp **7,5 ile en yüksek puanı** verdi (kopyalamadan, kaynağından
okuyarak). Kullanıcı bunu açıkça reddetti: **"yok düşsün demiyorum."**

Karar: **plan §4.6'nın Sözlerim çipi maddesi iptaldir.** Tasarım `tools/`de değil, bu belgede
kalır; birileri ileride yeniden önerirse burada iptal edildiği ve neden edildiği yazılıdır.
Tasarımın keşif bulguları (aşağıda §3.2) geçerliliğini korur, çünkü onlar Yapılacaklar'ın kendi
kusurlarıdır, sözlerle ilgisi yoktur.

---

## 1. Ölçülen gerçekler (6 Eylül 2026, canlı arşiv)

Aşağıdaki her satır `%LOCALAPPDATA%\SocialZeka.Data\voicetranscript.db` üzerinde salt okunur
sorguyla ölçüldü. Bu plandaki her eşik bu sayılara göre kuruldu.

**Arşivin büyüklüğü**

| Ne | Sayı |
|---|---|
| Görüşme | 52 |
| Döküm satırı | 2825 |
| Kişi | 9 (biri kişisiz görüşme) |
| İddia | 133 |
| Bayrak | 4 |
| Söz | 13 |
| Önerilen aksiyon | 44 |

**Görüşmelerin kişiye dağılımı** — sorunun kökü burada görünüyor.

| Kişi | Görüşme |
|---|---|
| Uliana | 25 |
| Bozkurt | 10 |
| Serdal | 4 |
| Samet | 4 |
| Mustafa | 3 |
| Sinan | 2 |
| Gurhan Abi | 2 |
| Avukat Polonya | 1 |
| (kişisiz) | 1 |

Uliana tek başına listenin %48,1'i. İlk iki kişi %67,3. İkisi arka plana alınırsa liste
52'den **17**'ye iner ve tepedeki kişi %23,5 olur.

**Sözlerin durumu**

| Ne | Sayı |
|---|---|
| Toplam söz | 13 |
| Kullanıcının verdiği (`by_me=1`) | 3 |
| Karşı tarafın verdiği | 10 |
| Tarihsiz | 12 |

Kullanıcının üç sözünün tamamı:

```
#99  "güzel bir kulaklık almak"   call 42, ms 51450, tarihsiz, tutuldu 6 Eyl 12:23:42
#100 "Whatsapp'tan ayırmak"       call 42, ms 51450, tarihsiz, tutuldu 6 Eyl 12:23:35
#118 "Coşkun'u halletmek"         call 62, ms 125310, ham vade "şimdi", açık
```

**#99 ve #100 aynı görüşmenin aynı milisaniyesinden çıkmış**, tek bir cümleden:

> "Yav bir kulaklık alacağım güzel ya. Dur Whatsapp'tan ayırayım seni bekle."

İkincisi bir söz değil, konuşma dilinde bir ara cümle. Kullanıcı 6 Eylül'de ikisini de yedi
saniye arayla "tutuldu" işaretledi. Yani **söz olmayan bir satır deftere tutulmuş söz olarak
geçti.** Bu, "sözleri nasıl hesaplamalıyız" sorusunun cevabını belirliyor: bugün hesaplanacak
bir taban yok, önce satırın kendisi düzelmeli.

Dökümden doğrulandı: #99/#100'ün hemen öncesindeki satır karşı tarafın "Sesim çok kötü geliyor.
Çok yankılı ve çok kötü geliyor." şikâyeti. #118'in öncesi karşı tarafın isteği ("Sen şey ara o
adama sor…"), sonrası "Tamam ben de soruyorum, hadi görüşürüz."

**Elle sınıflandırma yüzeylerinin kullanımı** — bu planın en önemli riski.

| Yüzey | Satır |
|---|---|
| `call_tag` (görüşme etiketi) | 0 |
| `board_card` (pano kartı) | 0 |
| `contact_field` (kişi alanı) | 0 |
| `call.is_pinned` | 0 |
| `todo` (kullanıcının yazdığı yapılacak) | 0 |
| `contact_profile` | 0 |
| `tag_def` | 6 (tohum, kullanıcı eklememiş) |

Kullanıcı bu üründe bugüne kadar **hiçbir elle sınıflandırma yapmadı ve tek bir yapılacak
yazmadı.** Dolu olan tek kullanıcıya dönük liste makinenin ürettiği 44 aksiyon. Sonuç: kullanıcı
tüketiyor, düzenlemiyor. Kurulum isteyen her özellik bu gerçeğe çarpar ve ölçüsü buna göre
kuruldu (§2.5).

---

## 2. Paket Ç — Çevreler

**Amaç.** Kişiye bir kez yazılan, her ekranda okunan bir aidiyet. Kullanıcı "aile" der, kişileri
seçer, Görüşmeler ekranı o çevreyi arka plana alır; aynı çevre Kişiler, Genel bakış, kişi kartı
ve Aynam'da da aynı biçimde görünür.

### 2.1 Şema v20

```sql
CREATE TABLE IF NOT EXISTS contact_circle (    -- KULLANICI: kendi kelimesi; boru hattı asla yazmaz
    circle_folded TEXT PRIMARY KEY,            -- kimlik: TurkishText.NormalizeForSearch ile katlanmış
    circle        TEXT    NOT NULL,            -- görünen yazım, kullanıcınınki
    icon          TEXT    NOT NULL,
    color         TEXT    NOT NULL,
    position      INTEGER NOT NULL DEFAULT 0
);
ALTER TABLE contact_profile ADD COLUMN circle_folded TEXT;   -- NULL = çevresiz
CREATE INDEX IF NOT EXISTS ix_profile_circle ON contact_profile(circle_folded);
```

`tag_def`'in (`Schema.cs:482`) birebir şekli ve anlambilimi. Atama `contact_profile`'a gidiyor
çünkü o tablo zaten **"SADECE KULLANICI YAZAR"** mührünü taşıyor (`Schema.cs:504`), `ON DELETE
CASCADE`'i var, ve `MergeContacts` ile `MergeArchive` onu zaten ele alıyor.

**Kimlik neden INTEGER id değil katlanmış metin:** arşiv birleştirmede id'ler makineler arasında
farklıdır ve bir `map_circle` eşlemesi gerekirdi; metin kimlik iki arşivde kendiliğinden aynıdır.
`call_tag` ↔ `tag_def` ikilisi bu sorunu zaten böyle çözmüş; karar icat değil kopya.

**Kayıt yerleri:** `Schema.Statements` + `Schema.Version` 19→20 + `Migrations.Steps`'e `new(20, …)`
+ `MigrationTests` bloğu + `MergeArchive`'a tek satır `Copy(connection, transaction,
"contact_circle")` + `MergeContacts`'ın `contact_profile` `ON CONFLICT` bloğuna
`circle_folded = COALESCE(contact_profile.circle_folded, excluded.circle_folded)`.
`LedgerTables` **gerekmiyor** (tablo `contact_id` taşımıyor). `contact_profile`'ın kendisi zaten
kopyalanıyor ve `Copy` sütun listesini `PRAGMA table_info` kesişiminden kurduğu için
(`Repository.cs:1492`) yeni sütun bedelsiz taşınıyor.

### 2.2 Görüşmeler ekranı

```
 Görüşmeler                                                    17 / 52 görüşme
 [Herkes ▾][Her zaman ▾][Hepsi ▾][Hepsi ▾][Hepsi ▾][🔍 Kişi adı] [Süzgeçleri temizle]
 Çevreler: (● Aile 35) (● İş 16) (Çevresiz 1)   [Çevreleri düzenle…]
  ⓘ Aile arka planda — 35 görüşme listede yok.                          [Göster]
 ────────────────────────────────────────────────────────────────────────────
 Bugün                                                             2 görüşme
  (S) Serdal                                    ● İş   WhatsApp      41:12
      2 saat önce · Hazır
  (M) Mustafa                                   ● İş   WhatsApp      06:40
```

Çip dolu daire + ad + sayı taşır. Dolu = gösteriliyor, soluk = arka planda. **Sayı her zaman
tüm arşivin sayısıdır**, çünkü sayaçlar süzgeçten önce hesaplanır (Defter ve Sözler'de zaten
böyle). Sol tık aç/kapa, sağ tık "yalnız bunu göster" / "hepsini göster".

**"Çevresiz" çipi kapatılamaz.** Yeni bir kişi asla sessizce kaybolmaz.

Satırda çevre işareti 8 piksellik renkli bir daire, pil değil. Sebep §2.6'da.

### 2.3 Çevreler penceresi

```
 Çevreler                                                                [ ✕ ]
 ┌─ Çevreler ─────────────┐ ┌─ Kişiler (çok konuşulandan aza) ──────────┐
 │ ● Aile      🏠    35   │ │ (U) Uliana     25 görüşme  [● Aile     ▾] │
 │ ● İş        💼    13   │ │ (B) Bozkurt    10 görüşme  [● Aile     ▾] │
 │                        │ │ (S) Serdal      4 görüşme  [● İş       ▾] │
 │ [+ Çevre ekle]         │ │ (S) Samet       4 görüşme  [● İş       ▾] │
 │ Ad:    [Aile        ]  │ │ (M) Mustafa     3 görüşme  [Çevresiz   ▾] │
 │ İkon:  [🏠 ▾]          │ │ (S) Sinan       2 görüşme  [Çevresiz   ▾] │
 │ Renk:  [■ ▾]           │ │ (G) Gurhan Abi  2 görüşme  [● İş       ▾] │
 │ [Sil]                  │ │ (A) Avukat Pol. 1 görüşme  [● İş       ▾] │
 └────────────────────────┘ │ 8 / 9 kişi bir çevrede                    │
                            └───────────────────────────────────────────┘
```

Sol pano `TagManagerWindow`'un birebir şekli. Sağ pano her satırda tek açılır liste; seçim
**anında yazılır, Kaydet düğmesi yoktur** (`SetBirthDate` ve `SaveNote` ile aynı davranış).
Kişiler `call_count DESC` sıralı: listeyi dolduran insan en üstte, yani ilk iki tık sorunun
üçte ikisini çözer.

Yeni **pencere**, yeni sayfa değil. Plan §5'in sekiz kaydının hiçbiri gerekmiyor; yalnız
`WindowSmokeTests.Build` satırı eklenir.

**Kurulum maliyeti (bugünkü 9 kişilik arşiv):** 1 + 16 + 1 + 1 = **19 tık, bir dakikanın altı.**
Sonrasında her yeni kişi için 2 tık (Kişiler sağ tık → Çevre ▸).

### 2.4 Diğer ekranlar — istenen "bütünlük" burada

| Ekran | Ne olur |
|---|---|
| Kişiler sayfası | Satırda renkli nokta; sağ tık → "Çevre ▸ (Aile / İş / Çevresiz)" |
| Kişi penceresi, Bilgiler | Doğum tarihinin altına "Çevre" açılır listesi; kart başlığında nokta + ad |
| Genel bakış | Son 12 görüşme aynı arka plan kümesine uyar; gizlerse "{n} görüşme gizli · göster" |
| Aynam | Kişi açılırı ikiye bölünür: "Herkes / ─ Çevreler ─ / Aile / İş / ─ Kişiler ─ / …" |

Aynam'daki değişiklik yeni bir denetim getirmiyor, yeni bir **soru** getiriyor: "iş
görüşmelerinde sözü ne sıklıkta kesiyorum".

**İlk sevkte bilerek dışarıda:** Sözler sayfası çip şeridi, Takvim süzgeci, Kişiler sayfasını
çevreye göre gruplama, görüşme satırının bağlam menüsüne çevre fiili (dokuzuncu fiil olurdu ve
"bu GÖRÜŞME aile mi" yanlış zihinsel modelini davet ederdi), çok değerli çevre, süzmenin
tamamının SQL'e inmesi. Yedisi de bilinçli.

### 2.5 Ölçü, eşik, geri alma

| # | Ölçü | Bugün | Eşik |
|---|---|---|---|
| 1 | Kapsama: çevresi olan kişi / görüşmesi olan kişi | 0/9 | İki hafta içinde ≥ %70 |
| 2 | Sıkışma: en kalabalık kişinin görünen listedeki payı | %48,1 | Arka plan kurulduktan sonra < %35 |
| 3 | Benimseme: `AppSettings.HiddenCircles` dört hafta sonra | — | Boş olmamalı |
| 4 | Kayıp yok | — | Sert değişmez, tolerans sıfır |

Ölçü 4 istatistik değil, değişmezdir: **gizleme yüzünden `Count < Total` olan hiçbir durumda
"{n} görüşme gizli · göster" satırı ekranda olmayamaz.** Testle sınanır, çünkü kabul edilebilir
bir başarısızlık oranı yok.

**Geri alma.** Yıkıcı olan kısım bir *tercih* (`AppSettings.HiddenCircles`), veri olan kısım
kullanıcının *atamaları*. Tercihi kapatmak özelliği tamamen etkisizleştirir ve tek bir kullanıcı
verisi kaybolmaz.

1. **Sessiz kayıp iddiası** (sert, anında): "görüşme kayboldu" diyen tek bir olay bile olursa,
   açılışta `HiddenCircles` boşaltılır ve çip şeridi devre dışı bırakılır. Atamalar kalır.
2. **Benimsenmeme** (dört hafta): `HiddenCircles` boş **ve** kapsama < %40 ise çip şeridi
   kaldırılır, çevre yalnız kişi kartında bilgi alanı olarak kalır. **Şema geri alınmaz** —
   kullanıcının yazdığı çevreleri silmek geri alınamaz veri kaybı olur.
3. **Yanlış eksen** (dört hafta): kapsama ≥ %70 ama sıkışma eşiği tutmuyorsa sorun çevrede değil
   sayfalama/tavan tarafındadır; bir sonraki iş "süzmeyi SQL'e indir + sanallaştırma" olur.

### 2.6 Riskler, en zayıf yerden başlayarak

- **EN ZAYIF YER: kullanıcı bu üründe hiç sınıflandırma yapmadı.** `call_tag` 0, `contact_field`
  0, `board_card` 0, `is_pinned` 0, `todo` 0 (§1). Elle sınıflandırma sunan beş yüzeyin beşi de
  hiç kullanılmamış. Bahis şu: **kişi başına 9 atama, görüşme başına 52-ve-günde-artan atamadan
  nitelik olarak farklıdır** ve karşılığı anında görünür (bir tık listenin yarısını siler).
  Bahis yanlışsa özellik etiketler gibi boş durur. Kapsama ölçüsü tam olarak bunu sınamak için
  var ve erken uyarı versin diye iki haftaya kurulu.
- **Gizlemek gerçek bir konuşmayı gizler.** "Aile" ile "önemsiz" aynı şey değil. Üç savunma
  tasarımın parçası, sonradan eklenecek yama değil: gizlenen sayı her zaman ekranda; **kişi
  süzgeci ve arama kutusu gizlemeyi yener**; geri getirmek tek tık.
- **Arama kutusu tuzağı.** Kutu yalnız kişi adında ve yalnız belleğe yüklenmiş satırlarda
  arıyor. Gizleme SQL'de uygulanırsa "gizle" sessizce "ara"yı da bozar. **Bu yüzden gizleme
  yalnız bellekteki `Filter`'da uygulanır**, `ListCalls` imzasına dokunulmaz, `Total` arşivin
  sayısı kalır. İki test sırf bunun için: `SecilenKisiGizlemeyiYener`, `AramaKutusuGizlemeyiYener`.
- **Tek değerli çevre**, hem kuzenin hem iş ortağın olan kişiyi zorlar. v21'de
  `contact_circle_map` tablosuna geçiş yolu açık; sütun aynı göçte düşürülmez. Tek yönlü kapı
  değil ama bir sürüm boyunca yanlış model.
- **2000'lik sessiz tavan çözülmüyor.** `ListCalls(limit: 2000)` bütün arşivi belleğe okuyup
  süzmeyi orada yapıyor ve tavana çarpıldığı kullanıcıya hiçbir yerde söylenmiyor. Bugün 52
  görüşme var; tavan yaklaşık 230 gün sonra ısırır. Çip sayıları tüm arşivden geldiği için bu
  tavan **ilk kez görünür hâle gelir** — çip toplamı ile yüklenen satır ayrışırsa sorun ekranda
  belirir. Sayfalama ve sanallaştırma bu sevkin dışında ama artık yazılı.
- **Performans.** Sayfa 2000 satırı sanallaştırmadan kuruyor ve arama kutusunun her harfinde
  baştan kuruyor (`ScrollViewer > StackPanel > ItemsControl`). Çevre işareti bu yüzden
  `Pill + TextBlock` değil, 8 piksellik `Ellipse`. Bu satır bir daha büyürse sanallaştırma
  önkoşuldur.
- **Ad çakışması.** "Grup" kelimesi alınmış: `CallKind.Group` kod içinde on beş yerde "üç
  kişilik, deşifre edilmeyen kayıt" demek. Kavramın adı bu yüzden **çevre**; sınıf ve sütun
  adlarında `group` geçmemeli.
- **Tohum çevreler** (Aile / İş) kullanıcının yazmadığı kelimeleri ekrana koyuyor.
  `SeedDefaultTagDefs`'in altı etiketi emsal; üçü de silinebilir ve yeniden adlandırılabilir.
  Boş liste kapsama ölçüsünü büyük olasılıkla öldürürdü, bu yüzden bilerek verildi.

**Efor:** 2–3 gün, yaklaşık 24 dosya (Core 7, App 12, test 5).

---

## 3. Paket S — Sözün tabanı

**Amaç.** Sözler sayfası olduğu gibi kalır. Üstüne, satırın kendisini görünür ve düzeltilebilir
yapan dört yüzey gelir. Hiçbiri şema değiştirmiyor, hiçbiri model çağırmıyor.

Otuz beş fikir üretildi, üç bağımsız sıralayıcı puanladı. Sevk edilen dördü:

### 3.1 Dört yüzey

**S1 — Sözün etrafı** *(9,0 · yarım gün · şema yok)*

Söz kartında alıntının altında, aynı görüşmenin `quote_start_ms`'inden önceki iki ve sonraki iki
döküm satırı. Her satır sen/o damgalı, tıklanınca o an çalıyor. Tek sorgu:
`segment WHERE call_id AND start_ms BETWEEN`. **Hiçbir yorum, hiçbir etiket yok — ham döküm.**

```
 ▸ 51:45  sen: "Yav bir kulaklık alacağım güzel ya. Dur Whatsapp'tan ayırayım seni bekle."
   o · 49:04  "Sesim çok kötü geliyor. Çok yankılı ve çok kötü geliyor."
```

Kullanıcı iki saniyede görür ki bu cümle bir yükümlülük değil, ses şikâyetine verilmiş tepki.
Uygulama **"bu söz değil" demez**; satırları gösterir, kararı kullanıcı mevcut [Reddet] ile
verir. 13 sözün 13'ünde çalışır; kullanıcının üç sözünün üçünde de cevabı doğrudan veriyor.

Kart şişmesin diye 2+2 satır ve satır başına 140 karakter kırpma.

**S2 — Tek cümle, iki söz** *(8,3 · yarım gün · şema yok)*

Aynı `(call_id, by_me, quote_start_ms, katlanmış alıntı)` dörtlüsüne düşen satırlar tek kart
olur. Kart bir alıntı, altında iki-üç aday yükümlülük ve tek soru: **"Bu cümlede gerçekten
verdiğin söz hangisi?"** Seçilmeyenler `dismissed_by_user` tombstone'uyla susar (yeniden
çözümlemede geri gelmezler, `SurvivingCommitmentKeys` bunu zaten garanti ediyor), tek [Geri al]
hepsini döndürür. Kullanıcı isterse ikisini de bırakabilir.

Bugünkü arşivde tam bir grup var ve o grup kullanıcının kendi sözlerinde: #99 + #100. Bu tek
özellik **kullanıcının kendi defterinin üçte birini temizler.**

Kökü kodda: `QuoteVerifier.Locate` tek segmentte bulunan alıntı için segmentin tamamını
döndürüyor, `AnalysisPipeline`'ın tekilleştirmesi ise `(ByMe, Obligation, Quote)` anahtarını
kullanıyor — yükümlülük farklıysa ikisi de hayatta kalıyor.

Zemin kuralı: iki aday kartın **içinde** kanıt olarak kalır (⌂), kullanıcının seçimi kartın
**altında** ayrı rozet olarak (✎). Adaylar hiç değiştirilmez.

**S3 — "Ne zamana?"** *(8,0 · yarım–bir gün · şema yok)*

Tarihsiz her kartın altına tek satırlık şerit:

```
 [Bu hafta] [Önümüzdeki hafta] [Bu ay içinde] [Tarih seç…] [Tarihsiz kalsın]
```

İlk dördü `user_deadline_date`'e yazar; **makinenin `deadline_date`'i el değmeden kalır.**
Beşincisi asıl yenilik: "tarihsiz" bir eksiklik değil, verilebilecek bir cevap olur.

13 sözün 12'si tarihsiz, kullanıcının üçünün üçü de. #118'in ham vadesi "şimdi" tarihe
çevrilememiş ve kart bugün "tarih net değil: şimdi" diyor. Şerit açıldığı gün sayfadaki
kartların neredeyse tamamında görünür.

**S4 — "Bu söz değildi"** *(7,3 · düşük–orta · şema yok)*

Aynam'ın kulak teyidini sözlere genişletir: `verdict` tablosuna `kind='soz'` ile üç yargı —
[Doğru] · [Yanlış duyulmuş] · [Bu söz değil]. "Bu söz değil" işareti `quote_folded` anahtarıyla
saklanır, yeniden çözümlemede diriltilmez (`DismissedFlagKeys` kalıbı) ve o satır bütün söz
sayımlarından düşer.

Yeni tablo yok, yeni sütun yok: `kind` beyaz listesine bir değer, iki görünümde üç düğme, bir
kesinlik satırı, sayımlara tek `WHERE`.

### 3.2 Beşinci madde: sütun altı dürüstlük satırı *(7,7 · yarım gün)*

Sözler sayfasında her sütunun altındaki "İşaretledin: N tutuldu · N vadesi geçti · N işaretsiz"
satırının yanına kaynak cümlesi: **"Bu sütun 52 görüşmeden çıkarıldı."** İki sütunun sayıları
belirgin biçimde ayrışıyorsa ikinci cümle: **"Bu fark çıkarımdan da olabilir — kendi cümlenden
söz çıkarmak daha zor."**

Bugün 3'e karşı 10 farkı eşiği geçiyor, yani uyarı bugün ekranda olurdu. Kullanıcının şu anda
gördüğü asimetrinin büyük kısmının sahte olduğunu söyleyen tek şey bu.

### 3.3 Ölçü ve geri alma

| Yüzey | Ölçü | Geri alma |
|---|---|---|
| S1 | Şerit açıldıktan sonraki 30 günde etrafı gösterilen sözlerde [Reddet] ve ✎ kullanımı. İlk kapı: kullanıcı bugünkü 13 sözü elden geçirsin; etraf satırı kararını 3'ten az sözde değiştirdiyse şerit yalnız "Tarihsiz" süzgecinde kalır. | Kullanıcı şeridi kapatır ve 30 gün açmazsa özellik geri alınır, günlüğe olumsuz sonuç yazılır. |
| S2 | Gruplanan kartlarda seçim yapılma oranı. | Grup kartı hiç kullanılmazsa görünüm katmanı geri alınır; veri etkilenmez. |
| S3 | Tarihsiz söz oranı. Bugün 12/13. Otuz gün sonra hâlâ ≥ %80 ise şerit işe yaramıyor. | Şerit kaldırılır, "Tarihsiz kalsın" işareti veri olarak kalır. |
| S4 | "Bu söz değil" işaretlenen satır sayısı ve kesinlik satırının doldurduğu payda. | Kullanıcı hiç işaretlemezse kesinlik satırı gizlenir, düğmeler kalır. |

### 3.4 Yapılmayacaklar (yeni; plan §7'ye eklenir)

Karşı çerçeve ajanı yedi madde saydı. Hepsinin gerekçesi aynı tek cümlede birleşiyor:
**sayının payı kullanıcının işaretleme alışkanlığı, paydası çıkarımın şansıdır; ikisinin bölümü
bir insan hakkında iddia gibi görünür ama değildir.**

1. **Sözüne sadakat puanı, rozeti ya da tek sayılık göstergesi.** Puan, yüzde, 5 üzerinden not,
   A/B/C, "sözünün eri" rozeti, çubuk göstergesi.
2. **Aylık karne ve "söz tutma" eğrisi.** İki ayrı sebep: (a) ayda ortalama birden az kendi
   sözü var, serinin her noktası 0 ya da 1 olur; (b) seri davranışı değil çıkarımın o aydaki
   kapsamasını ölçer — plandaki "bulgu yoğunluğu serisi" yasağının birebir kardeşi.
3. **Makinenin kendiliğinden "tutuldu" işaretlemesi.** `SuggestFulfilment` iki ortak kelimeye
   dayanan bir dizgi eşleşmesi; "gönderdim" ile "göndereceğim" önemli olan her kelimeyi
   paylaşır. Sözün geçmesi ile yapılması aynı şey değil.
4. **Utandıran ya da dürten bildirim ve dil.** "Sözünü 3 gündür tutmadın", vade sabahı
   bildirimi, büyüyen gün sayacı. Makine "tutmadı" diyemez; 13 sözün 12'sinde basılacak tarih
   zaten yok; utanç kullanıcıyı işaretlemekten kaçırır ve sayfa daha da boşalır.
5. **Karşı tarafı etiketlemek ya da kişileri söz davranışına göre sıralamak.** Sıralamayı
   belirleyen şey kişinin davranışı değil, kullanıcının o kişiyle kaç görüşme kaydettiği. Ceza
   yakınlığa verilir.
6. **"Daha az söz ver" koçluğu.** Sayılan şey söz verme davranışı değil, çıkarımın söz sandığı
   cümle sayısı. Ayrıca az söz vermek bir erdem değil.
7. **Kullanıcı görmeden sözün kaydına yazan otomatik işler.** Üç kılık, tek hata: modelin
   belirsiz yükümlülüğü yeniden yazması; çözülemeyen vadeye tahmini tarih koyması; sözün
   sessizce başka bir listeye kopyalanması.

---

## 4. Paket B — Arayüz bütünlük sözleşmesi

**Amaç.** "Bu üründe her sayfa şunları aynı biçimde yapar" diyen, **testle zorlanabilen** on iki
kural. Kullanıcının "UI'de bütünlük" isteğinin karşılığı bu: bir kural konuşmayla değil, kırmızı
bir testle korunur.

Aşağıdaki ihlal sayılarının hepsi kod taramasıyla ölçüldü.

| # | Kural | Bugünkü ihlal |
|---|---|---|
| K1 | **Kayıt.** Kullanıcının gördüğü her kavramın `SurfaceRegistry`'de tek satırı vardır: kimliği, zemini (⌂/≈/✎), çekirdek fiil kümesi, göründüğü her yüzey. | 8 kavramın 8'i kayıtsız |
| K2 | **Eksiksizlik.** Kayıtta "Tam" diyen yüzeyde, kayıtta adı geçen her fiil gerçek bir `ICommand` olarak vardır ve XAML'de bağlıdır. | Kayıt yokken ölçülemez |
| K3 | **Tek saat.** Bir alıntının anı yalnız `Timestamps.Clip(ms)` ile yazılır; hiçbir dosya dakikayı elle hesaplamaz. | 7 satır; **altısı bir saati aşan görüşmede saati düşürüyor** |
| K4 | **Kullanıcının kalemi.** Düzeltilmiş metin ve tarih her yüzeyde `EffectiveObligation`/`EffectiveDeadline` üzerinden okunur. | 4 yer |
| K5 | **Tek geri alma.** Hüküm yazan her görünüm modeli bir `UndoSlot` taşır; her şerit tek bir `UndoBar`. | 6 elde kopya şerit + 3 görünüm modeli; biri hiç bağlı değil |
| K6 | **Sayfa iskeleti.** Her `*Page.xaml` tam olarak bir `PageTitle`, bir `PageSubtitle` ve kökte `PadPage` taşır. | 5 yer |
| K7 | **Boş durum.** Liste gösteren her sayfa bir `EmptyState` taşır; tek satır gri yazı boş durum sayılmaz. | 4 sayfa |
| K8 | **Süzgecin kimliği.** Süzgeç seçimi dilden bağımsız bir enum ile taşınır; hiçbir `CommandParameter` metin değildir. | 8 parametre; **İngilizce arayüzde bu çipler hiç seçili görünmüyor** |
| K9 | **Sözlük fiili.** Anahtarın adındaki fiil değerindeki fiille aynıdır; aynı Türkçe metin aynı İngilizceyi taşır. | 5 anahtar adı çelişkisi + 16 ayrışmış çeviri |
| K10 | **Onay sözlükten konuşur.** `Dialogs.Confirm` ve `Dialogs.Say` çağrılarında gömülü metin yok, varsayılan düğmeler dahil. | 8 çağrı + 2 varsayılan; **ürünün en geri alınamaz anı İngilizce arayüzde Türkçe konuşuyor** |
| K11 | **Cetvel: tarih.** Kullanıcıya görünen her tarih `Core.Text.Dates`'teki dört adlı biçimden birini kullanır. | 40 çağrı, 14 ayrı biçim; **aynı görüşme satırı dört ekranda dört türlü** |
| K12 | **Cetvel: sözlük dışı metin.** Kullanıcıya görünen hiçbir metin `.cs` içinde gömülü değildir. | 422 dize (yalnız `App/ViewModels`) |

**Başarı eşiği.** K1–K10 sert sıfır: 63 nokta yeşil. K11 ve K12 **cetveldir**, sert sıfır değil:
sayılar 40 ve 422 olarak bir sabite çivilenir, testler `<=` ile bakar. Artış kırmızı, azalış
serbest. Sert sıfır yapılırsa sözleşme ilk gün kırmızı doğar ve kapatılır.

**Test maliyeti:** 12 metot, tek dosya. Onunun tekniği metin taraması, ikisininki yansıma.
Hiçbiri STA iş parçacığı, veritabanı ya da Python istemiyor.

### 4.1 Yargıçların iki düzeltmesi (uygulanacak)

Bu tasarım 5,0 aldı ve iki ölümcül kusuru vardı. İkisi de düzeltildi:

1. **K8, K10 ve K12'nin tespit ölçütü yanlıştı.** "Türkçe harf içeriyor mu" diye arıyorlardı,
   oysa kendi ihlal listelerindeki metinlerin çoğu saf ASCII: "Sil", "Evet", "Tamam", "Hepsi",
   "Kural", "Bu ay". Kurallar kendi saydıkları ihlallerin çoğunda yeşil kalırdı.
   **Düzeltme:** ölçüt "Türkçe harf içeriyor mu" değil, **"sözlükten mi geliyor"** olur —
   kullanıcıya ulaşan her dize ya bir `{loc:T anahtar}` ya bir `Localisation.T(...)` çağrısıdır,
   alfabesi ne olursa olsun. Tarama `.xaml`'i de kapsar.
2. **Zemin rozeti tek kaynaktan gelmeli.** `GroundBadge` yalnız `TodoEntryKind` için değil, aynı
   dört kavramı zaten gösteren `CalendarViewModel.cs:39` ve `ContactsViewModel.cs:72` için de
   tek kaynak olur; test bir kaynak taramasına dönüşür: "App katmanında satır rozeti için emoji
   sabiti yok."

### 4.2 Yol boyunca bulunan, sözleşmeden bağımsız kusurlar

Bu üç bulgu Yapılacaklar ekranının kendi kusurları; iptal edilen Sözlerim çipiyle ilgileri yok
ve tek başlarına düzeltilmeye değer.

- **Yapılacaklar'da kanıt zemini tamamen kayıp.** `action_item.quote` ve `quote_start_ms` şemada
  `NOT NULL` (`Schema.cs:341`) — her öneri çalınabilir bir alıntıya çıpalı — ama `TodoPage` bu
  iki sütunu hiç okumuyor. Kullanıcı öneriyi kaynağıyla görmek için satıra tıklayıp görüşme
  penceresine gitmek zorunda. Ürünün taşıyıcı kuralı bir ekranda tamamen düşmüş.
- **Yapılacağa kişi seçilemiyor.** `todo.contact_id` sütunu var ve `ListTodos` kişi adını
  getiriyor, ama `Add()` → `AddTodo(text, due)` çağrısı `contactId`/`callId` parametrelerini hiç
  geçmiyor (`TodoViewModel.cs:249`). Sütunu dolduran hiçbir arayüz yolu yok. Kullanıcının
  istediği "kişi seçimi bütünlüğü"nün düştüğü ilk yer burası.
- **Hatırlatma bir satır değil, bir sütun.** `board_card.remind_on`. Üç sonuç: `TodoEntry.Id`
  alanına `callId` yazılıyor (üç kaynağın kimlik uzayları karışık); bir görüşmeye ikinci
  hatırlatma kurulamıyor (`board_card.call_id` birincil anahtar); başlıksız bir kartın
  hatırlatması listede **metinsiz bir satır** olarak görünüyor.
- **Değişiklik yayınının iki abonesi var** (`ShellViewModel.cs:175/179`). Ekranlar birbirinden
  habersiz kalıyor.
- **Yapılacaklar'ın çipleri `WrapPanel` değil `StackPanel` içinde.** Plan §4.5 çipler için
  `WrapPanel` şart koşuyor; dördüncü bir çip dar pencerede taşar.

---

## 5. Sıra, efor, karar bekleyenler

| Sıra | Paket | İçerik | Efor | Şema |
|---|---|---|---|---|
| 1 | **S** | Sözün tabanı: S1 etraf, S2 tek cümle iki söz, S3 ne zamana, S4 bu söz değildi, + dürüstlük satırı | 2–3 gün | yok |
| 2 | **Ç** | Çevreler: v20, çip şeridi, Çevreler penceresi, dört ekran | 2–3 gün | v20 |
| 3 | **B** | Bütünlük sözleşmesi: K1–K10 sert sıfır, K11/K12 çivilenir | 3–4 gün | yok |
| ∥ | **Y** | Yapılacaklar'ın üç kusuru (§4.2) | 1 gün | yok |

**Sıra neden böyle.** S önce, çünkü hiçbir şey istemiyor: kullanıcı zaten baktığı kartta daha
fazlasını görüyor ve §1'in en sert bulgusunu (söz olmayan satırın tutuldu işaretlenmesi) doğrudan
kapatıyor. Ç ikinci, çünkü 19 tıklık bir kurulum istiyor ve kullanıcının bu üründe hiç
yapmadığı türden bir iş; S'in kazandırdığı güvenle gelmeli. B üçüncü, çünkü S ve Ç yeni yüzeyler
ekleyecek ve sözleşme onları da kapsamalı — sözleşmeyi önce yazmak, iki kez düzeltmek demek.

**Kullanıcının verdiği kararlar (6 Eylül 2026):**

- **Paket sırası onaylandı:** S → Ç → B, Y paralel. Yukarıdaki gerekçe kabul edildi.
- **Tohum çevreler: Aile ve İş.** Üçüncü tohum (Arkadaş) istenmedi; kullanıcı isterse
  Çevreler penceresinden kendisi ekler. `SeedDefaultCircles` iki satır eker, ikisi de
  silinebilir ve yeniden adlandırılabilir.

**Karar bekleyenler (kullanıcıya):**

1. **Sürüm etiketi.** Birinci turun A1'den I'ya kadarki paketleri hâlâ etiketsiz. Tek etiket mi,
   plandaki gibi paket paket mi?

**Bu belgeye girmeyen, kullanıcıda kalan işler** `docs/YAPILACAKLAR.md §25`'te duruyor: OpenAI
anahtarının döndürülmesi, VoiceTranscript'in dondurulması, dinleme gerektiren ölçüm kapıları.

## Ek A - Bitis denetiminin dogrulanan bulgulari (6 Eylul 2026)

Dokuz denetci birinci turun butun paketlerini planin kabul olcutlerine karsi sinadi; 45 ciddi
bulgunun her biri onu curutmekle gorevli ayri bir ajana verildi. 19'u curutuldu, 26'si
dogrulandi. Biri (ertelenen tarihsiz soz cokmesi) ayni gun duzeltildi; kalanlar asagida.

| # | Ciddiyet | Alan | Bulgu | Yer |
|---|---|---|---|---|
| 1 | eksik | a1-a2 | ReprocessWindow'un yerel motor listesinde "indirildi" rozeti hiç yazılmadı | `src/VoiceTranscript.App/Views/ReprocessWindow.xaml.cs:146` |
| 2 | eksik | a1-a2 | Şikâyet 2'nin kendi ölçüsü ("Yaptım" sonrası Yapılacaklar aynı anda güncel) hiçbir testle korunmuyor | `tests/VoiceTranscript.Tests/SuggestionsOnTheTodoPageTests.cs:108` |
| 3 | eksik | a1-a2 | Defter'de değişen rakam satırının [Yolculuk] düğmesi yok | `src/VoiceTranscript.App/Views/LedgerPage.xaml:284` |
| 4 | kirik | b-sozler | Tarihsiz bir söz ertelenince, o kişiyle yapılan her yeni görüşmenin çözümlemesi çöküyor | `src/VoiceTranscript.Core/Analysis/DeterministicChecks.cs:36` |
| 5 | tutarsizlik | b-sozler | "Açık" çipindeki sayı açık sözleri değil, tutulanlar ve reddedilenler dahil hepsini sayıyor | `src/VoiceTranscript.App/Views/PromisesPage.xaml:183` |
| 6 | eksik | b-sozler | ✎ düzeltmesi, sayfadaki tek geri alınamayan fiil — dönen PendingUndo çöpe atılıyor | `src/VoiceTranscript.App/Views/PromisesPage.xaml.cs:34` |
| 7 | tutarsizlik | b-sozler | Komut paleti ve Ctrl+? listesi İngilizce kipte de Türkçe kalıyor: ActionRegistry başlıkları sözlükte değil | `src/VoiceTranscript.App/Services/ActionRegistry.cs:57` |
| 8 | eksik | c-d-aynam | "İstemedim" işareti hiçbir yerde yok; altıncı kart makine sayımını "istemeden verilen bilgi" diye adlandırıyor | `src/VoiceTranscript.App/ViewModels/MirrorViewModel.cs:427` |
| 9 | eksik | c-d-aynam | "Bu o değil" habit_lexicon hariç tutma listesine yazmıyor; Anlar başlığında da Sözlük ✎ yok | `src/VoiceTranscript.App/ViewModels/MirrorViewModel.cs:759` |
| 10 | eksik | c-d-aynam | "Yalnızca yeniden çözümle" yolunda alışkanlıklar hiç sayılmıyor | `src/VoiceTranscript.App/Services/CallOrchestrator.cs:1386` |
| 11 | eksik | c-d-aynam | Paket D'nin "Core sert Türkçe → loc anahtarı" maddesi yapılmamış; Core'daki Türkçe doğrudan ekrana bağlanıyor | `src/VoiceTranscript.Core/Analysis/DeceptionAnalysis.cs:20` |
| 12 | tutarsizlik | c-d-aynam | Koçluk sayfasının açıklama yorumu eskimiş: Ses grubunda tek anahtar olduğunu ve Kişi grubunun var olmadığını söylüyor, ikisi de artık doğru değil | `src/VoiceTranscript.App/Views/SettingsWindow.xaml:1576` |
| 13 | eksik | e-kisi-karti | Kalıplar'ın üçüncü kaynağı yok: `consistency_note` olumlu gözlemleri ("Tutarlı kalanlar") ne saklanıyor ne sayılıyor | `src/VoiceTranscript.Core/Storage/Repository.cs:3210` |
| 14 | tutarsizlik | e-kisi-karti | Grup görüşmeleri Kalıplar'dan düşmüyor; "N grup görüşmesi sayılmadı" cümlesi Kalıplar yerine Gidişat'a yazılmış | `src/VoiceTranscript.App/ViewModels/ContactCardViewModel.cs:678` |
| 15 | eksik | e-kisi-karti | `LedgerContext` yardımcısı yazılmamış; `BuildPriorStatements`'ın 30 kontenjanı hâlâ tamamen iddialara gidiyor | `src/VoiceTranscript.Core/Analysis/ConsistencyAnalysis.cs:313` |
| 16 | tutarsizlik | e-kisi-karti | Kalıplar tür etiketleri plandaki tek kaynaktan gelmiyor; aynı `FlagKind` iki ekranda iki ayrı adla görünüyor | `src/VoiceTranscript.App/ViewModels/ContactCardViewModel.cs:758` |
| 17 | eksik | g-h-j-ses | MAD sıfıra düşünce devreye giren yedek ölçek hiçbir testle korunmuyor | `tests/VoiceTranscript.Tests/ProsodySeriesTests.cs:34` |
| 18 | kirik | i-modelin-gorusu | "Yetersiz kayıt" reddi ekrana hiç ulaşmıyor — [Yeniden sor] sessiz kalıyor | `src/VoiceTranscript.App/ViewModels/ContactCardViewModel.cs:1076` |
| 19 | tutarsizlik | i-modelin-gorusu | Okuma varken "henüz bir okuma istenmedi" yazısı imzayla aynı anda görünüyor | `src/VoiceTranscript.App/Views/ContactCardView.xaml:573` |
| 20 | eksik | i-modelin-gorusu | Ayar kartında planın istediği "yerel modelde çoğu kişi için çalışmaz" uyarısı yok | `src/VoiceTranscript.App/Views/SettingsWindow.xaml:1558` |
| 21 | eksik | i-modelin-gorusu | Arşiv birleştirmede kişi okumasının ücret satırları düşüyor (call_id NULL olan tek aşama) | `src/VoiceTranscript.Core/Storage/Repository.cs:1323` |
| 22 | eksik | urun-kurallari | Aynam'ın "rol yapma neden ölçülmüyor" gerekçesi, var olmayan bir [İstemedim] sayacını varmış gibi anlatıyor | `src/VoiceTranscript.Core/Resources/strings.tr.json:1010` |
| 23 | eksik | kayit-yerleri | Kullanım ekranının bütün sayı cümleleri sözlük dışı, sabit Türkçe | `src/VoiceTranscript.App/ViewModels/AiStatusViewModel.cs:436` |
| 24 | eksik | kayit-yerleri | LocalisationTests'in .cs taraması yardımcı metoda verilen tam anahtarları görmüyor | `src/VoiceTranscript.App/ViewModels/ContactCardViewModel.cs:1020` |
| 25 | eksik | yarim-kalanlar | "Tutuldu" hangi görüşmede tutulduğunu hâlâ yazmıyor: fulfilled_by_call_id her yolda null | `src/VoiceTranscript.App/ViewModels/PromisesViewModel.cs:311` |
| 26 | tutarsizlik | yarim-kalanlar | Sözler sayfası, A2'de "tek fiil kümesi" diye kaydedilen LedgerActions'ı atlıyor | `src/VoiceTranscript.App/ViewModels/PromisesViewModel.cs:459` |

**Duzeltildi:** 4 numara (`DeterministicChecks.cs:36`), ISLEM-GUNLUGU 2026-09-06.

**Siradaki kirik:** 18 numara - kisi kartinda "yetersiz kayit" reddi ekrana ulasmiyor,
[Yeniden sor] cogu kiside sessiz kaliyor. S paketiyle birlikte kapatilir.

Kalan 24 bulgu paket sahiplerine dagitilir: a1-a2 ve b-sozler bulgulari **Y** paketine,
c-d-aynam ve e-kisi-karti bulgulari kendi ekranlarinin bir sonraki dokunusuna, kayit-yerleri
ve urun-kurallari bulgulari **B** paketinin kural listesine.
