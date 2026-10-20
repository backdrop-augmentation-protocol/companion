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

## End-to-end smoke test

Start the server on a loopback port, then run the repeatable HTTP and WebSocket
acceptance check:

```powershell
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\companion-e2e.ps1 -BaseUrl http://127.0.0.1:4311
```

The check covers health, bearer session creation, authenticated component
discovery, wallpaper configuration persistence, and the Host/Companion hello
handshake with permission negotiation.

## Workshop discovery

Set `BAP_WORKSHOP_ROOTS` to one or more local Workshop component directories,
separated by the platform path separator. The Companion recursively scans for
`manifest.json` or `component.manifest.json` and exposes only manifests that
pass the BAP validator:

```powershell
$env:BAP_WORKSHOP_ROOTS = 'D:\path\to\workshop-components'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

After creating a session, authenticated clients can read a component resource
at `GET /v1/components/{id}/resources/{path}`. Paths are confined to the
component directory and traversal outside it is rejected.

## Portable Windows package

Create a framework-dependent `win-x64` package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-companion.ps1
```

Add `-SelfContained` for a package that includes the .NET runtime. The output
contains `start-companion.cmd`; double-click it to start the tray host and
service on `http://127.0.0.1:4311`. The tray menu provides a health check and
clean exit. Pass another port as its first argument when needed.
