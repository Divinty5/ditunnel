#ifndef AppVersion
  #define AppVersion "0.5.8"
#endif
#ifndef PublishDir
  #error PublishDir must point to the self-contained Windows publish directory
#endif

[Setup]
AppId={{635B3DC1-565C-4E43-9BAF-22805C13716D}
AppName=Di-Tunnel
AppVersion={#AppVersion}
AppPublisher=Di Vinty Interactive
AppPublisherURL=https://github.com/Divinty5/ditunnel
AppSupportURL=https://github.com/Divinty5/ditunnel/issues
DefaultDirName={autopf}\Di-Tunnel
DefaultGroupName=Di-Tunnel
DisableProgramGroupPage=yes
; The first pass only chooses a language. The installation always uses /ALLUSERS.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
UsePreviousPrivileges=no
ShowLanguageDialog=no
DisableStartupPrompt=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\Di-Tunnel.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=Di-Tunnel-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Di-Tunnel

#include "Branding.iss"

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
russian.CleanupStartFailed=Di-Tunnel: не удалось запустить восстановление сети.
english.CleanupStartFailed=Di-Tunnel: could not start network cleanup.
spanish.CleanupStartFailed=Di-Tunnel: no se pudo iniciar la restauración de la red.
chinesesimplified.CleanupStartFailed=Di-Tunnel：无法启动网络恢复。
russian.CleanupIncomplete=Di-Tunnel: восстановление сети не завершено. Дождитесь завершения сетевого модуля перед обновлением.
english.CleanupIncomplete=Di-Tunnel: network cleanup is not complete. Wait for the network host to exit before updating.
spanish.CleanupIncomplete=Di-Tunnel: la restauración de la red no ha finalizado. Espera a que el módulo de red se cierre antes de actualizar.
chinesesimplified.CleanupIncomplete=Di-Tunnel：网络恢复尚未完成。请等待网络模块退出后再更新。
russian.UpdateHandoffFailed=Di-Tunnel: приложение не подтвердило передачу управления установщику. Повторите обновление из настроек.
english.UpdateHandoffFailed=Di-Tunnel: the app did not hand over to Setup. Try updating again from Settings.
spanish.UpdateHandoffFailed=Di-Tunnel: la aplicación no transfirió el control al instalador. Reintenta desde Configuración.
chinesesimplified.UpdateHandoffFailed=Di-Tunnel：应用未将控制权移交给安装程序。请在设置中重试更新。
russian.ExitBeforeInstall=Di-Tunnel: выйдите из приложения через трей, затем продолжите установку.
english.ExitBeforeInstall=Di-Tunnel: exit the app from the notification area, then continue setup.
spanish.ExitBeforeInstall=Di-Tunnel: sal de la aplicación desde la bandeja del sistema y continúa con la instalación.
chinesesimplified.ExitBeforeInstall=Di-Tunnel：请通过系统托盘退出应用，然后继续安装。
russian.ExitBeforeUninstall=Di-Tunnel: выйдите из приложения через трей и дождитесь восстановления сети перед удалением.
english.ExitBeforeUninstall=Di-Tunnel: exit the application from the tray and wait for network cleanup before uninstalling.
spanish.ExitBeforeUninstall=Di-Tunnel: cierra la aplicación desde la bandeja del sistema y espera a que se restaure la red antes de desinstalar.
chinesesimplified.ExitBeforeUninstall=Di-Tunnel：请通过系统托盘退出应用，等待网络恢复后再卸载。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Languages\LICENSE-ChineseSimplified"; DestDir: "{app}\Licenses"; DestName: "Inno-Setup-ChineseSimplified-MIT.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"; AppUserModelID: "DiVintyInteractive.DiTunnel"
Name: "{autodesktop}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"; AppUserModelID: "DiVintyInteractive.DiTunnel"; Tasks: desktopicon

[UninstallRun]
Filename: "{app}\Di-Tunnel.NetworkHost.exe"; Parameters: "--cleanup-wfp"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupDiTunnelWfp"

[Registry]
; Startup remains opt-in in the application. Track only its value for uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Di-Tunnel"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\Di-Tunnel.exe"; Description: "{cm:LaunchProgram,Di-Tunnel}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\Di-Tunnel.exe"; Flags: nowait skipifnotsilent runasoriginaluser; Check: IsAppUpdate

[Code]
function OpenSetupExecutable(Window: HWND; Verb, Filename, Params, WorkingDir: String;
  ShowCmd: Integer): LongWord;
  external 'ShellExecuteW@shell32.dll stdcall';

function InitializeSetup: Boolean;
var
  Language, Argument, Arguments: String;
  Index: Integer;
  LaunchResult: LongWord;
begin
  Result := IsAdminInstallMode;
  if Result then Exit;

  Language := ExpandConstant('{param:LANG|}');
  if Language = '' then
  begin
    if WizardSilent then Exit;
    Language := ChooseSetupLanguage;
    if Language = '' then Exit;
  end;
  { ActiveLanguage is chosen before Pascal code starts. Pass the chosen language
    to the elevated installer so all native pages and messages use that locale. }
  if (Language <> 'russian') and (Language <> 'english') and
    (Language <> 'spanish') and (Language <> 'chinesesimplified') then Exit;
  Arguments := '/ALLUSERS /LANG=' + Language;
  for Index := 1 to ParamCount do
  begin
    Argument := ParamStr(Index);
    if (CompareText(Argument, '/CURRENTUSER') <> 0) and
      (CompareText(Copy(Argument, 1, 6), '/LANG=') <> 0) then
      Arguments := Arguments + ' ' + AddQuotes(Argument);
  end;
  { Inno's Exec and ShellExec reject the source package before installation.
    Open it through Windows and let Inno request elevation itself. }
  LaunchResult := OpenSetupExecutable(0, 'open', ExpandConstant('{srcexe}'),
    Arguments, ExpandConstant('{src}'), SW_SHOWNORMAL);
  if LaunchResult <= 32 then
    MsgBox(SysErrorMessage(Integer(LaunchResult)), mbError, MB_OK);
end;

function OpenUpdateEvent(Access: LongWord; Inherit: Longint; Name: String): LongWord;
  external 'OpenEventW@kernel32.dll stdcall';
function SignalUpdateEvent(Event: LongWord): Longint;
  external 'SetEvent@kernel32.dll stdcall';
function CloseUpdateEvent(Event: LongWord): Longint;
  external 'CloseHandle@kernel32.dll stdcall';
function SetupProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessSessionId(ProcessId: LongWord; var SessionId: LongWord): Longint;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';

function UpdateId: String;
var
  Index: Integer;
begin
  Result := ExpandConstant('{param:DITUNNELUPDATE|}');
  if Length(Result) <> 32 then
  begin
    Result := '';
    Exit;
  end;
  for Index := 1 to Length(Result) do
    if Pos(Result[Index], '0123456789abcdef') = 0 then
    begin
      Result := '';
      Exit;
    end;
end;

function IsAppUpdate: Boolean;
begin
  Result := WizardSilent and (UpdateId <> '');
end;

function SignalAppExit(EventName: String): Boolean;
var
  Event: LongWord;
begin
  Result := False;
  Event := OpenUpdateEvent(2, 0, EventName);
  if Event = 0 then Exit;
  try
    Result := SignalUpdateEvent(Event) <> 0;
  finally
    CloseUpdateEvent(Event);
  end;
end;

function WaitForAppExit: Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to 150 do
  begin
    if not CheckForMutexes('DiTunnel.Desktop') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(100);
  end;
  Log('Update handoff: timed out waiting for the application to exit.');
end;

function CloseInstalledLegacyClient: Boolean;
var
  Locator, Wmi, Processes, Process: Variant;
  Index: Integer;
  Session: LongWord;
  Path, InstalledClient: String;
begin
  Result := False;
  InstalledClient := ExpandConstant('{app}\Di-Tunnel.exe');
  if not FileExists(InstalledClient) then Exit;
  if ProcessSessionId(SetupProcessId, Session) = 0 then Exit;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Wmi.ExecQuery('SELECT * FROM Win32_Process WHERE Name = ''Di-Tunnel.exe'' AND SessionId = ' + IntToStr(Session));
    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      if VarIsNull(Process.ExecutablePath) then Continue;
      Path := Process.ExecutablePath;
      if CompareText(Path, InstalledClient) = 0 then
      begin
        if Process.Terminate(0) = 0 then Result := True;
      end;
    end;
  except
    Log('Unable to close the installed legacy Di-Tunnel client.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  CleanupPath: String;
  Attempt: Integer;
begin
  Result := '';
  if IsAppUpdate then
  begin
    if not SignalAppExit('Local\DiTunnel.Update.' + UpdateId) or not WaitForAppExit then
    begin
      Result := CustomMessage('UpdateHandoffFailed');
      Exit;
    end;
  end
  else
  begin
    if CheckForMutexes('DiTunnel.Desktop') then
    begin
      { Older clients lack the shutdown event. Limit fallback to this installation
        in the current session. The independent host handles rollback on UI exit. }
      if not SignalAppExit('Local\DiTunnel.CloseForSetup') and not CloseInstalledLegacyClient then
      begin
        Result := CustomMessage('ExitBeforeInstall');
        Exit;
      end;
      if not WaitForAppExit then
      begin
        Result := CustomMessage('ExitBeforeInstall');
        Exit;
      end;
    end;
  end;
  { Wait for the independent host's rollback before replacing its files. }
  CleanupPath := '';
  if FileExists(ExpandConstant('{app}\Di-Tunnel.NetworkHost.exe')) then
    CleanupPath := ExpandConstant('{app}\Di-Tunnel.NetworkHost.exe')
  else if FileExists(ExpandConstant('{app}\Di-Tunnel.exe')) then
    CleanupPath := ExpandConstant('{app}\Di-Tunnel.exe');
  if CleanupPath <> '' then
    for Attempt := 1 to 60 do
    begin
      if not Exec(CleanupPath, '--cleanup-wfp', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        Result := CustomMessage('CleanupStartFailed');
        Exit;
      end;
      if ResultCode = 0 then Exit;
      if ResultCode <> 4 then Break;
      Sleep(1000);
    end;
  if (CleanupPath <> '') and (ResultCode <> 0) then
    Result := CustomMessage('CleanupIncomplete');
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('DiTunnel.Desktop,Global\DiTunnel.NetworkHost.v1');
  if not Result then
    MsgBox(CustomMessage('ExitBeforeUninstall'), mbError, MB_OK);
end;
