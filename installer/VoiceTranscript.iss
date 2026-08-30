; VoiceTranscript installer.
;
; Build with Inno Setup 6:  iscc installer\VoiceTranscript.iss
; It expects "dotnet publish" to have already produced dist\VoiceTranscript.
;
; Two decisions worth knowing about:
;
;   Per-user install, no administrator rights. The application records audio and drives a GPU in
;   the interactive session; it has no reason to touch machine-wide state, and asking for
;   elevation it does not need is how people learn to click through elevation prompts.
;
;   Nothing but the application ships here. Python, the model weights and the Python packages are
;   fetched by the setup wizard on first run instead. Bundling them would turn a 200 MB installer
;   into a multi-gigabyte one, and would pin versions that the wizard can otherwise keep current.

#define AppName "VoiceTranscript"
#define AppVersion "1.0.0"
#define AppPublisher "VoiceTranscript"
#define AppExe "VoiceTranscript.exe"
#define SourceDir "..\dist\VoiceTranscript"

[Setup]
AppId={{7B3E9C41-2D6A-4F18-9E52-1A4C8D0F6B23}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\dist
OutputBaseFilename=VoiceTranscript-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\VoiceTranscript.App\icon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Per-user: no UAC prompt, and the application never needs machine-wide access.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Windows 10 2004 is the floor: the audio APIs the recorder uses do not exist before it.
MinVersion=10.0.19041
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.LaunchDescription=VoiceTranscript uygulamasini calistir
turkish.AutoStartTask=Windows ile birlikte baslat
turkish.AutoStartInfo=Aramalari yakalayabilmesi icin uygulamanin arka planda calisiyor olmasi gerekir.
turkish.PrepareTask=Gerekli bilesenleri simdi hazirla (Python ve Whisper)
turkish.PrepareInfo=Kurulum bitince acilan pencere Python'u ve model paketlerini kendisi indirir. Internet gerekir; birkac dakika surer.
turkish.PrepareDescription=Gerekli bilesenleri hazirla
turkish.DataNotice=Kayitlar ve ayarlar su klasorde tutulur ve kaldirma isleminde SILINMEZ:%n%n%1
english.LaunchDescription=Launch VoiceTranscript
english.AutoStartTask=Start with Windows
english.AutoStartInfo=The application must be running in the background to capture calls.
english.PrepareTask=Prepare prerequisites now (Python and Whisper)
english.PrepareInfo=After installing, a window fetches Python and the model packages by itself. Needs internet; takes a few minutes.
english.PrepareDescription=Prepare prerequisites
english.DataNotice=Recordings and settings are kept in this folder and are NOT removed when uninstalling:%n%n%1

[Tasks]
; Checked by default: a recorder that is not running records nothing, and the single most common
; way this application disappoints is by being closed when a call comes in.
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AutoStartInfo}"

; Also checked by default, and the reason the installer does not merely copy files.
;
; Whisper needs Python, and somebody who has just installed a call recorder has no reason to know
; that. Leaving it out means the first thing they meet is a settings screen reporting exit code
; 9009 from a Windows Store stub, which explains nothing. The application fetches and installs
; everything itself; this task simply says to do it now rather than later.
Name: "prepare"; Description: "{cm:PrepareTask}"; GroupDescription: "{cm:PrepareInfo}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: autostart
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: autostart

[Run]
; With the prepare task, the application opens straight into the setup wizard and starts working
; on its own. Without it, it simply starts.
Filename: "{app}\{#AppExe}"; Parameters: "--setup"; Description: "{cm:PrepareDescription}";   Flags: nowait postinstall skipifsilent; Tasks: prepare
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchDescription}";   Flags: nowait postinstall skipifsilent unchecked; Tasks: not prepare

[UninstallDelete]
; Only what the installer created. The data folder is deliberately left alone — see below.
Type: filesandordirs; Name: "{app}"

[Code]
function GetDataDir(Param: string): string;
begin
  Result := ExpandConstant('{localappdata}\VoiceTranscript.Data');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\VoiceTranscript.Data');

    // Recordings are conversations the user chose to keep. Deleting them because a program was
    // uninstalled would be the wrong default in a way that cannot be undone, so the folder is
    // left in place and its location is stated plainly.
    if DirExists(DataDir) then
      MsgBox(FmtMessage(CustomMessage('DataNotice'), [DataDir]), mbInformation, MB_OK);
  end;
end;
