# BAP Companion

[English](README.md) | 简体中文

BAP 官方开源 Windows Companion，用于发现 Workshop 组件、校验 Manifest、提供本地资源服务、持久化配置，并与兼容的 BAP Host 协作。

Companion 是可选服务，不取代 Wallpaper Engine 作为渲染宿主。

> **实验性 / 受限预览：授权来源尚未验证。** Host 身份和安装来源由请求方自报，
> Companion 不会独立认证。每个新的 Host 会话（包括断线重连）都需要人工批准；
> 已保存的 Host 记录不会跳过审批。只有在你能独立确认 Wallpaper Engine 当前壁纸时
> 才应批准。本预览版不是可不受限制地服务任意第三方壁纸的已验证授权产品。官方
> Host 自动批准目前关闭。

## 桌面应用与本地服务

当前实现使用 .NET 10 和 ASP.NET Core Minimal API。桌面入口托管服务时，服务绑定回环地址，并提供：

- `POST /v1/session`：创建随机 Bearer 会话令牌；
- 需要认证的组件发现和 Manifest 校验端点；
- 需要认证的全局配置和壁纸配置 JSON 读写端点；
- 需要认证的 `/v1/ws`：执行版本化 Host hello/Companion hello 握手；
- `GET /v1/hosts/{hostId}/components`：返回按顺序解析后的有效预设；
- `/v1/catalog/components/{key}/resources/{path}`：按明确来源读取组件资源。

桌面应用同时列出 WE 订阅目录和本地项目目录中的 Web 宿主、BAP 组件。预设保存
组件顺序与明确的来源；指定宿主的专用预设覆盖全局预设。暂时缺失的组件仍保留在预设中。
未激活任何预设时默认展示已发现组件；同 ID 同时有本地与订阅版时优先订阅版。

桌面窗口可编辑预设，以预览卡片展示已安装的宿主与组件；点击卡片可查看来源、BAP ID、版本、状态和项目位置，并可从宿主详情通过 WE 官方命令行发起切换。手动启动会打开主窗口。
设置页提供英文与简体中文，默认英文；首次运行默认启用当前用户级开机自启，可随时取消勾选。自动启动时进入托盘。服务不弹控制台窗口，滚动日志位于
`%LOCALAPPDATA%\BAP Companion\logs`，可从设置页打开。

单独启动服务时使用 `dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311`。
即使将监听地址配置得更宽，服务也只接受来自回环地址、且 Host 名称为回环地址的请求。

## 端到端冒烟测试

先为服务配置本地授权控制管道并启动服务，再在第二个 PowerShell 窗口运行验收脚本。脚本会先确认未批准的会话令牌无法发现组件，再通过当前用户的管道显式批准测试 Host，随后继续进行 HTTP 与 WebSocket 检查。

```powershell
$env:BAP_AUTHORIZATION_PIPE = 'Bap.Companion.E2E'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

在第二个 PowerShell 窗口运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\companion-e2e.ps1 -BaseUrl http://127.0.0.1:4311
```

检查覆盖健康状态、Bearer 会话、未批准时拒绝组件发现、显式审批后的组件发现、壁纸配置持久化，以及带权限协商的 Host/Companion hello 握手。脚本还会记住首次批准，确认同一会话令牌断开后重连仍需重新批准，同一 Host ID 的新会话也仍需显式批准；随后撤销该 Host，并验证活动 WebSocket 被关闭且会话令牌均失去访问权限。脚本也会验证只授予组件发现权限时不能读取配置，并确认不兼容的协议版本会在授权前被拒绝。
组件和配置的 HTTP 接口既需要有效的 Bearer Token，也需要当前 WebSocket `hello`
会话授予相应权限。单独创建会话不会授予访问权；WebSocket 关闭后权限清空。

## 已安装内容发现

默认情况下，Companion 会通过 Windows 注册表定位 Steam（并回退检查常见安装目录），读取 `libraryfolders.vdf`，自动发现 AppID `431960` 下已安装的 Wallpaper Engine Workshop 组件。它会递归查找 `manifest.json` 或 `component.manifest.json`，只公开通过 BAP 校验器的 Manifest。正常安装 Steam 时无需配置路径。

目录清单还扫描各 Steam 库中的 `wallpaper_engine/projects/myprojects` 本地 WE 项目。
`BAP_LOCAL_ROOTS` 可添加其他开发包目录。本地宿主与订阅组件、订阅宿主与本地组件
都可混用；预设里的明确来源选择用于区分同 ID 的副本。

本地开发时，可用 `BAP_WORKSHOP_ROOTS` 指定一个或多个组件目录，多个目录使用当前平台的路径分隔符连接。显式目录会取代自动发现，除非同时设置 `BAP_DISCOVER_STEAM_WORKSHOP=1`：

```powershell
$env:BAP_WORKSHOP_ROOTS = 'D:\path\to\workshop-components'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

创建会话后，经过认证的客户端可以通过 `GET /v1/components/{id}/resources/{path}` 读取组件资源。资源路径被限制在组件目录内，越界访问会被拒绝。

设置 `BAP_DISCOVER_STEAM_WORKSHOP=0` 可关闭自动发现；设置为 `1` 可将自动发现与显式目录合并。若无法自动找到 Steam，可用 `BAP_STEAM_ROOT` 指定其安装目录。

## 便携式 Windows 软件包

创建一个依赖目标机 .NET 运行时的 `win-x64` 软件包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-companion.ps1
```

默认打包采用 Fluent 风格的 WPF 前台；如需打包原有 WinForms 前台，可添加 `-LegacyUi`。

如需自包含软件包，添加 `-SelfContained` 参数。输出目录中包含 `start-companion.cmd`，双击即可启动桌面应用和服务，默认地址为 `http://127.0.0.1:4311`。关闭窗口会保留托盘进程；通过托盘菜单“Exit”才会退出。如需更换端口，可将端口号作为脚本的第一个参数传入。

## 当前用户安装

在已发布的软件包目录中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-companion.ps1
```

安装位置为 `%LOCALAPPDATA%\Programs\BAP Companion`，并在当前用户的“开始”菜单添加 `BAP Companion` 快捷方式。无需管理员权限；首次运行默认启用开机自启，可在设置页取消。更新前请通过托盘菜单退出已安装的 Companion；安装程序会保留其配置数据。
