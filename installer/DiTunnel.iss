#ifndef AppVersion
  #define AppVersion "0.3.5"
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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Di-Tunnel"; Filename: "{app}\Di-Tunnel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[UninstallRun]
Filename: "{app}\Di-Tunnel.exe"; Parameters: "--cleanup-wfp"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupDiTunnelWfp"

[Run]
Filename: "{app}\Di-Tunnel.exe"; Description: "{cm:LaunchProgram,Di-Tunnel}"; Verb: "runas"; Flags: shellexec nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  { Avoid the Restart Manager confirmation page: the independent network host restores }
  { routes/WFP after the UI is terminated, and cleanup below also removes stale objects. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Di-Tunnel.exe', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  { Leave the independent network host alive long enough to restore routes and DNS. }
  Sleep(3000);
  if FileExists(ExpandConstant('{app}\Di-Tunnel.exe')) then
    Exec(ExpandConstant('{app}\Di-Tunnel.exe'), '--cleanup-wfp', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('DiTunnel.Desktop');
  if not Result then
    MsgBox('Di-Tunnel: please exit the application from the tray before uninstalling.' + #13#10 +
      'Di-Tunnel: выйдите из приложения через трей перед удалением.', mbError, MB_OK);
end;
