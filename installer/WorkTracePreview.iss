; WorkTrace VISUAL PREVIEW installer (Inno Setup 6): one isolated app per skin, installable next to WorkTrace.
; Built by .github/workflows/visual-preview.yml:
;   ISCC /DVariant=ModernFluent "/DVariantName=Modern Fluent" /DAppIdGuid=<guid> /DAppVersion=... /DSourceDir=... installer\WorkTracePreview.iss
; Own AppId, install folder, Start Menu entry and single-instance names; data in %LocalAppData%\WT-Preview\<Variant>.
; Never touches the WorkTrace installation, its process or its data (%LocalAppData%\WorkTrace).

#ifndef Variant
  #error Variant is required (e.g. /DVariant=ModernFluent)
#endif
#ifndef VariantName
  #error VariantName is required (e.g. "/DVariantName=Modern Fluent")
#endif
#ifndef AppIdGuid
  #error AppIdGuid is required
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\" + Variant
#endif

#define AppName "WT - " + VariantName
#define AppExe "WorkTrace.exe"
; Must match src/WorkTrace.App/Shell/SingleInstanceGuard.cs + PreviewIdentity.InstanceSuffix
#define AppMutexName "Local\WorkTrace.SingleInstance.6E1B9F4A." + Variant
#define ExitEventName "Local\WorkTrace.Exit.6E1B9F4A." + Variant

[Setup]
; Never change AppId: it is how upgrades find the existing installation.
AppId={{{#AppIdGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WorkTrace (visual preview)
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\WT-Preview-{#Variant}
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
OutputBaseFilename=WT-{#Variant}-Setup
SetupIconFile=..\assets\icons\preview\{#Variant}.ico
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
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

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
    { Only the copy running from this preview's own folder: WorkTrace and the other previews share the exe name. }
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -Command "Get-Process WorkTrace -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' +
        ExpandConstant('{app}\{#AppExe}') + ''' } | Stop-Process -Force"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
    if MsgBox('{#AppName} needs the Microsoft .NET 10 Desktop Runtime (x64), which was not found.' + #13#10#13#10 +
              'Open the download page now? Setup continues either way; install the runtime before starting it.',
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
      MsgBox('{#AppName} was removed. Its sample data is kept in ' +
             ExpandConstant('{localappdata}') + '\WT-Preview\{#Variant}. Delete that folder yourself if you no longer need it.',
             mbInformation, MB_OK);
end;
