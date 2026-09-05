# SocialZeka — VoiceTranscript'ten çatallanan sosyal zekâ koçu: özellik ve arayüz planı

*5 Eylül 2026 · HEAD `694555c` · Şema sürümü bugün 14 (`Schema.cs:20`) · Son sürüm etiketi `v2.9.21` (belgelerdeki "v2.2.0 sıradaki" satırı eskimiş).*

**Kullanıcı kararları (5 Eylül):** (1) Kişi kartındaki opt-in görüş paneli izlenim yazabilir, iki sınır kalır (psikolojik durum/duygu verilmez; "argümanlar" istenmez). (2) **SocialZeka ayrı repo olur: VoiceTranscript çatallanır**, geliştirme orada sürer (§8.3, Paket R0). (3) **GSM / Phone Link kapsam dışı** ("gsm e phone linke gerek yok" — "ikisine de gerek yok" diye okundu; yanlışsa düzeltilir). (4) Apple Watch ertelendi (Mac/ücretli program yok).

Bu plan; yedi salt-okunur keşif ajanı, sekiz şikâyetin koda karşı doğrulaması, mevcut planın (`splendid-hopping-emerson.md`) 66 iddiasının satır satır denetimi, üç bağımsız tasarım (ürün/arayüz · ölçüm/dürüstlük · mimari/sıra), her tasarıma üç mercekten eleştiri, ayrı bir plan eleştirmeni ve bir fizibilite araştırmasından (Phone Link, Apple Watch, ayrı repo) sentezlendi. Aşağıdaki her dosya:satır atfı bugünkü çalışma ağacında en az bir ajan tarafından açılıp okundu; eleştirmenlerin çürüttüğü maddeler plana girmedi.

---

## 0. Context

Kullanıcı ürünü bir **sosyal zekâ koçuna** dönüştürmek istiyor: (1) kendi konuşma alışkanlıklarını görüp düzeltmek, (2) iki yönlü söz takibi, (3) karşı tarafta baskı/manipülasyon birikimi, (4) ses tonu / duygu durumu, (5) kişi profili ve ilişkinin gidişatı — koçluk üç biçimde: görüşme sonrası rapor, zaman içinde eğri, konuşurken canlı uyarı. İki karar verdi: ücretli duygu servisi **ölçerek** denensin; kişi bazında **kanıt varsayılan biriksin, modelin yorumu opt-in** olsun. Ayrıca sekiz gerçek arayüz şikâyeti bildirdi ve GSM görüşmeleri, Apple Watch ve "SocialZeka" adlı ayrı repo fikrini sordu.

Kullanıcı ayrıca bu işin **SocialZeka** adlı yeni bir repoda, VoiceTranscript'ten çatallanarak yürümesine karar verdi (Paket R0); GSM/Phone Link ve Apple Watch kapsam dışı.

Depo bunun büyük kısmına hazır: manipülasyon tespiti üç yerde zaten var (`ScamPatterns`, tutarlılık `baski`, opt-in değerlendirme), söz tablosu iki yönü ve tutulma durumunu zaten taşıyor (`commitment.by_me`, `status`, `fulfilled_by_call_id` — `Schema.cs:231-247`), kişiler arası açık söz sorguları var (`Repository.AllOpenCommitments :1913`, `OverdueCommitments :1881`), kelime güveni worker'dan geliyor ama C#'ta düşüyor (`__main__.py:160` → `CallOrchestrator.cs:1998-1999`). Eksik olan tespit değil: **yüzey, birikim ve ölçü.**

Bu planın bağlayıcı ilkeleri, deponun kendi kuralları: her iddia birebir alıntı + çalınabilir zaman damgası; doğrulanamayan alıntı kodda düşer; sayısal güven/yalan skoru yok; kanıt zemini ile modelin görüşü zemini aynı kartın içine giremez; kullanıcı verisine boru hattı yazmaz; her özellik ölçüsü, eşiği ve geri alma koşuluyla gelir; tutmayan özellik `docs/ISLEM-GUNLUGU.md`'ye olumsuz sonuç olarak yazılıp geri alınır.

---

## 1. Mevcut plana ikinci görüş

### 1.1 Katıldıklarım (gerekçesiyle)

| Plan maddesi | Neden doğru |
|---|---|
| Manipülasyon için yeni tespit değil birikim gerekiyor | Sekiz taktik `DeceptionPrompt.cs:34-39`, alıntısız taktik elenir `DeceptionAnalysis.cs:142-146`, kanıtsız düzey düşürülür `:165`; baskı → `PressureTactic` `ConsistencyAnalysis.cs:171`; `ScamPatterns.Scan` `IsHeuristic=true` `ScamPatterns.cs:134`. |
| Ton için puan yok, ölçülen değişim var | `YAPILACAKLAR.md:852-861` §10.3; `DeceptionPrompt.cs:48-52`; `ReadingPrompt.cs:108-112`; dış araştırma (SER Türkçe yok, ses-stres yalan tespiti şans düzeyi) bu kuralı güçlendiriyor. **Katılıyorum.** |
| Deterministik alışkanlık sayımı ilk sevk paketi | `DeterministicChecks.cs:6-17` kalıbı; `YAPILACAKLAR.md:832-844` §10.1 tablosu birebir bunu istiyor; arşive geriye dönük uygulanır. |
| Prosody numpy-only, CPU'da, yorum yok | `worker/requirements.txt:24-35` torch/onnxruntime-gpu yasağı; `speaker.py` el yazması ön ucun çalışan örneği. |
| Hume özellik değil kapılı deney | `PRODUCT.md:160` bulut varsayılan değil; `ISLEM-GUNLUGU.md:1923-1949` ölçüyle geri alma emsali. |
| Kişi profili opt-in, numaralı alıntı + atıf doğrulaması, tam metin birleştirme yok | `ConsistencyAnalysis.cs:52-60` parçalama yasağı; `ArchiveQuestions.cs:314-335` atıf doğrulaması. |
| `contact_profile`'a makine yazmaz; `deception_note` çıkmaz sokak | `Schema.cs:439-452`, `:353-364`. |

### 1.2 Yanlış ya da eskimiş olanlar

| Plan iddiası | Gerçek |
|---|---|
| "Hepsi opt-in, hepsi alıntı doğrulamalı" | `ScamPatterns.Scan` ve deterministik denetimler **her çözümlemede koşulsuz** koşar (`AnalysisPipeline.cs:222, 230-242`). Opt-in olan yalnız `DeceptionEnabled` (`AppSettings.cs:510`) ve `ConsistencyAutomatically` (`:470`). Sonuç: kişi bazında zaten biriken, LLM istemeyen bayraklar var (ScamPattern, MovedDeadline, ChangedAmount, EvasionRate) — plan bunları Kalıplar listesine almamış. |
| `Repository.cs:1264` "silme listesi" | O liste `MergeArchive`'ın **kopyalama** listesi (`:1262-1267`). Silme `DELETE FROM call` + `ON DELETE CASCADE` (`:3996-4014`). Yeni tablo silme listesi istemez; MergeArchive listesine girmezse yedek birleştirmede sessizce düşer. |
| `DeterministicChecks.Overdue (:62)` | `:62` `MovedDeadlines`; vadesi geçenler `OverdueCommitments :27-54`. Plan yazıldığında da yanlıştı. |
| "Sözler için eksik olan tek şey bir yüzey; `fulfilled_at` küçük sütun" | Yüzeyin yarısı var: Takvim `OwnPromise/TheirPromise` (`CalendarViewModel.cs:12-28`), Defter "Verdiğim sözler" çipi + Tutuldu (`LedgerViewModel.cs:331-344` → `FulfilCommitment :1953`), `AllOpenCommitments :1913`. Asıl eksikler: geri alma yok (`Dismiss/Fulfil` tek yönlü), `fulfilled_by_call_id` hep null, zaman damgası yok, düzenleme yok, üç defter yüzeyi üç ayrı fiil kümesi, ve `TurkishDates.TryResolve` görüşme tarihi almıyor (aşağıda). |
| `_frame_levels` "kare başına RMS" | Ortalama mutlak genlik (`chunking.py:54, 87`), saf Python döngü — 20 dk kanalda ~19 M yineleme. Prosody bunun üstüne oturmaz; RMS `speaker.py:190-191`, çerçeveleme `speaker.py:155-159`. |
| ElevenLabs `audio_event` için "`_to_segments`'e küçük ekleme" | İstek `tag_audio_events: "false"` gönderiyor (`cloud_providers.py:64`); bayrak açılmadan olay hiç gelmez. `diarize: "false"` (`:65`). |
| "Motorlar kelime başına güven döndürüyor" → tek eşik 0,6 | Ölçekler farklı: faster-whisper 0..1, Deepgram 0..1, **ElevenLabs `logprob` ≤ 0 exp alınmadan aynı alana** (`cloud_providers.py:87`, `_prob :150-154`), **OpenAI/ex5 `None`** (`cloud_engine.py:714`), whisper.cpp kelime vermiyor. Tek eşik ElevenLabs'ta her kelimeyi "belirsiz" yapar, OpenAI'de hiç çalışmaz. `Models.cs:142-143`'ün gerekçesi ("motorlar arası anlamı farklı") cevaplanmamış. |
| Aşama 5: "canlı küfür GPU'yu Whisper'la paylaşmak zorunda" | Görüşme sırasında GPU çoğu zaman boş (LLM `UnloadWhenDone`, `AnalysisPipeline.cs:264-265`); asıl engeller: worker iş başına süreç (~2 sn spawn, `SpeakerIdentifier.cs:25-26`) ve **küçük modelin küfür sözlüğünde doğruluğunun hiç ölçülmemiş olması**. Sonuç doğru, gerekçe yanlış; ölçüsü çevrimdışı alınabilir. |
| Eskimiş satırlar | `GetOpenCommitments :2505→:2557`; `deception_note :339→:353-364`; `contact_profile :425→:439-452`; EK'teki `RestoreTranscriptVersion :1577`, `LoadQuality :566`, `PixelsPerSecond(durationMs, viewportHeight)` — üçü de 76d3564 ve 694555c ile değişti. |

### 1.3 Fazla temkinli

- **Aşama 0'ı önkoşul yapmak.** Kelime güveni yalnız küfür/dolgu sayacına lazım; konuşma payı, kesme, sözler, sözlük dışı her şey onsuz sevk edilir. Küfür için de hazır bir kaba kapı var: segment düzeyi `LowConfidence` (`Models.cs:201-206`, `merge.py:62-79`). Kelime güveni **ölçek normalizasyonuyla birlikte** gelir, ilk paket değil.
- **"1 ve 2 birkaç görüşme biriktirdikten sonra."** `commitment/claim/flag` tabloları bugün dolu; deterministik sayım ~200 görüşmelik arşive geriye dönük uygulanır (`YAPILACAKLAR.md:843-844`). Eğri ilk gün dolu gelir.
- **F0 için görüşmeler arası yasak.** `speaker.py:37-40` bulgusu konuşmacı gömmesinin kanal uyumsuzluğuna dayanamadığını ölçüyor; dBFS için de geçerli (kazanç donanıma bağlı). **Temel frekans donanıma bağlı değil**; kendi F0 medyanı görüşmeler arası karşılaştırılabilir tek prozodi ölçüsü — ama önce kararlılığı ölçülerek (§6.3).
- **Canlı uyarıyı en sona atıp "en zor" demek.** Alarm en sona, doğru; ama kelime istemeyen **sessiz ölçer** (son 60 sn konuşma payı, kendi ses düzeyi) bugünkü altyapıyla ucuz ve §10.3'ün izin verdiği tek canlı biçim.

### 1.4 Fazla iddialı

- **Küfür sözlüğü "katlamalı eşleşmeyle biter" sanmak.** Alt-dize yanlış pozitif üretir ("sik"→"klasik", "am"→"aman"); tam token Türkçe ekleri kaçırır ("siktir/siktirdim"). Gerekli: token sınırı + gövde + izinli ek listesi; **yankı** (`SuspectedEcho`, `CallOrchestrator.cs:1992`) segmentleri sayımdan dışlanmalı; ve altın etiket (dinlenerek sayılmış küfür).
- **Şive "işareti".** `-yom/-yon`, "valla" İstanbul konuşma dilinde de var; Whisper çıktıyı yazı diline **normalize eder** — sayılan şey konuşmacının şivesi değil motorun o gün ne kadar normalize ettiğidir. Özellik yazılmadan önce **tek sorguluk ön-ölçüm** (§6.1).
- **"Bu ay görüşme başına 6,1 küfür."** Yanlış payda; görüşme 14 sn ile 4 saat arasında. Doğru payda: kullanıcının kendi konuşma dakikası / 100 kelimesi.
- **"Verilen bilgi" listesi.** IBAN/TC/telefon değerini tabloya yazmak yeni bir hassas depo yaratır (yedek şifresiz olabilir). Yalnız **tür + zaman damgası** saklanır, değer saklanmaz. Özel ad tespiti (STT büyük harf güvenilmez) yok.
- **"Gerginlik eğrisi" adı** ürün kuralını adıyla ihlal ediyor. Ölçülen şey ses düzeyi (dBFS), perde (Hz), hız; adı da bu.
- **4c girdi kurgusu belirsiz.** `ArchiveQuestions.Find` anahtar kelime ister; `BuildPriorStatements` 30 kontenjanını iddialara veriyor, 30+ iddialı kişide söz hiç gitmiyor (`ConsistencyAnalysis.cs:297, 313`); `contact_profile_note` PK `contact_id` ise "ilişkinin gidişatı" için tarih kalmaz.
- **Hume "5 görüşme".** ~25 altın an; iki dedektörü ayırt etmek için güç ≈ %45. Karar değil deneme (§6.5).
- **"Rol yapamama" ve "fazla bilgi verme"** planda yok; ölçülemez olduğu da söylenmemiş.

### 1.5 Kaçırdıkları

1. **Sekiz şikâyet** planda hiç yok; üçü (bayatlık, defter fiilleri, aksiyon↔yapılacaklar) yeni özelliklerin oturacağı yüzeyler ve ön koşul.
2. **Arayüz** hiç düşünülmemiş: bugün Ctrl numaraları ray sırasını izlemiyor (`MainWindow.xaml:25-33` vs `:195-291`), palette Yapılacaklar yok (`ActionRegistry.cs:24-49`), yeni sayfa 8 dosyaya dokunuyor.
3. **`TurkishDates.TryResolve` görüşme tarihi almıyor** — `AnalysisPipeline.cs:440` ve `ActionExtraction.cs:162` `spokenOn` vermiyor, `TurkishDates.cs:68` `DateTime.Now`'a düşüyor; sınıf yorumu `:14-15` tersini vaat ediyor. Eski görüşme yeniden çözümlenince "cuma" bugüne göre çözülür → **sahte vadesi-geçmiş söz**. Sözler ekranından önce kapatılmalı. Planın gerçek Aşama 0'ı bu.
4. **`baski_isaretleri` istenip çöpe atılıyor**: `ExtractionPrompt.cs:119-133` zorunlu alan, `AnalysisPipeline.Absorb :405-486` okumuyor. `sorular` da yalnız bellekte (`:478-485`).
5. **Türev tabloların bayatlığı**: `reading_note/deception_note/consistency_note/action_item` hangi dökümden üretildiğini bilmiyor; yeniden dökümde `ReplaceSegments + SaveTranscriptVersion` dışında hiçbir şey olmuyor (`CallOrchestrator.cs:1981-2028`); `DeleteReading :2377` ve `DeleteDeception :2412` tanımlı ama **çağıranı yok**. Planın `speech_habit`/`prosody` tabloları `call_id` PK ile aynı hatayı tekrarlar.
6. **Sesten kişi tanıma hattı** (`SpeakerIdentifier.cs:93-134` ikinci-abone kalıbı, `contact_voice`) canlı ölçer ve prosody için hazır altyapı; plan yalnız `speaker.py`'yi örnek diye anıyor.
7. **`MergeArchive` kopya listesi, `LedgerTables` (`Repository.cs:416`), `MigrationTests` bloğu, `ActionRegistry`, `WindowSmokeTests.Build`** — her yeni tablo/sayfa için zorunlu kayıt yerleri.
8. **Hume protokolü**: altın etiket tanımsız, körleme yok, karar eşiği "belirgin biçimde daha iyi" ölçüsüz.
9. **Ölçüm hattı kalıcı değil**: dört motorlu karşılaştırma (`sayfa-dort.py`, 7 gerçek görüşmenin çözülmüş WAV'ları) başka bir oturumun Temp scratchpad'inde (`…/08715993…/scratchpad/olcum/`).
10. **Güvenlik**: aynı scratchpad'in `kisa/oai-*.json` istek dosyaları `model_ref` içinde **düz metin OpenAI anahtarı** taşıyor.

### 1.6 EK bölümündeki beş düzeltmenin durumu

| # | Düzeltme | Durum |
|---|---|---|
| 1 | `transcript_version_id` | **Yapıldı** — 76d3564 (`Schema.cs:95`, `Repository.cs:1613-1633` kopya yazmıyor, `CallWindowViewModel.cs:584`). |
| 2 | Kısa görüşmede OpenAI ölçümü | **Kısmen** — ölçüm `scratchpad/kisa/`'da koşulmuş (#57/#58/#61; #56 yok), sonuç: **sistematik değil** (#57/#58'de OpenAI bir kanalda iyi, ötekinde kötü). Karar/kod/günlük yok. |
| 3 | Zaman çizgisi yoğunluğu | **Yapıldı** — 694555c (`TimelineLayout.cs:109-114`, `TimelinePanel.cs:102-106`, dört yeni test). |
| 4 | VAD ilk sözü düşürüyor | **Kısmen** — `vadsiz-*.json` ölçülmüş: #61'de "Alo" geri geliyor (0,383→0,473) ama #57'de far kanalı **sıfıra** iniyor → "VAD'i kapat" elendi; `min_speech_duration_ms` hiç denenmedi (`faster_whisper_engine.py:52-59` `vad_parameters` geçirmiyor). |
| 5 | Varsayılan bulut servisi seçimi | **Yapılmadı** — `CloudAsrModelId` duruyor (`AppSettings.cs:389`), `strings.tr.json:659` hâlâ "Sırayla denenir". Bu plana **girmez**; ayrı iş emri (§9). |

### 1.7 Sıra farkı (özet)

Mevcut plan: 0 kelime güveni → 1 alışkanlıklar → 2 prosody → 3 ElevenLabs/Hume → 4 sözler+kişi → 5 canlı. Bu plan: **şikâyetler + bayatlık + tarih düzeltmesi → Sözler → Aynam (kelime güveni onunla) → Kişi kartı (kanıt) → Ses (ölçüm kapılı) → Modelin görüşü (opt-in)**; Hume, canlı küfür ve GSM ölçümleri koddan bağımsız paralel turlar. Gerekçe: sözler model istemez ve kullanıcının ikinci başlığı; şikâyetlerin üçü yeni özelliklerin ön koşulu; kişi kartı bugünkü arşivle çalışır.

---

## 2. Ürün kuralları — ne kalıyor, ne nasıl gevşiyor

| Kural | Karar | Gerekçe |
|---|---|---|
| "Ses tonu hakkında hiçbir iddia yok" (`DeceptionPrompt.cs:48`, `ReadingPrompt.cs:108`) | **Kalır, mutlak.** Prosody eklense bile metin okuyan istemlere ses verisi verilmez. | Model gerçekten duymuyor; SER Türkçede doğrulanmadı. |
| Sayısal skor/yüzde yok (`DeceptionPrompt.cs:52`, `PRODUCT.md:155`) | **Kalır, mutlak — kişi düzeyinde de.** | Taban oranı hesabı görüşmeler boyunca daha kötüleşir; hata birikir. |
| Alıntı doğrulama + kanıtsız düzey düşürme (`DeceptionAnalysis.cs:142-165`) | **Kalır, mutlak.** Yeni her LLM yüzeyi aynı zinciri kullanır. | §22.4 "DEĞİŞMEYEN YASA". |
| §10.3 "ton için puan değil ölçülen değişim" | **Katılıyorum; bir ek:** "değişim" de bir ölçüdür ve kesinliği ölçülür — z-skor zirvesinin kullanıcıya bir şey söyleyip söylemediği ölçülmeden şerit kalıcı olmaz. | |
| `deception_note` çıkmaz sokak (`Schema.cs:353-356`, §22.4 "başka tabloya sızmaz") | **Düzey ve değerlendirme paragrafı için kalır.** Doğrulanmış taktik **alıntısı** ayrı `tactic_evidence` tablosuna kopyalanır (§5 Paket E) — `flag` tablosuna değil (dokuz tüketici ve iki dışa aktarım onu kanıt sayar), JSON join'e değil. Bu bir **kural değişikliğidir**: `Schema.cs` yorumu, `YOLHARITASI` demir kuralı ve `ISLEM-GUNLUGU` güncellenir; kullanıcının 2. kararı ("doğrulanmış alıntı biriksin") bunun onayı sayılır. | Kopyalanan şey model etiketi + makine doğrulamış alıntı — `ConsistencyAnalysis`'in flag'e yazdığıyla aynı sınıf; ama DeceptionPrompt şüphe arayan görev tanımı taşıdığı için satırlar "model etiketi" rozetiyle, hiçbir isteme geri beslenmeden, ayrı kaynak süzgecinde. |
| `contact_profile` USER-ENTERED ONLY (`Schema.cs:441`) | **Kalır.** Kişi analizi `contact_reading` ayrı tablo. | |
| "Hüküm yasağı" (§22.4 ile opt-in'e çevrilmişti) | Opt-in **Kişi kartı: modelin görüşü** paneli `ReadingPrompt`'un sınırlarını kullanır: izlenim dili, simetri, `baska_okuma` zorunlu, dayanaksız madde düşer. "Kişilik/karakter/güçlü-zayıf yan **izlenimi**" bu panelde verilebilir (kullanıcı kararı, §10). "Psikolojik durum / duygu durumu" **verilmez** ve panel bunu yazılı söyler; "kullanabileceğin argümanlar" istemi yazılmaz — karşılığı kanıt zeminindeki "Elindeki kayıtlar" listesi (§4.4). | `YOLHARITASI.md:39-41` "yorum TAM SERBEST, dürüst paketlemeyle"; `AppSettings.cs:483-485` "bir kişide kusur bulmakla görevli araç gözlemci olmaktan çıkar". |
| Kullanıcı verisine boru hattı yazmaz (`YOLHARITASI.md:99`) | **Kalır.** Yeni kullanıcı tabloları: `verdict` (kulak teyidi), `habit_lexicon`, `call_intent`, `commitment` kullanıcı sütunları (`user_deadline_date`, `user_obligation`, `edited_at`). `ClearAnalysis` bunlara dokunmaz. | |
| Kayıt/tespit yolunda iş yok (§1.4 zincir A) | **Kalır.** Canlı ölçer `PacketReady`'ye üçüncü abone, yalnız kopyalar; `LevelChanged` (`CallRecorder.cs:183`, kilit altında) ve `Tick()` dokunulmaz. | |

---

## 3. Bilgi mimarisi

### 3.1 Üç zemin, üç yer

- **Kanıt** ⌂ (alıntı + ms; deterministik ya da doğrulanmış): Defter, Sözler, Aynam, Kişi kartı üst yarısı, zaman çizgisi işaretleri. Varsayılan açık.
- **Modelin görüşü** ≈ (öznel, imzalı, ayarla kapanır): Okuma, Değerlendirme, Kişi kartı alt yarısı. Ayrı zemin + şapka + model/tarih imzası.
- **Kullanıcının yazdıkları** ✎: Notlar, Hakkında, Niyet kartı, sözlükler, kulak teyitleri. Makine yazmaz.

Bir ekran üçünü yan yana koyabilir; **aynı kartın içine koyamaz.** Her sayının yanında, model etiketi taşıyorsa, "M/N dinlendi" teyit oranı durur.

### 3.2 Ana pencere rayı (Windows 11 Ayarlar mantığı; Ctrl numaraları ray sırasını izler)

```
 ┌ Durum kartı: ● Kayıt bekleniyor | Kaydediliyor 12:04   mic ▮▮▮▯  hop ▮▮▯▯  [Kaydı başlat]
 ├─ GÖRÜŞMELER
 │  Ctrl+1  Genel bakış
 │  Ctrl+2  Görüşmeler
 │  Ctrl+3  Kişiler
 ├─ HAFIZA
 │  Ctrl+4  Defter          (n dikkat)      ← söz çipleri ÇIKTI; değişenler + işaretler + reddedilenler
 │  Ctrl+5  Sözler          (n vadesi geçen) ← YENİ, iki yönlü, bütün kişiler
 │  Ctrl+6  Takvim
 │  Ctrl+7  Yapılacaklar
 ├─ KOÇLUK
 │  Ctrl+8  Aynam                            ← YENİ, kendi alışkanlıkların + eğri
 ├─ BUL
 │  Ctrl+F  Arama
 │  Ctrl+9  Sor
 ├──────────────────────────────
 │  Ctrl+0  Durum   ·  🔔 Bildirimler (n)  ·  ⚙ Ayarlar
```

- **Tek kaynak:** `ActionRegistry.AppAction`'a `Key` alanı; `MainWindow` `InputBindings`, komut paleti ve Ctrl+? örtüsü aynı listeden kurulur (bugün `MainWindow.xaml:25-33` sabit, örtü `MainWindow.xaml.cs:335-343` sabit ve eksik, `ActionRegistry.cs:24-49`'da Yapılacaklar yok). `ActionRegistryTests`: her `ShellPage` için bir eylem.
- **Durum ray öğesi kalır** (İşlemler/Yapay zekâ sekmelerine tek görünür yol; şikâyet 3'ün düzeltmesi buradan geçiyor, `MainWindow.xaml.cs:488-494`).
- Kısayol değişimi bilinçli ve `ISLEM-GUNLUGU`'ne yazılır. Ctrl+4 Arama çiftliği (`MainWindow.xaml:28,34`) kalkar; Ctrl+F tek.
- Gruplar `Hairline` (`Components.xaml:50`) + `Caption` başlık; `NavButton` stili değişmez.

### 3.3 Pencereler

| Pencere | Bugün | Yeni |
|---|---|---|
| CallWindow | Görüşme · Defter · Aksiyonlar · Tutarlılık · [Okuma] · Sor · Notlar | Görüşme · Defter · **Aynam** · Aksiyonlar · Tutarlılık · [Okuma] · Sor · Notlar. Kalite + konuşma payı **tek şerit**, dikkat şeridi tek satır, oynatıcı katlanır; türev sekmelerde bayatlık InfoBar'ı. Okuma **ayrı sekme kalır** (`CallWindow.xaml:975-979` gerekçesi). |
| ContactWindow | Akış · Görüşmeler · Ara · Defter · Notlar · Hakkında | Akış · **Kişi kartı** · Görüşmeler · Ara · Notlar · Hakkında. Salt okunur Defter sekmesi (`ContactWindow.xaml:528-631`) Kişi kartı içinde erir: Gidişat / Sözler / Kalıplar / Rakam yolculuğu / [Modelin görüşü]. |
| ContactsPage (kabuk) | master/detail; Konuşma/Özet/Aksiyonlar + Defter + Açık sözler; oynatıcı | **Dokunulmaz** (kullanıcı "basitleştirme" dedi; `PLAN-UI.md:198`; `MainWindow.xaml.cs:632-656` klavye bağları). Yalnız **"Kişi kartı" sekmesi eklenir** — ContactWindow ile aynı `UserControl`; kullanıcının "kişiye tıklayınca" isteği kabukta da karşılanır. |
| SettingsWindow | 7 kategori | 8: + **Koçluk**. Alt bar içerik sütununa hizalı. Çözümleme'ye "Kişi kartı: modelin görüşü" kartı. |
| Yeni | — | **NiyetWindow** (RemindWindow kalıbı): "bu görüşmede söylemek istemediğim şey" → `call_intent` (kullanıcı tablosu). **SozlukWindow** (TagManagerWindow kalıbı): küfür/şive/dolgu listeleri → `habit_lexicon`. |

### 3.4 Üst katman

| Yüzey | Yeni |
|---|---|
| RecordingOverlay (`RecordingOverlay.xaml:42-91`) | + sessiz ölçer: "son 60 sn: sen %64" mini çubuk + kendi ses düzeyi için ▲/— (yalnız göreli ok, dB rakamı yok). Alarm **yok**. `Begin(startedAt, headline)` ikinci parametresi ilk kez kullanılır; `Label.Text` literal ezmesi (`RecordingOverlay.xaml.cs:85`) loc'a. Kulaklıksız görüşmede ölçer gizli + "kulaklık yok, ölçülmedi". |
| CallerOverlay | "Sana verilen / senin verdiğin" iki satır (bugün `GetOpenCommitments(...).Take(3)`, `MainWindow.xaml.cs:171-175`); Niyet satırı; `Title="Kim arıyor"` literal (`CallerOverlay.xaml:5`) loc'a. |
| Bildirim merkezi | Yeni türler: "Sözün vadesi bugün (sen → Ali)", "Döküm yenilendi; okuma eski dökümden", görüşme sonrası "Ne oldu?" tostuna üç sayı ("sen %61 · küfür 3 · 2 açık söz", tıkla → Aynam sekmesi) — **görüşme sonrası raporun teslim anı**. |

### 3.5 Silinen / taşınan

| Ne | Nereden | Nereye |
|---|---|---|
| Söz çipleri (Açık, Verdiğim, Vadesi geçti) | `LedgerPage.xaml:42-75` | Sözler sayfası |
| Genel bakış "Vadesi geçen sözler" **liste** bölümü | `OverviewPage.xaml:151-195` | Silinir (`PLAN-UI.md:57`); iki Dikkat kartı (§18.1 kullanıcı kararı, `OverviewViewModel.cs:361-368`, `PromiseSideTests.cs:143-157`) **kalır** |
| ContactWindow Defter sekmesi | `ContactWindow.xaml:528-631` | Kişi kartı içine |
| Ctrl+4 Search çiftliği | `MainWindow.xaml:28` | Ctrl+F tek |
| `Setup_Click` ölü işleyici | `MainWindow.xaml.cs:355` | Silinir |

---

## 4. Ekran ekran değişiklik ve tel çerçeveler

Gösterim: `[ ]` düğme · `( )` çip · `▸` tıklanınca o anı çalar · ⌂ kanıt · ≈ modelin görüşü · ✎ kullanıcı verisi.

### 4.1 Sözler (yeni sayfa, `PromisesPage`, Ctrl+5)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Sözler                                                         (Herkes ▾)  (Açık ▾)  [Yenile] │
│ Kim kime ne söz verdi; vadesi ne zaman; tutuldu mu. "Tutuldu"yu sen işaretlersin.              │
│ (Hepsi 23) (Vadesi geçti 3) (Bu hafta 4) (Tarihsiz 9) (Koşullu 2) (Tutuldu 6) (Reddedilen 1)   │
├───────────────────────────────────────────┬────────────────────────────────────────────────────┤
│ ⌂ SENİN VERDİKLERİN (11)                  │ ⌂ SANA VERİLENLER (12)                             │
│ ┌───────────────────────────────────────┐ │ ┌────────────────────────────────────────────────┐ │
│ │ ▲ 3 gün geçti          Gürhan  [G]    │ │ │ ▲ 12 gün geçti · 2 görüşme oldu   Avukat [A]   │ │
│ │ Sözleşme taslağını göndermek          │ │ │ Dilekçeyi Polonya'ya iletmek                   │ │
│ │ vade: 1 Eyl (cuma) · söylendi 28 Ağu  │ │ │ vade: 23 Ağu · söylendi 18 Ağu                 │ │
│ │ ▸ 07:12 "…cumaya sana yollarım…"      │ │ │ ▸ 02:40 "…hafta içinde gönderiyorum…"          │ │
│ │ [✓ Tutuldu] [Ertele ▾] [Reddet] [✎]   │ │ │ [✓ Tutuldu] [Hatırlat] [Reddet] [✎]            │ │
│ │ ⓘ 4 Eyl görüşmesinde geçti — tutuldu mu? │ │                                                │ │
│ └───────────────────────────────────────┘ │ └────────────────────────────────────────────────┘ │
│ İşaretledin: 4 tutuldu · 2 vadesi geçti · 5 işaretsiz │ İşaretledin: 2 tutuldu · 1 açık kaldı · 9 işaretsiz │
├───────────────────────────────────────────┴────────────────────────────────────────────────────┤
│ Tutuldu olarak işaretlendi: "Sözleşme taslağını göndermek" — [Geri al]  ✕                     │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- Sütunlar `ByMe`; < 1100 px'te alt alta. Her kart alıntı + ▸ (`CallWindow.Show(owner, callId, startMs, isMe, tab)`).
- **Fiiller:** Tutuldu → `FulfilCommitment(id, byCallId, now)` + `fulfilled_at`; Ertele → `user_deadline_date` (makine `deadline_date` **değişmez**; `MovedDeadlines` yalnız konuşmadan gelen vadeyi okur, kullanıcının ertelemesi kişiye bayrak olmaz); Hatırlat → mevcut `RemindWindow`; Reddet → `DismissCommitment` + `decided_at`; ✎ → `user_obligation` (alıntı değişmez, "senin düzeltmen" rozeti). **Geri al** şeridi `TodoViewModel.cs:264-300` `PendingUndo` kalıbı; `ReopenCommitment`/`RestoreCommitment` yeni.
- **Oran yok.** "Tutulan 4/9" gibi bir oran, kullanıcının işaretleme ihmalini kişiye yazar (`DeterministicChecks.cs:38-41` uyarısının tersi). Üç sayı: tutuldu · vadesi geçti · işaretsiz.
- **"Açık kaldı"** (karşılıksız değil): vade ≥ 14 gün geçmiş **ve** vade sonrası o kişiyle ≥ 1 görüşme olmuş **ve** hâlâ açık. Fırsat olmadıysa söylenmez. "Tutulmadı" yalnız kullanıcı `Abandoned` seçince (`CommitmentStatus.Abandoned`, `Analysis.cs:52`, bugün yazıcısı yok).
- **"Tutuldu mu?" önerisi:** aynı kişiyle sonraki görüşmede sözün katlanmış `Obligation`'ıyla ≥ 2 ortak anlamlı kelime (durak listesi `ArchiveQuestions.cs:61-62`) taşıyan satır varsa kart altında ⓘ satırı; öneri, işaret değil. Kabul oranı ölçülür; < %30 → kapatılır.
- Koşullu/belirsiz rozeti `TurkishDates.NonCommittal :45-49`.
- Takvim, CallerOverlay ve Genel bakış aynı Core sorgu katmanından (`Repository.PromiseLedger`) beslenir — "dört kopya" sorunu sözlerde tekrarlanmaz.
- **Önkoşul:** `TurkishDates.TryResolve(phrase, spokenOn: DateOnly.FromDateTime(call.StartedAt.LocalDateTime))` — `AnalysisPipeline.cs:440` (Absorb'a tarih parametresi) ve `ActionExtraction.cs:162`. Ölçü: aynı görüşme iki farklı günde yeniden çözümlenir, `deadline_date` değişmez.

### 4.2 Aynam (yeni sayfa, `MirrorPage`, Ctrl+8) — eğri; ve CallWindow "Aynam" sekmesi — rapor

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Aynam                                            (Son 3 ay ▾)  (Herkes ▾)  (Motor: hepsi ▾)   │
│ Konuşurken ne yaptığın — sayılarak, dinlenerek. Yorum yok; anları sen dinlersin.               │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐   │
│   │ 0,42 /dk   │ │ 6,1 /100   │ │ 142 k/dk   │ │ %58        │ │ 1,8 /10dk  │ │ 4          │   │
│   │ küfür      │ │ dolgu      │ │ hız        │ │ konuşma    │ │ söz kesme  │ │ istemeden  │   │
│   │ önceki: 0,71│ │ önceki: 7,4│ │ önceki: 139│ │ payın      │ │            │ │ verilen    │   │
│   └────────────┘ └────────────┘ └────────────┘ │ önceki: %61│ └────────────┘ │ (senin     │   │
│   ⓘ 14 sayımın 11'i dinlendi, 10'u doğru · küfür kesinliği %91 · şive: ölçülmüyor (neden ▸)  │ işaretin)  │   │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ (Küfür) (Dolgu) (Hız) (Pay) (Kesme) (Bilgi)   — dakika başına, görüşme başına nokta, ay çizgisi│
│   0,8 ┤              ●                                                                          │
│   0,4 ┤    ●    ●        ●     ┆   ●                                                            │
│   0,0 ┼────┬────┬────┬────┬────┆────┬────      ┆ = motor değişti (large-v3 → nova-3)            │
│        Haz  Tem  Ağu  Eyl                       tıkla → o ayın görüşmeleri                      │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ Anlar (Eylül, küfür, 11)                                   [Yalnız dinlenmemişler] [Sözlük ✎]│
│   ▸ 04 Eyl · Gürhan · 12:41  "…"                      [Doğru] [Yanlış duyulmuş] [Bu küfür değil] │
│   ▸ 02 Eyl · Avukat · 01:10  "…"  (ses net değil)                                               │
│   ▸ 01 Eyl · Uliana · 00:48  "…IBAN…"  bilgi: IBAN kalıbı   [İstemedim] [Sorun yok]             │
│ ⓘ Ölçülmeyenler: şive (STT yazı diline çeviriyor, ön-ölçüm sonucu) · rol yapma (ölçülemez;     │
│   karşılığı Niyet kartı) · duygu (ses/metinden duygu okuması Türkçede doğrulanmadı)             │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- Kartlar `StatValue/StatLabel` (`Components.xaml:127-133`); ok + önceki değer = ölçülen değişim dili; renk yok.
- **Payda:** kullanıcının kendi konuşma dakikası / 100 kelimesi (`TalkStats.MineMs`), görüşme başına adet değil.
- **Motor:** her nokta `transcript_version.engine` rozeti; eğri varsayılan aynı motorlu görüşmeleri bağlar, motor değişince dikey kesik çizgi. Hız yalnız `Words` dolu ve aynı motor içinde.
- **Kovalar:** kelime `p < t_motor` **veya** segment `LowConfidence` → "belirsiz"; `p = null` (OpenAI/ex5/whisper.cpp) → yalnız segment kapısıyla sayılır + "kelime güveni yok" rozeti; `SuspectedEcho` segmentleri **sayılmaz**; `LikelyNoHeadphones` görüşmeler eğride içi boş nokta.
- **Kulak teyidi** → `verdict` tablosu (kullanıcı); "Bu küfür değil" `habit_lexicon` hariç tutma listesine; yeniden hesapta `quote_folded` anahtarıyla dirilmez (`DismissedFlagKeys` kalıbı, `Repository.cs:2207`).
- `(Herkes ▾)` kişi süzgeci: "Gürhan'la 0,9/dk, herkesle 0,4" (§10.4 kişiler arası karşılaştırma, bedava: aynı `speech_habit` satırları `contact_id` kırılımıyla).
- Eğri saf `HabitTrendLayout` fonksiyonu üstünde (`TimelineLayout` gibi testli), `Polyline` + `Ellipse`.

CallWindow **Aynam** sekmesi (Okuma sekmesi `CallWindow.xaml:981-1025` şablon): "Bu görüşmede sen" başlığı; altı sayı tek satırda; küfür/dolgu/bilgi anları tıklanır listelerle; "Hesaplandı: 4 Eyl · döküm v3" damgası; Niyet kartı ✎ satırı ("söylemeyeceğim: kira rakamı" → [İstemedim] işaretleri buradan sayılır); karşı taraf için alışkanlık **sayılmaz**.

### 4.3 Görüşme penceresi — dikey alan (şikâyet 8), zaman çizgisi işaretleri

| Bant | Bugün (≈px @720) | Kaynak | Yeni |
|---|---|---|---|
| TitleBar | 30 | `CallWindow.xaml:27` | 30 |
| Başlık bandı (subtitle · etiketler · Hatırlat/Önemli/⋯) | ~36 (sarınca 2 satır) | `:32-187` | 32, tek satır; etiket pilleri sarınca ikinci satır yalnız o zaman |
| Dikkat şeridi | ~56 | `:192-212` | 32 tek satır (`TextTrimming`) |
| Sekme şeridi | ~46 | `:215-216` | 40 |
| Kalite satırı | ~58 | `:280-320` | **tek şerit 36**: motor · kapsama · pay barı (6 px) · söz kesme · (Sohbet)(Zaman çizgisi) · "3 döküm" rozeti; "Dökümler/Yeniden çevir" ⋯ menüsüne (`:62-80`) **ve** rozet tıklanınca (şikâyet 5 sınıfına düşmesin) |
| Konuşma payı + çipler | ~40 | `:230-270` | ↑ aynı şeride |
| Oynatıcı | ~100 (140-180 iş/hata ile) | `:1227-1384` | 64 katlanır: dalga 38→28, Padding 10,6→8,4, iş/hata şeridi **dalganın yerine** (Visibility); `Playback.IsLoaded=false` iken Border Collapsed |
| **Toplam** | **≈366 → döküm ≈354 (%49)** | | **≈234 → döküm ≈486 (%67)** |

`MinHeight` 480→560; `TranscriptScroller MinHeight=240`. **Ölçü:** ayrı `LayoutTests` (seed'li VM + `Measure(880,720)`/`Arrange`; smoke pencereyi göstermediği için orada ölçülemez): üst Auto satırların `DesiredSize` toplamı ≤ 240 px; döküm ≥ 400 px. Taban önce ölçülüp `ISLEM-GUNLUGU`'ne yazılır. ContactWindow HeroCard `20,18 → 16,12`; `Theme.xaml:65` PageSubtitle alt boşluğu 24 → 14 (bütün sayfalar +10 px).

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Gürhan Abi · 4 Eyl 14:02 · 18:49 · WhatsApp   (önemli)(kira) [+etiket]   [⏰] [📌] [⋯]   — □ ✕ │
│ ⚠ 2 çelişki, 1 baskı işareti — Tutarlılık sekmesine git                                        │
│ (Görüşme) (Defter) (Aynam) (Aksiyonlar) (Tutarlılık) (Okuma) (Sor) (Notlar)                   │
│ Deepgram nova-3 · kapsama 0,83 · ▐███████▌▒▒▒ sen %63 · kestin 2 · [3 döküm]  (Sohbet)(Zaman)  │
│────────────────────────────────────────────────────────────────────────────────────────────────│
│  00:00 ┃                          │                                                   ┃▁       │
│        ┃ Sen: Alo, Gürhan abi…    │                                                   ┃▂       │
│  00:05 ┃                          │ Gürhan: Buyur oğlum, …                            ┃▅       │
│  00:20 ┃ Sen: Şimdi şu kira…      │                                          😄 kahkaha┃▂       │
│   ▲    ┃ (ses düzeyi şeridi 6 px, yalnız kalibrasyon geçince; yankı/overlap bölgeleri boş)     │
│────────────────────────────────────────────────────────────────────────────────────────────────│
│ ▂▃▅▇▅▃▂▁▂▄▆▇▆▄▂▁  ▶ ⏮ ⏭  Sen/O   03:41 / 18:49                                             ▾  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Zaman çizgisi şeritleri `TimelinePanel.OnRender` (`:145-204`) içinde iki `DependencyProperty` (`ProsodyMine/ProsodyTheirs`); mic şeridi kalibrasyon geçince açık, far şeridi ayrı kalibrasyon geçmeden gizli; `audio_event` glifleri (ElevenLabs anahtarı gelince). Aynam'ın anları çizgide **gösterilmez** (kanıt zemininde küfür işareti = karşı tarafı puanlıyor izlenimi).

### 4.4 Kişi penceresi — "Kişi kartı" sekmesi (ContactsPage'de aynı UserControl)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ [G] Gürhan Abi · WhatsApp · 31 görüşme · son: 4 Eyl                                  [Yenile] │
│ (Akış) (Kişi kartı) (Görüşmeler) (Ara) (Notlar) (Hakkında)                                     │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ GİDİŞAT                                              (3 ay) (6 ay) (1 yıl) (Hepsi)  bu kişi / herkes │
│   Görüşme sıklığı  ▁▂▃▅▆▅▃▂▁▂▄  4/ay → 2/ay     Konuşma payın   ▃▃▄▅▆▆▇  %45 → %68 (herkes %58) │
│   Kim aradı        ↓12 ↑7 ?3                     Söz kesme (o)   ▂▂▃▃▅▆▇  1 → 4 /10 dk           │
│   Cevapsız soru(o) ▁▂▂▄▅  1 → 3/gör. (N/M görüşmede ölçüldü)  Perde medyanı: henüz ölçülmedi    │
│   Her sayı: son dönem → önceki dönem. Yorum yok; tıkla → o dönemin görüşmeleri.                │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ SÖZLER                                                                 [Sözler sayfasında aç]│
│   Sana verilen açık: 3 (2 vadesi geçti) · Senin verdiğin açık: 2 · işaretlediklerin: 4 tutuldu │
│   ▸ 12 gün geçti · 2 görüşme oldu  "…hafta içinde gönderiyorum…"  [Tutuldu] [Reddet]           │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ KALIPLAR (doğrulanmış alıntılar, 31 görüşme)              Kaynak: (Hepsi)(Kural)(Denetim)(Değerlendirme) │
│   Kaçamak cevap       9  · 6 görüşmede · 7/9 dinlendi      ▸ son: 02 Eyl 06:41                 │
│   Baskı / aciliyet    6  · 4 görüşmede · 3/6 dinlendi      ▸ son: 04 Eyl 14:02  (2'si model etiketi ≈) │
│   Rakam değişti       2  · kira · tutar                    ▸ (Rakam yolculuğu'nda)             │
│   Vade kaydı          1 söz · 3 kez, +19 gün               ▸                                   │
│   Dolandırıcılık kalıbı (sezgisel kural)  0                                                    │
│   Tutarlı kalanlar    5 gözlem                             ▸                                   │
│   Reddettiklerin (3) sayılmaz. N grup görüşmesi sayılmadı.                                     │
│   ▸ Kaçamak (9):  4 Eyl 12:40 "Onu sonra konuşuruz abi" ▶  [Dinledim: doğru] [Yanlış] [Reddet]  │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ RAKAM YOLCULUĞU                                                                              │
│   kira · tutar     15.000 (12 Haz) ▸ → 18.000 (3 Tem) ▸ → 20.000 (28 Ağu) ▸     3 farklı değer │
│   teslim · tarih   "cuma" (28 Ağu) ▸ → "gelecek hafta" (1 Eyl) ▸                               │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⌂ ELİNDEKİ KAYITLAR (kişinin kendi sözleri, açık konulara göre)                                │
│   kira:     12 Haz "15.000 dedik" ▸ · 28 Ağu "20.000'in altı olmaz" ▸  (çelişki bayrağı var)   │
│   teslim:   18 Ağu "hafta içinde gönderiyorum" ▸ — açık kaldı (12 gün)                         │
├────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ≈ MODELİN GÖRÜŞÜ — öznel yorumdur, bulgu değildir. Yukarıdaki kanıtlardan bağımsızdır.         │
│   Qwen3.5-4B · 04 Eyl · 12 görüşme · 40 alıntı · 2 madde dayanaksız düştü   [Yeniden sor] [Katılmıyorum] │
│   Genel izlenim            "…" [A3][B7]                                                        │
│   İletişim tarzı (izlenim) "Uzun cümlelerle konuyu dağıtıp sonunda tarihe geliyor" [B7]▸       │
│   Güçlü / zayıf yan izlenimi   "…" [A9]▸  /  "…" [B12]▸                                        │
│   Cevapsız kalan konular   "Sözleşme tarihi sorulunca konu değişti" [B9]▸ [B14]▸               │
│   Görüşmeye giderken       "Tarihi yazılı iste" (dayanak [B9]) · "Şu soruyu tekrar sor: …" [A4] │
│   Senin için notlar        "Rakamı sen açtın [A2]; …"                                          │
│   Başka bir okuma          "Bu kalıplar iş yoğunluğuyla da açıklanabilir."                      │
│   Ölçülmeyenler: psikolojik durum ve duygu durumu verilmiyor — ses/metinden duygu okuması       │
│   Türkçede doğrulanmadı; "kullanabileceğin argümanlar" istenmiyor — karşılığı Elindeki kayıtlar.│
│   Bu panel Ayarlar > Çözümleme > "Kişi kartı: modelin görüşü" ile kapanır (varsayılan kapalı). │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Gidişat serisi yalnız kod-doğrulanabilir kaynaklardan:** sıklık (`ListCalls`), yön (`call.direction`, Unknown paydada değil), konuşma payı ve kesme (`TalkStats`, `SuspectedEcho` hariç), cevapsız soru (`speech_act`, "N/M görüşmede ölçüldü" paydasıyla — eski görüşmelerde yok), açık söz sayısı. **"Bulgu yoğunluğu" serisi yok** (hangi denetimlerin koştuğuna bağlı, `MIMARI.md:200-202`); model etiketli sayılar yalnız Kalıplar'da, "M/N dinlendi" ile.
- **Kalıplar kaynakları:** `flag` (pipeline + consistency; ScamPattern, EvadedQuestion, PressureTactic, VagueShift, TimelineMismatch, Contradiction, MovedDeadline, ChangedAmount) ∪ `tactic_evidence` (deception, pipeline `baski_isaretleri` — kapıya bağlı) ∪ `consistency_note.tutarli_gozlemler` (olumlu). Yalnız `confidence` orta/yüksek ve `low_confidence=0` çubuğa girer; düşükler gri satırda. Ölçü: tür başına ret oranı (`dismissed/total`) ≤ %30; > %30 → o türün çubuğu kalkar, alıntı listesi kalır.
- **Elindeki kayıtlar** = "kullanabileceğim argümanlar" isteğinin kanıt karşılığı: kişinin `claim`/`commitment` satırları açık konulara göre, tarihli, çalınabilir; `BuildPriorStatements` kalıbı; model yok.
- **Modelin görüşü** (§5 Paket I): opt-in, ayrı zemin, imza, `baska_okuma` zorunlu, dayanaksız madde düşer, [Katılmıyorum] → `contact_reading.user_verdict` (ölçü). Girdi: `[B#]` defter çıpaları (iddia 20 + söz 20 + pipeline/consistency bayrağı 20; **deception/tactic_evidence asla**) + `[A#]` `RecentSegments` 40 + sayılabilir özet satırı; `call_summary` **girmez** (alıntı doğrulamasız, `AnalysisPipeline.cs:581-586`).

### 4.5 Defter (şikâyet 1)

```
│ Defter                                                  [Temizle]  (Kişiye göre süz…)   [☐ Seç] │
│ (Hepsi 41) (Değişenler 6) (Dikkat 35) (Reddedilenler 4)   Kaynak: (Hepsi)(Kural)(Denetim)   Sırala: (Tarih ▾) │
│ ☐ [G] Gürhan · Baskı işareti · denetim · 04 Eyl · 3/4 dinlendi                    ▸ [Reddet]   │
│   "…bugün karar vermezsen başkasına vereceğim…" 14:02  ↔ 28 Ağu "…acelesi yok…" 07:12          │
│ ☐ [A] Avukat · Değişen rakam · kira · 15.000 → 18.000 → 20.000                    ▸ [Yolculuk] │
│ Reddedildi: "Baskı işareti — 04 Eyl" — bir daha bu alıntıyla gelmez.  [Geri al]  ✕            │
```

- Reddet → `DismissFlag` + `decided_at`; **Geri al** → `RestoreFlag` (yeni); Reddedilenler çipi + [Geri getir]; Seç modu → `DismissFlags(ids)`; Sırala Tarih/Kişi/Tür; çipler `WrapPanel` (`PLAN-UI 5.4`).
- **Tutuldu düğmesi Defter'den kalkar** (sözler Sözler'de); `LedgerViewModel.cs:335-336` sessiz dal ve `:319` "tek tek kapatılamaz" bildirimi ölür. Değişen rakamda Reddet yok, Yolculuk var.
- "Reddettiklerimi kalıcı sil" **yok**: `dismissed_by_user=1` satırlar tombstone; silinirse `DismissedFlagKeys` (`:2207-2216`) bir sonraki koşumda aynı bulguyu diriltir.
- Üç defter yüzeyi (LedgerPage, CallWindow Defter, ContactsPage Defter) aynı fiil kümesini `Services/LedgerActions.cs` üzerinden çağırır (`CallActions.cs:46-75` kalıbı).

### 4.6 Aksiyonlar → Yapılacaklar, Bitenler, "Reddet" (şikâyet 2, 4)

- `ShellViewModel.RefreshAll` (`:479-492`) `Todo.Refresh()` alır; `CallWindowViewModel.SetActionStatus` (`:1045-1050`), `OverviewViewModel.SetDayActionStatus` (`:636-641`), `ContactsViewModel` (`:533`) → `CallActions.NotifyChanged()` (`CallActions.cs:33` public).
- `TodoPage.xaml:189-192` "Bitenleri göster" süzgeç satırına; `DoneCount` `ShowDone`'dan bağımsız (kutu "Bitenler (12)" der); `AppSettings.TodoShowDone` kalıcı; "Sözlerim" çipi (`commitment ByMe=1 status=0`, Toggle → `FulfilCommitment`).
- **Dil (tek fiil):** Reddet = öneri/bulgu reddi; Kaldır = kullanıcının kurduğunu kaldırma; Sil = kalıcı, onaylı; Gizle = yalnız görsel (şerit/pano). `TodoViewModel.cs:273` `"Gizlendi: …"` literal → `Localisation.T("todopage.reddedildi-n")`; `callwindow.bu-oneri-bir-daha-gosterilmez` → "Reddedersen bir daha önerilmez."; `ledgerpage.bu-satiri-kaldir`, `contactspage.bu-kaydi-defterden-kaldir` → "Bu bulguyu reddet"; belgeler (`YOLHARITASI.md:42`, `YAPILACAKLAR.md:1316,1472`) "Gizle" → "Reddet". `LocalisationTests` kuralı: `strings.tr.json`'da "Gizle" yalnız overlay anahtarlarında.
- PLAN-UI 4.3/4.5 kalıntıları aynı turda: `ShellViewModel.cs:244/318/321` "Arama çalıyor / Arama başlayınca" → "Gelen çağrı / Görüşme başlayınca"; `settingswindow.uzun-aramalar`, `healthpage.60-dakikalik-arama-icin`; `settingswindow.krediyi-sor` → "Bakiyeyi sor".

### 4.7 İşlem durumu (şikâyet 3)

Üç kusur kapalı (7a700c6, d8026a6, 503c1e9, 4a7bfa2). Kalan yan etki: `ProcessingViewModel.Requeue` (`:484-490`) Notice yazıp `Refresh()` çağırıyor, `Refresh :358` `Notice=null` → "N görüşme yeniden kuyruğa alındı" hiç görünmüyor. Düzeltme: `Refresh()` önce, Notice sonra. Test: Requeue sonrası `Notice != null`.

### 4.8 Ayarlar (şikâyet 5, 6; yeni Koçluk kategorisi)

- **Uzak düğme:** alt bar (`SettingsWindow.xaml:1665-1693`) pencere genişliğinde sağa yaslı; içerik `MaxWidth=760 Left` (`:98`); 1920 px'te Kaydet ~870 px uzakta. Düzeltme: bar `Grid.Column=1`, içerikle aynı `MaxWidth` + Left. Ayrıca `:473-476` "Yenile" ComboBox satırına; `:948-968` "Bağlantıyı sına / Bakiyeyi sor" model kutusunun altına, expander'ın üstüne; `:1034` `UsesLocalAsr=false` iken yerel blok soluk değil **Collapsed**. Ölçü: Kaydet sağ kenarı − içerik sağ kenarı ≤ 32 px @1920.
- **Motor listesi:** `SttProbe.TranscriptionFirst` (`:231-243`) yalnız sıralar; `SttEndpointViewModel.TestAsync :166` alfabetik ezer. `TranscriptionCandidates(models, catalogue)`: pozitif süzgeç (`whisper|transcribe|scribe|stt|speech|asr`; `nova` yalnız Deepgram dalında) ∪ katalog; boş kalırsa tam liste; Status "N modelden M'si yazıya dökme modeli"; kutuda **"Tümünü göster"** geçişi (`SttProbe.cs:226-229` "gizleme yok" gerekçesi korunur). `OrderBy` kaldırılır. `ReprocessWindow` yerel listesine "indirildi" rozeti (`SettingsWindow.xaml:1112-1127` `ModelPresence`).
- **Koçluk kategorisi** (`Category_Checked` `:65-86`, `ShowSection` `:93-107`, `SettingsViewModel` + `ToSettings` — 8 dosya kalıbı):

```
│ Koçluk                                                                                          │
│ ┌ Aynam ──────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ Alışkanlık sayımı (küfür, dolgu, hız, verilen bilgi)                     [●  ] Açık         │ │
│ │   Bu makinede, model yok, ücretsiz. Yalnız senin kanalın sayılır.                           │ │
│ │ Sözlükler ✎  Küfür (23) · Dolgu (9) · Şive (ölçülmüyor)                  [Düzenle]          │ │
│ │ Niyet kartı — kayıt başlarken "bu görüşmede söylemeyeceğim" sorar        [  ●] Kapalı       │ │
│ │ Kelime güveni eşiği (motor başına, ölçülmüş): large-v3 0,55 · nova-3 0,62 · OpenAI: yok     │ │
│ ├ Ses ────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ Ses düzeyi ve perde ölçümü (yerel, CPU, ~10 sn/görüşme)                  [●  ] Açık         │ │
│ │ Zaman çizgisinde ses şeridi                                              [  ●] Kapalı       │ │
│ │   Kalibrasyon: 60 zirveden 0'ı dinlendi — ölçüm geçince açılabilir.                          │ │
│ │ Kayıt şeridinde canlı ölçer (konuşma payı + kendi ses düzeyi; uyarı vermez) [  ●] Kapalı    │ │
│ │ Kişinin ses perdesi saklansın (görüşmeler arası karşılaştırma için)      [  ●] Kapalı       │ │
│ ├ Kişi ───────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ Kanıt birikimi (Kalıplar, Gidişat, Rakam yolculuğu)                      [●  ] Açık         │ │
│ │ Kişi kartı: modelin görüşü — ücretli istek, metin makineden çıkabilir    [  ●] Kapalı       │ │
│ ├ Ölçülmeyenler ──────────────────────────────────────────────────────────────────────────────┤ │
│ │ Duygu/ton etiketi · yalan/güven skoru · şive (STT normalize ediyor) · rol yapma — neden ▸    │ │
│ └─────────────────────────────────────────────────────────────────────────────────────────────┘ │
```

Hume kartı **yok** — ölçüm geçmeden ürün koduna girmez. Her anahtarın yanında ölçüm durumu caption olarak.

### 4.9 Eski okuma / bayatlık (şikâyet 7)

Kanıt: `CallOrchestrator.cs:1981-2028` yalnız `ReplaceSegments + SaveTranscriptVersion`; `DeleteReading :2377` / `DeleteDeception :2412` çağrısız; `LoadReading :1114-1130` yalnız damga.

1. v15: `reading_note`, `deception_note`, `consistency_note`, `action_item`, `call_summary` → `transcript_version_id INTEGER NULL REFERENCES transcript_version(id) ON DELETE SET NULL`; `Save*` yazarken `call.transcript_version_id`'yi alt sorguyla kopyalar.
2. **Silmek yerine bayat etiketiyle tutmak** (okuma/değerlendirme ücretli; tutarlılık bulgularında `dismissed` kullanıcı kararı). `IsStale = note.VersionId != call.TranscriptVersionId` (sütun NULL ise "bilinmiyor", asla "bayat"). Her türev sekmede InfoBar: "Bu okuma önceki dökümden (v2, Deepgram) üretildi; ekrandaki metin v3 (yerel large-v3). Alıntılar eski metne ait olabilir. [Yeniden oku] [Sil]". Otomatik silme yalnız aynı koşumda yeniden üretilecekse (`ConsistencyAutomatically` açıkken `ClearConsistency`). `DeleteReading/DeleteDeception` [Sil] düğmesine bağlanır.
3. Defter sekmesi de `call_summary.transcript_version_id`'den bayatlık alır (çözümleme atlanınca, `CallOrchestrator.cs:1312` kapısı). Bildirim: "Metin yenilendi; defter ve okuma eski metinden — Yeniden çözümle".
4. `TranscriptVersionsWindow` geri yükleme aynı InfoBar.
5. Yeni türev tablolar (`speech_habit`, `audio_event`) `transcript_version_id` taşır; `prosody` **ses** anahtarı taşır (`audio_sha256`/`trimmed_at` bugün hiç yazılmıyor — anahtar `mic_path+far_path+dosya uzunluğu` ya da prosody koşumunda hesaplanan sha).

### 4.10 Kayıt şeridi — canlı ölçer, brifing (alarm yok)

```
 ┌────────────────────────────────────────────────────────────────────────────────┐
 │ ● Kaydediliyor  12:41  │ sen %64 ▐████▌░░ │ ▲ │ Gürhan · sana 3 açık söz, 1 senin ▾ │ Durdur │ ✕ │
 └────────────────────────────────────────────────────────────────────────────────┘
   ▾ katlanır (3 satır): Sana: "hafta içinde gönderiyorum" · 12 gün geçti / Senin: "cumaya yollarım" · bu cuma / Niyet ✎: "Kira rakamını ben söylemeyeceğim"
```

- **Mekanizma:** `App/Services/LiveTalkMeter.cs`, `PacketReady`'ye üçüncü abone (`SpeakerIdentifier.Listen/OnPacket :93-134`, `CaptureSelfTest.OnPacket :100-135` kalıbı); paket başına RMS, −40 dBFS eşiği (`SpeakerIdentifier.IsSpeech :137-152` aynı formül; dördüncü kapı yazılmaz), `Interlocked` sayaçlar + 60 sn halka tampon; kilit yok, tahsis yok; okuma `RecordingOverlay`'in 1 sn `DispatcherTimer`'ı. `LevelChanged`'e (kilit altında, `CallRecorder.cs:183`) ve `Tick()`'e dokunulmaz.
- **Kulaklık kapısı:** aynı 10 sn pencerede far kanalı da −40 dBFS üstündeyse pencere sayılmaz; son görüşmesi `LikelyNoHeadphones` ise ölçer gizli.
- **Taban:** yalnız konuşma çerçeveleri, son 120 sn **medyanı**; ilk 30 sn boş. Gösterim yalnız göreli ok; dB rakamı yok (Windows communications hattı işlenmiş sinyal, `WasapiCaptureBackend.cs:173-198`).
- **Brifing** kişi tanınınca (`SpeakerIdentified`) ya da başlık eşleşince (`AssignContactFromTitle`/`IdentifySpeakers` kapalıysa görünmez — şeritte tek satır açıklama).
- **Alarm v2 kapısı:** görüşme sonrası Aynam "ses düzeyi zirveleri"ni listeler; [Uyarı isterdim] [Gereksiz]; 10+ zirvede "isterdim" ≥ %70 → Ayarlar'da "Canlı uyarı" kartı görünür; < %50 → kaybolur. Sevk sonrası ölçü: alarmdan sonraki 30 sn'de kendi seviyesi tabana dönen görüşme oranı ≥ %60.

### 4.11 Şikâyet → çözüm tablosu

| # | Şikâyet | Durum | Kök (dosya:satır) | Çözüm | Paket | Ölçü |
|---|---|---|---|---|---|---|
| 1 | Defter temizlenemiyor/silinemiyor/düzenlenemiyor | kısmen | Tek yönlü Dismiss/Fulfil, geri alma yok (`LedgerViewModel.cs:298-344`, `Repository.cs:1944-1959`); üç yüzey üç fiil kümesi | Sözler ayrılır; Reddet + Geri al + Reddedilenler + Seç + Sırala; `LedgerActions`; kullanıcı düzenlemeleri ayrı sütunlarda | A2 | `LedgerUndoTests`; Defter'de Tutuldu 0; düzenledikten sonra iki kez çözümle → tek satır |
| 2 | Aksiyonlar takip edilemiyor; bitenler görünmüyor | kısmen | `RefreshAll` Todo yok (`ShellViewModel.cs:479-492`); `SetActionStatus` olay yaymıyor; Bitenler dipte (`TodoPage.xaml:189`) | `NotifyChanged` üç yerde; Bitenler süzgeç satırında sayılı; `TodoShowDone` | A1 | "Yaptım" sonrası Done listesi aynı anda güncel |
| 3 | İşlenemeyenler ekranı | **kapalı** | kalan: Requeue Notice sırası (`ProcessingViewModel.cs:484-490`, `:358`) | Refresh önce, Notice sonra | A1 | Requeue sonrası Notice ≠ null |
| 4 | "gizle" → "reddet" | kısmen | Düğmeler değişmiş; `TodoViewModel.cs:273` literal; ipuçları ve Defter "kaldır" dili | Tek fiil; loc; belgeler | A1 | LocalisationTests kuralı + `.cs` tarama testi |
| 5 | Ayarlar düğmesi uzakta | açık | Alt bar tam genişlik (`SettingsWindow.xaml:1665-1693`), içerik 760 sol (`:98`) | Bar içerik sütununa; hizalar | A1 | Kaydet − içerik ≤ 32 px @1920 |
| 6 | Yanlış modeller listede | açık | `SttProbe.cs:231-243` süzmez; `SttEndpointViewModel.cs:166` ezer | Pozitif süzgeç + katalog + Tümünü göster + M/N | A1 | `DoesNotContain("gpt-4o")`; sıra korunur |
| 7 | Eski okuma silinmiyor | açık | `CallOrchestrator.cs:1981-2028` türevleri temizlemiyor; `Delete*` çağrısız | `transcript_version_id`; bayat InfoBar + [Yeniden oku]/[Sil]; bildirim | A2 | `DerivedFreshnessTests` |
| 8 | Dikey alan dar | açık | 7 bant ≈366 px @720 (`CallWindow.xaml:27-320, 1227-1384`) | Tek şerit; oynatıcı katlanır; MinHeight 560; PageSubtitle 24→14 | A2 | `LayoutTests` döküm ≥ 400 px @720 |

---

## 5. Aşamalar

Ortak doğrulama (her paket): `./test.ps1` (PowerShell; `dotnet test` **yalan söyler**, `GELISTIRME.md:103-106`); tek sınıf `VoiceTranscript.Tests.exe --filter-class …` (bayrak tekrarlanır, `|` desteklenmez); `worker/.venv/Scripts/python -m pytest worker/tests -q`. Taban güncel kayıt: **913 C# / 908 geçti / 5 atlandı + 115 Python** (`ISLEM-GUNLUGU.md:2014`; `GELISTIRME.md:130-131` eskimiş, P0'da düzeltilir). Sayılar düşerse sebep o pakettir.

Ortak kayıt yerleri: yeni tablo → `Schema.Statements` + `Version` + `Migrations.Steps` (sevk edilen adım düzenlenmez, `Migrations.cs:18-22`) + `MigrationTests.AnUpgradedDatabaseMatchesAFreshOne` bloğu + `MergeArchive` kopya listesi (`Repository.cs:1262-1288`; `transcript_version_id` gibi FK'ler için `map_version` remap — bugün v14'ün kendi sütunu da ham kopyalanıyor, `:1238`) + `LedgerTables` (`:416`, contact_id taşıyanlar) + `MergeContacts` (`:537-631`, kişi anahtarlılar); tarih sütunları `Iso/ParseIso` + Row sınıfı (Dapper DateOnly tuzağı, `YAPILACAKLAR §15.1`). Yeni sayfa → 8 dosya (`ShellPage` + `PageName` + `Navigate`, XAML + `PageHost`, ray, `ActionRegistry`, loc tr+en, `WindowSmokeTests.Build`). Her kullanıcıya görünen metin `strings.tr.json` + `strings.en.json`; `.cs`'teki `Localisation.T("…")` çağrıları için **yeni** tarama testi (`LocalisationTests` yalnız XAML tarıyor, `:72-79`). Şema sürümleri **tek sıra**: v15 (A2) → v16 (D) → v17 (E) → v18 (G) → v19 (I); paralel dallar şema adımı eklemez.

### Paket R0 — SocialZeka çatallama (her şeyden önce; yarım gün mekanik + kullanıcı adımları)

**Amaç.** VoiceTranscript'i tam geçmişiyle yeni repoya taşımak; yeni ürün ve kurulum kimliği; aynı makinede iki uygulamanın çakışmaması; bundan sonraki her paket SocialZeka'da.

**Adımlar.**
1. **Repo:** kullanıcı GitHub'da `SocialZeka` reposunu açar (fork ya da boş repo). Yerel: `git clone` → `C:\Voice\SocialZeka` → `git remote set-url origin …` → bütün dallar + etiketler push. `.claude/skills` repoyla gelir; Claude belleği yeni yol altında (`~/.claude/projects/C--Voice-SocialZeka`) sıfırdan başlar — bu planın kendisi yeni repoya `docs/PLAN-SOSYALZEKA.md` olarak kopyalanır.
2. **Ürün kimliği** (dosya listesi): `Directory.Build.props` `Company/Product` → SocialZeka; `installer/VoiceTranscript.iss` → `installer/SocialZeka.iss` (`AppName`, `AppPublisher`, `DefaultDirName`, `DefaultGroupName`, `OutputBaseFilename`, `UninstallDisplayName` + **yeni `AppId` GUID** — mevcut `{7B3E9C41-…}` VoiceTranscript kurulumunun üstüne yazar); `publish.ps1:25,55,128,131` (`dist/SocialZeka`, iss yolu, setup adı); `.github/workflows/ci.yml`, `release.yml` (artefakt/asset adları); `UpdateService.cs:43,46` repo yolu + UserAgent `:65,166,180`; `AutoStart.cs:29` kayıt defteri değer adı (VoiceTranscript'in açılış kaydıyla çakışmasın); `App.xaml.cs` 8 MessageBox başlığı → tek loc anahtarı `app.ad`; `strings.tr/en.json` `mainwindow.voicetranscript`, `setupwindow.voicetranscript-*`; `ShellViewModel.cs:216` başlık; `README.md`, `OKUBENI.txt`, `PRODUCT.md` başlığı ve tek cümle; `tools/make_icon.py` yeni simge (isteğe bağlı).
3. **Namespace/csproj adları kalır** (`VoiceTranscript.Core/App/Capture/Worker`, test yolu `tests/VoiceTranscript.Tests/bin/…/VoiceTranscript.Tests.exe`); `test.ps1` değişmez.
4. **Veri klasörü:** `AppPaths` kökü `%LOCALAPPDATA%\SocialZeka.Data` (+ `--data` aynen); ilk açılışta `VoiceTranscript.Data` varsa diyalog: "Arşivi taşı / kopyala / yeni başla" (`BackupService.ImportAsync` kalıbı; `Migrate` koşar). Aynı klasörü paylaşmak iki uygulamanın aynı SQLite/WAL'a yazması demek — **yok**. Ayar dosyası da yeni kökte (`--data` > `DataRoot` > varsayılan sırası `GELISTIRME.md:82-83`).
5. **VoiceTranscript:** README'ye "geliştirme SocialZeka'da sürüyor" satırı; v2.9.21 son sürüm; repo arşiv (read-only) — §12-1 kararına bağlı. Yalnız kayıt/döküm hataları VoiceTranscript → SocialZeka tek yönlü birleştirilir; ters yön yok.
6. **Sürüm:** ilk etiket `v3.0.0` (A1 ile). `YOLHARITASI.md`/`ISLEM-GUNLUGU.md` yeni repoda devam eder, geçmiş korunur.

**Kabul.** Yeni klonda `./test.ps1` 913/115 yeşil; `DataDirectoryTests` yeni kök adıyla; `publish.ps1` paket üretir ve yeni AppId ile VoiceTranscript'in **yanına** kurulur (iki uygulama aynı anda açık, iki ayrı veri kökü, iki ayrı açılış kaydı); `UpdateService` yeni repoya bakar (ilk sürüme kadar dürüst "sürüm bulunamadı"); `LocalisationTests` eşliği.

**Risk.** VoiceTranscript2'nin tekrarı: iki repoya aynı düzeltmeyi iki kez yazmak. Tek kural onu keser: koç işi ve arayüz yalnız SocialZeka'da; VoiceTranscript'te yeni özellik yok.

### Paket P0 — Kod dışı borçlar (bir gün; R0 ile birlikte, yeni repoda)

1. `…/08715993…/scratchpad/kisa/oai-*.json` dosyalarındaki düz metin OpenAI anahtarı: dosyalar silinir, **anahtar döndürülür**.
2. Ölçüm hattı `tools/olcum/` altına (betikler + `SONUC.md` kalıbı; WAV'lar hariç): `sayfa-dort.py`, `karsilastir.py`, `hazirla.py`, `vad_olcum.py`. Aksi hâlde Hume/ElevenLabs/prosody karşılaştırması tekrar edilemez.
3. `docs/ISLEM-GUNLUGU.md`: 2026-09-04 turu (19 commit; EK-2 ve EK-4 ölçümleri sayılarıyla: #57/#58/#61 kapsama; sonuç "sistematik değil", "VAD kapatma elendi", `min_speech_duration_ms` denenmedi, #56 ölçülmedi).
4. `docs/GELISTIRME.md:130-131` taban → 913/115; `:66-69` "worker/tests yalnız stdlib" → numpy var. `docs/MIMARI.md:205` "Python'da doğrulama" → C# `QuoteVerifier`. `docs/YOLHARITASI.md:49` sürüm sütunu (VoiceTranscript'in son etiketi v2.9.21; SocialZeka v3.0.0'dan başlar). `VoiceTranscript.Tests.csproj` "global.json" yorumu.
5. `docs/YAPILACAKLAR.md`'ye bu planın paketleri ve kabul eşikleri; §18'e VoiceTranscript2 iptal gerekçesi (kullanıcıdan alınır).

### Paket A1 — Arayüz borçları, şema yok (v3.0.0)

**Amaç.** Şikâyet 2, 3, 4, 5, 6 ve dil kalıntıları; yeni özelliklerin oturacağı yüzeyleri doğrultmak.

**Dosyalar.** `ShellViewModel.cs:479-492`; `CallWindowViewModel.cs:1045-1050`; `OverviewViewModel.cs:636-641`; `ContactsViewModel.cs:533`; `TodoViewModel.cs:166-173, 273`; `TodoPage.xaml:189-192`; `ProcessingViewModel.cs:484-490`; `SettingsWindow.xaml:98, 473-476, 948-968, 1034, 1665-1693`; `Core/Asr/SttProbe.cs:231-243`; `SttEndpointViewModel.cs:161-170`; `strings.tr/en.json` (reddet dili, PLAN-UI 4.3/4.5 kalıntıları, `.cs` literal'leri: `MainWindow.xaml:306/323/347`, `ProcessingPage.xaml:329-331`, `CallWindow.xaml:645/648/1006/1009`, `ShellViewModel.cs:244/318/321`, `LedgerViewModel.cs:319/326/343`, `CallerOverlay.xaml:5`, `RecordingOverlay.xaml.cs:85`); `AppSettings.TodoShowDone`; `MainWindow.xaml.cs:355` ölü `Setup_Click`.

**Testler.** `SuggestionsOnTheTodoPageTests` ek (NotifyChanged sonrası Done güncel; DoneCount ShowDone kapalıyken dolu); `ProcessingViewModelTests` (Requeue Notice); `SttProbeTests:72` yeniden (`DoesNotContain("gpt-4o")`, sıra `[gpt-4o-transcribe, whisper-1]`); `SttEndpointViewModelTests` (TestAsync sırayı bozmaz); `LocalisationTests` ek: "Gizle" yalnız overlay anahtarlarında + `.cs` içindeki `Localisation.T` anahtarları mevcut; `WindowSmokeTests` mevcut.

**Kabul.** Kaydet − içerik ≤ 32 px @1920 (elle + ekran görüntüsü, günlüğe); STT kutusunda ilk beş satır transkripsiyon modeli (OpenAI ile canlı); "Yaptım" sonrası Yapılacaklar aynı anda güncel.

### Paket A2 — Şema v15: bayatlık, söz kararları, kulak teyidi; şikâyet 1, 7, 8 (v3.0.x)

**Şema v15**
```sql
ALTER TABLE reading_note     ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;
ALTER TABLE deception_note   ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;
ALTER TABLE consistency_note ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;
ALTER TABLE action_item      ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;
ALTER TABLE call_summary     ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;
ALTER TABLE commitment ADD COLUMN created_at TEXT;          -- eski satırlarda NULL: "bilinmiyor"
ALTER TABLE commitment ADD COLUMN fulfilled_at TEXT;
ALTER TABLE commitment ADD COLUMN decided_at TEXT;          -- dismiss/fulfil/reopen damgası
ALTER TABLE commitment ADD COLUMN user_deadline_date TEXT;  -- KULLANICI: Ertele; deadline_date makine alanı kalır
ALTER TABLE commitment ADD COLUMN user_obligation TEXT;     -- KULLANICI: ✎ düzenleme; quote/quote_start_ms dokunulmaz
ALTER TABLE commitment ADD COLUMN edited_at TEXT;
ALTER TABLE flag       ADD COLUMN decided_at TEXT;
ALTER TABLE action_item ADD COLUMN decided_at TEXT;
CREATE TABLE IF NOT EXISTS verdict (                        -- KULLANICI: kulak teyidi, tek tablo
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    call_id      INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
    kind         TEXT    NOT NULL,   -- 'flag' | 'kufur' | 'dolgu' | 'bilgi' | 'ton' | 'canli' | 'kalip'
    target_id    INTEGER,            -- flag.id / tactic_evidence.id (varsa)
    quote_folded TEXT    NOT NULL,   -- yeniden hesapta eşleme anahtarı (start_ms kayabilir)
    start_ms     INTEGER NOT NULL,
    verdict      INTEGER NOT NULL,   -- 1 doğru · 0 yanlış duyulmuş · 2 bu o değil · 3 uyarı isterdim · 4 gereksiz
    decided_at   TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_verdict_call ON verdict(call_id, kind);
```
`ALTER … ADD COLUMN` `Database.Migrate` tarafından idempotent (`Database.cs:130`); NOT NULL yok. `verdict` YOLHARITASI Faz 3.2 "kulak teyidi"nin (`flag_verification`) genellenmiş hâli — Aynam kesinliği, Kalıplar teyidi, prosody kalibrasyonu ve canlı alarm kapısı **aynı tablodan** okur. `MergeArchive` `toCall` + `map_version` remap.

**Dosyalar (Core).** `Schema.cs`, `Migrations.cs` (`new(15, "Türev notlar hangi dökümden üretildiğini bilsin; söz kararları damgalansın; kulak teyidi", [...])`); `Repository.cs`: `SaveReading/SaveDeception/SaveConsistencyNote/SaveSummary/InsertAction` (`:2352, 2387, 2419`) `transcript_version_id` alt sorgusu; `DerivedStale(callId)`; `ReopenCommitment`, `RestoreCommitment`, `RestoreFlag`, `SetUserDeadline`, `SetUserObligation`, `DismissCommitments(ids)`, `DismissFlags(ids)`, `DismissedLedger()`, `PromiseLedger(since?, contactId?, includeClosed)` (tek sorgu, `LEFT JOIN contact`, `JOIN call`; durum kod tarafında), `FulfilCommitment(id, byCallId, at)`, `SaveVerdict/Verdicts(callId, kind)`; `ClearAnalysis :2148-2181` koşuluna `AND edited_at IS NULL AND user_deadline_date IS NULL` + `SurvivingCommitmentKeys(callId)` = (ByMe, katlanmış Quote) süzgeci `InsertCommitment` öncesi (**K4 aynası**: korunan satır yeniden eklenmesin; `DismissedFlagKeys :2207` kalıbı); `SweepLedger :3352` aynı koruma. `Analysis/AnalysisPipeline.cs:158, 396-403, 440` `spokenOn`; `Analysis/ActionExtraction.cs:162` aynı. `Domain/Analysis.cs` `Commitment` yeni alanlar + `EffectiveDeadline`.

**Dosyalar (App).** `Services/LedgerActions.cs` (yeni); `LedgerViewModel.cs:298-344` → `PendingUndo` (`TodoViewModel.cs:264-291` kalıbı), `CanFulfil` kalkar (Tutuldu Defter'den çıkar), `LedgerFilter.Dismissed`, `SortMode`, kaynak süzgeci; `LedgerPage.xaml` (çipler `WrapPanel`, Reddedilenler, Seç, Sırala, Reddet 12 px ayrık, Geri al InfoBar); `CallWindow.xaml` Defter/Aksiyonlar/Tutarlılık/Okuma başına `ui:InfoBar` bayatlık + söz kartlarına Tutuldu/Reddet/✎; `ContactWindow.xaml:528-631` aynı fiiller; `CallWindowViewModel.cs:1114-1130` `IsReadingStale` (+ Deception/Consistency/Actions/Ledger); `CallOrchestrator.cs:2017` sonrası: `ConsistencyAutomatically` açıksa `ClearConsistency`, aksi hâlde yalnız Notice; `:1312` dalında bildirim; `TranscriptVersionsWindow` InfoBar; **dikey alan**: `CallWindow.xaml` bant birleştirmeleri (§4.3), `ContactWindow.xaml:33-82` HeroCard, `Theme.xaml:65`.

**Testler.** `MigrationTests` v15 bloğu (+ eksik v8/v9/v10/v14 assertion'ları — `MigrationTests.cs:265-276` v13'te bitiyor); `DerivedFreshnessTests` (yeni: yeniden döküm → `IsStale` true; sütun NULL → "bilinmiyor"); `LedgerUndoTests` (Dismiss→Undo; Fulfil→Reopen; Ertele sonrası `MovedDeadlines` kişiye bayrak yazmaz; düzenlenmiş satır `ClearAnalysis`'ten sağ çıkar ve **iki kez çözümlemede tek satır kalır**); `PromiseLedgerTests`; `TurkishDatesTests` + `AnalysisPipelineTests` ("3 hafta önceki görüşmede 'cuma' o haftaya"); `ArchiveMergeTests` v15 arşivle (`map_version`); `LayoutTests` (yeni; seed'li `CallWindowViewModel`, `Measure(880,720)`); `WindowSmokeTests` mevcut kurulumlar yeni kaynakları yakalar.

**Kabul.** Yeniden döküm sonrası Okuma ya boş ya uyarılı, asla sessiz bayat; farklı günde yeniden çözümleme `deadline_date`'i değiştirmez; döküm alanı ≥ 400 px @720 (taban önce ölçülüp günlüğe).

**Risk / geri alma.** Göç geri alınmaz (sevk kuralı); sütunlar nullable, eski kod okumaz. `SurvivingCommitmentKeys` yanlış eşleşirse söz kaybolmaz (satır korunur), yalnız kopya eklenmez.

### Paket B — Sözler sayfası + ray düzeni (v3.1.0)

**Yeniden kullanılan.** `PromiseLedger` (A2), `AllOpenCommitments :1913`, `OverdueCommitments :1881`, `Own/TheirCommitmentsBetween :2906/:2941`, `DeterministicChecks.OverdueCommitments :27-54`, `CalendarEntryKind` (`CalendarViewModel.cs:12-28`), `RemindWindow.Open(owner, repository, callId, subject, reason)` (`RemindWindow.xaml.cs:131-132`), `CallWindow.Show` (`CallWindow.xaml.cs:232`), `LedgerActions`.

**Dosyalar.** `App/ViewModels/PromisesViewModel.cs`, `App/Views/PromisesPage.xaml(.cs)` (§4.1); `ShellViewModel` (`ShellPage.Promises`, `PageName`, `Navigate`, `RefreshAll`; ray rozeti `OpenFlagCount :491` → Defter(dikkat) + Sözler(vadesi geçen) iki rozet); `MainWindow.xaml` ray yeniden düzeni + gruplar; `ActionRegistry` `Key` alanı + `Todo`/`Promises`/`Mirror` kayıtları; `MainWindow.xaml.cs:335-343` Ctrl+? örtüsü `ActionRegistry`'den ve loc'a; `MainWindow.xaml:25-33` `InputBindings` kodla `ActionRegistry`'den; `CallerOverlay` iki yön; `OverviewPage.xaml:151-195` liste bölümü → tek satır kart (iki Dikkat kartı kalır); `LedgerPage` söz çipleri kalkar; `Repository.SuggestFulfilment(commitmentId)` ("tutuldu mu?" önerisi); metinler `promisespage.*`, `mainwindow.sozler`, `mainwindow.aynam`.

**Testler.** `PromiseLedgerTests` genişletme (iki yön doğru sütun; koşullu söz "vadesi geçti" olmaz; "açık kaldı" yalnız vade sonrası görüşme varsa; reddedilen yalnız Reddedilen çipinde); `ActionRegistryTests` (her `ShellPage` için eylem; `Key` tekil); `PromiseSideTests` mevcut ayrık Dikkat kartları korunur; `WindowSmokeTests.Build("Sözler", …)`.

**Kabul.** Kullanıcının arşivinde sayfa toplamı `AllOpenCommitments` ile birebir; 10 satır tıklanınca doğru ms'ten çalar; "tutuldu mu?" öneri kabul oranı ölçülür (30 öneri; < %30 → kapatılır).

### Paket C — Kelime güveni: ölçek + saklama (D ile aynı sürüm)

- `worker/vt_worker/engines/cloud_providers.py:87` ElevenLabs `logprob` → `math.exp(logprob)`; `merge.py:36-40` `Word.probability` sözleşmesi "0..1 ya da None"; `test_cloud_providers.py` ek (logprob −0,1 → p≈0,905).
- `Core/Domain/Models.cs:148` `SpokenWord(int StartMs, int EndMs, string Text, double? Probability = null)` (141-143 yorumu güncellenir: artık okuyan var); `Core/Storage/SegmentWords.cs:28` dörtlü (p null ise üçlü yaz), `:50` `< 3` kuralı dörtlüyü zaten okur; `CallOrchestrator.cs:1998-1999` `w.Probability` aktarır; `transcript_version.segments` aynı yazıcıdan geçer (`Repository.cs:1456`), eski sürümler p=null.
- **Eşik sabit 0,6 değil, motor başına ölçülür:** arşivde küfür sözlüğü eşleşmeleri (≥ 60) kulakla etiketlenir (`verdict`), motor başına "kesinlik ≥ 0,9 veren en düşük t" seçilir; **eşik seçimi ve kesinlik raporu ayrı örneklemde** (30/30). Sonuç `ISLEM-GUNLUGU`'ne ve Ayarlar > Koçluk tablosuna.
- Testler: `SegmentWordsTests` (üçlü okunur; dörtlü gidiş-dönüş; p null → üçlü); `WorkerProtocolTests` gerçek satır.

### Paket D — Aynam: sayılabilir davranış (şema v16; v3.1.x)

**Şema v16**
```sql
CREATE TABLE IF NOT EXISTS speech_habit (            -- makine önbelleği
    call_id               INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
    transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
    lexicon_version       INTEGER NOT NULL,
    json                  TEXT    NOT NULL,            -- HabitReport + TalkStats (seri tek SELECT olsun)
    created_at            TEXT    NOT NULL
);
CREATE TABLE IF NOT EXISTS habit_lexicon (           -- KULLANICI (tag_def kalıbı: tohumlanır, düzenlenir, yedeğe girer)
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    kind           TEXT    NOT NULL,                   -- 'kufur' | 'dolgu' | 'sive' | 'haric'
    lexeme_folded  TEXT    NOT NULL,                   -- gövde, NormalizeForSearch ile
    suffixes       TEXT,                               -- izinli ek listesi (JSON) — token sınırı + gövde + ek
    lexeme         TEXT    NOT NULL,
    position       INTEGER NOT NULL DEFAULT 0,
    UNIQUE(kind, lexeme_folded)
);
CREATE TABLE IF NOT EXISTS call_intent (             -- KULLANICI: Niyet kartı
    call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
    text       TEXT    NOT NULL,
    updated_at TEXT    NOT NULL
);
```
Tohum sözlük `Core/Resources/habits.tr.json` gömülü kaynak (`VoiceTranscript.Core.csproj:17-26` `WithCulture="false"` + `LogicalName` **zorunlu** — `.tr.` adı MSBuild'de kültür etiketi sayılır, ana derleme sessizce boş kalır) → ilk açılışta `habit_lexicon`'a yazılır (`tag_def` 6 tohum etiket kalıbı); Ayarlar'daki [Düzenle] `SozlukWindow` (TagManagerWindow kalıbı). Loglara kelime yazılmaz.

**Dosyalar.** `Core/Analysis/TalkStats.cs` (gövde `CallWindowViewModel.cs:296-343`; `ContactsViewModel.cs:461` kopyası silinir; `SuspectedEcho`/`OverlapsOtherSpeaker` dışlanır; soru sayımı `EndsWith('?')`; cevap gecikmesi medyanı); `Core/Analysis/SpeechHabits.cs` (küfür/dolgu/hız/verilen bilgi; yalnız `IsMe && !SuspectedEcho`; güven kovaları §4.2; "verilen bilgi" yalnız biçimi sayılabilir türler — tutar, ≥ 6 hane, IBAN kalıbı, telefon kalıbı, `TurkishDates.TryExplicit` — **değer saklanmaz**, tür + StartMs); `Core/Analysis/HabitLexicon.cs` (token sınırı + gövde + ek; `text_normalised` üzerinde); `Core/Analysis/HabitTrend.cs` + `HabitTrendLayout` (saf, testli; dakika/100-kelime paydası; motor kırılımı); `Repository`: `SaveHabits/GetHabits` (`SaveReading` upsert kalıbı), `HabitSeries(since, contactId?, engine?)` tek SELECT, `Lexicon*`, `CallIntent*`; `CallOrchestrator`: `TranscriptReplaced` (`:2034`) tetiği ile hesap (tek tetik; `keepTranscript` yolunda "satır yoksa hesapla"), ayar kapısı yok; geriye dönük toplu hesap `CompressBacklog` kalıbı (`:1769`, `IsBackground + Lowest`), `_processing` kuyruğundan **geçmez**, Durum > İşlemler'de ilerleme; `App/ViewModels/MirrorViewModel.cs` + `Views/MirrorPage.xaml`; `CallWindowViewModel.LoadHabits` + `CallWindow.xaml` "Aynam" sekmesi; `NiyetWindow` + `CallerOverlay`/`RecordingOverlay` satırı; `SettingsWindow` Koçluk kategorisi; "Ne oldu?" tostu (`ShellViewModel.cs:163-192`) üç sayı; kabuk kayıtları (8 dosya); `DeceptionAnalysis.cs:12-25` ve `DeterministicChecks` özetleri gibi Core sert Türkçe → loc anahtarı (App çevirir).

**Şive:** yazılmadan önce ön-ölçüm — arşivde `segment.text_normalised` üzerinde `\b\w+yom\b|\b\w+yon\b|\bnapiyon\b|\bgari\b|\bhele\b` sayımı + 40 eşleşme dinleme. Eşleşme < 1/görüşme ya da kesinlik < 0,6 → **yazılmaz**, Aynam'da "şive: ölçülmüyor (neden ▸)". Recall zaten ölçülemez (STT normalize eder) — yazılırsa yanına "yalnız yazıya dökülen biçimler" notu.

**Testler.** `TalkStatsTests` (pay/kesme/soru; yankı vakası; iki VM aynı sonuç); `SpeechHabitsTests` (tam-kelime+ek eşleşme; "klasik" küfür değil; p<t belirsiz; p=null segment kapısı; yankılı segment sayılmaz; kelimesiz satır çökmez; `verdict=0` düşer); `HabitLexiconTests` (gömülü kaynak yüklenir ve boş değil; tohumlama idempotent); `HabitTrendTests`/`HabitTrendLayoutTests`; `MigrationTests` v16; `RepositoryTests` (bayat sürümde null); smoke `Build("Aynam", …)`, `NiyetWindow`, `SozlukWindow`.

**Kabul (ölçü · eşik · geri alma).** İlk 5 gerçek görüşmede (#22, #17, #38, #24, #14 — çözülmüş WAV'lar mevcut) kullanıcı her sayımı dinler (`verdict`). Dedektör kesinliği: küfür ≥ %90, dolgu ≥ %85; ≥ 30 dinlenmiş sayım. Eşiğin altındaki dedektör "belirsiz"e düşer, kart kalkar, anlar kalır, `ISLEM-GUNLUGU`'ne yazılır. İki-motor tutarlılığı: #22'nin dört motorlu çıktısında (ölçüm hattı) sayaç farkı — küfür ±1, dolgu ±%15; aşıyorsa seri motor başına ayrı. `HabitSeries(365 gün)` < 1 sn.

### Paket E — Kişi kartı (kanıt): Gidişat, Kalıplar, Rakam yolculuğu, Elindeki kayıtlar (şema v17; v3.2.0)

**Şema v17**
```sql
CREATE TABLE IF NOT EXISTS tactic_evidence (          -- makine kanıt; DÜZEY/DEĞERLENDİRME ASLA KOPYALANMAZ; hiçbir isteme beslenmez
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    call_id           INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
    contact_id        INTEGER REFERENCES contact(id) ON DELETE CASCADE,
    transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
    source            TEXT    NOT NULL,                -- 'deception' | 'pipeline'
    tactic            TEXT    NOT NULL,                -- beyaz liste; bilinmeyen DÜŞER ('diger' yazılmaz)
    by_me             INTEGER NOT NULL,
    quote             TEXT    NOT NULL,
    quote_start_ms    INTEGER NOT NULL DEFAULT 0,
    low_confidence    INTEGER NOT NULL DEFAULT 0,      -- located.LowConfidence
    model_used        TEXT,
    dismissed_by_user INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_tactic_contact ON tactic_evidence(contact_id, dismissed_by_user);
CREATE TABLE IF NOT EXISTS speech_act (                -- makine kanıt: extraction 'sorular' kalıcılaşır
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    call_id        INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
    contact_id     INTEGER REFERENCES contact(id) ON DELETE CASCADE,
    by_me          INTEGER NOT NULL,
    kind           TEXT    NOT NULL,                   -- 'soru' (ilk sürüm yalnız bu)
    answer_status  TEXT,                               -- cevaplandi | kismi | kacamak | savusturuldu
    quote          TEXT    NOT NULL,
    quote_start_ms INTEGER NOT NULL DEFAULT 0,
    low_confidence INTEGER NOT NULL DEFAULT 0,
    created_at     TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_speech_act_contact ON speech_act(contact_id, kind);
```
`tactic_evidence` ve `speech_act` `LedgerTables` (`:416`) ve `MergeContacts`'a girer, `MergeArchive` `toCallAndContact`; `ClearAnalysis`'e `DELETE FROM speech_act WHERE call_id=@c` ve `DELETE FROM tactic_evidence WHERE call_id=@c AND source='pipeline' AND dismissed_by_user=0`; `ReplaceTacticEvidence(callId, 'deception', lines)` DeceptionAnalysis koşumunda (`DeceptionAnalysis.cs:113` sonrası), `DismissedTacticKeys` ile dirilme yok; `CountLedgerEntriesForCall` sayımı `speech_act`'i **saymaz**.

**Kararlar.** (1) `deception_note` çıkmaz sokak kuralı yalnız alıntı için gevşer (§2); `DeceptionPrompt.cs:76` `taktik` **enum**'a; şemasız düşürme yolunda beyaz liste (`ActionExtraction.cs:129-132` kalıbı). (2) `baski_isaretleri` (`ExtractionPrompt.cs:119-133`): **önce ölçüm** — 5 görüşmede kaçı `QuoteVerifier`'dan geçiyor, kullanıcı kaçını doğru buluyor; kesinlik ≥ 0,6 ise `Absorb`'a dördüncü döngü → `tactic_evidence(source='pipeline')`, Kalıplar'da varsayılan süzgeç dışı; değilse şemadan çıkarılır ve parça başına `completion_tokens` düşüşü ölçülür.

**Dosyalar.** `Core/Analysis/ContactTrend.cs` (saf; ay serisi; Unknown yön paydada değil); `Repository`: `ContactSeries(contactId)` (`speech_habit.json` içindeki TalkStats'tan tek SELECT), `ContactPatterns(contactId)` (`flag` ∪ `tactic_evidence` ∪ `consistency_note` olumlu gözlemler; kind × source; teyit sayıları `verdict`'ten), `PatternRows(contactId, kind, source)`, `FigureJourney(contactId)` (`claim`, `ix_claim_lookup`; YOLHARITASI Faz 3.4), `OwnWords(contactId)` (Elindeki kayıtlar; `BuildPriorStatements` sıra hatası düzeltilmiş yeni yardımcı `LedgerContext(repository, contactId, excludeCallId?, claims, promises, flags)` — instance metodu statik yapılmaz); `AnalysisPipeline.Absorb` `sorular` → `speech_act` (`EvasionRate` çalışmaya devam eder); `App/Views/ContactCardView.xaml` (UserControl; ContactWindow + ContactsPage paylaşır) + `ContactCardViewModel`; `ContactWindow.xaml` Defter sekmesi kalkar, Kişi kartı girer; `ContactsPage.xaml` sekme eklenir; Kalıplar rozet etiketleri `ContactsViewModel.cs:84-96` `Kind` sözlüğüyle aynı kaynaktan.

**Testler.** `TacticEvidenceTests` (düzey/gerekçe kopyalanmaz; bilinmeyen taktik düşer; dismissed dirilmez; pipeline/deception sahiplik ayrı; hiçbir prompt girdisinde geçmez — `LastUserPrompt` denetimi); `SpeechActTests`; `ContactTrendTests`; `ContactPatternsTests` (kind×source; dismissed hariç; düşük güven gri); `FigureJourneyTests`; `DeceptionAnalysisTests` ek; `AnalysisPipelineTests` ek; `MigrationTests` v17; smoke (ContactWindow, ContactsPage).

**Kabul.** En çok görüşülen kişide Kalıplar toplamı Defter Dikkat satırlarıyla (kaynak süzgeciyle) birebir; tür başına ret oranı ≤ %30 (30 satır sonra); aşanı çubuktan düşür. Gidişat serisi `TalkStatsTests` + SQL elle.

### Paket G — Ses düzeyi ve perde (worker `prosody`, şema v18; v3.3.0)

- `worker/vt_worker/prosody.py` (yeni, tembel numpy): `read_wav` (`speaker.py:197`), `as_strided` çerçeveleme (`:155-159`), RMS→dBFS (`:190-191`), konuşma maskesi −40 dBFS (`:75`, tek eşik), YIN/CMND vektörize (parti 4096 çerçeve × rfft 1024; 60–400 Hz; eşik 0,15), 0,5 sn kutu: medyan F0, IQR, ortalama dBFS, sesli oran; parça parça bellek. `__main__.py` `cmd_prosody` (`cmd_speaker :422-463` kalıbı), `choices` (`:484`), olay `{type:"prosody", …, channels:{mic:{floor_dbfs, speech_seconds, bins:[[t,dbfs,f0|null,voiced]]}, far:{…}}}`; `test_prosody.py` (220 Hz sinüs ±2 Hz; −12,2 dBFS; sessizlik voiced 0; stereo ValueError); `test_imports` kapsar.
- C#: `WorkerProtocol.cs:42-51` `"prosody"`, `ProsodyRequest`; `PythonWorkerHost.AnalyseProsodyAsync` (`EmbedSpeakerAsync :156-179` kalıbı); şema v18 `prosody(call_id PK, audio_key TEXT, json, created_at)` + `audio_event(id, call_id, transcript_version_id, channel, start_ms, end_ms, kind)` + `live_alert`'e gerek yok (`verdict kind='canli'`); `Core/Analysis/ProsodySeries.cs` (saf: kanal başına **MAD** z-skor, yalnız sesli kutular, yarım ton birimi `12·log2(f0/medyan)`; zirve: z > 2, ≥ 4 ardışık kutu, iki ölçüden biri; `overlaps_other_speaker`/`suspected_echo` bölgeleri boş); `CallOrchestrator`: döküm bittikten sonra `_gpu` içinde CPU'da (`MightUseGpu` bağımsız), `EnsurePcm` (`AudioMaterialiser.cs:36`), `AudioToSweep` görüşmelerde "ses yok"; `ProcessingStage.Prosody`; `TimelinePanel.OnRender` şeritleri (varsayılan **kapalı**); `ProsodySeriesTests`.
- **Kurallar koda yazılır:** dBFS görüşmeler arası **asla** (mic kazancı donanıma, far kazancı WhatsApp AGC'sine bağlı — `chunking.py:184-187` far −95 dBFS taban); F0 görüşmeler arası **yalnız** kararlılık ölçümü geçince (§6.3) ve kişi başına F0 medyanı saklamak `contact_voice` kuralıyla (opt-in "kişinin ses perdesi saklansın", `Schema.cs:490`; "Bütün ses izlerini sil" ile silinir); far kanal şeridi kendi kalibrasyonu geçmeden gizli; prosody ile manipülasyon bayrakları aynı satırda **birleştirilmez**, zirve işareti Tutarlılık/Kalıplar satırlarında yok.
- **Kabul.** 20 dk × 2 kanal CPU ≤ 15 sn; mic: 60 zirve dinlenir (`verdict kind='ton'`), "değişim var" ≥ %70 → şerit açılabilir; far: ayrı 60 zirve "o yükseldi mi" ≥ %70; < eşik → z 2,5 / 4 sn; ikinci turda da altında → şerit kaldırılır, sayılar Aynam'da kalır. Kalibrasyon ve eşikler `prosody.py` sabitleri olarak ölçüm cümlesiyle (`speaker.py:68-70` kalıbı); `ISLEM-GUNLUGU`'ne.

### Paket H — Kayıt şeridi canlı ölçer + brifing (v3.3.x)

§4.10. `App/Services/LiveTalkMeter.cs`; `CallOrchestrator.BeginRecordingAsync` (`:904-912` SpeakerIdentifier yanı, `settings.LiveTalkMeter` kapısı, varsayılan kapalı); `RecordingOverlay.xaml:42-91` iki öğe + katlanır satır; `AppSettings.LiveTalkMeter`, `BriefingExpanded`; `LiveTalkMeterTests` (`FileAudioSource` ile sentetik iki kanal → pay %60±2; sessiz kanal 0; **abone 50 ms bloke edince iki akış da yazılmaya devam eder**); çevrimdışı alarm ön-ölçümü: 20 arşiv görüşmesinde `FileAudioSource` gerçek zamanlı akışla v1 kuralı (+6 dB / 8 sn / histerezis 3 dB 5 sn / refrakter 60 sn / tavan 5) → alarm/saat, 40 alarm dinlenir, kesinlik ≥ 0,6; kulaklıksız görüşmeler ayrı küme. **Kabul:** kayıt sırasında CPU ≤ +2 puan; görüşme kaybı sıfır.

### Paket I — Kişi kartı: modelin görüşü (opt-in, şema v19; v3.4.0)

**Şema v19**
```sql
CREATE TABLE IF NOT EXISTS contact_reading (           -- ölü uç: join yok, hiçbir isteme beslenmez; geçmişli
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    contact_id     INTEGER NOT NULL REFERENCES contact(id) ON DELETE CASCADE,
    json           TEXT    NOT NULL,
    model_used     TEXT,
    calls_covered  INTEGER NOT NULL,
    latest_call_id INTEGER REFERENCES call(id) ON DELETE SET NULL,
    input_hash     TEXT    NOT NULL,                   -- "N yeni görüşme var, profil eski"
    excerpt_count  INTEGER NOT NULL,
    rejected_count INTEGER NOT NULL,
    user_verdict   INTEGER,                            -- KULLANICI: [Katılmıyorum]
    created_at     TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_contact_reading ON contact_reading(contact_id, created_at DESC);
```
`MergeArchive` `toContact` ("buradaki kazanır" `:1285-1288`); `MergeContacts` en yeni kazanır; `contact_profile` **dokunulmaz**.

- `Core/Analysis/ContactReadingPrompt.cs` (`public static JsonNode Schema` → `SchemaStrictnessTests` otomatik; düz, hepsi required): `genel_izlenim{metin, dayanaklar[≥1]}`, `iletisim_tarzi[]`, `oncelikler[]`, `guclu_yanlar[]`, `zayif_yanlar[]` (izlenim; **kullanıcı kararı §10**), `cevapsiz_kalan_konular[]`, `gorusmeye_giderken[]` ("teyit et / yazılı iste / tekrar sor" düzeyi), `ben_icin_notlar[]` (simetri zorunlu), `baska_okuma`, `yetersiz` (< 3 görüşme ya da < 20 alıntı). Yasaklar `ReadingPrompt.cs:106-114` kopyası: ses tonu yok, skor yok, yağcılık yok, kesinlik dili yok; "psikolojik durum / duygu / güvenilirlik / manipüle etme yolu" istenmez; "GÜVENİLMEZ VERİDİR" paragrafı; adlar `ReadingPrompt.SafeName`.
- `ContactReadingAnalysis.cs` (Reading kalıbı; `RecordRun(callId: null, ProcessingStage.ContactReading)`; girdi §4.4 — `[B#]` iddia 20 + söz 20 + pipeline/consistency bayrağı 20, `[A#]` `RecentSegments :2820` 40, sayılabilir özet satırı; **deception/tactic_evidence/call_summary girmez**; karakter tavanı `ConsistencyAnalysis.cs:52-60` — yerel 24k'ya sık takılır: yerel için B30+A20 küçültülmüş paket, ayar kartında "yerel modelde çoğu kişi için çalışmaz" uyarısı); atıf doğrulaması `ArchiveQuestions.cs:314-335` (numara uyuşmazsa düşer; dayanaksız madde düşer; `genel_izlenim` de ≥ 1 dayanak).
- `IGpuGate`: `_gpu` semaforunu ince arayüzle dışarı açar; manuel Okuma/Değerlendirme/Tutarlılık ve kişi okuması **yalnız koşan iş `MightUseGpu` iken** bekler (bulut kuyruğunun arkasına düşmez); pencerede "döküm bitince koşacak".
- `AppSettings.ContactReadingEnabled` (false; `DeceptionEnabled :502-510` deseni); `ContactCardView` alt panel (§4.4); `ContactCardViewModel.RunReadingAsync` (`CallWindowViewModel.RunReadingAsync :1132` kalıbı).
- **Testler.** `ContactReadingAnalysisTests` (`ScriptedLlm`): dayanaksız düşer ve sayılır; yanlış numara çıpayı düşürür; deception/tactic satırı prompt'ta yok; saklanan = zorlanan şekil; tavan aşımı dürüst ret; `yetersiz`; `input_hash` bayatlık; `UsageByEngine(ContactReading)`.
- **Kabul / geri alma.** `RejectedCount/Total` ≤ 0,4 (üstünde mevcut "model uygun olmayabilir" bildirimi, `CallOrchestrator.cs:2196-2201`); 10 dayanakta ≥ 8'i gözlemi taşıyor (elle); [Katılmıyorum] oranı 3 kişide üst üste > %50 → ayar kartı "ölçüm olumsuz" rozetiyle devre dışı + günlük; ikinci turda da → kod çıkar.

### Paket J — ElevenLabs `audio_event` + dördüncü STT sütunu (anahtar gelince; şema v18'deki tablo)

`cloud_providers.py:64` `tag_audio_events: "true"`; `_to_segments` `type=="audio_event"` → `AudioEvent` listesi (Segment'e değil); worker `result.audio_events[]`; `WorkerResult`; `audio_event` tablosu (`transcript_version_id`, `TranscriptReplaced` ile sil-yaz; `ClearAnalysis` **dokunmaz**); zaman çizgisi glifi; Aynam "3 kez güldün"; Kalıplar'a girmez. Ölçüm hattında (`tools/olcum/`) 7 görüşmede beşinci sütun: kanal başına kapsama, kelime sayısı, sıra uyumu, kelime güveni dağılımı (exp(logprob)). Kahkaha doğrulaması: 40 olay, kesinlik ≥ 0,8; altında glif kalkar. Test: `test_cloud_providers.py` (olay Segment'e girmez, listeye girer). Motor değişince "bu motor olay etiketlemiyor" satırı.

---

## 6. Ölçüm kapıları ve deney protokolleri

Hepsi `tools/olcum/` hattında, kod sevkinden bağımsız; sonuç ne olursa olsun `docs/ISLEM-GUNLUGU.md`'ye sayılarla.

### 6.1 Şive ön-ölçümü (D'den önce, yarım gün)
Tek sorgu (`segment.text_normalised`, kalıplar §5-D) + 40 dinleme. Kabul: ≥ 1 eşleşme/görüşme **ve** kesinlik ≥ 0,6; değilse dedektör yazılmaz, Aynam "ölçülmüyor" der.

### 6.2 Aynam dedektör kalibrasyonu (D ile)
5 gerçek görüşme; kullanıcı her sayımı dinler; eşik seçimi (30) ve kesinlik raporu (30) ayrı örneklem; küfür ≥ 0,9 / dolgu ≥ 0,85; motor başına `t`. İki-motor tutarlılığı #22'de.

### 6.3 Prosody kalibrasyonu ve F0 kararlılığı (G ile)
Birim: sinüs/sessizlik/stereo. Gerçek: 6 görüşme (SONUC.md tablosu + #22) zirve listesi; mic 60 zirve "değişim var" ≥ %70; far ayrı 60 "o yükseldi mi" ≥ %70. **Kararlılık:** ≥ 5 görüşmeli 3 kişi (far) + kullanıcı (mic): görüşme-medyan F0 sapması σ ≤ 2 yarım ton → görüşmeler arası F0 medyanı Gidişat'a girebilir (opt-in ses özelliği kapısıyla); σ > 3 → yazılmaz. Sentetik sessizlikte bayrak ölçülmez (`ISLEM-GUNLUGU.md:2103`).

### 6.4 Canlı alarm çevrimdışı ön-ölçümü (H ile) — §5-H.

### 6.5 Hume AI (kod yok)
1. **Güç hesabı:** ölçü hit@±10 sn, eşleştirilmiş (McNemar); beklenen yerel 0,45 / Hume 0,70 → %80 güç için ≈ **70 altın an** + 70 kontrol anı → **12–15 görüşme**, iki kanal. 5 görüşme "deneme", karar değil.
2. **Altın etiket, körleme, iki küme:** kullanıcı zaman çizgisinde dinlerken "burada bir değişim var" işaretler (şerit kapalı, Hume görülmeden) — bu küme akustik değişimi ölçer ve yerel z-skorun tanımıyla çakışır; bu yüzden **ikinci küme**: kullanıcı dökümü **okuyarak**, ses dinlemeden "burada bir şey oldu" işaretler (akustik olmayan anlar). Hume'un katma değeri ancak ikinci kümede görülebilir; birinci kümede beklenen sonuç "yerel yeter" — bu kullanıcıya deneyden önce yazılı söylenir.
3. **Veri/gizlilik:** `tools/olcum/` WAV'ları; far kanalı üçüncü kişinin sesi — yalnız kullanıcının seçtiği görüşmeler, "ses buluta gider, üçüncü kişinin sesi dahil" yazılı; sonuç dosyalarında anahtar yok (P0 temizliğinden sonra).
4. **Maliyet:** ~0,05 $/dk × 15 × 8 dk × 2 ≈ 12 $; asıl maliyet 3–4 saat etiketleme.
5. **Karşılaştırma:** aynı görüşmelerde yerel z-skor zirveleri, ElevenLabs kahkaha (varsa), Hume'dan türetilen tek "yoğunluk" serisi (48 sınıf **kullanılmaz**; en yüksek sınıf olasılığındaki değişimin z-skoru; tanım deneyden önce sabitlenir); `sayfa-dort.py` kalıbında HTML.
6. **Karar:** hit farkı McNemar p < 0,05 **ve** Hume yanlış alarm/10 dk ≤ yerelin 1,5 katı **ve** ikinci kümede ≥ 15 puan fark. Tutarsa: ayrı `ProcessingStage`, varsayılan kapalı, "ses buluta gider" uyarısı, maliyet/dk Usage'da, çıktı yalnız "değişim anı" (sınıf adı hiçbir durumda ekrana çıkmaz). Tutmazsa: olumsuz sonuç günlüğe, kod girmez.

### 6.6 Canlı küfür / dolandırıcılık kalıbı çevrimdışı ön-ölçümü (v2 kapısı)
Arşiv WAV'ları 10 sn parçalarla `faster-whisper small int8 CPU` (`AsrCatalog.cs:162-176`; WER 24) ile; küfür sözlüğü ve `ScamPatterns` eşleşmeleri large-v3 + kulak referansına karşı; **en kötü koşul** dahil (ProcessQueue GPU'da large-v3 dökerken). Geri çağırma ≥ 0,7 ve kesinlik ≥ 0,7 → canlı kelime uyarısı ikinci tur adayı (dolandırıcılık kalıbı küfürden **önce**; fayda/bedel farklı); değilse belgelenir. Gecikme ≤ 15 sn ölçülür.

---

## 7. Yapılmayacaklar (gerekçeli; ürün içinde "neden ▸" ile görünür)

1. Güven / yalan / manipülasyon **skoru**, kişi düzeyinde de (`PRODUCT.md:155`; taban oranı görüşmeler boyunca kötüleşir).
2. **Duygu etiketi** ses ya da metinden, kendi/karşı (SER Türkçede doğrulanmadı; §10.3). Hume tutsa bile sınıf adı ekrana çıkmaz.
3. Ses stresinden **yalan tespiti** (şans düzeyi); prosody ile manipülasyon bayrakları birleştirilmez.
4. **Psikolojik durum / duygu durumu / güvenilirlik** analizi (ölçülemez, yanlışı zararlı). Kişi kartı bunu yazılı söyler.
5. **"Kullanabileceğin argümanlar / manipüle etme yolu"** istemi (`AppSettings.cs:483-485`); karşılığı Elindeki kayıtlar + cevapsız sorular + açık kalan sözler (kanıt).
6. **Rol yapamama** ölçüsü (niyet ölçülemez); karşılığı Niyet kartı + [İstemedim] sayacı; Aynam bunu yazılı söyler.
7. **Şive sayacı** ön-ölçüm geçmeden; **özel ad** sayımı (STT büyük harf güvenilmez).
8. Görüşmeler arası **dBFS** karşılaştırması, iki kanalda da; görüşmeler arası **F0** kararlılık ölçülmeden; kişi başına ses özelliği opt-in kapısı olmadan.
9. Makinenin **"tutuldu"** işareti (sesten çıkmaz; yalnız öneri) ve **tutulma oranı** (kullanıcı ihmalini kişiye yazar).
10. `deception_note` **düzey/değerlendirme**sinin kişi düzeyine kopyalanması ya da herhangi bir isteme beslenmesi; `tactic_evidence`/`deception` satırlarının profil istemine girmesi.
11. **Gidişat'ta "bulgu yoğunluğu" serisi** (hangi denetimlerin koştuğunu ölçer, davranışı değil).
12. **Canlı kelime uyarısı** (küfür, dolandırıcılık) çevrimdışı ölçüm geçmeden; canlı alarm "isterdim ≥ %70" kapısı geçmeden.
13. Kayıt/tespit yolunda yeni iş (`Tick()`, `OnPacket`, `LevelChanged`).
14. Grup görüşmelerinde kişi bazlı birikim (far kanal birden çok kişi); Kalıplar "N grup görüşmesi sayılmadı" der.
15. Prosody şeridinin **kalibrasyonsuz** sevki; Hume'un ürün koduna ölçüm bitmeden girmesi.
16. ContactsPage'den oynatıcı/iç sekme kaldırma; "Reddettiklerimi kalıcı sil".
17. **"Verilen bilgi" değerlerinin** (IBAN, telefon, tutar) saklanması — yalnız tür + zaman.

---

## 8. GSM, Apple Watch, SocialZeka

### 8.1 GSM / Phone Link — kapsam dışı (kullanıcı kararı, 5 Eylül)

Kullanıcı: "gsm e phone linke gerek yok." Bu plana GSM işi girmez; Paket L/M ve "ses oturumlarını dök" tanı düğmesi çıkarıldı. Kayıt için araştırılan bulgu (ileride açılırsa): Phone Link aramayı PC'ye alınca ses Bluetooth HFP oturumundan geçer, yalnız arama sırasında var olan "… Hands-Free HF Audio" uç noktası oluşur, süreçler `PhoneExperienceHost.exe` + `CallingShellApp.exe`; masaüstü (cihaz) yakalama çalışıyor, uygulama-bazlı yakalama çalışmıyor; BT kulaklığa aktaramıyor; iPhone'da 26200 serisinde "Transfer to PC" başarısız kaydı var. Elle kayıt (`StartManualRecordingAsync`, `WasapiCaptureBackend`) bugün değişmeden iki kanalı korurdu. Telefonun kendi kaydını içe aktarmak **tek karışık kanal** demek (kim dedi bilinmez; Defter/Aynam/Gidişat kapalı kalır) — kullanıcı "GSM evet, Phone Link hayır" demek istediyse geri gelecek tek yol budur (§12-4). Hukuk notu (tavsiye değil): tarafı olunan görüşmenin kaydı Yargıtay uygulamasında TCK 133/1 dışında; üçüncü kişiye iletme/ifşa TCK 134/2, 133/3; KVKK m.28/1-a "üçüncü kişilere verilmemek" kaydıyla — bulut döküm bu açıdan da `PRODUCT.md:160` kuralına tabi.

### 8.2 Apple Watch — ertelendi (kullanıcı kararı)

Mac/Xcode ve ücretli program yok. Kayıt için: Apple'ın kuralı gereği kayıt yalnız ön planda başlar ve **gelen arama kaydı keser** → Watch telefon görüşmesini kaydedemez; yüz yüze kayıt tek karışık kanal; Windows'a doğrudan yol yok (WatchConnectivity → iPhone → dosya). İleride açılırsa önce "Dosyadan görüşme ekle" (tek kanal içe aktarma, `CallKind.Imported`, `ByMe`'ye dayanan analizler `CallKind.Group` kapısıyla kapalı, `AnalysisPipeline.cs:114-120`) ve `docs/IMPORT-SOZLESMESI.md`; Watch ancak o yol kullanılıyorsa.

### 8.3 SocialZeka — ayrı repo (kullanıcı kararı) ve çatallama

Öneri "marka adı, repo değil" idi (VoiceTranscript2 emsali `YAPILACAKLAR.md:1274-1275`; `UpdateService.cs:43,46` repo adına sert bağlı; test/CI/şema/loc/smoke tek yerde). Kullanıcı **ayrı repo** dedi; plan bu kararla ilerler ve bölünmenin bedelini tek kuralla sınırlar: **iki yönlü birleştirme yok** — koç işi yalnız SocialZeka'da, VoiceTranscript dondurulur (§12-1). Mekanik **Paket R0**'da (§5). Ad, ürün kimliği ve kurulum kimliği değişir; **namespace'ler ve csproj adları `VoiceTranscript.*` kalır** (200+ dosyada mekanik yeniden adlandırma, sıfır kullanıcı değeri, loc/smoke kırılma riski; `docs/MIMARI.md`'ye yazılır).

---

## 9. Mevcut planın EK-5'i ve sıra dışı işler

- **EK-5 (varsayılan bulut servisi seçimi):** koçluk programına girmez; işleme hattının kendi iş emri, A1'e paralel. Ölçümler (#56 dahil) tamamlanmadan karar yok; `DefaultSttEndpointId`, `CloudAsrModelId` kaldırma, "sırayla denenir" metinleri, yerele düşme + kapsama tekrarı; risk altındaki testler EK'te listeli.
- **EK-4 (`min_speech_duration_ms`):** ölçülmemiş tek seçenek; `faster_whisper_engine.py:52-59` `vad_parameters` geçirilerek #57/#58/#61'de ölçülür; günlüğe.

---

## 10. Doğrulama (uçtan uca)

| Katman | Nasıl |
|---|---|
| Birim (C#) | `./test.ps1`; sınıf bazında `--filter-class` (bayrak tekrar); yeni sınıflar: `DerivedFreshnessTests`, `LedgerUndoTests`, `PromiseLedgerTests`, `ActionRegistryTests`, `LayoutTests`, `TalkStatsTests`, `SpeechHabitsTests`, `HabitLexiconTests`, `HabitTrendTests`, `TacticEvidenceTests`, `SpeechActTests`, `ContactTrendTests`, `ContactPatternsTests`, `FigureJourneyTests`, `ProsodySeriesTests`, `LiveTalkMeterTests`, `ContactReadingAnalysisTests`, `SttEndpointViewModelTests`; her testin yorumu "ne kırılınca kırmızı" yazar |
| Şema | `MigrationTests.AnUpgradedDatabaseMatchesAFreshOne` her sürüm bloğu; `TheShippedStepListIsWellFormed`; `ArchiveMergeTests` v15+ arşivle |
| Yerelleştirme | `LocalisationTests` eşlik + yeni `.cs` `Localisation.T` taraması + "Gizle yalnız overlay" kuralı + `{0}` yer tutucu eşliği |
| Pencere | `WindowSmokeTests.Build`: `PromisesPage`, `MirrorPage`, `NiyetWindow`, `SozlukWindow`, `ContactCardView`; mevcut CallWindow/ContactWindow/Settings kurulumları |
| Düzen | `LayoutTests` (Measure/Arrange): CallWindow üst bantlar ≤ 240 px, döküm ≥ 400 px @720; Ayarlar alt bar ≤ 32 px @1920 elle ekran görüntüsü + günlük |
| Worker | `pytest worker/tests -q`; `test_prosody.py`, `test_cloud_providers.py` ek, `test_imports` |
| Gerçek görüşme | `tools/olcum/` WAV'ları (#22 18:49, #17 2:44, #38, #24, #14, #16, #60): Aynam sayımları dinlenerek (verdict), prosody zirveleri kulakla, Sözler toplamı elle 10 satır, Kalıplar toplamı Defter'le çapraz |
| Kayıt |ISLEM-GUNLUGU her paket için: ne bozuktu → ne yapıldı → hangi komut/sayı; olumsuz sonuçlar dahil |

---

## 11. Sıra ve sürümler (özet)

| Sıra | Paket | İçerik | Bağımlılık |
|---|---|---|---|
| 0 | P0 | Anahtar temizliği + belge/günlük borcu + `tools/olcum/` | — |
| 0' | R0 | **SocialZeka çatallama** (§8.3): yeni repo, ad/AppId/güncelleme yolu/veri klasörü, taban testleri yeşil | P0 ile birlikte, her şeyden önce |
| 1 | A1 → v3.0.0 | Şikâyet 2/3/4/5/6, dil kalıntıları | R0 |
| 2 | A2 → v3.0.x | v15; bayatlık (7), Defter fiilleri (1), dikey alan (8), `spokenOn`, `verdict` | A1 |
| 3 | B → v3.1.0 | Sözler sayfası, ray + `ActionRegistry` tek kaynak, CallerOverlay | A2 |
| 4 | C + D → v3.1.x | Kelime güveni ölçek; v16 Aynam (sayfa + sekme), Niyet, Sözlük, Koçluk ayarları, "Ne oldu?" tostu | A2; şive ön-ölçümü |
| 5 | E → v3.2.0 | v17 Kişi kartı (kanıt), `tactic_evidence`, `speech_act`, Elindeki kayıtlar, ContactsPage sekmesi | D |
| 6 | G + H (+J) → v3.3.x | v18 prosody (şerit kalibrasyon kapılı), canlı ölçer, audio_event (anahtar gelince) | D; §6.3-6.4 |
| 7 | I → v3.4.0 | v19 modelin görüşü, `IGpuGate` | E |
| ∥ | Ölçüm turları | Hume (§6.5), canlı kelime (§6.6), F0 kararlılığı (§6.3) | kod yok |
| ∥ | EK-5, EK-4 | ayrı iş emri (VoiceTranscript'te mi SocialZeka'da mı — §12) | — |
| kapsam dışı | GSM / Phone Link (kullanıcı kararı) · Apple Watch (ertelendi) | §8 | — |

Her paket YOLHARITASI "yığın bitince tek sürüm" kuralına uyar. Yeni repo yeni ürün: sürümler **v3.0.0**'dan başlar (VoiceTranscript'in son etiketi `v2.9.21`; `UpdateService` yeni repoya baktığı için etiket uzayları çakışmaz).

---

## 12. Karar durumu

**Verildi (5 Eylül):** kişi kartı görüş paneli = izlenim serbest, iki sınır (§2, Paket I); SocialZeka = ayrı repo, çatal (Paket R0); GSM/Phone Link = kapsam dışı; Apple Watch = ertelendi.

**Planın varsayılanla ilerlediği, itiraz gelirse değişecek kararlar:**
1. **VoiceTranscript'in akıbeti:** v2.9.21'de dondurulur (README'de SocialZeka'ya işaret), tüm geliştirme SocialZeka'da; VoiceTranscript'te yalnız kayıt/döküm hataları için tek yönlü (VoiceTranscript → SocialZeka) birleştirme. İki yönlü birleştirme yok — VoiceTranscript2'nin bölündüğü yer buydu.
2. **Veri klasörü:** SocialZeka `%LOCALAPPDATA%\SocialZeka.Data` kullanır; ilk açılışta VoiceTranscript arşivini **taşıma/içe aktarma** teklif eder (BackupService.ImportAsync kalıbı, Migrate ile). Aynı klasörü paylaşmak iki uygulamanın aynı SQLite'a yazması demek — yapılmaz.
3. **EK-5 / EK-4** (bulut servisi seçimi, VAD parametresi): SocialZeka'da, koçluk paketlerine paralel.
4. **GSM yorumu:** "gsm e phone linke gerek yok" = ikisi de kapsam dışı. "GSM evet ama Phone Link'siz" denmek istendiyse: PC'ye ses getirmenin Phone Link dışındaki tek yolu telefonun kendi kaydını **tek kanal** içe aktarmaktır (kim dedi bilinmez; Defter/Aynam/Gidişat kapalı) — o zaman §8.1'deki tek kanal içe aktarma maddesi geri gelir.
5. **Yeniden dökümde eski okuma:** bayat etiketiyle tut + [Sil]; otomatik silme yalnız aynı koşumda yeniden üretilecekse.
6. **Ctrl numaraları** ray sırasına göre yeniden dağıtılır, tek kaynaktan; `ISLEM-GUNLUGU`'ne yazılır.
