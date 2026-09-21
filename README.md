# trutility

A minimal Windows system-tray launcher utility built with C# / .NET Framework 4.6.2 and WinForms. No external dependencies.

## What it does

trutility sits in the system tray and builds a dynamic right-click menu from files placed in two watched folders:

| Folder | Purpose |
|---|---|
| `C:\ProgramData\trutility\` | Global tools (all users) |
| `%LOCALAPPDATA%\trutility\` | Per-user tools (override global) |

Both trees are scanned recursively. The folder structure becomes the menu structure.


All other files are silently ignored.

## Menu example

```
C:\ProgramData\trutility\
    Windows Tools\
        eventvwr.msc
        services.msc
    Servers\
        server1.rdp
```

Produces:

```
Windows Tools  ▶  eventvwr
                  services
Servers        ▶  server1
──────────────
About
Exit
```

## Auto-start with Windows

To start trutility automatically, add a shortcut to `trutility.exe` in:

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

## Design notes

- **No blocking UI thread** – folder scanning runs on a `Task.Run` background thread; the rebuilt `ContextMenuStrip` is applied back on the UI thread via `SynchronizationContext`.
- **Debounced refresh** – a 400 ms `CancellationToken`-based debounce collapses rapid file-system events into a single rebuild.
- **User overrides global** – both trees are scanned in order (global first, user second); user entries simply overwrite global entries in the same `SortedDictionary` key.
- **Single instance** – a named `Mutex` prevents running two copies simultaneously.
