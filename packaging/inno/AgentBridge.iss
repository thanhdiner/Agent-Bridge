#define AppName "AgentBridge"
#define AppVersion GetEnv("AGENTBRIDGE_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0"
#endif
#define PayloadRoot GetEnv("AGENTBRIDGE_INNO_SOURCE")
#define OutputRoot GetEnv("AGENTBRIDGE_INNO_OUTPUT")
#define IconFile "..\..\src\AgentBridge.Desktop\Assets\agentbridge.ico"

[Setup]
AppId={{7F2D25CB-9793-4D64-B069-70B674BE812D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=AgentBridge
AppPublisherURL=https://github.com/thanhdiner/Agent-Bridge
AppSupportURL=https://github.com/thanhdiner/Agent-Bridge
AppUpdatesURL=https://github.com/thanhdiner/Agent-Bridge
DefaultDirName={localappdata}\AgentBridge\App
DefaultGroupName=AgentBridge
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputRoot}
OutputBaseFilename=AgentBridgeSetup-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=AgentBridge.Desktop.exe,LocalMcp.Gateway.exe,LocalMcp.Agent.Windows.exe
RestartApplications=no
SetupLogging=yes
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\agentbridge.ico

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#IconFile}"; DestDir: "{app}"; DestName: "agentbridge.ico"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{group}\AgentBridge"; Filename: "{app}\AgentBridge.Desktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\agentbridge.ico"
Name: "{autodesktop}\AgentBridge"; Filename: "{app}\AgentBridge.Desktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\agentbridge.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AgentBridge Desktop"; ValueData: """{app}\AgentBridge.Desktop.exe"" --hidden"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\AgentBridge.Desktop.exe"; WorkingDir: "{app}"; Description: "Launch AgentBridge"; Flags: nowait postinstall skipifsilent
