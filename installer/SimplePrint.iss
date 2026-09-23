#define MyAppName "SimplePrint"
#define MyAppVersion "0.1.2"
#define MyAppPublisher "SimplePrint"
#define RootDir ".."

[Setup]
AppId={{71D8D5B8-5D4A-4898-9454-B5F6D95B9AE1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SimplePrint
DefaultGroupName=SimplePrint
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#RootDir}\dist\Installer
OutputBaseFilename=SimplePrint-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RootDir}\assets\app.ico
UninstallDisplayIcon={app}\assets\app.ico

[Components]
Name: "server"; Description: "PrintServer (Dienst + GUI)"; Types: full custom
Name: "client"; Description: "PrintClient (Agent + GUI)"; Types: full custom

[Files]
Source: "{#RootDir}\dist\Server\Service\*"; DestDir: "{app}\Server\Service"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server
Source: "{#RootDir}\dist\Server\Gui\*"; DestDir: "{app}\Server\Gui"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server
Source: "{#RootDir}\dist\Client\Service\*"; DestDir: "{app}\Client\Service"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client
Source: "{#RootDir}\dist\Client\Gui\*"; DestDir: "{app}\Client\Gui"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client
Source: "{#RootDir}\installer\Install-Component.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "{#RootDir}\assets\logo.png"; DestDir: "{app}\assets"; DestName: "logo.png"; Flags: ignoreversion onlyifdoesntexist
Source: "{#RootDir}\assets\app.ico"; DestDir: "{app}\assets"; DestName: "app.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\assets\app.ico"; Components: server
Name: "{group}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\assets\app.ico"; Components: client
Name: "{commondesktop}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\assets\app.ico"; Components: server; Tasks: desktopicon
Name: "{commondesktop}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\assets\app.ico"; Components: client; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintServerGui"; ValueData: """{app}\Server\Gui\SimplePrint.Server.Gui.exe"" --tray"; Components: server; Flags: uninsdeletevalue
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintClientGui"; ValueData: """{app}\Client\Gui\SimplePrint.Client.Gui.exe"" --tray"; Components: client; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-Component.ps1"" -Mode Server -AppPath ""{app}"""; Flags: runhidden waituntilterminated; Components: server
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-Component.ps1"" -Mode Client -AppPath ""{app}"""; Flags: runhidden waituntilterminated; Components: client
Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; Description: "SimplePrint Server öffnen"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: server
Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; Description: "SimplePrint Client öffnen"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: client

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SimplePrintServer"; Flags: runhidden waituntilterminated; RunOnceId: StopServer
Filename: "{sys}\sc.exe"; Parameters: "delete SimplePrintServer"; Flags: runhidden waituntilterminated; RunOnceId: DeleteServer
Filename: "{sys}\sc.exe"; Parameters: "stop SimplePrintClient"; Flags: runhidden waituntilterminated; RunOnceId: StopClient
Filename: "{sys}\sc.exe"; Parameters: "delete SimplePrintClient"; Flags: runhidden waituntilterminated; RunOnceId: DeleteClient
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden waituntilterminated; RunOnceId: RemoveFirewall

[Code]
procedure StopService(Name: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if WizardIsComponentSelected('server') then StopService('SimplePrintServer');
    if WizardIsComponentSelected('client') then StopService('SimplePrintClient');
  end;
end;
