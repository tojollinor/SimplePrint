#define MyAppName "SimplePrint Online-Installer"
#define MyAppPublisher "tojollinor"
#define MyAppURL "https://github.com/tojollinor/SimplePrint"
#define RootDir ".."

[Setup]
AppId={{3DB9A4BA-16B7-43CC-BF0D-ADDD339E29F0}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
OutputDir={#RootDir}\dist\Installer
OutputBaseFilename=SimplePrint-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#RootDir}\assets\app.ico
DisableProgramGroupPage=yes
DisableReadyMemo=yes

[Files]
Source: "{#RootDir}\installer\Get-Latest-Installer.ps1"; Flags: dontcopy

[Run]
Filename: "{tmp}\SimplePrint-Latest-Offline.exe"; Parameters: "/SP-"; Description: "Aktuelles SimplePrint installieren"; Flags: shellexec waituntilterminated

[Code]
var
  DownloadPage: TDownloadWizardPage;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResolverPath: String;
  UrlPath: String;
  DownloadUrl: AnsiString;
  ResultCode: Integer;
begin
  Result := '';
  ResolverPath := ExpandConstant('{tmp}\Get-Latest-Installer.ps1');
  UrlPath := ExpandConstant('{tmp}\SimplePrint-latest-url.txt');

  ExtractTemporaryFile('Get-Latest-Installer.ps1');
  DeleteFile(UrlPath);

  WizardForm.StatusLabel.Caption := 'Aktuelles SimplePrint-Release wird ermittelt ...';

  if not Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + ResolverPath +
      '" -OutputPath "' + UrlPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    Result := 'Die GitHub-Abfrage konnte nicht gestartet werden.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := 'Das aktuelle SimplePrint-Release konnte nicht von GitHub ermittelt werden.';
    exit;
  end;

  if not LoadStringFromFile(UrlPath, DownloadUrl) then
  begin
    Result := 'Die Downloadadresse des aktuellen SimplePrint-Installers konnte nicht gelesen werden.';
    exit;
  end;

  DownloadUrl := Trim(DownloadUrl);
  if DownloadUrl = '' then
  begin
    Result := 'GitHub hat keine Downloadadresse für den aktuellen SimplePrint-Installer geliefert.';
    exit;
  end;

  DownloadPage := CreateDownloadPage(
    'SimplePrint wird heruntergeladen',
    'Der Online-Installer lädt automatisch die aktuellste veröffentlichte Version von GitHub.',
    nil);

  DownloadPage.Clear;
  DownloadPage.Add(
    String(DownloadUrl),
    'SimplePrint-Latest-Offline.exe',
    '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      Result := 'Der aktuelle SimplePrint-Installer konnte nicht heruntergeladen werden: ' +
        GetExceptionMessage;
    end;
  finally
    DownloadPage.Hide;
  end;
end;
