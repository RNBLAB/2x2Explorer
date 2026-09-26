# 2x2 Explorer

[한국어](README.md) · **English**

A Windows file explorer split into four panes. Each pane has its own tabs and preview, and batch renaming, image editing and PDF editing happen in the same window instead of a separate tool.

This started as a personal tool and is published as-is. Plenty of rough edges remain.

## What it does

**Four panes** — A fixed 2x2 grid. Each pane carries its own tab strip, address bar, toolbar and preview. The layout can also be switched to two or three panes in settings.

**Preview** — Images and text are drawn directly, with syntax highlighting for code. PDF, Hangul word processor files (.hwp/.hwpx) and Office documents are handed to the preview handlers installed on Windows. Ctrl+wheel zooms, wheel scrolls.

**Batch rename (beta)** — Edit names in a spreadsheet-style grid, paste from the clipboard, find and replace, add sequence numbers, and normalize names by splitting them into date, document number, project, body, version, author and note and reassembling them. Every run is logged and can be undone.

**Image editing** — Crop, rotate (multiples of 90° are lossless), straighten, resize and convert. A separate window handles many files at once.

**PDF editing** — Reorder pages, remove pages, merge files, and write images out as PDF.

**Batch printing** — Send a selection to the printer in one go.

**Shell integration** — Copy, move and delete are delegated to Windows `IFileOperation`, so the progress dialog, name-collision prompts, Recycle Bin and undo all behave exactly as they do in File Explorer. Context menus, shortcuts (.lnk) and the properties dialog are the Windows ones too.

**Search** — Queries [Everything](https://www.voidtools.com/) when it is running, and falls back to walking the folder when it is not.

**Theme** — Follows the Windows light/dark setting and reacts while running.

## Requirements

- Windows 10 1809 or later (x64)
- .NET 10 SDK — required to build. The runtime alone is not enough.
- Internet access for the first build — [PDFsharp](https://github.com/empira/PDFsharp) (MIT) is restored once from NuGet for the PDF editor. Not needed afterwards.

## Getting it running

### 1. Install the .NET 10 SDK

```cmd
winget install Microsoft.DotNet.SDK.10
```

Or download the **SDK x64** installer from <https://dotnet.microsoft.com/download/dotnet/10.0>. Open a new command window afterwards so it is picked up.

```cmd
dotnet --list-sdks
```

A line starting with `10.` means you are set.

### 2. Clone and run

```cmd
git clone https://github.com/RNBLAB/2x2Explorer.git
cd 2x2Explorer
start.cmd
```

`start.cmd` builds, runs, and writes everything it printed to `run.log`. Double-clicking it works too.

To run it directly:

```cmd
dotnet run --project src\Explorer.App\Explorer.App.csproj
```

In Visual Studio, open `2x2Explorer.sln`, set `Explorer.App` as the startup project and press F5.

> `start.cmd` runs `dotnet Explorer.dll` rather than the `Explorer.exe` produced by the build. Windows protection features sometimes refuse to start the unsigned launcher.

## Tests

```cmd
test.cmd
```

Checks the rules in `Explorer.Core` and writes the result to the console and to `test.log`. The test project pulls no packages, so it runs offline.

## Settings

The first run creates `%APPDATA%\2x2Explorer\settings.json`. A sample sits in [`docs/settings.sample.json`](docs/settings.sample.json).

Per-pane start locations and startup behaviour (`startPath` / `restoreTabs`) live there. A corrupt file is moved aside as `.broken-<time>` and defaults are used.

## Layout

| Project | Target | Responsibility |
|---|---|---|
| `Explorer.Core` | net10.0 | Settings model, path and name rules, sorting, search. Knows nothing about the UI |
| `Explorer.Shell` | net10.0-windows | Windows shell P/Invoke — file operations, thumbnails, preview handlers, printing, Recycle Bin |
| `Explorer.App` | net10.0-windows (WPF) | Windows, panes, editor dialogs |

`Explorer.Core` never depends on the UI, which is what lets the settings and naming rules be tested from a console app. `DllImport` never leaves `Explorer.Shell`.

## Documentation

The design documents are written in Korean.

- [`docs/decisions.md`](docs/decisions.md) — settled design decisions and why
- [`docs/architecture.md`](docs/architecture.md) — structure and layer responsibilities
- [`docs/roadmap.md`](docs/roadmap.md) — implementation stages and current position
- [`docs/releasing.md`](docs/releasing.md) — version number rules and how release notes are written
- [`CHANGELOG.md`](CHANGELOG.md) — what changed in each release

## Known limitations

- The large-icon view is not virtualized and slows down in folders with thousands of files.
- Hangul preview relies on the Hancom Office preview handler. Without it, the app falls back to the thumbnail embedded in the document.
- The name normalizer recognises Korean document naming conventions (district names, phase numbers, neighbourhood names). It is of limited use for names in other languages.
- Batch rename is beta. It can be turned off in settings.
- Office previews are drawn by a handler outside the WPF tree, so no UI can be layered on top of them.

## License

Apache License 2.0. The full text is in [`LICENSE`](LICENSE); the copyright notice and third-party list are in [`NOTICE`](NOTICE).

Copyright 2026 RNBLAB
