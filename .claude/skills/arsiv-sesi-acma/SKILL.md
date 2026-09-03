---
name: arsiv-sesi-acma
description: VoiceTranscript'in .ogg görüşme arşivlerini WAV'a açar. ffmpeg ve PyAV bu dosyaları okuyamaz; tek yol uygulamanın kendi çözücüsüdür. Kayıtları ölçmek, dinlemek ya da yeniden çözümlemek gerektiğinde kullan.
---

# Görüşme arşivini WAV'a açmak

Kayıtlar `%LOCALAPPDATA%\VoiceTranscript.Data\recordings\YYYY-MM\call-N-{mic,far}.ogg`
altında Opus olarak durur. `mic` kullanıcı, `far` karşı taraf — bu ayrım konuşmacı
atamasını bir tahmin değil olgu yapan şeydir, ölçerken de koru.

## Neden ffmpeg olmuyor

Başlık geçerli bir `OggS`, ama yazan taraf (Concentus.Oggfile) OpusHead'e ön atlama
değerini 0 yazıyor. ffmpeg ve PyAV bunu reddediyor. **Dosyalar yalnız uygulamanın
kendi çözücüsüyle okunabiliyor** — bu, kullanıcının kendi kayıtları için taşınabilirlik
riski, ve gerektiğinde ayrıca ele alınmalı.

## Yol: Core'a başvuran küçük bir konsol

`VoiceTranscript.Core`'daki `OpusArchive.Decode(oggPath, wavPath)` kare sayısı döner
(16 kHz mono 16-bit). Scratchpad'de tek dosyalık bir proje yeter:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="...\src\VoiceTranscript.Core\VoiceTranscript.Core.csproj" />
  </ItemGroup>
</Project>
```

`UseWPF` gerekli — Core bazı yerlerde WPF tiplerine bakıyor.

`dotnet run -- dosya1.ogg dosya2.ogg` ile çalıştır; `-v q --nologo` bayraklarını
`--` sonrasına koyma, program argümanı sanılır.

## Açtıktan sonra

Süre `kare / 16000`. Kanal başına yükseklik ölçmek için 50 ms blok dBFS yeterli;
medyan −90 dB civarıysa loopback, −35…−70 arasıysa canlı mikrofondur.

**Arşivin kendisi bir kayıp taşır.** Bit hızı 24 kbps'ken bir kayıt 1624 yerine 330
kelime veriyordu; şimdi 64 kbps. Eski kayıtları yeniden çözümlerken beklentiyi
buna göre kur — 8-24 kbps arşivlerden çıkacak sonuç bugünkü motorla bile sınırlıdır.
