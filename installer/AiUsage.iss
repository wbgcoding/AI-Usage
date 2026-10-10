; AI-Usage installer (Inno Setup).
; Built by build.bat, which passes /DAppVersion. The version lives in the csproj only.
;
; The scope question (for everyone or just me) is Inno's own elevation dialog, asked before the
; wizard itself runs (PrivilegesRequired/PrivilegesRequiredOverridesAllowed below). The folder
; page that follows carries the second choice: a checkbox switches to a portable copy - one
; single exe dropped into a folder the user picks, with nothing else written to this PC.
; Everything downstream reads IsPortableMode in [Code]. The foreground-activation plumbing below brings
; Setup to the front when a browser, Explorer or another app launched it. The in-app update downloads
; this setup and runs it silently.
;
; The app needs the .NET 10 Desktop Runtime and does not carry its own copy. When the runtime is
; missing, the [Code] section below downloads Microsoft's installer through RuntimeSetup.ps1 (which
; checks address, signature and signer before anything runs) and installs it before any file of the
; app is written, so a failed runtime step leaves nothing half installed. Standard and portable
; mode both need it, and so does a silent update.

#ifndef AppVersion
  #error AppVersion is not defined: pass /DAppVersion=<version> (build.bat reads it from the csproj).
#endif

#define AppName "AI-Usage"
#define AppPublisher "BGCoding"
#define AppExeName "AI-Usage.exe"
#define AppUrl "https://github.com/wbgcoding/AI-Usage"

#define PayloadX64 AddBackslash(SourcePath) + "..\build\publish\win-x64\AI-Usage.exe"
#define PayloadArm64 AddBackslash(SourcePath) + "..\build\publish\win-arm64\AI-Usage.exe"

; Size of the file that actually gets installed, read from the real payload at compile time so the
; figure can never drift from what ships. Exactly one of the two single-file exes is installed and
; they differ by a few MB, so the larger one is the honest estimate.
#define PayloadSize FileSize(PayloadX64)
#if FileSize(PayloadArm64) > PayloadSize
  #define PayloadSize FileSize(PayloadArm64)
#endif
#if PayloadSize < 0
  #define PayloadSize 0
#endif

[Setup]
AppId={{9FF5FEED-5C44-428A-97A9-028E7614B137}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© 2026 BGCoding
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Shows its own page between Welcome and the folder page, in both languages (Inno picks the
; matching text automatically); the file itself is the plain MIT licence, the same one linked from
; README.md.
LicenseFile=..\LICENSE

; Always show the folder page. The default (auto) SKIPS it once a previous install is detected and
; reuses that folder - which is exactly why picking a folder for a portable copy would appear to do
; nothing after the app had been installed once.
DisableDirPage=no

; Reuse the folder of an existing installation when Setup runs with no wizard at all.
UsePreviousAppDir=yes

; Updating an existing installation is a normal case, and the folder page is prefilled with the
; folder that installation really lives in (see CurPageChanged), so there is nothing left to warn
; about. Portable would hit the warning on every single run too: its default is the folder Setup
; was started from, which by definition exists.
DirExistsWarning=no

; A portable copy leaves nothing behind: no uninstaller files beside the exe, no Programs and
; Features entry. Both directives take a scripted boolean, evaluated when the install runs, so
; they follow the checkbox on the directory page.
Uninstallable=not IsPortableMode
CreateUninstallRegKey=not IsPortableMode

; Per-machine or per-user is its own choice, separate from the standard/portable one on the
; directory page - Inno's own built-in dialog asks it, right before Setup's language page. "lowest" is the baseline
; (a deliberate design choice): admin is requested only if the user actually picks "for all users"
; in that dialog, never upfront. PrivilegesRequiredOverridesAllowed still shows both choices; "lowest"
; is also what makes Inno's compiler stop warning about this script's HKCU (per-user) entries,
; since with "admin" as the baseline it otherwise flags them as a possible privilege mismatch even
; though the runtime override already handles both cases correctly.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Without this, Inno reuses the scope of an existing installation of this AppId and never asks
; again. An unattended run still gets a deterministic answer: Inno itself falls back to the
; previous scope once /CURRENTUSER or /ALLUSERS is passed on the command line.
UsePreviousPrivileges=no

OutputDir=..\dist
OutputBaseFilename=Setup-AI-Usage-{#AppVersion}
SetupIconFile=..\src\AiUsage\Assets\app.ico
UninstallDisplayIcon={code:GetAppExePath}
UninstallDisplayName={#AppName}

#if PayloadSize > 0
ExtraDiskSpaceRequired={#PayloadSize}
UninstallDisplaySize={#PayloadSize}
#endif

Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern

; The generated log is copied out to a known, bounded location in CurStepChanged below - Setup's
; own temp copy is gone once the process exits.
SetupLogging=yes

MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64

; Reinstalling/updating over a running copy: closed via the Restart Manager without asking first
; (force rather than yes - yes stops on a wizard page listing the running copy and waits for a
; click, which is a question with exactly one sensible answer), and NOT restarted through it: the
; app is brought back explicitly in [Run] instead, guarded on WasAppRunning so a silent reinstall
; does not leave the user's tray icon gone.
CloseApplications=force
CloseApplicationsFilter=*.exe
RestartApplications=no

; Otherwise Setup's own language picker opens before any [Code] runs, ahead of the
; AllowSetForegroundWindow call below - the system's own UI language is used instead, silently.
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.AutostartTask=Start {#AppName} automatically when I sign in to Windows
en.DesktopShortcutTask=Create a desktop shortcut
en.LaunchAfter=Launch {#AppName} now
en.PortableCheck=Portable
en.PortableDirDescription=Choose a folder for the portable copy of {#AppName}. Setup suggests a folder of its own next to this installer and places {#AppExeName} into it. Nothing else is written to this PC.
en.SourceLink=Source code and issues: github.com/wbgcoding/AI-Usage
en.RuntimeComponent=Required component
en.RuntimeNeeded=AI-Usage needs the .NET 10 Desktop Runtime from Microsoft. Setup downloads and installs it now (about 60 MB).
en.RuntimeInstalling=Downloading and installing the .NET 10 Desktop Runtime. Windows asks for permission once the download is done.
en.RuntimeFailed=The .NET Desktop Runtime could not be installed. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and start setup again.
en.UpdateDirDescription={#AppName} is already installed on this PC and will be updated in the folder shown below. Click Next to continue, or Browse to choose a different folder.
en.RemoveDataQuestion=Also remove {#AppName}'s saved settings and history for the current user in %APPDATA%\{#AppName}\?
en.DowngradeQuestion=Version {0} is already installed, and this installer carries the older version {1}. Continue anyway?
de.AutostartTask={#AppName} automatisch bei der Windows-Anmeldung starten
de.DesktopShortcutTask=Verknüpfung auf dem Desktop erstellen
de.LaunchAfter={#AppName} jetzt starten
de.PortableCheck=Portable
de.PortableDirDescription=Wähle einen Ordner für die portable Version von {#AppName}. Setup schlägt einen eigenen Ordner neben diesem Installationsprogramm vor und legt {#AppExeName} darin ab. Sonst wird nichts auf diesem PC gespeichert.
de.SourceLink=Quelltext und Fehlermeldungen: github.com/wbgcoding/AI-Usage
de.RuntimeComponent=Erforderliche Komponente
de.RuntimeNeeded=AI-Usage braucht die .NET 10 Desktop Runtime von Microsoft. Das Setup lädt sie jetzt herunter und installiert sie (etwa 60 MB).
de.RuntimeInstalling=Die .NET 10 Desktop Runtime wird heruntergeladen und installiert. Windows fragt nach der Erlaubnis, sobald der Download fertig ist.
de.RuntimeFailed=Die .NET Desktop Runtime ließ sich nicht installieren. Installiere sie von https://dotnet.microsoft.com/download/dotnet/10.0 und starte das Setup erneut.
de.UpdateDirDescription={#AppName} ist auf diesem PC bereits installiert und wird im unten angezeigten Ordner aktualisiert. Klicke auf Weiter, oder auf Durchsuchen, um einen anderen Ordner zu wählen.
de.RemoveDataQuestion=Sollen auch die gespeicherten Einstellungen und der Verlauf von {#AppName} für den aktuellen Benutzer in %APPDATA%\{#AppName}\ entfernt werden?
de.DowngradeQuestion=Version {0} ist bereits installiert, dieses Installationsprogramm enthält die ältere Version {1}. Trotzdem fortfahren?

[Files]
; The published single-file exe for the architecture being installed, taken from the build stage.
; In portable mode this one file is the entire installation - it goes straight into the chosen
; folder, with no subfolder and no uninstaller (Uninstallable above).
Source: "..\build\publish\win-x64\AI-Usage.exe"; DestDir: "{app}"; DestName: "{#AppExeName}"; \
    Check: not IsArm64; Flags: ignoreversion
Source: "..\build\publish\win-arm64\AI-Usage.exe"; DestDir: "{app}"; DestName: "{#AppExeName}"; \
    Check: IsArm64; Flags: ignoreversion

; The runtime step's script. Extracted to Setup's own temporary folder when needed, never installed.
Source: "RuntimeSetup.ps1"; Flags: dontcopy

[Icons]
; Portable mode has no Start Menu entry - it carries no Task or Registry entry of its own either,
; the same guard everywhere else that only applies to a standard installation.
Name: "{group}\{#AppName}"; Filename: "{code:GetAppExePath}"; Check: not IsPortableMode

[Run]
; All three finish-page choices are pre-ticked (no "unchecked" flag).
; Portable mode shows only "Launch now": autostart and the desktop shortcut carry
; Check: not IsPortableMode, same as the Start Menu entry and the registry entry below.
;
; Autostart is written by the APP itself (AutostartService.Enable), not from here - Setup's own
; HKCU is the hive of whoever approved the elevation prompt, which on a machine with a separate
; administrator account is not the person installing. runasoriginaluser puts the app back in the
; installing user's context, where its own HKCU is the right one. It writes the value and exits;
; nothing is shown.
; skipifsilent: an in-app update runs Setup silently and must not re-enable autostart the user turned off.
Filename: "{code:GetAppExePath}"; Description: "{cm:AutostartTask}"; Parameters: "--set-autostart"; \
    Flags: runhidden waituntilterminated postinstall skipifsilent runasoriginaluser; Check: not IsPortableMode
; The desktop shortcut is written by the app itself too (DesktopShortcutService.Create), for the
; same originaluser-context reason as autostart above. skipifsilent: an update must not bring back
; a shortcut the user deleted.
Filename: "{code:GetAppExePath}"; Description: "{cm:DesktopShortcutTask}"; \
    Parameters: "--create-desktop-shortcut"; \
    Flags: runhidden waituntilterminated postinstall skipifsilent runasoriginaluser; Check: not IsPortableMode
; runasoriginaluser: without it a per-machine install would start the app with Setup's admin
; token, and the app is required to run unelevated.
Filename: "{code:GetAppExePath}"; Description: "{cm:LaunchAfter}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser
; A dedicated relaunch switch instead of RestartApplications: only needed when no wizard was shown
; to offer the checkbox above, and only when the app was actually closed as part of this run
; (WasAppRunning, captured before CloseApplications acted).
Filename: "{code:GetAppExePath}"; Flags: nowait runasoriginaluser; \
    Check: WasAppRunning and WizardSilent and not IsPortableMode

[UninstallDelete]
; Same hive limitation as autostart above: on a per-machine install approved by a different
; administrator account, the shortcut was written to the installing user's desktop, which an
; elevated uninstall running under that administrator's own account cannot see. Not worked around.
Type: files; Name: "{autodesktop}\{#AppName}.lnk"

[Registry]
; Creates nothing - the app writes the value itself (see [Run]). This entry exists only so
; uninstalling takes it away again. [UninstallRun] cannot do the same job: it has no
; runasoriginaluser flag, so it would look at the elevating account's hive rather than the user's.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "AI-Usage"; Flags: uninsdeletevalue; Check: not IsPortableMode
; The app registers its notification identity itself on the first alert; this entry only removes it.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\BGCoding.AI-Usage"; ValueType: none; \
    Flags: dontcreatekey uninsdeletekey; Check: not IsPortableMode

[Code]
const
  { Where Inno records a standard installation of this AppId - it appends "_is1" to the AppId. }
  UninstallKeyName =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9FF5FEED-5C44-428A-97A9-028E7614B137}_is1';
  AppPathValueName = 'Inno Setup: App Path';
  { Where the .NET installers record the shared runtimes they put on this PC: one value per
    installed version under the key of the processor type. }
  RuntimeKeyRoot = 'SOFTWARE\dotnet\Setup\InstalledVersions\';
  RuntimeKeyTail = '\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeUrlBase = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-';

var
  { The portable checkbox lives directly on the built-in directory page: everything downstream
    (default folder, whether Icons/registry entries happen, whether an uninstaller is built at
    all) reads it rather than a Task, because a Task is only chosen AFTER the directory page in
    Inno's fixed page order - far too late to steer it. }
  PortableCheck: TNewCheckBox;
  { Whether the directory page's default folder text has been set for its first display yet - set
    once, so returning to the page (Back, then Next again) never overwrites a folder the user
    picked or typed themselves. }
  DirPageInitialized: Boolean;
  { Whether the finished page's own default text has already had the archive address appended. }
  FinishedLabelExtended: Boolean;
  { The directory page's own default wording, captured once before anything here overwrites it -
    SetupMessage(msgSelectDirDesc) cannot stand in for it: unlike the page's own default caption,
    it does not substitute the "[name]" token, so re-using it would leave a literal "[name]" on screen.
    Restored verbatim when returning to a plain standard
    install after visiting Portable or an update folder. }
  DefaultSelectDirCaption: String;
  HoldingFront: Boolean;
  FrontRequested: Boolean;
  { Captured in InitializeSetup, before CloseApplications can act - the only point at which "was
    the app running" can still be answered honestly. }
  AppWasRunning: Boolean;
  { Shown before the install when the runtime is missing, and the busy page while it is fetched. }
  RuntimePage: TOutputMsgWizardPage;
  RuntimeProgress: TOutputMarqueeProgressWizardPage;

function FindWindowA(lpClassName: LongInt; lpWindowName: String): HWND;
  external 'FindWindowA@user32.dll stdcall';

function SetForegroundWindow(Wnd: HWND): BOOL;
  external 'SetForegroundWindow@user32.dll stdcall';

function GetForegroundWindow: HWND;
  external 'GetForegroundWindow@user32.dll stdcall';

function GetWindowThreadProcessId(Wnd: HWND; ProcessId: Longint): DWORD;
  external 'GetWindowThreadProcessId@user32.dll stdcall';

function GetCurrentThreadId: DWORD;
  external 'GetCurrentThreadId@kernel32.dll stdcall';

function AttachThreadInput(AttachTo, AttachFrom: DWORD; Attach: BOOL): BOOL;
  external 'AttachThreadInput@user32.dll stdcall';

function SwitchToThisWindow(Wnd: HWND; AltTab: BOOL): Longint;
  external 'SwitchToThisWindow@user32.dll stdcall';

function SetWindowPos(Wnd: HWND; WndInsertAfter: Longint; X, Y, Cx, Cy: Integer; Flags: UINT): BOOL;
  external 'SetWindowPos@user32.dll stdcall';

{ Lets Setup hand foreground activation rights to itself before the wizard window exists yet,
  so the later ForceForeground/BringWizardToFront calls are not refused by Windows. }
function AllowSetForegroundWindow(dwProcessId: DWORD): BOOL;
  external 'AllowSetForegroundWindow@user32.dll stdcall';

const
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOMOVE = $2;
  SWP_NOSIZE = $1;
  ASFW_ANY = $FFFFFFFF;

{ Windows refuses SetForegroundWindow to a process that is not already in front, which is exactly
  the position Setup is in when launched from a browser, Explorer or another app. This
  procedure attaches to the foreground thread's input for the call and, if that is not enough,
  switches to the window and toggles topmost. }
procedure ForceForeground(Wnd: HWND);
var
  ForegroundWnd: HWND;
  ForegroundThread, OwnThread: DWORD;
  Attached: BOOL;
begin
  ForegroundWnd := GetForegroundWindow;
  OwnThread := GetCurrentThreadId;
  Attached := False;

  if (ForegroundWnd <> 0) and (ForegroundWnd <> Wnd) then
  begin
    ForegroundThread := GetWindowThreadProcessId(ForegroundWnd, 0);
    if (ForegroundThread <> 0) and (ForegroundThread <> OwnThread) then
      Attached := AttachThreadInput(OwnThread, ForegroundThread, True);
  end;

  SetForegroundWindow(Wnd);

  if Attached then
    AttachThreadInput(OwnThread, ForegroundThread, False);

  if GetForegroundWindow <> Wnd then
  begin
    SwitchToThisWindow(Wnd, True);
    SetWindowPos(Wnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
    SetWindowPos(Wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
  end;
end;

function IsArm64: Boolean;
begin
  Result := ProcessorArchitecture = paArm64;
end;

{ The processor type the app being installed runs as, spelled as in the runtime's registry key and
  in Microsoft's download address. }
function RuntimeArch: String;
begin
  if IsArm64 then
    Result := 'arm64'
  else
    Result := 'x64';
end;

{ Whether one registry view lists a released 10.x version under the runtime key AND that version
  is still on disk. The listing alone is not enough: uninstalling the runtime leaves its version
  values behind, so a PC without the runtime can still list it. A preview (a name with a dash) does
  not count, because the app does not roll forward onto one. On disk, a known file of the runtime
  has to be there, not just a folder. Both are looked up under the install location the installers
  record, or the standard one when none is recorded. }
function RuntimeListedIn(Root: Integer): Boolean;
var
  Names: TArrayOfString;
  Location: String;
  I: Integer;
begin
  Result := False;
  if not RegGetValueNames(Root, RuntimeKeyRoot + RuntimeArch + RuntimeKeyTail, Names) then
    Exit;
  if not RegQueryStringValue(Root, RuntimeKeyRoot + RuntimeArch, 'InstallLocation', Location)
     or (Location = '') then
    Location := ExpandConstant('{commonpf64}\dotnet');

  for I := 0 to GetArrayLength(Names) - 1 do
    if (Copy(Names[I], 1, 3) = '10.') and (Pos('-', Names[I]) = 0)
       and FileExists(AddBackslash(Location) + 'shared\Microsoft.WindowsDesktop.App\' + Names[I] +
                      '\PresentationFramework.dll') then
    begin
      Result := True;
      Exit;
    end;
end;

{ Whether a .NET 10 Desktop Runtime for this processor type is installed. Microsoft's installers
  are 32-bit programs and record the runtime in the 32-bit registry view (Wow6432Node), which is
  where a real PC has it; the 64-bit view is read too, for an installer that writes there. }
function DesktopRuntimeInstalled: Boolean;
begin
  Result := RuntimeListedIn(HKLM32) or RuntimeListedIn(HKLM64);
end;

{ Runs the download and install script and then asks the registry again - the runtime being there
  is what counts, not what the script or the installer reported. The script's own log goes into
  Setup's log. }
function InstallDesktopRuntime: Boolean;
var
  Script, LogPath, Parameters, LogText: String;
  ResultCode: Integer;
  LogContent: AnsiString;
begin
  Result := False;
  ExtractTemporaryFile('RuntimeSetup.ps1');
  Script := ExpandConstant('{tmp}\RuntimeSetup.ps1');
  LogPath := ExpandConstant('{tmp}\runtime-setup.log');
  Parameters :=
    '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + Script + '"' +
    ' -Url "' + RuntimeUrlBase + RuntimeArch + '.exe"' +
    ' -Folder "' + ExpandConstant('{tmp}') + '"' +
    ' -LogFile "' + LogPath + '"';

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Runtime step: PowerShell could not be started.');
    Exit;
  end;

  if LoadStringFromFile(LogPath, LogContent) then
  begin
    LogText := LogContent;
    Log('Runtime step log:' + #13#10 + LogText);
  end;
  Log('Runtime step: script exit code ' + IntToStr(ResultCode));

  Result := (ResultCode = 0) and DesktopRuntimeInstalled;
end;

{ True once the user has ticked the portable checkbox on the directory page. False before the
  page has been seen at all, which is what a silent run needs: no wizard is shown there, so
  nothing is ticked and it always behaves like a standard installation. }
function IsPortableMode: Boolean;
begin
  Result := (PortableCheck <> nil) and PortableCheck.Checked;
end;

function GetAppExePath(Param: String): String;
begin
  Result := ExpandConstant('{app}\{#AppExeName}');
end;

{ The folder an existing standard installation lives in, or an empty string if there is none. Only
  the hive for the scope this run actually installs into is consulted: a per-user run finding a
  machine-wide installation would prefill a Program Files folder it cannot write to, and with both
  scopes present it would update the wrong one. }
function ExistingInstallDir: String;
var
  Hive: Integer;
begin
  Result := '';
  if IsAdminInstallMode then
    Hive := HKLM
  else
    Hive := HKCU;

  if not RegQueryStringValue(Hive, UninstallKeyName, AppPathValueName, Result) then
    Result := '';
  if (Result <> '') and not DirExists(Result) then
    Result := '';
end;

{ The version an existing standard installation of this AppId reports, or an empty string if
  there is none - read from the same hive ExistingInstallDir uses, for the same reason. Inno
  writes DisplayVersion into every uninstall key itself, so there is nothing of our own to keep
  in sync. }
function GetInstalledVersion: String;
var
  Hive: Integer;
begin
  Result := '';
  if IsAdminInstallMode then
    Hive := HKLM
  else
    Hive := HKCU;

  if not RegQueryStringValue(Hive, UninstallKeyName, 'DisplayVersion', Result) then
    Result := '';
end;

{ CustomMessage substitution only understands the message's own name, not parameters - the
  placeholders here are ours, filled in by hand. }
function FormatDowngradeQuestion(const InstalledVersion, OfferedVersion: String): String;
begin
  Result := CustomMessage('DowngradeQuestion');
  StringChangeEx(Result, '{0}', InstalledVersion, True);
  StringChangeEx(Result, '{1}', OfferedVersion, True);
end;

{ The directory page's text and suggested folder differ by mode: portable suggests the folder
  Setup itself was started from; a standard installation suggests the folder an existing
  installation already uses, and Program Files only when there is none. Called once when the page
  is first shown, and again on every tick/untick of the portable checkbox. }
procedure ApplyPortableSuggestion(Portable: Boolean);
var
  Existing: String;
begin
  if Portable then
  begin
    WizardForm.SelectDirLabel.Caption := CustomMessage('PortableDirDescription');
    WizardForm.DirEdit.Text := AddBackslash(ExpandConstant('{src}')) + '{#AppName}';
  end
  else
  begin
    Existing := ExistingInstallDir;
    if Existing <> '' then
    begin
      WizardForm.SelectDirLabel.Caption := CustomMessage('UpdateDirDescription');
      WizardForm.DirEdit.Text := Existing;
    end
    else
    begin
      WizardForm.SelectDirLabel.Caption := DefaultSelectDirCaption;
      WizardForm.DirEdit.Text := ExpandConstant('{autopf}\{#AppName}');
    end;
  end;
end;

procedure PortableCheckClick(Sender: TObject);
begin
  ApplyPortableSuggestion(PortableCheck.Checked);
end;

function InitializeSetup: Boolean;
var
  InstalledVersion: String;
  InstalledPacked, OfferedPacked: Int64;
begin
  Result := True;

  { Must happen before anything else here: grants the not-yet-created wizard window the right to
    take the foreground later, which Windows would otherwise refuse to a process that starts out
    behind whatever launched it. }
  AllowSetForegroundWindow(ASFW_ANY);

  { The one moment "was the app running" can still be answered honestly - CloseApplications acts
    between here and CurStepChanged. A plain window-title lookup (the app's own title, matching
    SingleInstanceService's lookup) rather than a process check, so a portable copy running from
    an unrelated folder does not falsely arm the relaunch. }
  AppWasRunning := FindWindowA(0, '{#AppName}') <> 0;

  { An equal version continues as a repair, an offered version that is newer is the ordinary
    upgrade case - only an older offered version needs a question, defaulting to "no" so an
    unattended run never installs one silently. }
  InstalledVersion := GetInstalledVersion;
  if (InstalledVersion <> '') and StrToVersion(InstalledVersion, InstalledPacked)
     and StrToVersion('{#AppVersion}', OfferedPacked) then
  begin
    if ComparePackedVersion(OfferedPacked, InstalledPacked) < 0 then
      if SuppressibleMsgBox(FormatDowngradeQuestion(InstalledVersion, '{#AppVersion}'),
           mbConfirmation, MB_YESNO, IDNO) = IDNO then
        Result := False;
  end;
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateOutputMsgPage(wpSelectDir, '.NET 10 Desktop Runtime',
    CustomMessage('RuntimeComponent'), CustomMessage('RuntimeNeeded'));
  RuntimeProgress := CreateOutputMarqueeProgressPage('.NET 10 Desktop Runtime', '');

  DefaultSelectDirCaption := WizardForm.SelectDirLabel.Caption;

  { WizardForm.SelectDirPage is a TNewNotebookPage, unlike the custom pages CreateCustomPage
    returns - it has no Surface, so the checkbox is parented directly to it and positioned by
    hand, below the existing folder edit and browse button. }
  PortableCheck := TNewCheckBox.Create(WizardForm);
  PortableCheck.Parent := WizardForm.SelectDirPage;
  PortableCheck.Caption := CustomMessage('PortableCheck');
  PortableCheck.Top := WizardForm.DirBrowseButton.Top + WizardForm.DirBrowseButton.Height + ScaleY(12);
  PortableCheck.Left := WizardForm.DirEdit.Left;
  PortableCheck.Width := WizardForm.SelectDirPage.Width - PortableCheck.Left;
  PortableCheck.OnClick := @PortableCheckClick;

  DirPageInitialized := False;
end;

{ The runtime page is only for a PC that lacks the runtime. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (RuntimePage <> nil) and (PageID = RuntimePage.ID) and DesktopRuntimeInstalled;
end;

{ Last chance before any file is written. A missing runtime is fetched and installed here, in a
  wizard run and in a silent one (an in-app update) alike; a failure returns the message and Setup
  stops with the app untouched. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if DesktopRuntimeInstalled then
    Exit;

  if not WizardSilent then
  begin
    RuntimeProgress.SetText(CustomMessage('RuntimeInstalling'), '');
    RuntimeProgress.Show;
  end;
  try
    if not InstallDesktopRuntime then
      Result := CustomMessage('RuntimeFailed');
  finally
    if not WizardSilent then
      RuntimeProgress.Hide;
  end;
end;

procedure WizardActivated(Sender: TObject);
begin
  if HoldingFront then
  begin
    HoldingFront := False;
    WizardForm.FormStyle := fsNormal;
  end;
end;

{ Setup starting behind the window that launched it looks like nothing happened at all - held on
  top and released only once it has actually been activated (see ForceForeground). }
procedure BringWizardToFront;
begin
  if FrontRequested then
    Exit;

  FrontRequested := True;
  HoldingFront := True;
  WizardForm.OnActivate := @WizardActivated;
  WizardForm.FormStyle := fsStayOnTop;
  WizardForm.BringToFront;
  ForceForeground(WizardForm.Handle);
end;

{ The directory page's default folder is set once, the first time it is shown - not on every
  visit, so going Back and Next again never throws away a folder the user picked. Every later
  change comes from the portable checkbox's own OnClick instead. The finished page gets the
  archive address appended to its own default text exactly once, the same one-shot guard. }
procedure CurPageChanged(CurPageID: Integer);
begin
  BringWizardToFront;

  if (CurPageID = wpFinished) and not FinishedLabelExtended then
  begin
    FinishedLabelExtended := True;
    WizardForm.FinishedLabel.Caption :=
      WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('SourceLink');
    Exit;
  end;

  if (CurPageID <> wpSelectDir) or DirPageInitialized then
    Exit;

  DirPageInitialized := True;
  ApplyPortableSuggestion(False);
end;

{ The auto-relaunch Check in [Run] reads this - only relevant to the standard (non-portable),
  silent path where no "launch now" checkbox was ever shown. }
function WasAppRunning: Boolean;
begin
  Result := AppWasRunning;
end;

{ Deletes every file in Folder matching "setup-*.log" beyond the newest KeepCount, sorted by name
  - safe because the timestamp in the name (yyyymmdd-hhnnss) sorts the same as the date it names. }
procedure PruneOldLogs(const Folder: String; KeepCount: Integer);
var
  FindRec: TFindRec;
  Names: TStringList;
  I: Integer;
begin
  Names := TStringList.Create;
  try
    if FindFirst(AddBackslash(Folder) + 'setup-*.log', FindRec) then
    begin
      try
        repeat
          Names.Add(FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;

    Names.Sort;
    for I := 0 to Names.Count - 1 - KeepCount do
      DeleteFile(AddBackslash(Folder) + Names[I]);
  finally
    Names.Free;
  end;
end;

{ Setup's own SetupLogging copy lives in a temp folder that is gone once the process exits, so it
  is copied out here to a known, bounded location. The log constant is only meaningful from this
  step onward, once the log is complete. A brace constant must never appear inside a brace comment:
  its closing brace ends the comment early and the rest of the line is parsed as code. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  LogFolder, DestFile: String;
begin
  if CurStep = ssDone then
  begin
    LogFolder := ExpandConstant('{localappdata}\{#AppName}\setup');
    if ForceDirectories(LogFolder) and FileExists(ExpandConstant('{log}')) then
    begin
      DestFile := AddBackslash(LogFolder) + 'setup-' +
        GetDateTimeString('yyyymmdd-hhnnss', #0, #0) + '.log';
      if CopyFile(ExpandConstant('{log}'), DestFile, False) then
        PruneOldLogs(LogFolder, 5);
    end;
  end;
end;

{ Uninstalling while the app runs blocks on files that cannot be deleted, and a silent uninstall
  has nobody to ask - so it is closed first, always, by folder rather than by image name: matching
  by name alone would also close an unrelated portable copy running from a stick or another
  folder. Only a copy inside the installation folder can hold this uninstall's files. }
function PsQuote(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
  Script: String;
begin
  Result := True;
  Script :=
    '$p = ''' + PsQuote(ExpandConstant('{app}\{#AppExeName}')) + '''; ' +
    'Get-Process -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq $p } | Stop-Process -Force -ErrorAction SilentlyContinue';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Settings and history in %APPDATA% are the user's and are left behind by default, as Windows apps
  normally do - offered as an explicit, deselectable choice on uninstall
  rather than removed silently. Guarded on "not UninstallSilent": a message box that showed
  unconditionally would hang a silent/unattended uninstall waiting for a click nobody can give -
  the exact failure mode /SUPPRESSMSGBOXES does not prevent. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(CustomMessage('RemoveDataQuestion'), mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
