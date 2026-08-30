# Produces a distributable build, and the installer when Inno Setup is available.
#
# The output is deliberately just the application. Python, the model weights and the Python
# packages are fetched by the setup wizard on first run instead: bundling them would turn a
# 200 MB download into a multi-gigabyte one, and would freeze versions the wizard can otherwise
# keep current.

param(
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out = Join-Path $root "dist/VoiceTranscript"

Write-Host "Uygulama derleniyor" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Self-contained, and that is not negotiable. This is a tray application that a
# non-developer installs and forgets about; sending them to hunt for a .NET runtime first is
# where most of them would stop. It costs about 140 MB, which the installer compresses.
dotnet publish (Join-Path $root "src/VoiceTranscript.App/VoiceTranscript.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -o $out `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Yayinlama basarisiz." }

# The Python worker travels as source. It is small, it has to be readable where it runs so the
# venv can be rebuilt against whatever CUDA the machine turns out to have, and shipping it as a
# frozen executable would hide exactly the errors that matter when a GPU is missing.
Write-Host "Python worker kopyalaniyor" -ForegroundColor Cyan

$workerOut = Join-Path $out "worker"
New-Item -ItemType Directory -Force -Path $workerOut | Out-Null

Copy-Item (Join-Path $root "worker/vt_worker") $workerOut -Recurse -Force
Copy-Item (Join-Path $root "worker/requirements.txt") $workerOut -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "worker/pyproject.toml") $workerOut -Force -ErrorAction SilentlyContinue

# Anything that only matters while developing would be noise in a release.
Get-ChildItem $workerOut -Recurse -Include "__pycache__", ".pytest_cache", "tests" -Directory |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

foreach ($file in @("KURULUM.bat", "setup.ps1")) {
    $source = Join-Path $root $file
    if (Test-Path $source) { Copy-Item $source $out -Force }
}

$readme = Join-Path $root "OKUBENI.txt"
if (Test-Path $readme) { Copy-Item $readme $out -Force }

$size = "{0:N0}" -f ((Get-ChildItem $out -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB)
Write-Host "Hazir: $out ($size MB)" -ForegroundColor Green

# ---- installer ----------------------------------------------------------------

Write-Host ""
Write-Host "Kurulum paketi" -ForegroundColor Cyan

$iscc = Get-Command "iscc" -ErrorAction SilentlyContinue
if (-not $iscc) {
    # winget installs it per-user by default, which is not on PATH and not under Program Files.
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { $iscc = $found }
}

if (-not $iscc) {
    Write-Host "Inno Setup 6 bulunamadi, kurulum paketi uretilmedi." -ForegroundColor Yellow
    Write-Host "Kurmak icin:  winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    Write-Host "Klasor hali hazir: $out" -ForegroundColor Yellow
    exit 0
}

& $iscc (Join-Path $root "installer/VoiceTranscript.iss")
if ($LASTEXITCODE -ne 0) { throw "Kurulum paketi uretilemedi." }

Write-Host "Kurulum paketi hazir: dist\VoiceTranscript-Setup-1.0.0.exe" -ForegroundColor Green
