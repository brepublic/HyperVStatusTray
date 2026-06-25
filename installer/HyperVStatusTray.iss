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

[Languages]
Name: "English"; MessagesFile: "compiler:Default.isl"
Name: "SimplifiedChinese"; MessagesFile: "compiler:Default.isl,{#SourceRoot}\installer\Languages\ChineseSimplified.isl"
Name: "TraditionalChinese"; MessagesFile: "compiler:Default.isl,{#SourceRoot}\installer\Languages\ChineseTraditional.isl"

[CustomMessages]
English.ConfiguringService=Configuring HyperVStatusTray service...
English.LaunchApp=Launch HyperVStatusTray
English.UninstallerExitedWithCode=The HyperVStatusTray uninstaller exited with code
English.AlreadyInstalled=HyperVStatusTray is already installed.
English.InstalledVersion=Installed version:
English.InstallerVersion=Installer version:
English.ActionUpgrade=upgrade
English.ActionReinstall=reinstall
English.ChooseActionPrefix=Choose Yes to
English.ChooseActionSuffix=, No to uninstall, or Cancel to exit setup.
English.NoUninstallCommand=HyperVStatusTray appears to be installed, but no uninstall command was found.
English.FailedStartUninstaller=Failed to start the HyperVStatusTray uninstaller.
SimplifiedChinese.ConfiguringService=正在配置 HyperVStatusTray 服务...
SimplifiedChinese.LaunchApp=启动 HyperVStatusTray
SimplifiedChinese.UninstallerExitedWithCode=HyperVStatusTray 卸载程序退出代码：
SimplifiedChinese.AlreadyInstalled=HyperVStatusTray 已安装。
SimplifiedChinese.InstalledVersion=已安装版本：
SimplifiedChinese.InstallerVersion=安装器版本：
SimplifiedChinese.ActionUpgrade=升级
SimplifiedChinese.ActionReinstall=重新安装
SimplifiedChinese.ChooseActionPrefix=选择“是”以
SimplifiedChinese.ChooseActionSuffix=，选择“否”以卸载，或选择“取消”退出安装程序。
SimplifiedChinese.NoUninstallCommand=HyperVStatusTray 似乎已安装，但未找到卸载命令。
SimplifiedChinese.FailedStartUninstaller=无法启动 HyperVStatusTray 卸载程序。
TraditionalChinese.ConfiguringService=正在設定 HyperVStatusTray 服務...
TraditionalChinese.LaunchApp=啟動 HyperVStatusTray
TraditionalChinese.UninstallerExitedWithCode=HyperVStatusTray 解除安裝程式結束代碼：
TraditionalChinese.AlreadyInstalled=HyperVStatusTray 已安裝。
TraditionalChinese.InstalledVersion=已安裝版本：
TraditionalChinese.InstallerVersion=安裝程式版本：
TraditionalChinese.ActionUpgrade=升級
TraditionalChinese.ActionReinstall=重新安裝
TraditionalChinese.ChooseActionPrefix=選擇「是」以
TraditionalChinese.ChooseActionSuffix=，選擇「否」以解除安裝，或選擇「取消」結束安裝程式。
TraditionalChinese.NoUninstallCommand=HyperVStatusTray 似乎已安裝，但未找到解除安裝命令。
TraditionalChinese.FailedStartUninstaller=無法啟動 HyperVStatusTray 解除安裝程式。

[Files]
Source: "{#SourceRoot}\publish\{#Runtime}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\configure-vms.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\PowerShellTui.psm1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\README-zhCN.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -UseInstalledFiles -DoNotStart -Language ""{language}"""; StatusMsg: "{cm:ConfiguringService}"; Flags: waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -SkipProgramFiles"; Flags: waituntilterminated; RunOnceId: "HyperVStatusTrayUninstallScript"

[Code]
const
  AppUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B682197-2F7E-4521-8AA3-04DCE89342A0}_is1';

function Cm(MessageName: String): String;
begin
  Result := ExpandConstant('{cm:' + MessageName + '}');
end;

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
      Cm('UninstallerExitedWithCode') + ' ' + IntToStr(ResultCode) + '.',
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
    ActionName := Cm('ActionUpgrade');
  end
    else begin
      ActionName := Cm('ActionReinstall');
    end;

  Prompt :=
    Cm('AlreadyInstalled') + #13#10 + #13#10 +
    Cm('InstalledVersion') + ' ' + InstalledVersion + #13#10 +
    Cm('InstallerVersion') + ' ' + InstallerVersion + #13#10 + #13#10 +
    Cm('ChooseActionPrefix') + ' ' + ActionName + Cm('ChooseActionSuffix');

  Response := MsgBox(Prompt, mbConfirmation, MB_YESNOCANCEL);
  if Response = IDYES then begin
    Result := True;
  end
    else if Response = IDNO then begin
      if UninstallCommand = '' then begin
        MsgBox(
          Cm('NoUninstallCommand'),
          mbError,
          MB_OK);
      end
        else if not RunInstalledUninstaller(UninstallCommand) then begin
          MsgBox(
            Cm('FailedStartUninstaller'),
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
