#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif

#define MyAppName "SimplePrint"
#define MyAppPublisher "tojollinor"
#define MyAppURL "https://github.com/tojollinor/SimplePrint"
#define RootDir ".."

[Setup]
AppId={{C8CE5BB7-806E-4D24-9B33-D2A98C3864DE}
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
OutputBaseFilename=SimplePrint-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RootDir}\assets\app-{#MyAppVersion}.ico
UninstallDisplayIcon={app}\Unified\assets\app.ico
UninstallDisplayName=SimplePrint
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#RootDir}\dist\Unified\Service\*"; DestDir: "{app}\Unified\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\dist\Unified\Gui\*"; DestDir: "{app}\Unified\Gui"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RootDir}\installer\Install-Unified.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "{#RootDir}\installer\Uninstall-Unified.ps1"; DestDir: "{app}\Unified\Tools"; Flags: ignoreversion
Source: "{#RootDir}\assets\logo.png"; DestDir: "{app}\Unified\assets"; DestName: "logo-default.png"; Flags: ignoreversion
Source: "{#RootDir}\assets\app-{#MyAppVersion}.ico"; DestDir: "{app}\Unified\assets"; DestName: "app.ico"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SimplePrintGui"; ValueData: """{app}\Unified\Gui\SimplePrint.exe"" --tray"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\SimplePrint"; Filename: "{app}\Unified\Gui\SimplePrint.exe"; IconFilename: "{app}\Unified\assets\app.ico"
Name: "{group}\SimplePrint deinstallieren"; Filename: "{uninstallexe}"; IconFilename: "{app}\Unified\assets\app.ico"
Name: "{commondesktop}\SimplePrint"; Filename: "{app}\Unified\Gui\SimplePrint.exe"; IconFilename: "{app}\Unified\assets\app.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"; Flags: unchecked

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-Unified.ps1"" -AppPath ""{app}"""; Flags: runhidden waituntilterminated
Filename: "{app}\Unified\Gui\SimplePrint.exe"; Description: "SimplePrint öffnen"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Unified\Tools\Uninstall-Unified.ps1"" -AppPath ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: CleanupSimplePrint

[Code]
procedure StopService(Name: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    StopService('SimplePrint');
    StopService('SimplePrintServer');
    StopService('SimplePrintClient');
  end;
end;
