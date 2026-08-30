# Runs both test suites.
#
# Deliberately not "dotnet test". On the .NET 10 SDK that command hands the xUnit v3 module off
# over the Microsoft.Testing.Platform protocol and reports "Zero tests ran" for a
# net10.0-windows target framework, even though the same module discovers and runs all 287 of
# its tests when launched directly. Running the module is the reliable path, and it produces the
# same output, filters and exit codes.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "C# testleri" -ForegroundColor Cyan

dotnet build (Join-Path $root "VoiceTranscript.slnx") -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Derleme basarisiz." }

$module = Join-Path $root "tests/VoiceTranscript.Tests/bin/Debug/net10.0-windows10.0.19041.0/VoiceTranscript.Tests.exe"
& $module @args
$csharp = $LASTEXITCODE

Write-Host ""
Write-Host "Python testleri" -ForegroundColor Cyan

# The worker virtual environment is the application runtime, not a test environment: on a real
# installation it holds only the pinned Whisper dependencies and no pytest at all. So it is used
# when it can actually run the tests, and the system interpreter otherwise.
$python = Join-Path $root "worker/.venv/Scripts/python.exe"

if (Test-Path $python) {
    & $python -c "import pytest" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $python = "python" }
} else {
    $python = "python"
}

Push-Location (Join-Path $root "worker")
try {
    & $python -m pytest -q
    $py = $LASTEXITCODE
}
finally {
    Pop-Location
}

Write-Host ""
if ($csharp -eq 0 -and $py -eq 0) {
    Write-Host "Tum testler gecti." -ForegroundColor Green
    exit 0
}

Write-Host "Basarisiz testler var (C#: $csharp, Python: $py)." -ForegroundColor Red
exit 1
