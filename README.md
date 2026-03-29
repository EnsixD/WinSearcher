# WinSearcher

A fast, minimal Windows search launcher — triggered by **Win+Q**, built with C# + WPF + .NET 8.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Language](https://img.shields.io/badge/language-C%23-green)

---

## Features

- **Win+Q global hotkey** — overrides the default Windows Search without leaving the Win key stuck
- **Recent apps on open** — instantly shows recently used programs when launched
- **Real-time search** — finds apps, files, folders, shortcuts as you type (50ms debounce)
- **In-memory index** — background file indexer for near-instant results after first launch
- **Smart scoring** — results ranked by exact match → prefix → contains → word boundary → fuzzy
- **Shell icons** — real per-app icons extracted via Windows Shell API
- **Keyboard navigation** — arrow keys to move, Enter to open, Escape to close
- **Click outside to close** — window hides on focus loss
- **System tray** — runs in background, accessible from tray icon

## Search locations

| Location | What's indexed |
|---|---|
| Start Menu (user + common) | `.exe`, `.lnk`, `.url` |
| Desktop (user + common) | all supported types |
| Documents | `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.txt` |
| Downloads | archives, media, documents |

## Keyboard shortcuts

| Key | Action |
|---|---|
| `Win+Q` | Open / close |
| `↑` `↓` | Navigate results |
| `Enter` | Open selected |
| `Escape` | Close |
| `Tab` | Keep focus on search box |

## Requirements

- Windows 10 / 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build & run

```bash
git clone https://github.com/EnsixD/WinSearcher.git
cd WinSearcher
dotnet run
```

Or build a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Stack

- **C# 12 / .NET 8** — language and runtime
- **WPF** — UI framework
- **WH_KEYBOARD_LL** — low-level keyboard hook for global Win+Q intercept
- **SendInput** — synthetic key injection to prevent Win-key stuck state
- **SHGetFileInfo** — Shell32 API for real application icons
- **WScript.Shell COM** — resolves `.lnk` shortcut targets for recent apps
