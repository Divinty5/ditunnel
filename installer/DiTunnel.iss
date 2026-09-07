#ifndef AppVersion
  #define AppVersion "0.3.0"
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
AppMutex=DiTunnel.Desktop
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

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('DiTunnel.Desktop');
  if not Result then
    MsgBox('Di-Tunnel: please exit the application from the tray before uninstalling.' + #13#10 +
      'Di-Tunnel: выйдите из приложения через трей перед удалением.', mbError, MB_OK);
end;
