# trutility

A minimal Windows system-tray launcher. C# / .NET Framework 4.6.2, WinForms, no external dependencies.

Put files, shortcuts or folders in a watched folder and they become a tray menu. The folder structure *is* the menu structure.

Built with AI assistance. I design the features, read the diffs and test every release myself. Found a problem? [Open an issue](https://github.com/vkuz-dev/trutility/issues).

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
[ Search…        ]         ← see Searching
───────────────
server1                    ← files first
───────────────
Windows Tools    ▶  eventvwr.msc     ← then folders
                    services.msc
═══════════════            ← the one thick line: above is the administrator's, below is yours
notes.txt
───────────────
Scripts          ▶  backup.ps1
───────────────
Settings
Exit
```

`.lnk` and `.url` always lose their extension in the menu, since the shortcut is not the point. Other files show theirs — `eventvwr.msc` stays `eventvwr.msc` — unless extensions are turned off in Settings. Folder names are never cut at a dot.

Within each folder, files are listed before subfolders, parted by a thin line — the order can be changed in [Settings](#settings). `desktop.ini` and `thumbs.db` are ignored; everything else becomes an entry.

## Organising the menu

Append to a **file or folder name**. Suffixes are never displayed — `putty$^_.lnk` shows as `putty`.

| Suffix | Effect |
|---|---|
| `$^` | separator above |
| `$_` | separator below |
| `$^_` | separators on both sides |
| `$F` | a shortcut to a folder opens as a submenu, like the folder itself, whatever its size — **adds** a menu level |
| `$X` | a folder, or a shortcut to one, is replaced by its contents — **removes** a menu level |

`$F` only applies to `.lnk` and `.url` shortcuts. `$X` applies to folders and shortcuts alike.

A shortcut to a folder opens as a submenu without any suffix too, as long as the folder holds no more than 250 items — files and folders, subfolders included. Above that it stays an ordinary shortcut that opens the folder in Explorer, so a link to a whole drive or a large share doesn't flood the menu. `$F` is how you ask for the submenu regardless of size. Both the behaviour and the limit can be changed in [Settings](#settings).

`$X` is how you keep a folder on disk without it showing up in the menu:

```
Tools$X\                          server1
    server1.lnk         becomes   ─────────
    Remote\                       Remote   ▶
```

Its children land exactly where the folder would have, keeping their own order. To collapse two levels, mark both — `Tools$X\Remote$X\`. On a `.lnk` or `.url` it implies `$F`, so `Finance$X.lnk` drops the target folder's contents straight into the menu without a `Finance` submenu around them.

A `$F` shortcut shows nothing at all — rather than a dead entry — when its target is unreachable, denied, or already on the menu. It cannot reach into another watched folder, so it can never pull shared tools across the line into your own section. A shortcut without `$F` is never hidden this way: when it can't be opened as a submenu it simply stays a shortcut.

## Using the menu

| Action | Effect |
|---|---|
| Click | launch |
| **Shift** + Click | run as Administrator |
| **Ctrl** + Click | remove from your folder (sent to the Recycle Bin) |
| Middle Click | open the folder location in Explorer |

Ctrl+Click only works on files in your own folder. Shared folders are read-only.
On an expanded folder shortcut it removes the shortcut only, never the folder it points at.
On a real folder it removes the folder and everything in it.
It asks for confirmation first; the Advanced tab of Settings turns that off for files, folders or both.

## Searching

The box at the top of the menu searches every folder at once, however deep. Type at least two characters; results appear once you stop typing for a second (the delay can be changed in [Settings](#settings)).

Search is on by default. Untick **Enable search box** in Settings to remove the box and get the plain menu back.

The box has the keyboard as soon as the menu opens, so you can type straight away. Letter keys then go into the search instead of jumping to an entry; to get that back, untick **Focus search box by default** in Settings and click the box when you want to search.

```
[ fin pay        ]
───────────────────────────────────
payroll.xlsx       Finance ▸ Reports
```

- Every word you type must appear in the file's name or in the folders it sits in, in any order — `fin pay` finds `payroll` inside `Finance`.
- Names that start with what you typed come first. Shared results stay above the thick line and yours below it, as in the menu.
- Each result shows where it is on the right: the first two folders, with `…` for deeper ones. Hover over it for the full location.
- At most 25 results are listed, with a last line saying how many more matched.
- Results are always files. Folder names count toward a match, but folders themselves aren't listed.

| Key | Effect |
|---|---|
| **Up** / **Down** | move the highlight through the entries — results, or the whole top level while the box is empty — wrapping at the ends |
| **Enter** | launch the highlighted entry, or the top result when nothing is highlighted — straight away, without waiting for the pause |
| **Shift** + **Enter** | launch it as Administrator |
| **Right** or **Enter** on a folder | open its submenu |
| **Esc** | clear the box; press it again to close the menu |

A result behaves like any other entry, so Shift+Click and Ctrl+Click work on it too.

## Settings

Click the tray icon → **Settings**. The dialog has three tabs.

**General**

- **Add to trutility in Explorer context menu** — right-click any file or folder to drop a shortcut into your folder
- **Run at startup**
- **Create Desktop shortcut** — off by default; greyed out when an administrator has already put one on the desktop for all users
- **Create Start Menu shortcut** — off by default; lists trutility under All apps, and is greyed out when an administrator has already added it there for all users
- **Pin icon to tray**
- **Enable search box** — on by default; turned off, the menu has no search box at all
- **Focus search box by default** — on by default, and greyed out while search is off; see [Searching](#searching)
- **Search delay** — 1000 milliseconds by default, and greyed out while search is off; how long to wait after the last keystroke before searching. Setting it to 0 or leaving it empty puts 1000 back.
- **Your folder** — change its location, reset it, or open it

**Advanced**

- **Show file extensions** — on by default
- **Expand folder shortcuts by default** — on by default; a shortcut to a folder opens as a submenu without needing `$F` in its name. Turned off, only shortcuts marked `$F` or `$X` expand.
- **Folder item limit** — 250 by default; the largest folder, counted in files and folders with subfolders included, that is expanded this way. A bigger one stays a plain shortcut unless its name ends in `$F`. Setting it to 0 or leaving it empty puts 250 back.
- **Ask for confirmation when** — on by default; turned off, Ctrl+Click never asks before deleting.
  - **Deleting files** — on by default; covers files, shortcuts and expanded folder shortcuts.
  - **Deleting folders** — on by default; covers real folders, which go with everything in them.
- **Sorting** — the order of files within each folder: by name (the default), by date modified with the newest first, or by extension. For a shortcut both the date and the extension are the shortcut's own, not its target's. Submenus always stay in name order.
  - **Reverse order** — turns the chosen order around: Z to A, oldest first. Submenus are reversed too.
  - **Folders before files** — lists submenus above the files of each folder instead of below them.

**About** — what trutility is, and the naming suffixes and click actions at a glance.

Under the tabs:

- **Refresh menu** — rescans every folder and reloads every icon. Changes are normally picked up on their own; this is for when they aren't, such as a share that just came back online or a program updated in place that still shows its old icon.
- **Apply** — saves like OK but keeps the dialog open, so you can try a few combinations against the real menu. What has been applied stays applied; Cancel only drops changes made since.

Any setting an administrator has decided is greyed out, and marked *[Managed by Administrator]* — except the two number boxes, which are only greyed out.

## Deploying to groups

The shared folder is visible to everyone on the machine, but a department usually needs its own tools. There are two ways to do this. Start with the first one.

### Shortcuts and share permissions

Put a `$F` shortcut to the department's share in the shared folder:

```
C:\ProgramData\trutility\
    Finance$F.lnk      →  \\fs01\dept\finance\tools
    Helpdesk$F.lnk     →  \\fs01\dept\helpdesk\tools
```

Then give only that department read access to its share. That's all the configuration there is:

- **Members** see a `Finance` submenu with the share's contents.
- **Everyone else** sees nothing — a `$F` shortcut whose target can't be read is left out of the menu entirely, not shown as a broken entry.
- Nobody can remove it: the shared folder is read-only to users, and it sits above the thick line with the rest of the administrator's tools.

Use `Finance$X.lnk` instead to place the share's contents directly in the menu, without a `Finance` submenu around them.

The same shortcuts go on every machine. Who sees what is decided by the share's permissions, so moving someone between departments means changing group membership — nothing on their PC.

### Registry folders

When shortcuts aren't enough, a shared folder can be defined in the registry instead. Use it when you need:

- **Folders that follow the user**, not the machine — no files to put on each PC.
- **Explicit order** — shortcuts sort alphabetically.
- **Per-user paths** — `\\fs01\home\%USERNAME%\tools` from one value.
- **Automatic removal** — Group Policy deletes the key when the policy no longer applies to someone.

#### Define a folder

Create one subkey per folder, named after it:

```
HKEY_CURRENT_USER\Software\Policies\trutility\Roots\Finance
    Path     REG_EXPAND_SZ   \\fs01\dept\finance\tools
    Label    REG_SZ          Finance
    Order    REG_DWORD       20
```

| Value | Required | Meaning |
|---|---|---|
| `Path` | yes | The folder to scan. `%VARIABLES%` are expanded. |
| `Label` | no | Nest the folder under a submenu of this name. Without it, the contents go straight into the top level. |
| `Order` | no | Sort position, ascending. Defaults to 100; ties sort alphabetically. Space them 10, 20, 30. |
| `Enabled` | no | `0` turns the folder off without deleting the key — including the built-in `Default`. |

#### Target it at a group

Create the key from a **user**-scoped GPO linked to the department's OU or security group. Each department writes its own subkey, so they never overwrite each other: a user in Finance *and* Helpdesk gets both folders, in `Order` sequence.

#### Where trutility looks

Lowest priority first. A later source overrides a folder with the same name, value by value — anything it doesn't set is kept from the earlier one:

```
built-in default                            → the folder named "Default"
HKLM\SOFTWARE\trutility\GlobalRoot           legacy single-folder value
HKLM\SOFTWARE\trutility\Roots\               installer / machine setup
HKCU\SOFTWARE\Policies\trutility\Roots\      user GPO — department targeting
HKLM\SOFTWARE\Policies\trutility\Roots\      machine GPO — final say
```

`HKCU\SOFTWARE\trutility` is deliberately not on this list, so users can't give themselves a shared folder.

### Other things an administrator can set

Under `HKLM\SOFTWARE\trutility`:

| Value | Type | Effect |
|---|---|---|
| `UserRoot` | `REG_SZ` | Pins **where** the user's own folder lives. What goes in it is still theirs alone. |
| `AddToShell` | `REG_DWORD` | Explorer context-menu entry on or off. |
| `PinToTray` | `REG_DWORD` | Tray icon pinned or not. |
| `ShowExtensions` | `REG_DWORD` | File extensions shown in the menu or not. |
| `EnableSearch` | `REG_DWORD` | Search box shown at the top of the menu, or not. |
| `FocusSearch` | `REG_DWORD` | Search box takes the keyboard when the menu opens, or not. |
| `ExpandFolderShortcuts` | `REG_DWORD` | Shortcuts to folders open as submenus without `$F`, or not. |
| `AutoExpandLimit` | `REG_DWORD` | Largest folder, in items, expanded without `$F`. 0 means the default of 250. |
| `SortBy` | `REG_DWORD` | Order of files within a folder: 0 by name, 1 by date (newest first), 2 by extension. |
| `ReverseSort` | `REG_DWORD` | Chosen order reversed, or not. |
| `FoldersFirst` | `REG_DWORD` | Submenus listed above files, or below. |
| `SearchDelayMs` | `REG_DWORD` | Pause after typing before a search runs, in milliseconds. 0 means the default of 1000. |
| `ConfirmDelete` | `REG_DWORD` | Ctrl+Click asks before deleting, or never does. |
| `ConfirmDeleteFiles` | `REG_DWORD` | Deleting a file or shortcut asks first, or not. |
| `ConfirmDeleteFolders` | `REG_DWORD` | Deleting a real folder asks first, or not. |

These apply to anyone who has not already changed the setting themselves — the dialog then shows it greyed out as managed. **A user who changed it before the policy arrived keeps their own choice**, because the matching `HKCU` value shadows `HKLM`. To re-assert one, delete it from `HKCU\SOFTWARE\trutility` as well.

Put a shortcut to `trutility.exe` in the **common** Startup folder to force auto-start for every user. A shortcut in the common Desktop folder (`C:\Users\Public\Desktop`) likewise gives every user the desktop icon, and one in the common Start Menu folder (`C:\ProgramData\Microsoft\Windows\Start Menu\Programs`, not a subfolder of it) the Start Menu entry. In each case the matching checkbox is greyed out as managed.

### Getting it right

- **Members get read access only.** If they can write to the share, any one of them can add a shortcut that everyone else in the department will run.
- **Test as a standard user.** An administrator can often read shares that a deny rule should block, so testing with your own account will show folders a real user wouldn't see.
- **Access-Based Enumeration** is optional for trutility, which hides unreadable folders on its own, but it keeps them hidden in Explorer too.

### Diagnosing

trutility writes to the debug trace, readable with DebugView on the affected machine:

- which registry folders were found and which key each came from, at start-up and whenever that changes
- every shared folder it couldn't read, including a `$F` target the user has no access to (unreadable folders in the user's own folder show a notification instead)

If a registry folder is missing from that list, it isn't configured for this user or is turned off. If it's listed but nothing from it shows up in the menu, the folder is unreachable or empty.

## Building

```
msbuild trutility.sln /p:Configuration=Release
```

No NuGet packages. Pushing a `v*` tag builds a release through GitHub Actions.

## Notes

- Folders are scanned and icons pre-loaded in the background, so opening a menu only reads what is already loaded. A submenu opened in the first moments after start-up or Refresh may still load its own icons as it opens.
- A 400 ms debounce collapses bursts of file-system changes into one rebuild.
- Folders that could not be watched — a share that was offline at start-up — are retried every five minutes until they are, then polling stops.
- A named mutex keeps one instance running.

## License

GNU AGPL v3 — see [LICENSE](LICENSE).
