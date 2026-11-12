# FPS Overlay

A tiny corner counter for Windows: FPS of the active game, plus CPU, GPU and RAM load.
Written in C# (WPF, .NET 8).

```
C 23  G 97  R 9.1/32  F 144
```

## Features

- **FPS without injecting into the game.** The app subscribes to the frame-present ETW events that Windows writes itself, the same way PresentMon and Xbox Game Bar do.
  DirectX 9-12 are supported, as well as Vulkan and OpenGL when the driver presents through DXGI. Administrator rights are required.
  Browsers (Chrome, Edge, Firefox) and Electron apps draw from a separate GPU process; its frames are counted for the window too.
- **CPU** is measured like Task Manager does (`% Processor Utility`), **GPU** is the busiest engine of the video card, **RAM** is used / total.
- **Stays out of the way:** clicks pass through and focus is never taken. Hold Alt to drag the window; on release it snaps to the nearest monitor corner.
- **FPS colour by thresholds:** green from 60, yellow from 30, red below.
- **Tray menu:** colour thresholds, start with Windows, reset position, language, theme, updates, exit.

## Languages

The interface is available in English (default), Russian and Ukrainian. Change it from the tray menu: **Language**.
The starting language is taken from `settings.json`, otherwise from the `language` field of the `.nimvoro.json` marker next to the exe (written by the NIMVORO launcher), otherwise English.

## Theme

Tray menu: **Theme** > System / Light / Dark. System follows Windows live. The choice is stored in `settings.json` (`Theme`).

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

Run `publish\FpsOverlay.exe` (it asks for administrator rights).
Settings: `%APPDATA%\FpsOverlay\settings.json` (`Corner`, `ColorFps`, `CheckUpdates`, `Language`, `Theme`).

## Layout

```
App.xaml.cs              single instance, starts the window and the tray
OverlayWindow.xaml(.cs)  click-through always-on-top window, Alt drag, corner snapping
Metrics/FpsMonitor.cs    FPS from DXGI / D3D9 / DxgKrnl ETW events
Native/ProcessTree.cs    child processes that draw for the window (a browser's GPU process)
Metrics/SystemMonitor.cs CPU, GPU (PDH) and RAM once a second
TrayIcon.cs              tray menu
Strings.cs               en/ru/uk string table and starting-language resolution
Theme.cs                 System/Light/Dark palettes and the tray menu renderer
Updater.cs               update check and hand-off to the NIMVORO launcher
Autostart.cs             autostart through Task Scheduler (with administrator rights)
```

## Updates and NIMVORO

The tool is part of the [NIMVORO](https://github.com/GrigoryyKach/nimvoro) suite: the launcher installs and updates it in one click.
On startup the app checks the latest [release](https://github.com/GrigoryyKach/fps-overlay/releases/latest) (toggle **Updates > Check for updates on startup** in the tray menu).
When a newer version exists, the Update item hands the update to the launcher; without the launcher it opens the release page.

Releases are built by GitHub Actions when a tag is pushed: bump the version in `FpsOverlay.csproj`, then `git tag v{version} && git push --tags`.
