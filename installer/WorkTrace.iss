; WorkTrace installer (Inno Setup 6). Built by CI:
;   ISCC /DAppVersion=0.2.<run> /DSourceDir=<publish folder> installer\WorkTrace.iss
; Per-user install (no admin prompt) to %LocalAppData%\Programs\WorkTrace. User data lives in
; %LocalAppData%\WorkTrace and is never touched by install, upgrade or uninstall.

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\WorkTrace"
#endif

#define AppName "WorkTrace"
#define AppExe "WorkTrace.exe"
; Must match src/WorkTrace.App/Shell/SingleInstanceGuard.cs
#define AppMutexName "Local\WorkTrace.SingleInstance.6E1B9F4A"
#define ExitEventName "Local\WorkTrace.Exit.6E1B9F4A"

[Setup]
; Never change AppId: it is how upgrades find the existing installation.
AppId={{5B0E2C71-8E4F-4C3A-9A7D-2F61C0D4E8A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WorkTrace
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\installer-output
OutputBaseFilename=WorkTrace-Setup
SetupIconFile=..\assets\icons\worktrace.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
; Remove the previous version's binaries so no stale files remain (user data is elsewhere).
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start WorkTrace"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Asks a running WorkTrace (incl. the tray-only instance) to close, waits for it, and only then
  force-closes it (older versions do not know the exit signal). }
procedure CloseRunningWorkTrace();
var
  Handle: THandle;
  Waited, ResultCode: Integer;
begin
  if not CheckForMutexes('{#AppMutexName}') then
    Exit;

  Handle := OpenEvent(EVENT_MODIFY_STATE, False, '{#ExitEventName}');
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
    Waited := 0;
    while CheckForMutexes('{#AppMutexName}') and (Waited < 15000) do
    begin
      Sleep(500);
      Waited := Waited + 500;
    end;
  end;

  if CheckForMutexes('{#AppMutexName}') then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
end;

function DesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not DesktopRuntimeInstalled() and not WizardSilent() then
    if MsgBox('WorkTrace needs the Microsoft .NET 10 Desktop Runtime (x64), which was not found.' + #13#10#13#10 +
              'Open the download page now? Setup continues either way; install the runtime before starting WorkTrace.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningWorkTrace();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningWorkTrace();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if not UninstallSilent() then
      MsgBox('WorkTrace was removed. Your data (time registrations, board, guides, settings and backups) is kept in ' +
             ExpandConstant('{localappdata}') + '\WorkTrace. Delete that folder yourself if you no longer need it.',
             mbInformation, MB_OK);
end;
