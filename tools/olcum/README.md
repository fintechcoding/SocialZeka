# Ölçüm tezgâhı

Motorları, eşikleri ve ileride Hume / ElevenLabs / prosody deneylerini **aynı gerçek görüşmeler**
üzerinde karşılaştırmak için kullanılan betikler. Başka bir oturumun geçici klasöründe duruyordu;
oradaki her karar (`SONUC.md`) tekrar edilemez olurdu. Buraya WAV'sız alındı.

Kural: bu klasöre **ses dosyası, döküm çıktısı ya da API anahtarı girmez**. Ses arşivden
`arsiv-sesi-acma` becerisiyle çözülür (`hazirla.py` listeyi hazırlar), çıktılar `.jsonl` olarak
bu klasörün yanında geçici bir yerde tutulur; `.gitignore` bunları dışarıda bırakır.

| Betik | Ne yapar |
|---|---|
| `hazirla.py` | Ölçülecek görüşmelerin arşiv dosyalarını (`SocialZeka.Data\voicetranscript.db`) çözücüye verilecek liste hâline getirir (`args.txt`). |
| `istekler.py`, `bulut-istekler.py` | Aynı WAV'lar için yerel ve bulut worker isteklerini üretir. Anahtar `settings.json`'dan okunur, dosyaya yazılmaz. |
| `karsilastir.py` | Kanal başına kapsama ve kelime sayısı tablosu. |
| `sayfa.py`, `sayfa-motorlar.py`, `sayfa-dort.py` | Bir görüşmenin iki/üç/dört motorlu sohbet ekranını yan yana koyan HTML sayfası. Yeni bir motor (ElevenLabs, Hume yoğunluk serisi, prosody z-skoru) beşinci sütun olarak buraya eklenir. |
| `esik.py` | `resegment_on_gaps` eşiği ölçümü (sonucu `SONUC.md`). |
| `vad_olcum.py` | VAD açık/kapalı kapsama karşılaştırması (EK-4). |
| `sive-onolcum.py` | Şive sayacı yazılmalı mı: kendi satırlarında işaret araması + 40 dinleme örneği (§6.1). |
| `aynam-kesinlik.py` | Aynam sayaçlarının kesinliği: `verdict` tablosundan tür ve motor başına doğru/dinlenmiş (§6.2). |

Ölçüm sonuçları `docs/ISLEM-GUNLUGU.md`'ye sayılarıyla yazılır; tutmayan özellik oradaki olumsuz
sonuçla geri alınır. Deney protokolleri: `docs/PLAN-SOSYALZEKA.md` §6.
