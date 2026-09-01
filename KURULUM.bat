@echo off
REM Double-clickable wrapper for the setup script.
REM
REM PowerShell deliberately refuses to run a script from the current directory by bare name, so
REM "powershell setup.ps1" fails with "not recognized" - a confusing error for a file that is
REM sitting right there. Passing it with -File names it explicitly, and -ExecutionPolicy Bypass
REM lifts the unsigned-script block for this one invocation only.
REM
REM Kept plain ASCII on purpose: cmd.exe reads .bat files in the OEM codepage, so Turkish
REM characters here would come out as mojibake.

setlocal
cd /d "%~dp0"

echo.
echo   VoiceTranscript kurulumu baslatiliyor...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set RESULT=%ERRORLEVEL%

echo.
if not "%RESULT%"=="0" (
    echo   Kurulum tamamlanamadi. Yukaridaki mesaja bakin.
) else (
    echo   Kurulum tamam. Simdi VoiceTranscript.exe dosyasini calistirabilirsiniz.
)
echo.
pause
exit /b %RESULT%
