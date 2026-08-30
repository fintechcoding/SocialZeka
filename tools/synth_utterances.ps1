<#
.SYNOPSIS
    Synthesises the individual utterances of a scripted test call to 16 kHz mono WAV files.

.DESCRIPTION
    The development machine has no audio hardware and cannot record a real call, so the pipeline
    is exercised with a synthetic one instead. This script only produces the individual
    utterances; make_test_call.py assembles them onto a timeline and writes the ground truth.

    Whisper wants 16 kHz mono 16-bit PCM, which is what SpeechAudioFormatInfo is set to here, so
    no resampling step is needed anywhere in the test path.

.PARAMETER ScriptPath
    JSON array of objects: { "id": "m0", "voice": "mic", "text": "..." }

.PARAMETER OutDir
    Directory to write <id>.wav into.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$lines = Get-Content -Raw -Path $ScriptPath | ConvertFrom-Json

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$installed = $synth.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }

# Two clearly different voices so a human listening to the assembled call can tell the streams
# apart, and so the far stream is not literally the same signal as the mic stream.
$micVoice = $installed | Where-Object { $_ -like '*David*' } | Select-Object -First 1
$farVoice = $installed | Where-Object { $_ -like '*Zira*' }  | Select-Object -First 1
if (-not $micVoice) { $micVoice = $installed | Select-Object -First 1 }
if (-not $farVoice) { $farVoice = $installed | Select-Object -Last 1 }

Write-Host "mic voice : $micVoice"
Write-Host "far voice : $farVoice"

# 16 kHz, 16-bit, mono — exactly the format the capture layer produces.
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

foreach ($line in $lines) {
    $voice = if ($line.voice -eq 'far') { $farVoice } else { $micVoice }
    $path = Join-Path $OutDir "$($line.id).wav"

    $synth.SelectVoice($voice)
    $synth.Rate = 0
    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($line.text)
    $synth.SetOutputToNull()

    Write-Host ("  {0,-6} {1,-4} {2}" -f $line.id, $line.voice, $line.text)
}

$synth.Dispose()
Write-Host "`nWrote $($lines.Count) utterances to $OutDir"
