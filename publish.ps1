# Produces a distributable build, and the installer when Inno Setup is available.
#
# The output is deliberately just the application. Python, the model weights and the Python
# packages are fetched by the setup wizard on first run instead: bundling them would turn a
# 200 MB download into a multi-gigabyte one, and would freeze versions the wizard can otherwise
# keep current.

param(
    [string] $Configuration = "Release",

    # The version this build is. Omitted means a development build, which is deliberately
    # 0.0.0-dev: it is plainly not a release and it sorts below every real one, so a developer
    # running from the checkout is never offered "an update" to something they are ahead of.
    #
    # A release passes the tag: .\publish.ps1 -Version 1.2.0
    [string] $Version = "",

    # Fail rather than warn when Inno Setup is missing. Continuous integration must never publish
    # a release whose only artefact is a folder nobody can install.
    [switch] $RequireInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out = Join-Path $root "dist/SocialZeka"

# The version is settled once, here, and every other place reads it from this script.
#
# It used to be written out three times — nowhere in the projects, hardcoded in the installer
# script, and hardcoded again in this file's final message — so the three could disagree with each
# other and with the running application. That is tolerable until the application starts checking
# for updates, at which point a build that misreports its own version either never updates or
# updates to itself in a loop.
if ($Version) {
    $versionPrefix = ($Version -split '-', 2)[0]
    $versionSuffix = if ($Version -match '-') { ($Version -split '-', 2)[1] } else { "" }
} else {
    $versionPrefix = "0.0.0"
    $versionSuffix = "dev"
}

$fullVersion = if ($versionSuffix) { "$versionPrefix-$versionSuffix" } else { $versionPrefix }

Write-Host "Surum: $fullVersion" -ForegroundColor Cyan
Write-Host "Uygulama derleniyor" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Self-contained, and that is not negotiable. This is a tray application that a
# non-developer installs and forgets about; sending them to hunt for a .NET runtime first is
# where most of them would stop. It costs about 140 MB, which the installer compresses.
#
# VersionPrefix and VersionSuffix rather than Version: setting Version alone leaves VersionPrefix
# at its default and the assembly then reports 0.0.0 whatever was asked for.
dotnet publish (Join-Path $root "src/VoiceTranscript.App/VoiceTranscript.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:VersionPrefix=$versionPrefix `
    -p:VersionSuffix=$versionSuffix `
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
    if ($RequireInstaller) { throw "Inno Setup 6 bulunamadi ve -RequireInstaller verildi." }

    Write-Host "Inno Setup 6 bulunamadi, kurulum paketi uretilmedi." -ForegroundColor Yellow
    Write-Host "Kurmak icin:  winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    Write-Host "Klasor hali hazir: $out" -ForegroundColor Yellow
    exit 0
}

# The version reaches the installer through a generated file rather than by editing the script.
#
# Generated because the alternative is a human remembering to change a number in a second place
# for every release, and the failure when they forget is silent: the installer builds, the file is
# named after the old version, and the update client refuses it as belonging to another tag.
$generated = Join-Path $root "installer/version.generated.iss"
"#define AppVersion `"$fullVersion`"" | Out-File -FilePath $generated -Encoding utf8 -NoNewline

& $iscc (Join-Path $root "installer/SocialZeka.iss")
if ($LASTEXITCODE -ne 0) { throw "Kurulum paketi uretilemedi." }

$setup = Join-Path $root "dist/SocialZeka-Setup-$fullVersion-win-x64.exe"
if (-not (Test-Path $setup)) { throw "Beklenen kurulum dosyasi uretilmedi: $setup" }

# Checksums, in the format sha256sum reads, so anybody can verify the download by hand and the
# update client can verify it without trusting the transfer.
#
# This does not protect against a compromised repository and the documentation says so. It
# protects against the thing that actually happens: a truncated or corrupted download that
# installs anyway, which afterwards is indistinguishable from a broken build.
Write-Host "Saglama toplami" -ForegroundColor Cyan

$hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $root "dist/SHA256SUMS"
"$hash *$(Split-Path $setup -Leaf)" | Out-File -FilePath $sums -Encoding ascii

Write-Host ""
Write-Host "Kurulum paketi hazir: $setup" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Green
