# HyperVStatusTray

Windows 11 上的 Hyper-V 双虚拟机托盘状态指示器。

- 上圆点：`Fedora-OpenClaw`
- 下圆点：`Win11-Sandbox`
- 灰色：Off / Saved
- 黄色：启动、停止、暂停、恢复中，或客户机暂未就绪
- 绿色：Heartbeat 正常，或配置的 ICMP Ping 成功
- 红色：启动超时、启动后掉回 Off、Hyper-V 关键故障、虚拟机不存在
- 蓝色带斜线：broker 服务不可用或监控未知

## 架构

本项目使用 Service/Broker 架构：

- `HyperVStatusTray.exe`：普通权限托盘 UI，只负责图标、菜单、状态机和打开 `vmconnect.exe`。
- `HyperVStatusTrayBroker.exe`：Windows Service，负责访问 Hyper-V WMI 并执行白名单电源操作。
- 两者通过固定 Named Pipe `HyperVStatusTrayBroker` 通信，消息是带长度前缀的 JSON。

托盘进程不再直接访问 Hyper-V，因此日常用户不需要加入 `Hyper-V Administrators`。

## 安全模型

安装目录：

```text
C:\Program Files\HyperVStatusTray
```

机器级配置：

```text
C:\ProgramData\HyperVStatusTray\config.json
```

broker 服务使用虚拟服务账户：

```text
NT SERVICE\HyperVStatusTrayBroker
```

安装脚本会把该服务账户加入本机 `Hyper-V Administrators` 组。该账户拥有完整 Hyper-V 管理能力，但 broker 只暴露以下固定操作：

- 查询两台配置内虚拟机状态
- 启动
- 正常关机
- 正常重启
- 强制关闭电源
- 强制重置
- 重新加载配置

broker 不接受任意 PowerShell、任意 WMI 查询、任意 VM 名称、硬盘/网络/安全设置修改。服务端还会校验 Pipe 客户端进程路径，只接受安装目录中的 `HyperVStatusTray.exe`。

## 构建和安装

需要 Windows 11、Hyper-V 和 .NET 10 SDK。

检查环境：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\check-environment.ps1
```

构建并安装：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

`install.ps1` 需要管理员权限；如果不是管理员运行，会请求 UAC 提升。默认发布自包含 `win-x64` 单文件版本，安装托盘程序和 broker 服务，并为当前用户写入 HKCU 登录启动项。

只构建、不安装：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1 -SelfContained
```

卸载程序、服务和开机启动项，但保留机器级配置、broker 日志和当前用户托盘日志：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\uninstall.ps1
```

彻底清理程序痕迹：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\uninstall.ps1 -PurgeData
```

`-PurgeData` 会额外删除：

- `C:\ProgramData\HyperVStatusTray`
- `%LOCALAPPDATA%\HyperVStatusTray`
- 旧版本可能留下的 `%LOCALAPPDATA%\Programs\HyperVStatusTray`
- 当前用户和已加载用户配置中的 `HKCU\...\Run\HyperVStatusTray`
- `NT SERVICE\HyperVStatusTrayBroker` 在 Hyper-V Administrators 组中的成员关系

## 配置

首次安装会创建：

```text
C:\ProgramData\HyperVStatusTray\config.json
```

默认内容：

```json
{
  "PollIntervalSeconds": 5,
  "StartupTimeoutSeconds": 180,
  "SignalLossGraceSeconds": 20,
  "MonitorFailureThreshold": 2,
  "VirtualMachines": [
    {
      "Name": "Fedora-OpenClaw",
      "Label": "Fedora-OpenClaw",
      "UseHeartbeat": true,
      "PingAddress": null,
      "PingTimeoutMilliseconds": 800
    },
    {
      "Name": "Win11-Sandbox",
      "Label": "Win11-Sandbox",
      "UseHeartbeat": true,
      "PingAddress": null,
      "PingTimeoutMilliseconds": 800
    }
  ]
}
```

`Name` 必须与 Hyper-V 管理器中的虚拟机名称完全一致。列表必须保持两项，第一项对应上圆点，第二项对应下圆点。

修改配置需要管理员权限。托盘菜单中的“以管理员身份编辑配置文件”会打开该配置；保存后选择“重新加载配置”。

## 状态判断

broker 读取：

- `Msvm_ComputerSystem`：电源状态、HealthState、OperationalStatus、运行时间
- `Msvm_HeartbeatComponent`：客户机 Heartbeat
- `Msvm_ShutdownComponent`：正常关机和正常重启

托盘侧保留状态机：

- Running 且 Heartbeat OK/Degraded：绿色
- Heartbeat 不可用但配置了 `PingAddress` 且 Ping 成功：绿色
- 启动超过 `StartupTimeoutSeconds` 仍无 Heartbeat/Ping：红色并锁存
- 已经绿色后短暂丢失信号：先黄，超过 `SignalLossGraceSeconds` 后红
- broker 服务不可用：蓝色未知

## 托盘菜单

每台虚拟机提供：

- 启动
- 连接控制台
- 正常关机
- 正常重启
- 强制关闭电源
- 强制重置
- 清除故障锁存

强制关闭和强制重置会二次确认。连接控制台由托盘进程直接启动 `vmconnect.exe localhost <VM 名称>`，服务不会在 Session 0 打开 UI。

## 日志

托盘日志：

```text
%LOCALAPPDATA%\HyperVStatusTray\HyperVStatusTray.log
```

broker 服务日志：

```text
C:\ProgramData\HyperVStatusTray\HyperVStatusTrayBroker.log
```

日志超过 2 MiB 时会轮换为 `.previous.log`。
