#define AppName "HyperVStatusTray"
#define AppPublisher "HyperVStatusTray"
#define AppExeName "HyperVStatusTray.exe"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

#ifndef Runtime
#define Runtime "win-x64"
#endif

#ifndef SourceRoot
#define SourceRoot ".."
#endif

#ifndef OutputDir
#define OutputDir "..\dist"
#endif

#ifndef SetupBaseName
#define SetupBaseName AppName + "-" + AppVersion + "-" + Runtime + "-setup"
#endif

[Setup]
AppId={{8B682197-2F7E-4521-8AA3-04DCE89342A0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\HyperVStatusTray
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#SetupBaseName}
SetupIconFile={#SourceRoot}\src\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=admin
#if Runtime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#SourceRoot}\publish\{#Runtime}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\configure-vms.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\PowerShellTui.psm1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -UseInstalledFiles -DoNotStart"; StatusMsg: "Configuring HyperVStatusTray service..."; Flags: waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "Launch HyperVStatusTray"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -SkipProgramFiles"; Flags: waituntilterminated

[Code]
const
  AppUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B682197-2F7E-4521-8AA3-04DCE89342A0}_is1';

function TryReadInstalledStringValue(ValueName: String; var Value: String): Boolean;
begin
  Result :=
    RegQueryStringValue(HKLM64, AppUninstallKey, ValueName, Value) or
    RegQueryStringValue(HKLM32, AppUninstallKey, ValueName, Value) or
    RegQueryStringValue(HKCU, AppUninstallKey, ValueName, Value);
end;

function GetNumericVersionPart(Version: String; PartIndex: Integer): Integer;
var
  I: Integer;
  CurrentPart: Integer;
  Segment: String;
  Character: String;
begin
  for I := 1 to Length(Version) do begin
    Character := Copy(Version, I, 1);
    if (Character = '-') or (Character = '+') then begin
      Version := Copy(Version, 1, I - 1);
      break;
    end;
  end;

  CurrentPart := 0;
  Segment := '';
  for I := 1 to Length(Version) + 1 do begin
    if (I > Length(Version)) or (Copy(Version, I, 1) = '.') then begin
      if CurrentPart = PartIndex then begin
        Result := StrToIntDef(Segment, 0);
        exit;
      end;

      CurrentPart := CurrentPart + 1;
      Segment := '';
    end
      else begin
        Segment := Segment + Copy(Version, I, 1);
      end;
  end;

  Result := 0;
end;

function CompareAppVersions(CurrentVersion: String; InstalledVersion: String): Integer;
var
  I: Integer;
  CurrentPart: Integer;
  InstalledPart: Integer;
begin
  Result := 0;
  for I := 0 to 3 do begin
    CurrentPart := GetNumericVersionPart(CurrentVersion, I);
    InstalledPart := GetNumericVersionPart(InstalledVersion, I);

    if CurrentPart > InstalledPart then begin
      Result := 1;
      exit;
    end;

    if CurrentPart < InstalledPart then begin
      Result := -1;
      exit;
    end;
  end;
end;

function TryGetInstalledApp(var InstalledVersion: String; var UninstallCommand: String): Boolean;
var
  DisplayName: String;
begin
  Result :=
    TryReadInstalledStringValue('DisplayName', DisplayName) or
    TryReadInstalledStringValue('DisplayVersion', InstalledVersion) or
    TryReadInstalledStringValue('UninstallString', UninstallCommand);

  if not TryReadInstalledStringValue('DisplayVersion', InstalledVersion) then begin
    InstalledVersion := 'unknown';
  end;

  if not TryReadInstalledStringValue('UninstallString', UninstallCommand) then begin
    UninstallCommand := '';
  end;
end;

function RunInstalledUninstaller(UninstallCommand: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{cmd}'),
    '/C ' + UninstallCommand,
    '',
    SW_SHOW,
    ewWaitUntilTerminated,
    ResultCode);

  if Result and (ResultCode <> 0) then begin
    MsgBox(
      'The HyperVStatusTray uninstaller exited with code ' + IntToStr(ResultCode) + '.',
      mbError,
      MB_OK);
  end;
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
  UninstallCommand: String;
  InstallerVersion: String;
  ActionName: String;
  Prompt: String;
  Response: Integer;
begin
  Result := True;
  InstallerVersion := '{#AppVersion}';

  if not TryGetInstalledApp(InstalledVersion, UninstallCommand) then begin
    exit;
  end;

  if CompareAppVersions(InstallerVersion, InstalledVersion) > 0 then begin
    ActionName := 'upgrade';
  end
    else begin
      ActionName := 'reinstall';
    end;

  Prompt :=
    'HyperVStatusTray is already installed.' + #13#10 + #13#10 +
    'Installed version: ' + InstalledVersion + #13#10 +
    'Installer version: ' + InstallerVersion + #13#10 + #13#10 +
    'Choose Yes to ' + ActionName + ', No to uninstall, or Cancel to exit setup.';

  Response := MsgBox(Prompt, mbConfirmation, MB_YESNOCANCEL);
  if Response = IDYES then begin
    Result := True;
  end
    else if Response = IDNO then begin
      if UninstallCommand = '' then begin
        MsgBox(
          'HyperVStatusTray appears to be installed, but no uninstall command was found.',
          mbError,
          MB_OK);
      end
        else if not RunInstalledUninstaller(UninstallCommand) then begin
          MsgBox(
            'Failed to start the HyperVStatusTray uninstaller.',
            mbError,
            MB_OK);
        end;

      Result := False;
    end
    else begin
      Result := False;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process HyperVStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force; $service = Get-Service -Name ''HyperVStatusTrayBroker'' -ErrorAction SilentlyContinue; if ($service -and $service.Status -ne ''Stopped'') { sc.exe stop HyperVStatusTrayBroker | Out-Null; Start-Sleep -Seconds 4 }"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Result := '';
end;
