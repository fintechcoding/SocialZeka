<#
.SYNOPSIS
    Prepares the Python side of SocialZeka on the machine that will actually record calls.

.DESCRIPTION
    Creates a virtual environment, installs the pinned dependencies, wires up the Windows DLL
    search path, and then verifies that CUDA is genuinely reachable rather than assuming it.

    The pins in worker/requirements.txt are load-bearing and the reasons are written there. The
    short version: CTranslate2 4.6.3 and later no longer need cuDNN at all, so the only NVIDIA
    runtime DLL required is cublas64_12.dll. Both the faster-whisper README and the CTranslate2
    installation docs still tell you to install cuDNN 9 — that advice is stale, and the widely
    repeated "downgrade to ctranslate2==4.4.0" fix is now actively wrong, because that version
    reintroduces a hard cuDNN 8 dependency.

.PARAMETER DataRoot
    Where the virtual environment and model cache go. Defaults to the application data folder,
    deliberately outside the program directory: the installer replaces its own folder on every
    update, and several gigabytes of model weights should not be re-downloaded each time.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup.ps1
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'SocialZeka.Data'),
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

function Step($message) { Write-Host "`n=== $message ===" -ForegroundColor Cyan }
function Ok($message)   { Write-Host "  [ok] $message" -ForegroundColor Green }
function Warn($message) { Write-Host "  [!]  $message" -ForegroundColor Yellow }
function Fail($message) { Write-Host "  [x]  $message" -ForegroundColor Red }

Step 'Checking prerequisites'

$python = Get-Command py -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python not found. Install Python 3.12 and re-run.' }
Ok "Python launcher: $($python.Source)"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { Ok ".NET SDK: $(& dotnet --version)" }
else { Warn '.NET SDK not found. Needed to build the application, not to run this script.' }

# The GPU is optional here: everything runs on the CPU too, just slowly. Say so plainly rather
# than failing, because a developer machine legitimately has no NVIDIA card.
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    $gpu = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader
    Ok "GPU: $gpu"
} else {
    Warn 'No NVIDIA GPU detected. Transcription will run on the CPU, which is far slower.'
}

Step 'Creating the virtual environment'

$venv = Join-Path $DataRoot 'python'
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

if (Test-Path (Join-Path $venv 'Scripts\python.exe')) {
    Ok "Already present: $venv"
} else {
    & $python.Source -3.12 -m venv $venv
    if ($LASTEXITCODE -ne 0) { & $python.Source -m venv $venv }
    Ok "Created: $venv"
}

$venvPython = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path $venvPython)) { throw "Virtual environment is incomplete: $venvPython missing" }

Step 'Installing pinned dependencies'

& $venvPython -m pip install --upgrade pip --quiet
& $venvPython -m pip install -r (Join-Path $repo 'worker\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
Ok 'Installed'

Step 'Registering the CUDA DLL directory'

# Since Python 3.8 the Windows loader no longer searches PATH for the dependencies of C
# extension modules, so pip installing nvidia-cublas-cu12 is not enough on its own: the DLL
# lands somewhere perfectly valid that ctranslate2.dll will never look. sitecustomize.py runs
# automatically before any import, which is more reliable than expecting every entry point to
# remember to do it. The LD_LIBRARY_PATH workaround in the faster-whisper README is Linux-only
# and silently does nothing here, which is why this failure is usually misread as a broken CUDA
# installation.
$sitePackages = & $venvPython -c "import site; print(site.getsitepackages()[-1])"
$siteCustomize = Join-Path $sitePackages 'sitecustomize.py'

@'
import os, sys, glob, site

if sys.platform == "win32":
    for _root in site.getsitepackages():
        for _directory in glob.glob(os.path.join(_root, "nvidia", "*", "bin")):
            try:
                os.add_dll_directory(_directory)
            except OSError:
                pass
'@ | Set-Content -Path $siteCustomize -Encoding UTF8

Ok "Written: $siteCustomize"

Step 'Creating data directories'

foreach ($name in @('recordings', 'models', 'logs')) {
    $path = Join-Path $DataRoot $name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
Ok $DataRoot

# Recordings inside a synced folder would be uploaded silently, with no visible symptom at all.
$recordings = Join-Path $DataRoot 'recordings'
foreach ($variable in @('OneDrive', 'OneDriveCommercial')) {
    $syncRoot = [Environment]::GetEnvironmentVariable($variable)
    if ($syncRoot -and $recordings.StartsWith($syncRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "Recordings folder is inside $variable. Every call would be uploaded. Choose another -DataRoot."
        exit 1
    }
}

if (-not $SkipVerify) {
    Step 'Verifying the installation'

    Push-Location (Join-Path $repo 'worker')
    try {
        $probe = & $venvPython -m vt_worker probe 2>$null
    } finally {
        Pop-Location
    }

    if (-not $probe) {
        Fail 'The worker did not respond. Run it by hand to see the error:'
        Write-Host "    cd `"$repo\worker`"; & `"$venvPython`" -m vt_worker probe"
        exit 1
    }

    $report = $probe | ConvertFrom-Json

    foreach ($engine in $report.engines) {
        if ($engine.available) { Ok "$($engine.name) $($engine.version) — $($engine.detail)" }
        else { Warn "$($engine.name): $($engine.detail)" }
    }

    if ($report.cuda.available) {
        Ok "CUDA ready: $($report.cuda.device_count) device(s), CTranslate2 $($report.cuda.ctranslate2_version)"
    } elseif ($nvidiaSmi) {
        # A GPU that exists but is not reachable is the failure worth catching, because
        # everything still "works" — just ten times slower, with no error anywhere.
        Fail 'An NVIDIA GPU is present but CUDA is not reachable from Python.'
        if ($report.cuda.missing_dlls) { Fail "Missing: $($report.cuda.missing_dlls -join ', ')" }
        if ($report.cuda.hint) { Write-Host "    $($report.cuda.hint)" }
        exit 1
    } else {
        Warn 'Running without CUDA. Choose the CPU model in settings.'
    }
}

Step 'Done'

Write-Host @"
  Data root : $DataRoot
  Python    : $venvPython

Next:
  1. Build the application:  dotnet build -c Release
  2. Run it, open Ayarlar, and pick the transcription and analysis models.
  3. Make one WhatsApp call and one Telegram call to confirm detection and naming.

Two things must be measured on this machine before trusting the results, because neither can be
established from documentation:

  * Record a call of at least an hour and confirm the two WAV files come out the same length.
    If they diverge, speaker attribution after the first silence is not reliable.

  * Transcribe five real Turkish calls, correct them by hand, and compute the error rate.
    Under about 15 percent is usable; well above that means trying another model.
"@ -ForegroundColor Gray
