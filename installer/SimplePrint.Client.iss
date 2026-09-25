#define MyAppName "SimplePrint Client"
#define MyAppVersion "0.2.6"
#define MyAppPublisher "tojollinor"
#define MyAppURL "https://github.com/tojollinor/SimplePrint"
#define RootDir ".."

[Setup]
AppId={{5D49BD99-7D65-44D6-BEC5-BCE455F79F52}
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
OutputBaseFilename=SimplePrint-Client-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RootDir}\assets\app-0.2.6.ico
UninstallDisplayIcon={app}\Client\assets\app-0.2.6.ico
UninstallDisplayName=SimplePrint Client
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#RootDir}\dist\Client\Service\*"; DestDir: "{app}\Client\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\dist\Client\Gui\*"; DestDir: "{app}\Client\Gui"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\installer\Install-Component.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "{#RootDir}\assets\logo.png"; DestDir: "{app}\Client\assets"; DestName: "logo.png"; Flags: ignoreversion onlyifdoesntexist
Source: "{#RootDir}\assets\app-0.2.6.ico"; DestDir: "{app}\Client\assets"; DestName: "app-0.2.6.ico"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintClientGui"; ValueData: """{app}\Client\Gui\SimplePrint.Client.Gui.exe"" --tray"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\Client\assets\app-0.2.6.ico"
Name: "{group}\SimplePrint Client deinstallieren"; Filename: "{uninstallexe}"; IconFilename: "{app}\Client\assets\app-0.2.6.ico"
Name: "{commondesktop}\SimplePrint Client"; Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; IconFilename: "{app}\Client\assets\app-0.2.6.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-Component.ps1"" -Mode Client -AppPath ""{app}"""; Flags: runhidden waituntilterminated
Filename: "{app}\Client\Gui\SimplePrint.Client.Gui.exe"; Description: "SimplePrint Client öffnen"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SimplePrintClient"; Flags: runhidden waituntilterminated; RunOnceId: StopClient
Filename: "{sys}\sc.exe"; Parameters: "delete SimplePrintClient"; Flags: runhidden waituntilterminated; RunOnceId: DeleteClient
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -Name 'SimplePrint-ClientDiagnostics' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; Get-NetFirewallRule -DisplayName 'SimplePrint Client Diagnostics' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden waituntilterminated; RunOnceId: RemoveClientDiagnosticsFirewall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Printer | Where-Object {{ $_.PortName -Like 'SimplePrint_*' -or $_.Comment -Like 'SimplePrint:*' -or $_.Name -Like '* (SimplePrint)' } | Remove-Printer -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Get-PrinterPort | Where-Object Name -Like 'SimplePrint_*' | Remove-PrinterPort -ErrorAction SilentlyContinue"""; Flags: runhidden waituntilterminated; RunOnceId: RemoveClientPrinters

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
    StopService('SimplePrintClient');
end;