# BAP Companion

[English](README.md) | 简体中文

BAP 官方开源 Windows Companion，用于发现 Workshop 组件、校验 Manifest、提供本地资源服务、持久化配置，并与兼容的 BAP Host 协作。

Companion 是可选服务，不取代 Wallpaper Engine 作为渲染宿主。

## MVP 技术骨架

当前实现使用 .NET 10 和 ASP.NET Core Minimal API。桌面入口托管服务时，服务绑定回环地址，并提供：

- `POST /v1/session`：创建随机 Bearer 会话令牌；
- 需要认证的组件发现和 Manifest 校验端点；
- 需要认证的全局配置和壁纸配置 JSON 读写端点；
- 需要认证的 `/v1/ws`：执行版本化 Host hello/Companion hello 握手；
- `IWorkshopComponentSource`：为未来 Steam Workshop 发现功能保留的明确适配边界。

服务不会解析 Steam Workshop 内部文件，当前默认适配器不返回组件。

托盘和 GUI 代码通过 `ITrayController` 与 `ICompanionGui` 保持在协议核心之外，未来可以在不改变服务契约的情况下增加 Windows 桌面外壳。

使用 `dotnet run --project src/Companion.Server` 启动服务。未来的托盘宿主应将其配置为只监听 `127.0.0.1`。

## 端到端冒烟测试

先在回环端口启动服务，再运行可重复的 HTTP 和 WebSocket 验收检查：

```powershell
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\companion-e2e.ps1 -BaseUrl http://127.0.0.1:4311
```

检查覆盖健康状态、Bearer 会话、认证后的组件发现、壁纸配置持久化，以及带权限协商的 Host/Companion hello 握手。

## Workshop 组件发现

将 `BAP_WORKSHOP_ROOTS` 设置为一个或多个本地 Workshop 组件目录，多个目录使用当前平台的路径分隔符连接。Companion 会递归查找 `manifest.json` 或 `component.manifest.json`，只公开通过 BAP 校验器的 Manifest：

```powershell
$env:BAP_WORKSHOP_ROOTS = 'D:\path\to\workshop-components'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

## 便携式 Windows 软件包

创建一个依赖目标机 .NET 运行时的 `win-x64` 软件包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-companion.ps1
```

如需自包含软件包，添加 `-SelfContained` 参数。输出目录中包含 `start-companion.cmd`，双击即可启动托盘宿主和服务，默认地址为 `http://127.0.0.1:4311`。托盘菜单提供健康检查和正常退出功能；如需更换端口，可将端口号作为它的第一个参数传入。
