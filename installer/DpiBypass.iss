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
; The Installing page is shown before the first file is written, and the housekeeping
; that runs there closes the copy already on the machine. Without a line of text it is
; a progress bar at nought per cent that appears to be doing nothing at all, which is
; the screenshot every "the installer is stuck" report arrives with.
turkish.ClosingPrevious=Çalışan sürüm kapatılıyor...
english.ClosingPrevious=Closing the running version...

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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "WinDivert64.sys"; Flags: ignoreversion recursesubdirs createallsubdirs
; The kernel may retain the driver after the application closes its handles.
; Respect its file version so an unchanged driver is not replaced on every update.
; A newer locked driver can be replaced at reboot without aborting the app update.
Source: "{#PublishDir}\WinDivert64.sys"; DestDir: "{app}"; Flags: restartreplace uninsrestartdelete
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
; And the Lunar Client advertisement block, which lives in the machine's hosts file and
; would otherwise outlive the program that wrote it. Its own verb rather than part of
; --restore-dns: that one also runs on upgrade, where the block must stay.
Filename: "{app}\{#AppExeName}"; Parameters: "--restore-hosts"; WorkingDir: "{app}"; RunOnceId: "RestoreHosts"; Flags: runhidden waituntilterminated
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
const
  { How long Setup is willing to wait for each maintenance command, in seconds.

    Every one of these numbers is longer than the honest worst case of the command it
    covers, because giving up early is not free: abandoning a DNS restore half way
    through leaves the machine resolving against a proxy that is being uninstalled.
    Restoring DNS writes every adapter in one PowerShell call, and that call's own
    budget grows with the number of adapters up to two minutes. }
  RestoreDnsDeadline = 150;
  RestoreLatencyDeadline = 120;
  { taskkill and schtasks answer in well under a second or not at all. }
  QuickDeadline = 30;

var
  { The folder the sweep has already covered. The sweep is asked for twice on purpose
    - once before the wizard, once when the directory is finally known - and repeating
    it for the same folder would only make the install slower. }
  SweptDir: String;
  SweptDirKnown: Boolean;

  { Makes each command's marker file its own. A command that overran its deadline is
    abandoned rather than killed on the spot, so it may still write a marker while the
    next command is waiting for one. }
  MaintenanceStep: Integer;

{ Says what Setup is doing, when there is a form to say it in.

  The sweep runs at the top of the Installing page - before the first file is written,
  so the progress bar is still at nought and nothing above it has been filled in yet.
  Every second spent there looks, from the outside, exactly like an installation that
  has died. The name is a [CustomMessages] entry; the whole thing is guarded because
  there is no wizard form during InitializeSetup and none at all during uninstall.

  Nothing repaints it by hand: the wait below keeps Setup's message loop pumping, so
  the caption reaches the screen within the second. }
procedure SayStep(const MessageName: String);
begin
  try
    if WizardForm <> nil then
    begin
      WizardForm.StatusLabel.Caption := ExpandConstant('{cm:' + MessageName + '}');
    end;
  except
    { A status line is never worth an installation. }
  end;
end;

{ Waits about a second without letting go of the message loop.

  Sleep() blocks the thread that draws Setup's window, so a minute of it is a minute
  during which Windows itself paints the installer as not responding. Waiting on a
  process keeps the loop pumping, which is what Exec's own wait does. }
procedure PumpedPause();
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\ping.exe'), '-n 2 127.0.0.1', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
  begin
    Sleep(1000);
  end;
end;

{ Runs one maintenance command and stops waiting for it once Deadline seconds are up.

  This is the whole reason the installer no longer stops dead on its Installing page.
  Exec's ewWaitUntilTerminated has no time limit, and what it was being pointed at is
  the copy already on the machine - a build this installer cannot fix, started hidden,
  by a Setup which in silent mode is itself only a progress bar. Anything that copy
  puts on screen is therefore a dialog nobody can see and nobody can click:

    - a .NET host whose payload has been half removed by an uninstall running
      alongside this one reports the missing runtime in a message box;
    - a verb an older build does not recognise as headless is answered with the
      application's own main window;
    - a failure during its startup ends in the crash dialog.

  Each of those is a wait that never ends, at nought per cent, with no way out but
  Task Manager. So the command is started through cmd, which writes a marker file the
  moment the command returns, and this waits for the marker rather than for a process.
  When the deadline passes the command is left to its own devices and the taskkill
  further down the sweep is what finally clears it away.

  Returns True when the command finished inside its deadline. }
function RunBounded(const Filename, Params, WorkingDir: String; const Deadline: Integer): Boolean;
var
  Marker: String;
  ResultCode, Waited: Integer;
begin
  Result := False;
  MaintenanceStep := MaintenanceStep + 1;

  { Setup's own scratch folder, which exists from the first line of the script. The
    environment's is a fallback and not an academic one: this routine is the only thing
    standing between a locked file and an installation that cannot replace it, so it
    must not be skipped over a constant that would not expand. }
  try
    Marker := ExpandConstant('{tmp}');
  except
    Marker := ExpandConstant('{%TEMP}');
  end;

  Marker := Marker + '\dpibypass-step-' + IntToStr(MaintenanceStep) + '.done';
  DeleteFile(Marker);

  { /S puts cmd's quote handling beyond doubt: it strips the outermost pair and passes
    everything between them through untouched, so an install folder with spaces in it
    survives. "&" rather than "&&" - the marker says the wait is over, not that the
    command agreed with us. }
  if not Exec(
    ExpandConstant('{cmd}'),
    '/S /C ""' + Filename + '" ' + Params + ' & echo done>"' + Marker + '""',
    WorkingDir,
    SW_HIDE,
    ewNoWait,
    ResultCode) then
  begin
    Log('Could not start: ' + Filename + ' ' + Params);
    Exit;
  end;

  Waited := 0;
  while Waited < Deadline * 1000 do
  begin
    if FileExists(Marker) then
    begin
      DeleteFile(Marker);
      Result := True;
      Exit;
    end;

    { Short steps for the first second, so a command that takes a moment costs a
      moment - most of these are a taskkill that is done before Setup has finished
      asking. After that it is a real wait, and a real wait has to keep the message
      loop turning or Windows paints the installer as a program that has stopped
      answering, which is the very thing this routine exists to prevent. }
    if Waited < 1000 then
    begin
      Sleep(200);
      Waited := Waited + 200;
    end
    else
    begin
      PumpedPause();
      Waited := Waited + 1000;
    end;
  end;

  Log('Gave up after ' + IntToStr(Deadline) + 's waiting for: ' + Filename + ' ' + Params);
end;

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
begin
  SayStep('ClosingPrevious');

  { Never kill the owner while Windows still points at its process-local DNS proxy.
    A forced termination cannot run the application's normal finally/Dispose path;
    restoring first is what keeps an upgrade or uninstall from taking the machine's
    internet connection down if the replacement then fails to launch. Both commands
    are separate helper instances and therefore still run when the UI copy is hung.

    Every one of them goes through RunBounded, because every one of them is a build
    that shipped before this installer did and none of them can be trusted to exit. }
  if (AppDir <> '') and FileExists(AppDir + '\{#AppExeName}') then
  begin
    RunBounded(AppDir + '\{#AppExeName}', '--restore-dns', AppDir, RestoreDnsDeadline);
    RunBounded(AppDir + '\{#AppExeName}', 'latency restore', AppDir, RestoreLatencyDeadline);
  end;

  { The app holds the driver handle open, so it has to go before files are replaced.
    This is also what clears away anything above that outstayed its deadline. }
  RunBounded(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '', QuickDeadline);

  { A separately named watchdog normally exits as soon as its owner does. Run the
    recovery command once more after that hand-off, then remove any orphan before
    Setup replaces the shared runtime files. }
  if (AppDir <> '') and FileExists(AppDir + '\{#RecoveryExeName}') then
  begin
    RunBounded(AppDir + '\{#RecoveryExeName}', '--restore-dns', AppDir, RestoreDnsDeadline);
    RunBounded(ExpandConstant('{sys}\taskkill.exe'), '/IM {#RecoveryExeName} /F', '', QuickDeadline);
  end;

  { And the pre-rename build, which holds the same driver handle under its old name. }
  RunBounded(ExpandConstant('{sys}\taskkill.exe'), '/IM AtomDpiBypass.exe /F', '', QuickDeadline);
  { The old logon task would keep starting a binary this installer just deleted. }
  RunBounded(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN AtomDpiBypass-Autostart /F',
    '', QuickDeadline);
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
