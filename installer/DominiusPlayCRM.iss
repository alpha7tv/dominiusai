; Instalador Windows do DOMINIUS PLAY AI CRM (Inno Setup 6)
; Compilar no Windows:  ISCC.exe installer\DominiusPlayCRM.iss   (após executar installer\build.ps1)
#define AppName "DOMINIUS PLAY AI CRM"
#define AppVersion "0.3.3"
#define Publisher "Dominius Play"

[Setup]
AppId={{6F1E7C43-9B0D-4C5E-9E0A-2D5B7A1C9D01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\Dominius Play\CRM
DefaultGroupName=Dominius Play
OutputDir=..\artifacts\installer
OutputBaseFilename=DominiusPlayCRM-Setup-{#AppVersion}-x64
SetupIconFile=..\src\DominiusPlay.Desktop\Assets\dominius.ico
UninstallDisplayIcon={app}\DominiusPlayCRM.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "server"; Description: "Instalar o servidor local como Serviço do Windows (recomendado para uso em uma única máquina)"; GroupDescription: "Servidor:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\server\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: server
; appsettings.json do servidor não é sobrescrito em atualizações
Source: "..\artifacts\publish\server\appsettings.json"; DestDir: "{app}\server"; Flags: onlyifdoesntexist uninsneveruninstall; Tasks: server

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\DominiusPlayCRM.exe"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\DominiusPlayCRM.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create DominiusPlayCRM binPath= ""{app}\server\DominiusPlay.Server.exe"" start= auto DisplayName= ""Dominius Play CRM (servidor)"""; Flags: runhidden; Tasks: server
Filename: "{sys}\sc.exe"; Parameters: "start DominiusPlayCRM"; Flags: runhidden; Tasks: server
Filename: "{app}\DominiusPlayCRM.exe"; Description: "Abrir o {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop DominiusPlayCRM"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete DominiusPlayCRM"; Flags: runhidden; RunOnceId: "DelSvc"

; Os dados (banco, chaves de criptografia, logs) ficam em C:\ProgramData\DominiusPlay\CRM
; e são preservados em atualizações e na desinstalação.
