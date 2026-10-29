# BAP Companion

[English](README.md) | [简体中文](README.zh-CN.md)

Open-source official Windows Companion for discovering Workshop components, validating manifests, serving local resources, persisting configuration, and coordinating with compatible BAP Hosts.

The Companion is optional and does not replace Wallpaper Engine as the rendering host.

> **Experimental / limited preview — authorization source is not verified.** Host
> identity and installation source are self-reported and are not independently
> authenticated by Companion. Every new Host session, including reconnects,
> requires manual approval; saved Host records do not bypass approval. Approve
> only when you can independently identify the active Wallpaper Engine
> wallpaper. This preview is not a verified authorization product for
> unrestricted use with arbitrary third-party wallpapers. Official Host
> auto-approval is disabled.

## Desktop app and local service

The current implementation uses .NET 10 and ASP.NET Core Minimal API. The service binds to loopback when hosted by the desktop entry point and exposes:

- `POST /v1/session` for a random bearer session token;
- authenticated component discovery and manifest validation endpoints;
- authenticated global and wallpaper-scoped JSON configuration endpoints;
- authenticated `/v1/ws` for a versioned Host hello/Companion hello handshake;
- `GET /v1/hosts/{hostId}/components` for the effective ordered preset, and
  source-keyed resources under `/v1/catalog/components/{key}/resources/{path}`.

The desktop app lists installed Web wallpaper hosts and BAP components from
both Workshop subscriptions and local WE projects. Presets hold ordered
component selections with explicit source keys. A host-specific preset overrides
the global selection; missing components remain in the preset. Without an active
preset, discovered components are shown by default, preferring a subscribed
copy when the same BAP ID also exists locally.

The WPF desktop window edits presets and displays installed hosts and components as
preview cards. Select a card to see its source, BAP ID, version, status, and
project location; host details can request a switch through WE's official
command-line controls. Manual launch opens the window. The Settings page offers
English and Simplified Chinese (English by default), and current-user automatic
startup is enabled by default on first launch; it can be disabled there. An
automatic launch starts minimized to the tray.
The service runs without a console window. The current and previous rolling
logs are in `%LOCALAPPDATA%\BAP Companion\logs` and can be opened from Settings.

Run the service alone with `dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311`.
The service accepts requests only from loopback clients using a loopback Host
name, even if its listening URL is configured more broadly.

## End-to-end smoke test

Start the server with a local authorization control pipe, then run the acceptance
check from a second PowerShell window. The script confirms an unapproved token
cannot discover components, approves its test Host through that current-user
pipe, and continues through the HTTP and WebSocket checks.

```powershell
$env:BAP_AUTHORIZATION_PIPE = 'Bap.Companion.E2E'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

In a second PowerShell window:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\companion-e2e.ps1 -BaseUrl http://127.0.0.1:4311
```

The check covers health, bearer session creation, authenticated component
discovery, wallpaper configuration persistence, and the Host/Companion hello
handshake with permission negotiation. It also remembers the first approval,
confirms that a new session with the same Host ID still requires explicit
approval, then revokes that Host and verifies the active WebSocket closes and
both session tokens lose access. It also checks that an approval granting only
component discovery cannot read configuration, and that an incompatible
protocol version is rejected before authorization. Reconnecting with the same
session token after a normal disconnect also requires a fresh approval.
HTTP component and configuration endpoints require both a valid Bearer token
and the corresponding permission granted by a live WebSocket `hello` session.
Session creation alone grants no access; closing the WebSocket clears its grants.

## Installed content discovery

By default, the Companion finds Steam through the Windows registry (with
standard installation paths as fallbacks), reads `libraryfolders.vdf`, and
discovers installed Wallpaper Engine Workshop components under AppID `431960`.
It recursively scans for `manifest.json` or `component.manifest.json` and
exposes only manifests that pass the BAP validator. No path setting is needed
for a normal Steam installation.

The catalog also reads local WE projects from each discovered Steam library's
`wallpaper_engine/projects/myprojects` directory. `BAP_LOCAL_ROOTS` can add
other development package roots. Local and subscribed items can be combined
freely; an explicit source selection in a preset disambiguates matching BAP IDs.

For local development, set `BAP_WORKSHOP_ROOTS` to one or more component
directories separated by the platform path separator. Explicit roots replace
automatic discovery unless `BAP_DISCOVER_STEAM_WORKSHOP=1` is also set:

```powershell
$env:BAP_WORKSHOP_ROOTS = 'D:\path\to\workshop-components'
dotnet run --project src/Companion.Server --urls http://127.0.0.1:4311
```

After creating a session, authenticated clients can read a component resource
at `GET /v1/components/{id}/resources/{path}`. Paths are confined to the
component directory and traversal outside it is rejected.

Set `BAP_DISCOVER_STEAM_WORKSHOP=0` to disable automatic discovery, or `1` to
combine it with explicit roots. If Steam cannot be found, `BAP_STEAM_ROOT`
can point to its installation directory.

## Portable Windows package

Create a framework-dependent `win-x64` package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-companion.ps1
```

The package uses the Fluent WPF front end. Add `-LegacyUi` to package the
previous WinForms front end instead, or `-SelfContained` for a package that
includes the .NET runtime. The output
contains `start-companion.cmd`; double-click it to start the desktop app and
service on `http://127.0.0.1:4311`. Closing the window leaves the tray app
running; use the tray Exit command to stop it. Pass another port as the
script's first argument when needed.

## Per-user installation

From the published package, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-companion.ps1
```

This installs to `%LOCALAPPDATA%\Programs\BAP Companion` and adds a
`BAP Companion` shortcut to the current user's Start menu. It does not require
administrator privileges. On first launch, the app enables current-user
automatic startup; uncheck it in Settings to opt out. Before installing an
update, exit the installed Companion from its tray menu; the installer preserves
its configuration data.
