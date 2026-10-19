# BAP Companion

[English](README.md) | 简体中文

BAP 官方开源 Windows Companion，用于发现 Workshop 组件、校验 Manifest、
提供本地资源服务、持久化配置，并与兼容的 BAP Host 协作。

Companion 是可选服务，不取代 Wallpaper Engine 作为渲染宿主。

## MVP 技术骨架

当前实现使用 .NET 10 和 ASP.NET Core Minimal API。桌面入口托管服务时，
服务绑定回环地址，并提供：

- `POST /v1/session`：创建随机 Bearer 会话令牌；
- 需要认证的组件发现和 Manifest 校验端点；
- 需要认证的全局配置和壁纸配置 JSON 读写端点；
- 需要认证的 `/v1/ws`：执行版本化 Host hello/Companion hello 握手；
- `IWorkshopComponentSource`：为未来 Steam Workshop 发现功能保留的明确适配边界。

服务不会解析 Steam Workshop 内部文件，当前默认适配器不返回组件。

托盘和 GUI 代码通过 `ITrayController` 与 `ICompanionGui` 保持在协议核心
之外，未来可以在不改变服务契约的情况下增加 Windows 桌面外壳。

使用 `dotnet run --project src/Companion.Server` 启动服务。未来的托盘宿主
应将其配置为只监听 `127.0.0.1`。
