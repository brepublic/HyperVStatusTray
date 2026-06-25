# HyperVStatusTray

[中文版 Chinese Version](README-zhCN.md)

A Windows 11 system tray status indicator for Hyper-V virtual machines.

- Monitor one or two Hyper-V virtual machines
- One VM: the tray icon shows one dot
- Two VMs: the first VM is shown on top, the second on the bottom
- Gray: Off / Saved
- Yellow: starting, stopping, paused, resuming, or the guest is not ready yet
- Green: Heartbeat is healthy, or the configured ICMP ping succeeds
- Red: startup timeout, startup fell back to Off, critical Hyper-V fault, or VM missing
- Blue with slash: broker service unavailable or monitoring state unknown
- View and configure each VM's Hyper-V automatic startup policy from the tray menu
- Switch the UI language between English, Simplified Chinese, and Traditional Chinese

## Architecture

This project uses a service/broker architecture:

- `HyperVStatusTray.exe`: unelevated tray UI for the icon, menu, state machine, and `vmconnect.exe`.
- `HyperVStatusTrayBroker.exe`: Windows Service that accesses Hyper-V WMI and performs allow-listed power operations.
- The two processes communicate through the fixed named pipe `HyperVStatusTrayBroker` using length-prefixed JSON messages.

The tray process no longer talks to Hyper-V directly, so daily users do not need to be members of `Hyper-V Administrators`.

## Security Model

Install directory:

```text
C:\Program Files\HyperVStatusTray
```

Machine-level configuration:

```text
C:\ProgramData\HyperVStatusTray\config.json
```

The broker service uses this virtual service account:

```text
NT SERVICE\HyperVStatusTrayBroker
```

The installer adds that service account to the local `Hyper-V Administrators` group. The account has full Hyper-V management permissions, but the broker only exposes these fixed operations:

- Query the status of one or two configured VMs
- Start
- Guest shutdown
- Guest restart
- Force power off
- Force reset
- Reload configuration
- Read and configure the monitored VMs' automatic startup policy and `AutomaticStartDelay`

The broker does not accept arbitrary PowerShell, arbitrary WMI queries, arbitrary VM names, or disk/network/security setting changes. It also validates the pipe client process path and only accepts `HyperVStatusTray.exe` from the installation directory.

## Build and Install

Requires Windows 11, Hyper-V, and the .NET 10 SDK.

Check the environment:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\check-environment.ps1
```

Build and install:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

`install.ps1` requires administrator permission. If it is not already elevated, it prompts for UAC elevation. By default it publishes a self-contained `win-x64` single-file build, installs the tray app and broker service, and writes an HKCU sign-in startup entry for the current user. During installation, `configure-vms.ps1` queries the local Hyper-V VMs and lets you select one or two VMs to monitor.

Build only:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1 -SelfContained
```

Uninstall the app, service, and startup entry while keeping machine configuration, broker logs, and current-user tray logs:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\uninstall.ps1
```

Remove all traces:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\uninstall.ps1 -PurgeData
```

`-PurgeData` also removes:

- `C:\ProgramData\HyperVStatusTray`
- `%LOCALAPPDATA%\HyperVStatusTray`
- `%LOCALAPPDATA%\Programs\HyperVStatusTray` from older versions
- `HKCU\...\Run\HyperVStatusTray` entries for the current and loaded user profiles
- `NT SERVICE\HyperVStatusTrayBroker` membership in the Hyper-V Administrators group

## Configuration

First install runs:

```powershell
.\configure-vms.ps1
```

The script queries local Hyper-V VMs, lets you choose one or two, and writes:

```text
C:\ProgramData\HyperVStatusTray\config.json
```

Example:

```json
{
  "Language": "English",
  "PollIntervalSeconds": 5,
  "StartupTimeoutSeconds": 180,
  "SignalLossGraceSeconds": 20,
  "MonitorFailureThreshold": 2,
  "VirtualMachines": [
    {
      "Name": "Dev-Linux",
      "Label": "Dev-Linux",
      "UseHeartbeat": true,
      "PingAddress": null,
      "PingTimeoutMilliseconds": 800
    },
    {
      "Name": "Win11-Test",
      "Label": "Win11-Test",
      "UseHeartbeat": true,
      "PingAddress": null,
      "PingTimeoutMilliseconds": 800
    }
  ]
}
```

`Language` can be `English`, `SimplifiedChinese`, or `TraditionalChinese`. It is saved in the same configuration file and can be changed from the tray menu: `Language -> English / 简体中文 / 繁體中文`.

`Name` must exactly match the virtual machine name in Hyper-V Manager. The list must contain one or two entries. With one entry, the tray icon shows one dot. With two entries, the first VM maps to the top dot and the second maps to the bottom dot.

Changing configuration requires administrator permission. Use the tray menu item `Edit configuration as administrator`, save the file, then choose `Reload configuration`.

## Status Logic

The broker reads:

- `Msvm_ComputerSystem`: power state, HealthState, OperationalStatus, uptime
- `Msvm_HeartbeatComponent`: guest Heartbeat
- `Msvm_ShutdownComponent`: guest shutdown and guest restart

The tray keeps a local state machine:

- Running with Heartbeat OK/Degraded: green
- Heartbeat unavailable but configured `PingAddress` succeeds: green
- No Heartbeat/Ping after `StartupTimeoutSeconds`: red and latched
- Readiness signal briefly lost after green: yellow first, then red after `SignalLossGraceSeconds`
- Broker service unavailable: blue unknown

## Tray Menu

Each VM provides:

- Start
- Connect console
- Guest shutdown
- Guest restart
- Force power off
- Force reset
- Clear latched fault
- Current automatic startup policy: `Do not start automatically`, `Start automatically if it was running before`, or `Always start automatically`
- Configure VM automatic startup policy: modifies Hyper-V `AutomaticStartupAction` through the broker; automatic startup policies can also set `AutomaticStartDelay`

Force power off and force reset ask for confirmation. Console connection is launched directly by the tray process with `vmconnect.exe localhost <VM name>`; the service does not open UI from Session 0.

## Logs

Tray log:

```text
%LOCALAPPDATA%\HyperVStatusTray\HyperVStatusTray.log
```

Broker service log:

```text
C:\ProgramData\HyperVStatusTray\HyperVStatusTrayBroker.log
```

Logs rotate to `.previous.log` after 2 MiB.

The original Simplified Chinese README is available as `README-zhCN.md`.
