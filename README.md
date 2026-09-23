# trutility

A minimal Windows system-tray launcher. C# / .NET Framework 4.6.2, WinForms, no external dependencies.

Put files, shortcuts or folders in a watched folder and they become a tray menu. The folder structure *is* the menu structure.

## Folders

trutility scans two kinds of folder:

| | Default location | Who owns it |
|---|---|---|
| **Shared folders** | `C:\ProgramData\trutility\` | The administrator. trutility only ever reads them. |
| **Your folder** | `%LOCALAPPDATA%\trutility\` | You. The only place trutility writes. |

Shared folders render first, then a thicker line, then your own. **Nothing merges:** a `Tools` folder in two places stays two separate entries — yours never replaces a shared one, and one shared folder never replaces another.

An administrator can assign more than one shared folder — see [Deploying to groups](#deploying-to-groups).

Upgrading from an earlier version keeps whatever you were already using: a `Documents\trutility` with shortcuts in it is pinned automatically, and a `Program Files\trutility` that still holds tools keeps working.

## Menu example

```
C:\ProgramData\trutility\          %LOCALAPPDATA%\trutility\
    Windows Tools\                     notes.txt
        eventvwr.msc                   Scripts\
        services.msc                       backup.ps1
    server1.lnk
```

```
server1                    ← files first
───────────────
Windows Tools    ▶  eventvwr.msc     ← then folders
                    services.msc
═══════════════            ← the one thick line: above is the administrator's, below is yours
notes.txt
───────────────
Scripts          ▶  backup.ps1
───────────────
About / Settings
Exit
```

`.lnk` and `.url` lose their extension in the menu, since the shortcut is not the point. Everything else keeps its name as-is — `eventvwr.msc` shows as `eventvwr.msc`.

Within each folder, files are listed before subfolders, parted by a thin line. `desktop.ini` and `thumbs.db` are ignored; everything else becomes an entry.

## Organising the menu

Append to a **file or folder name**:

| Suffix | Effect |
|---|---|
| `$^` | separator above |
| `$_` | separator below |
| `$^_` | separators on both sides |
| `$F` | on a `.lnk` or `.url`: expand the target folder as a submenu instead of showing the shortcut |
| `$X` | show a folder's contents in its place — the folder itself never appears |

Example: `putty$^_.lnk` gets a separator on each side.

`$X` is how you keep a folder on disk without it showing up in the menu:

```
Tools$X\                          server1
    server1.lnk         becomes   ─────────
    Remote\                       Remote   ▶
```

Its children land exactly where the folder would have, keeping their own order. To collapse two levels, mark both — `Tools$X\Remote$X\`. On a `.lnk` or `.url` it implies `$F`, so `Finance$X.lnk` drops the target folder's contents straight into the menu without a `Finance` submenu around them.

A `$F` shortcut shows nothing at all — rather than a dead entry — when its target is unreachable, denied, or already on the menu. It cannot reach into another watched folder, so it can never pull shared tools across the line into your own section.

## Using the menu

| Action | Effect |
|---|---|
| Click | launch |
| **Shift** + Click | run as Administrator |
| **Ctrl** + Click | remove from your folder (sent to the Recycle Bin) |

Ctrl+Click only works on files in your own folder. Shared folders are read-only.

## Settings

Right-click the tray icon → **About / Settings**:

- **Add to trutility in Explorer context menu** — right-click any file or folder to drop a shortcut into your folder
- **Run at startup**
- **Pin icon to tray**
- **Your folder** — change its location, reset it, or open it

Any setting an administrator has decided is shown as *[Managed by Administrator]* and greyed out.

## Deploying to groups

The single shared folder is often too broad: a department needs its own tools without every user on the machine seeing them. Additional shared folders are defined as **registry subkeys**, one per folder.

Subkeys are used rather than one list value on purpose: two policies that each assign a folder both apply, so a user in two departments receives both. Nothing collides.

### Define a folder

Create a subkey named after the folder — the name is its identity:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\trutility\Roots\Finance
    Path     REG_EXPAND_SZ   \\fs01\dept\finance\tools
    Label    REG_SZ          Finance
    Order    REG_DWORD       20
```

| Value | Required | Meaning |
|---|---|---|
| `Path` | yes | The folder to scan. `REG_EXPAND_SZ`, or `%VARIABLES%` in a `REG_SZ`, are expanded — so `\\fs01\home\%USERNAME%\tools` serves every user from one value. |
| `Label` | no | Nest the folder under a submenu of this name. Without it the contents splice in at the top level. |
| `Order` | no | Sort position, ascending. Defaults to 100; ties break alphabetically. Space them 10, 20, 30. |
| `Enabled` | no | `0` switches the folder off without deleting the key — including the built-in `Default`. |

### Target it at a group

Link a **user**-scoped GPO to the department's OU or security group and have it create the key under:

```
HKEY_CURRENT_USER\Software\Policies\trutility\Roots\<Name>
```

Because each department writes its own subkey, a user in Finance *and* Helpdesk gets both folders, in `Order` sequence, with no configuration between the two policies.

### Where trutility looks

Lowest authority first; a later source overrides a folder of the same name, value by value, inheriting whatever it does not set:

```
built-in default                            → the folder named "Default"
HKLM\SOFTWARE\trutility\GlobalRoot           legacy single-folder value
HKLM\SOFTWARE\trutility\Roots\               installer / machine setup
HKCU\SOFTWARE\Policies\trutility\Roots\      user GPO — department targeting
HKLM\SOFTWARE\Policies\trutility\Roots\      machine GPO — final say
```

`HKCU\SOFTWARE\trutility` is deliberately not a source: a user cannot grant themselves a shared folder.

Policy keys live under `Software\Policies` because Group Policy owns and cleans up that subtree — when someone leaves a department, their folder goes with the policy instead of lingering.

### Other things an administrator can set

Under `HKLM\SOFTWARE\trutility`:

| Value | Type | Effect |
|---|---|---|
| `UserRoot` | `REG_SZ` | Pins **where** the user's own folder lives. What goes in it is still theirs alone. |
| `AddToShell` | `REG_DWORD` | Explorer context-menu entry on or off. |
| `PinToTray` | `REG_DWORD` | Tray icon pinned or not. |

These apply to anyone who has not already changed the setting themselves — the dialog then shows it greyed out as managed. **A user who changed it before the policy arrived keeps their own choice**, because the matching `HKCU` value shadows `HKLM`. To re-assert one, delete it from `HKCU\SOFTWARE\trutility` as well.

Put a shortcut to `trutility.exe` in the **common** Startup folder to force auto-start for every user.

### Two things worth getting right

- **ACL the share read-only** for its members. A shared folder anyone can write to is a way to get a shortcut executed by everyone in the department.
- Enable **Access-Based Enumeration** on the share so members of other departments do not see folders they cannot open.

### Diagnosing

trutility writes to the debug trace, readable with DebugView on the affected machine:

- which folders resolved and from which registry key, at start-up and again whenever the set changes
- any folder it could not read

A folder missing from that list was never configured for this user, or is switched off. One that appears but contributes nothing to the menu is unreachable or empty.

## Building

```
msbuild trutility.sln /p:Configuration=Release
```

No NuGet packages. Pushing a `v*` tag builds a release through GitHub Actions.

## Notes

- Folder scanning and icon loading run off the UI thread; a 400 ms debounce collapses bursts of file-system changes into one rebuild.
- Folders that could not be watched — a share that was offline at start-up — are retried every five minutes until they are, then polling stops.
- A named mutex keeps one instance running.

## License

GNU AGPL v3 — see [LICENSE](LICENSE).
