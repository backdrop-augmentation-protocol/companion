# BAP Companion

[English](README.md) | [简体中文](README.zh-CN.md)

Open-source official Windows Companion for discovering Workshop components, validating manifests, serving local resources, persisting configuration, and coordinating with compatible BAP Hosts.

The Companion is optional and does not replace Wallpaper Engine as the rendering host.

## MVP technical skeleton

The current implementation uses .NET 10 and ASP.NET Core Minimal API. The service binds to loopback when hosted by the desktop entry point and exposes:

- `POST /v1/session` for a random bearer session token;
- authenticated component discovery and manifest validation endpoints;
- authenticated global and wallpaper-scoped JSON configuration endpoints;
- authenticated `/v1/ws` for a versioned Host hello/Companion hello handshake;
- `IWorkshopComponentSource` as the explicit adapter boundary for future Steam Workshop discovery.

Steam Workshop internal files are intentionally not parsed by the service. The current default adapter returns no components.

Tray and GUI code is kept outside the protocol core behind `ITrayController` and `ICompanionGui`, so a future Windows desktop shell can be added without changing the service contracts.

Run the server with `dotnet run --project src/Companion.Server`; it should be configured by the future tray host to listen only on `127.0.0.1`.
