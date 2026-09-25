#define MyAppName "SimplePrint Server"
#define MyAppVersion "0.2.6"
#define MyAppPublisher "tojollinor"
#define MyAppURL "https://github.com/tojollinor/SimplePrint"
#define RootDir ".."

[Setup]
AppId={{A7F92C46-6234-4B78-A0AB-8F83E317C221}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\SimplePrint
DefaultGroupName=SimplePrint
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#RootDir}\dist\Installer
OutputBaseFilename=SimplePrint-Server-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RootDir}\assets\app-0.2.6.ico
UninstallDisplayIcon={app}\Server\assets\app-0.2.6.ico
UninstallDisplayName=SimplePrint Server
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#RootDir}\dist\Server\Service\*"; DestDir: "{app}\Server\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\dist\Server\Gui\*"; DestDir: "{app}\Server\Gui"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\installer\Install-Component.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "{#RootDir}\assets\logo.png"; DestDir: "{app}\Server\assets"; DestName: "logo.png"; Flags: ignoreversion onlyifdoesntexist
Source: "{#RootDir}\assets\app-0.2.6.ico"; DestDir: "{app}\Server\assets"; DestName: "app-0.2.6.ico"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintServerGui"; ValueData: """{app}\Server\Gui\SimplePrint.Server.Gui.exe"" --tray"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\Server\assets\app-0.2.6.ico"
Name: "{group}\SimplePrint Server deinstallieren"; Filename: "{uninstallexe}"; IconFilename: "{app}\Server\assets\app-0.2.6.ico"
Name: "{commondesktop}\SimplePrint Server"; Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; IconFilename: "{app}\Server\assets\app-0.2.6.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-Component.ps1"" -Mode Server -AppPath ""{app}"""; Flags: runhidden waituntilterminated
Filename: "{app}\Server\Gui\SimplePrint.Server.Gui.exe"; Description: "SimplePrint Server öffnen"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SimplePrintServer"; Flags: runhidden waituntilterminated; RunOnceId: StopServer
Filename: "{sys}\sc.exe"; Parameters: "delete SimplePrintServer"; Flags: runhidden waituntilterminated; RunOnceId: DeleteServer
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
    StopService('SimplePrintServer');
end;