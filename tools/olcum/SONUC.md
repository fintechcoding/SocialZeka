# Bölme eşiği ölçümü — 6 görüşme, 39 dakika

Ölçülen görüşmeler (hepsi yerelde large-v3 ile yeniden çevrildi, kelime damgalarıyla):

| id | kişi | tarih / saat | süre |
|---|---|---|---|
| 24 | Uliana | 1 Eylül 2026, 15:14 | 12:29 |
| 14 | Serdal | 30 Ağustos 2026, 17:54 | 08:39 |
| 38 | Bozkurt | 2 Eylül 2026, 13:51 | 07:02 |
| 16 | Sinan | 30 Ağustos 2026, 19:01 | 05:18 |
| 60 | Uliana | 3 Eylül 2026, 21:51 | 03:20 |
| 17 | Avukat Polonya | 31 Ağustos 2026, 12:52 | 02:44 |

Uygulamanın kendi `resegment_on_gaps` kodu çağrıldı; değişen tek şey eşik.

## Sayılar

| eşik | bölünmüş cümle | yutulan cevap | satır | ort. satır sn |
|---|---|---|---|---|
| 1,0 | 47 | 57 | 496 | 3,9 |
| **1,5 (bugünkü)** | **34** | **51** | **423** | **4,8** |
| 2,0 | 30 | 46 | 389 | 5,4 |
| 2,5 | 21 | 47 | 364 | 5,9 |
| 3,0 | 17 | 43 | 347 | 6,4 |
| 4,0 | 12 | 44 | 334 | 6,7 |

Sayılara bakıp 3,0 demek isterdim: bölünme yarıya iniyor ve "yutulan cevap"
artmıyor. **Ama sayı yanlış şeyi ölçüyor.**

## Metin ne diyor

Aynı görüşmenin iki eşikteki hâli okununca ödünleşme görünüyor.

**#38 Bozkurt — eşik 1,5 (doğru sıralama):**

```
karşı  33.77- 36.43  Olur. Olur olur abi. Söyleriz biz de.
ben    34.54- 37.02  UltraPay diye bir yer varmış. UltraPay'i sen duydun mu abi?
karşı  39.01- 40.39  Ultra Pay mi varmış?
ben    39.14- 42.56  UltraPay diye bir yeri biliyor musun sen?
```

**#38 — eşik 3,0 (soru, kendisini doğuran cümlenin ÜSTÜNE çıkıyor):**

```
karşı  33.77- 40.39  Olur. Olur olur abi. Söyleriz biz de. Ultra Pay mi varmış?
ben    34.54- 37.02  UltraPay diye bir yer varmış. UltraPay'i sen duydun mu abi?
ben    39.14- 42.56  UltraPay diye bir yeri biliyor musun sen?
```

Karşı tarafın iki ayrı sırası tek satıra yapışıyor; araya girmesi gereken cevap
o satırın altında kalıyor. Konuşma tersten okunuyor.

**#60 Uliana** aynı örüntü:

```
eşik 1,5   karşı 4.32- 8.32  oturuyorum şimdi yatacağım sen
           ben   7.64- 9.92  Ne yaptın şimdi?
           karşı 9.89-17.47  İşte her şey ne konuştuk yaptık normal

eşik 3,0   karşı 4.32-17.47  oturuyorum şimdi yatacağım sen İşte her şey ne konuştuk yaptık normal
           ben   7.64- 9.92  Ne yaptın şimdi?
```

## Sonuç: eşik doğru, değiştirilmeyecek

"Bölünmüş cümle" sayacı gerçek bir kusuru sayıyor, ama onu düzeltmenin bedeli
daha büyük bir kusur: **gerçek bir sıra sınırının üstünden birleştirmek.**
"Yutulan cevap" sayacı bunu yeterince yakalamıyordu — yalnız tam içine alınan
satırları sayıyor, sıralamanın bozulmasını saymıyor.

1,5 sn, altı görüşmede de doğru yerde duruyor. Modülün belgesindeki gerekçe
("gerçek sıra sınırları birkaç saniyedir") ölçümle doğrulandı.

## Peki #60'ın başındaki bölünme neden oluyor

```
ben   0.00-0.94  Alo, ne
karşı 1.62-2.02  da
ben   2.99-3.51  yapıyorsun canım?
```

Bu eşik sorunu değil. Yerel motor, tek bir kısa cümlenin İÇİNE 2,05 saniyelik bir
kelime boşluğu koyuyor ("ne" 0,94'te bitiyor, "yapıyorsun" 2,99'da başlıyor) —
oysa bulut aynı yerde tek kelimeye 2,7 saniye veriyor ve boşluk oluşmuyor.
Yani kaba kelime damgası. Eşiği büyütmek bunu, gerçek sıraları birleştirme
pahasına örter.
