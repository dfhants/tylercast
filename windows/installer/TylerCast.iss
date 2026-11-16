; TylerCast's Windows installer (Inno Setup 6). Built on Windows by CI from the published
; tray app (out/app/TylerCast.exe); the Mac can't run the Inno compiler.
;   iscc installer\TylerCast.iss       -> out\installer\TylerCast-Setup-<version>.exe
;
; Per user: no admin rights, installs to %LOCALAPPDATA%\Programs\TylerCast, adds a Start
; menu entry and an entry in Settings > Apps (uninstall), and optionally starts TylerCast at
; sign-in (the same Run value the app's own "Start with Windows" switch sets).

#define AppExe "..\out\app\TylerCast.exe"
#define AppVersion GetVersionNumbersString(AppExe)

[Setup]
AppId={{21E9C01C-404B-4506-98D1-0FE5D04E23DD}
AppName=TylerCast
AppVersion={#AppVersion}
AppVerName=TylerCast {#AppVersion}
AppPublisher=TylerCast
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\TylerCast
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
WizardStyle=modern
WizardSizePercent=100
SetupIconFile=..\src\TylerCast.App\TylerCast.ico
UninstallDisplayIcon={app}\TylerCast.exe
UninstallDisplayName=TylerCast
OutputDir=..\out\installer
OutputBaseFilename=TylerCast-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; A running TylerCast is closed first (it stops casting cleanly as it goes).
CloseApplications=force
RestartApplications=no

[Tasks]
Name: startup; Description: "Start TylerCast when I sign in to Windows"; Flags: unchecked

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TylerCast"; Filename: "{app}\TylerCast.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TylerCast";   ValueData: """{app}\TylerCast.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
; Uninstalling also removes the value if it was set later from TylerCast's Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TylerCast"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\TylerCast.exe"; Description: "Open TylerCast"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im TylerCast.exe /f"; Flags: runhidden; RunOnceId: "StopTylerCast"

[UninstallDelete]
; Settings and log.
Type: filesandordirs; Name: "{localappdata}\TylerCast"
