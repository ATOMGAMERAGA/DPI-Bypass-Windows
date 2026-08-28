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
#define RecoveryExeName "DpiBypass.Recovery.exe"
; One GUID, written once. [Setup] needs the leading brace doubled, because Inno
; unescapes "{{" to "{" there; the [Code] section is never constant-expanded, so the
; uninstall key path in it is built from the plain form.
#define AppIdGuid "{9F4C1C3E-7B21-4C0A-9E52-6A2D5B71C4A8}"
#define AppId "{" + AppIdGuid

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
var
  { The folder the sweep has already covered. The sweep is asked for twice on purpose
    - once before the wizard, once when the directory is finally known - and repeating
    it for the same folder would only make the install slower. }
  SweptDir: String;
  SweptDirKnown: Boolean;

{ The uninstall key Windows writes for this AppId. It is the one place Setup can read
  where an existing installation put its files without knowing anything about where
  this installation is going. }
function UninstallKeyPath(): String;
begin
  Result := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdGuid}_is1';
end;

function ReadInstallLocation(const RootKey: Integer; var Dir: String): Boolean;
var
  Value: String;
begin
  Result := False;

  try
    if RegQueryStringValue(RootKey, UninstallKeyPath(), 'InstallLocation', Value) and (Value <> '') then
    begin
      Value := RemoveBackslashUnlessRoot(Value);
      if DirExists(Value) then
      begin
        Dir := Value;
        Result := True;
      end;
    end;
  except
    { An unreadable registry view is not a reason to refuse to install. }
    Log('Could not read the previous install location: ' + GetExceptionMessage);
  end;
end;

{ Where the copy already on this machine lives, or an empty string when there is none.

  This function exists because the "app" constant does not yet: it is set when the
  wizard settles on a directory, and Setup ends with an internal error the moment
  anything running before that asks for it - InitializeSetup included. With
  /SUPPRESSMSGBOXES, which the one line installer passes, that error is never printed
  and the whole failure reaches the user as nothing but "exit code 1", before a single
  file has been written. The uninstall key the previous install wrote answers the same
  question and is readable from the first line of the script. }
function PreviousInstallDir(): String;
begin
  Result := '';

  { The install runs in 64-bit mode, so that is where its uninstall key is. The other
    two views are read as well, for a machine carrying an older 32-bit installation
    or one registered per user. }
  if IsWin64 then
    if ReadInstallLocation(HKLM64, Result) then
      Exit;

  if ReadInstallLocation(HKLM32, Result) then
    Exit;

  if ReadInstallLocation(HKCU, Result) then
    Exit;
end;

{ The install folder once it is known, and an empty string while it is not - so that
  asking too early degrades into doing less rather than into ending the installation. }
function AppDirIfKnown(): String;
begin
  try
    Result := ExpandConstant('{app}');
  except
    Result := '';
  end;
end;

{ Ends the copies of the app that hold the packet driver open and own the files Setup
  is about to replace, after asking them to put the machine's network settings back.

  AppDir may be empty: the steps that need the installed executable are skipped then,
  and the process sweep still runs. }
procedure StopRunningInstance(const AppDir: String);
var
  ResultCode: Integer;
begin
  { Never kill the owner while Windows still points at its process-local DNS proxy.
    A forced termination cannot run the application's normal finally/Dispose path;
    restoring first is what keeps an upgrade or uninstall from taking the machine's
    internet connection down if the replacement then fails to launch. Both commands
    are separate helper instances and therefore still run when the UI copy is hung. }
  if (AppDir <> '') and FileExists(AppDir + '\{#AppExeName}') then
  begin
    Exec(AppDir + '\{#AppExeName}', '--restore-dns', AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(AppDir + '\{#AppExeName}', 'latency restore', AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  { The app holds the driver handle open, so it has to go before files are replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);

  { A separately named watchdog normally exits as soon as its owner does. Run the
    recovery command once more after that hand-off, then remove any orphan before
    Setup replaces the shared runtime files. }
  if (AppDir <> '') and FileExists(AppDir + '\{#RecoveryExeName}') then
  begin
    Exec(AppDir + '\{#RecoveryExeName}', '--restore-dns', AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#RecoveryExeName} /F', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
  end;

  { And the pre-rename build, which holds the same driver handle under its old name. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM AtomDpiBypass.exe /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  { The old logon task would keep starting a binary this installer just deleted. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN AtomDpiBypass-Autostart /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure SweepRunningInstance(const AppDir: String);
begin
  if SweptDirKnown and (CompareText(SweptDir, AppDir) = 0) then
    Exit;

  SweptDir := AppDir;
  SweptDirKnown := True;

  { Housekeeping for an upgrade over a running copy is never allowed to decide whether
    there is an installation at all: an exception leaving an event function is fatal to
    Setup, and a machine where this fails still has an installer to run. }
  try
    StopRunningInstance(AppDir);
  except
    Log('StopRunningInstance failed: ' + GetExceptionMessage);
  end;
end;

function InitializeSetup(): Boolean;
begin
  { Before Setup opens anything the running copy owns. Where that copy lives comes
    from its uninstall key, because the directory this install will use has not been
    decided yet - see PreviousInstallDir. }
  SweepRunningInstance(PreviousInstallDir());
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { And again against the folder actually chosen, which is knowable by now and covers
    a directory typed into the wizard that no uninstall key pointed at. ssInstall is
    the last step before the first file is written. }
  if CurStep = ssInstall then
    SweepRunningInstance(AppDirIfKnown());
end;

function InitializeUninstall(): Boolean;
begin
  { The uninstaller reads the install folder out of its own log, so it has known the
    answer since it started. }
  SweepRunningInstance(AppDirIfKnown());
  Result := True;
end;
