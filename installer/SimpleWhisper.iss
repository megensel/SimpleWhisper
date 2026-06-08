; SimpleWhisper Inno Setup Script
; Requires Inno Setup 6+: https://jrsoftware.org/isinfo.php

#define AppName "SimpleWhisper"
#define AppVersion "1.3.2"
#define AppPublisher "SimpleWhisper"
#define AppExeName "SimpleWhisper.exe"
#define AppDescription "Speech-to-text anywhere with a hotkey"

[Setup]
AppId={{7B2F8A3E-9D4C-4F1A-B5E6-8C7D9E0F1A2B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\src\SimpleWhisper\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\src\SimpleWhisper\bin\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "{#AppDescription}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Comment: "{#AppDescription}"

[Run]
; Interactive installs: offer a "Launch SimpleWhisper" checkbox on the finished page.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; Silent installs (the in-app auto-updater runs Setup with /VERYSILENT): relaunch the
; app automatically. The app shuts itself down before launching Setup, so Inno's Restart
; Manager never closes it and therefore never restarts it — without this it would stay
; closed after an update. Check: WizardSilent ensures this only fires for silent installs,
; avoiding a double launch alongside the postinstall checkbox above.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: WizardSilent

[Registry]
; Clean up the startup registry entry on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; Flags: deletevalue uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Close the running instance before upgrading/uninstalling.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Attempt to close the app gracefully via taskkill.
  Exec('taskkill.exe', '/f /im {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
