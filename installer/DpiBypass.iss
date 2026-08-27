; DPI Bypass - Inno Setup script
;
; Built by the release pipeline as:
;   ISCC /DAppVersion=1.0.0.42 /DPublishDir=..\artifacts\publish installer\DpiBypass.iss

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "DPI Bypass"
#define AppPublisher "Atom Gamer Arda A.G.A"
#define AppExeName "DpiBypass.exe"
#define AppId "{{9F4C1C3E-7B21-4C0A-9E52-6A2D5B71C4A8}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (c) {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} kurulumu
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=output
OutputBaseFilename=DpiBypass-Setup-{#AppVersion}
SetupIconFile=..\assets\logo\dpibypass.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The driver and the installed files both need an elevated context.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=no
DisableProgramGroupPage=yes
ShowLanguageDialog=auto
; Every size the wizard may ask for, so the artwork is never upscaled.
WizardImageFile=assets\wizard-large-164x314.bmp,assets\wizard-large-192x386.bmp,assets\wizard-large-256x459.bmp,assets\wizard-large-328x604.bmp,assets\wizard-large-355x700.bmp,assets\wizard-large-410x797.bmp
WizardSmallImageFile=assets\wizard-small-55x55.bmp,assets\wizard-small-64x64.bmp,assets\wizard-small-83x83.bmp,assets\wizard-small-92x92.bmp,assets\wizard-small-110x110.bmp,assets\wizard-small-119x119.bmp,assets\wizard-small-138x138.bmp

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.LaunchAfterInstall=Kurulumdan sonra {#AppName} uygulamasını başlat
turkish.CreateDesktopIcon=Masaüstü kısayolu oluştur
turkish.AutoStartTask=Windows açılışında otomatik başlat (önerilen)
english.LaunchAfterInstall=Launch {#AppName} after installation
english.CreateDesktopIcon=Create a desktop shortcut
english.AutoStartTask=Start automatically with Windows (recommended)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStartTask}"

[InstallDelete]
; The app used to be called "Atom DPI Bypass" and shipped AtomDpiBypass.exe. Leaving
; the old binary behind would keep a broken Start menu entry working just well enough
; to confuse people, so it goes - along with the shortcuts that point at it.
Type: files; Name: "{app}\AtomDpiBypass.exe"
Type: files; Name: "{app}\AtomDpi.Core.dll"
Type: files; Name: "{group}\Atom DPI Bypass.lnk"
Type: files; Name: "{autodesktop}\Atom DPI Bypass.lnk"
Type: filesandordirs; Name: "{autoprograms}\Atom DPI Bypass"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; WorkingDir is on every entry on purpose. A child process inherits its working
; directory, Setup's own is a temporary folder it deletes as it exits, and a process
; whose working directory has been deleted cannot start any child of its own -
; CreateProcess fails with "the system cannot find the path specified". The app
; launched by the last entry here outlives Setup, so without this the very first run
; after an installation is the one run that cannot register its logon task or
; configure DNS, and it reports a path error that has nothing to do with either.
; Registering the logon task through the app keeps one implementation of it.
Filename: "{app}\{#AppExeName}"; Parameters: "--install-autostart"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; Tasks: autostart
; And the other way round, or the checkbox only works when it is ticked: autostart is
; on by default in the settings file, so leaving it unticked has to be recorded too -
; otherwise the app reconciles the missing task on first launch and puts it back.
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-autostart"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExeName}"; Parameters: "--show"; WorkingDir: "{app}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent
; A silent install - which is what the one line PowerShell installer runs - never
; reaches the checkbox above, and the logon task does not fire until the next sign
; in. Without this the whole installation finishes having put nothing on screen,
; which is indistinguishable from it having failed.
Filename: "{app}\{#AppExeName}"; Parameters: "--show"; WorkingDir: "{app}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Restore persistent NIC properties before the executable or WinDivert is removed.
Filename: "{app}\{#AppExeName}"; Parameters: "latency restore"; WorkingDir: "{app}"; RunOnceId: "RestoreLatency"; Flags: runhidden waituntilterminated
; Put the user's DNS back before anything is deleted, using the same code that changed it.
Filename: "{app}\{#AppExeName}"; Parameters: "--restore-dns"; WorkingDir: "{app}"; RunOnceId: "RestoreDns"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-autostart"; WorkingDir: "{app}"; RunOnceId: "RemoveTask"; Flags: runhidden waituntilterminated
; The driver service is created on demand by WinDivert; remove it so nothing is left behind.
Filename: "{sys}\sc.exe"; Parameters: "stop WinDivert"; WorkingDir: "{sys}"; RunOnceId: "StopDriver"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete WinDivert"; WorkingDir: "{sys}"; RunOnceId: "DeleteDriver"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\{#AppName}\logs"
; The folder the app used before it was renamed; settings were copied out of it on
; first run, so there is nothing left worth keeping.
Type: filesandordirs; Name: "{commonappdata}\Atom DPI Bypass"

[Code]
procedure StopRunningInstance();
var
  ResultCode: Integer;
begin
  { The app holds the driver handle open, so it has to go before files are replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  { And the pre-rename build, which holds the same driver handle under its old name. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM AtomDpiBypass.exe /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  { The old logon task would keep starting a binary this installer just deleted. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN AtomDpiBypass-Autostart /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  StopRunningInstance();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningInstance();
  Result := True;
end;
