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
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\src\DiTunnel.Desktop\app.ico
UninstallDisplayIcon={app}\Di-Tunnel.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=Di-Tunnel-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Di-Tunnel

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
russian.ExitBeforeUninstall=Di-Tunnel: выйдите из приложения через трей и дождитесь восстановления сети перед удалением.
english.ExitBeforeUninstall=Di-Tunnel: exit the application from the tray and wait for network cleanup before uninstalling.
spanish.ExitBeforeUninstall=Di-Tunnel: cierra la aplicación desde la bandeja del sistema y espera a que se restaure la red antes de desinstalar.
chinesesimplified.ExitBeforeUninstall=Di-Tunnel：请通过系统托盘退出应用，等待网络恢复后再卸载。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Languages\LICENSE-ChineseSimplified"; DestDir: "{app}\Licenses"; DestName: "Inno-Setup-ChineseSimplified-MIT.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[UninstallRun]
Filename: "{app}\Di-Tunnel.NetworkHost.exe"; Parameters: "--cleanup-wfp"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupDiTunnelWfp"

[Run]
Filename: "{app}\Di-Tunnel.exe"; Description: "{cm:LaunchProgram,Di-Tunnel}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  CleanupPath: String;
  Attempt: Integer;
begin
  Result := '';
  { Avoid the Restart Manager confirmation page: the independent network host restores }
  { routes/WFP after the UI is terminated, and cleanup below also removes stale objects. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Di-Tunnel.exe', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
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
