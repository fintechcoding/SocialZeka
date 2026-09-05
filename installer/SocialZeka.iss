; SocialZeka installer.
;
; Build with Inno Setup 6:  iscc installer\SocialZeka.iss
; It expects "dotnet publish" to have already produced dist\SocialZeka.
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

#define AppName "SocialZeka"

; The version comes from publish.ps1, which gets it from the git tag.
;
; Generated rather than edited, because the alternative is somebody remembering to change a number
; here for every release and the failure when they forget is silent: the installer builds fine, the
; file carries the old version in its name, and the update client then refuses it as belonging to
; another tag. The fallback keeps a bare "iscc installer\SocialZeka.iss" working.
#ifexist "version.generated.iss"
  #include "version.generated.iss"
#else
  #define AppVersion "0.0.0-dev"
#endif

#define AppPublisher "SocialZeka"
#define AppExe "VoiceTranscript.exe"
#define SourceDir "..\dist\SocialZeka"

[Setup]
; Its own AppId, not VoiceTranscript's. The same GUID would make Windows treat this as an upgrade
; of that installation and replace it in place; SocialZeka is a different product with its own
; data folder, and the two are meant to sit side by side while the archive is handed over.
AppId={{A867C415-6BBA-4325-9628-25FE6CE3FD0D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\dist

; The architecture is in the name because the update client matches on the whole file name, and
; because a second architecture later must not make the old name ambiguous.
OutputBaseFilename=SocialZeka-Setup-{#AppVersion}-win-x64
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

; Wait for the running copy to quit. Never close it.
;
; This is the difference between an update and losing a conversation. The application holds this
; mutex while it runs, and it is a tray recorder — it is running essentially always, which is the
; point of it. CloseApplications=yes would let Restart Manager terminate it, and it cannot even do
; that cleanly: MainWindow.OnClosing sets e.Cancel = true so the window refuses to close, leaving
; a kill as the only outcome. Killing it mid-call ends the recording with the WAV headers unwritten
; and the row never completed.
;
; So the installer waits instead, and the application closes itself when the user agrees to the
; update — after it has stopped detection and finished anything in flight. Somebody who runs the
; installer by hand during a call sees a wait rather than a lost recording.
AppMutex=Global\SocialZeka.SingleInstance
CloseApplications=no
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.LaunchDescription=SocialZeka uygulamasini calistir
turkish.DesktopTask=Masaustune kisayol ekle
turkish.DesktopInfo=Ek kisayollar
turkish.PrepareTask=Gerekli bilesenleri simdi hazirla (Python ve Whisper)
turkish.PrepareInfo=Kurulum bitince acilan pencere Python'u ve model paketlerini kendisi indirir. Internet gerekir; birkac dakika surer.
turkish.PrepareDescription=Gerekli bilesenleri hazirla
turkish.DataNotice=Kayitlar ve ayarlar su klasorde tutulur ve kaldirma isleminde SILINMEZ:%n%n%1
english.LaunchDescription=Launch SocialZeka
english.DesktopTask=Create a desktop shortcut
english.DesktopInfo=Additional shortcuts
english.PrepareTask=Prepare prerequisites now (Python and Whisper)
english.PrepareInfo=After installing, a window fetches Python and the model packages by itself. Needs internet; takes a few minutes.
english.PrepareDescription=Prepare prerequisites
english.DataNotice=Recordings and settings are kept in this folder and are NOT removed when uninstalling:%n%n%1

[Tasks]
; Starting with Windows is deliberately NOT here any more.
;
; It was an install-time checkbox that wrote a startup-folder shortcut, and that arrangement had
; no way back: somebody who unchecked it could not change their mind, somebody who checked it had
; to go and find the shortcut to stop it, and a silent update reruns this installer with its
; default selection — so a deliberate "no" could be overturned by an update approved for entirely
; unrelated reasons.
;
; The application now owns it, as a visible switch in Settings that it reconciles to the registry
; on every start. One mechanism, one source of truth, changeable at any time.
Name: "desktopicon"; Description: "{cm:DesktopTask}"; GroupDescription: "{cm:DesktopInfo}"

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
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; With the prepare task, the application opens straight into the setup wizard and starts working
; on its own. Without it, it simply starts.
Filename: "{app}\{#AppExe}"; Parameters: "--setup"; Description: "{cm:PrepareDescription}";   Flags: nowait postinstall skipifsilent; Tasks: prepare
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchDescription}";   Flags: nowait postinstall skipifsilent unchecked; Tasks: not prepare

; And the entry that matters for an update.
;
; Both entries above carry skipifsilent, which is right for them — a silent install should not
; offer a checkbox nobody can see. But it meant a silent install finished by leaving the machine
; with no recorder running at all: the application had closed itself to let the installer through,
; and nothing started it again. The user agreed to an update and silently lost call recording until
; they next opened the application by hand — which, for a tray application they are supposed to
; forget about, could be weeks.
;
; This entry carries no skipifsilent and runs only when the wizard was silent, so exactly one of
; the three fires in every case.
Filename: "{app}\{#AppExe}"; Flags: nowait postinstall; Check: WizardSilent

[InstallDelete]
; The worker tree is replaced wholesale rather than merged into.
;
; Inno overwrites the files it ships and leaves behind anything the previous version had that this
; one does not. For compiled assemblies that is untidy; for a Python package it is a hazard, because
; a module that was renamed or split still sits there as a valid import and Python will happily load
; it. The failure would be a stale code path running inside a worker that otherwise reports the new
; version — which is the hardest kind of bug to see.
;
; Only this directory. Wiping all of {app} would be tidier still and is not worth the trade: an
; install interrupted between the delete and the copy would leave nothing to run at all.
Type: filesandordirs; Name: "{app}\worker"

[UninstallDelete]
; Only what the installer created. The data folder is deliberately left alone — see below.
Type: filesandordirs; Name: "{app}"

[Code]
function GetDataDir(Param: string): string;
begin
  Result := ExpandConstant('{localappdata}\SocialZeka.Data');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\SocialZeka.Data');

    // Recordings are conversations the user chose to keep. Deleting them because a program was
    // uninstalled would be the wrong default in a way that cannot be undone, so the folder is
    // left in place and its location is stated plainly.
    if DirExists(DataDir) then
      MsgBox(FmtMessage(CustomMessage('DataNotice'), [DataDir]), mbInformation, MB_OK);
  end;
end;
