; Atom DPI Bypass - Inno Setup script
;
; Built by the release pipeline as:
;   ISCC /DAppVersion=1.0.0.42 /DPublishDir=..\artifacts\publish installer\AtomDpiBypass.iss

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Atom DPI Bypass"
#define AppPublisher "Atom Gamer Arda A.G.A"
#define AppExeName "AtomDpiBypass.exe"
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
OutputBaseFilename=AtomDpiBypass-Setup-{#AppVersion}
SetupIconFile=..\assets\logo\atomdpi.ico
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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Registering the logon task through the app keeps one implementation of it.
Filename: "{app}\{#AppExeName}"; Parameters: "--install-autostart"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Put the user's DNS back before anything is deleted, using the same code that changed it.
Filename: "{app}\{#AppExeName}"; Parameters: "--restore-dns"; RunOnceId: "RestoreDns"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-autostart"; RunOnceId: "RemoveTask"; Flags: runhidden waituntilterminated
; The driver service is created on demand by WinDivert; remove it so nothing is left behind.
Filename: "{sys}\sc.exe"; Parameters: "stop WinDivert"; RunOnceId: "StopDriver"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete WinDivert"; RunOnceId: "DeleteDriver"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\{#AppName}\logs"

[Code]
procedure StopRunningInstance();
var
  ResultCode: Integer;
begin
  { The app holds the driver handle open, so it has to go before files are replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
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
