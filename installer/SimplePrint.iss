#define MyAppName "SimplePrint"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "SimplePrint"
#define RootDir ".."

[Setup]
AppId={{71D8D5B8-5D4A-4898-9454-B5F6D95B9AE1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
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
SetupIconFile={#RootDir}\assets\app-0.2.1.ico
UninstallDisplayIcon={app}\assets\app-0.2.1.ico
UninstallDisplayName=SimplePrint
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

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
Source: "{#RootDir}\assets\app-0.2.1.ico"; DestDir: "{app}\assets"; DestName: "app-0.2.1.ico"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintServerGui"; ValueData: """{app}\Server\Gui\SimplePrint.Server.Gui.exe"" --tray"; Flags: uninsdeletevalue; Components: server
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintClientGui"; ValueData: """{app}\Client\Gui\SimplePrint.Client.Gui.exe"" --tray"; Flags: uninsdeletevalue; Components: client

[Icons]
Name: "{group}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\assets\app-0.2.1.ico"; Components: server
Name: "{group}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\assets\app-0.2.1.ico"; Components: client
Name: "{group}\SimplePrint deinstallieren"; Filename: "{uninstallexe}"; IconFilename: "{app}\assets\app-0.2.1.ico"
Name: "{commondesktop}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\assets\app-0.2.1.ico"; Components: server; Tasks: desktopicon
Name: "{commondesktop}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\assets\app-0.2.1.ico"; Components: client; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

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
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Printer | Where-Object { $_.PortName -Like 'SimplePrint_*' -or $_.Comment -Like 'SimplePrint:*' -or $_.Name -Like '* (SimplePrint)' } | Remove-Printer -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Get-PrinterPort | Where-Object Name -Like 'SimplePrint_*' | Remove-PrinterPort -ErrorAction SilentlyContinue"""; Flags: runhidden waituntilterminated; RunOnceId: RemoveClientPrinters
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
