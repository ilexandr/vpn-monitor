#ifndef AppVersion
 #define AppVersion "0.1.75"
#endif
#ifndef StageDir
 #define StageDir "..\artifacts\installer\stage"
#endif
#ifndef LegacyCleanup
 #define LegacyCleanup "..\artifacts\installer\legacy-cleanup.iss"
#endif
#ifndef ReleaseDir
 #define ReleaseDir "..\artifacts\releases"
#endif
[Setup]
AppId={{3A7D1DC4-7D80-4B3B-B17D-0A69BFD956CC}
AppName=AWG Dash Monitor
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
AppPublisher=ilexandr
AppPublisherURL=https://github.com/ilexandr/vpn-monitor
DefaultDirName={autopf}\VpnMonitor
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=AWG Dash Monitor
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDir}
OutputBaseFilename=VpnMonitor-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\Client\VpnMonitor.Client.exe
SetupIconFile=..\VpnMonitor.Client\Assets\AWG_Monitor.ico

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#StageDir}\App\*"; DestDir: "{app}\Client"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\Licenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Setup-Components.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Setup-Components.ps1"; Flags: dontcopy
Source: "Update-Application.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Resume-AfterUpdate.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Service-Recovery.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion

[InstallDelete]
#include LegacyCleanup
Type: files; Name: "{commonprograms}\VPN Monitor\VPN Monitor.lnk"
Type: files; Name: "{commonprograms}\VPN Monitor\Управление общими туннелями AWG.lnk"
Type: files; Name: "{commonprograms}\VPN Monitor\Удалить VPN Monitor.lnk"
Type: dirifempty; Name: "{commonprograms}\VPN Monitor"

[Registry]
Root: HKLM; Subkey: "Software\VpnMonitor"; Flags: uninsdeletekey

[Run]
Filename: "{app}\Client\VpnMonitor.Client.exe"; Description: "Запустить AWG Dash Monitor"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Icons]
Name: "{commonprograms}\AWG Dash Monitor\AWG Dash Monitor"; Filename: "{app}\Client\VpnMonitor.Client.exe"
Name: "{commonprograms}\AWG Dash Monitor\Управление общими туннелями AWG"; Filename: "{app}\Client\AwgPrototype.Manager.exe"
Name: "{commonprograms}\AWG Dash Monitor\Удалить AWG Dash Monitor"; Filename: "{uninstallexe}"

[Code]
var ComponentError: String;
    ComponentInstallFailed: Boolean;

function RunComponents(ScriptPath, Phase: String): Boolean;
var Code: Integer;
    DiagnosticPath: String;
    DiagnosticBytes: AnsiString;
begin
  DiagnosticPath := ExpandConstant('{tmp}\vpnmonitor-component-error.txt');
  DeleteFile(DiagnosticPath);
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -Phase ' + Phase + ' -Root "' + ExpandConstant('{app}') + '" -DiagnosticPath "' + DiagnosticPath + '" -SetupSource "' + ExpandConstant('{srcexe}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  ComponentError := '';
  if not Result then begin
    if LoadStringFromFile(DiagnosticPath, DiagnosticBytes) then
      ComponentError := UTF8Decode(DiagnosticBytes);
    if ComponentError = '' then ComponentError := 'PowerShell: exit code ' + IntToStr(Code);
    Log('AWG Dash Monitor ' + Phase + ': ' + ComponentError);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('Setup-Components.ps1');
  if not RunComponents(ExpandConstant('{tmp}\Setup-Components.ps1'), 'Preflight') then
    Result := 'Проверка установки: ' + ComponentError
  else Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not RunComponents(ExpandConstant('{app}\Setup\Setup-Components.ps1'), 'Install') then
    begin
      ComponentInstallFailed := True;
      RaiseException('Не удалось настроить компоненты службы. Проверьте журнал установки; профили пользователей сохранены.');
    end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ComponentInstallFailed then Result := 20 else Result := 0;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    UninstallProgressForm.StatusLabel.Caption := 'Закрытие AWG Dash Monitor и менеджера во всех сеансах, остановка общего туннеля…';
    if not RunComponents(ExpandConstant('{app}\Setup\Setup-Components.ps1'), 'PrepareRemove') then
    begin
      SuppressibleMsgBox('Не удалось подготовить удаление: ' + ComponentError, mbError, MB_OK, IDOK);
      Abort;
    end;
    if not RunComponents(ExpandConstant('{app}\Setup\Setup-Components.ps1'), 'Remove') then
    begin
      SuppressibleMsgBox('Не удалось удалить регистрацию службы. Файлы профилей сохраняются.', mbError, MB_OK, IDOK);
      Abort;
    end;
  end;
end;
