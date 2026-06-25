# Release Packaging

HyperVStatusTray ships as an Inno Setup installer built from already-published
.NET binaries. End-user machines do not need the .NET SDK when you publish the
default self-contained installer.

## Prerequisites

- Windows 11
- .NET 10 SDK
- Inno Setup 6, with `ISCC.exe` available on PATH or installed in the default
  `Program Files` location

## Build A Release

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.0
```

The default release targets `win-x64` and creates:

```text
dist\1.0.0\HyperVStatusTray-1.0.0-win-x64-setup.exe
dist\1.0.0\SHA256SUMS.txt
```

To build for Windows on ARM64:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.0 -Runtime win-arm64
```

To build a smaller framework-dependent installer:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.0.0 -FrameworkDependent
```

Framework-dependent installers require the matching .NET 10 Desktop Runtime on
the target PC.

## Installer Behavior

The installer:

- installs files to `C:\Program Files\HyperVStatusTray`
- runs `install.ps1 -UseInstalledFiles -DoNotStart -Language <selected language>` with administrator rights
- configures the `HyperVStatusTrayBroker` Windows Service
- writes the current-user startup entry
- offers to launch `HyperVStatusTray.exe` after install

The uninstaller:

- runs `uninstall.ps1 -SkipProgramFiles`
- unregisters the service and startup entry
- removes service Hyper-V group membership
- leaves machine config and logs in place, matching the script's default manual
  uninstall behavior
- lets Inno Setup remove the installed program files
