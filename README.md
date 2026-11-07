# trutility

**A lightweight folder-driven launcher for the Windows system tray.**

trutility turns the contents of watched folders into a searchable tray menu. Add files, shortcuts, and folders; organize them on disk; append suffixes to the filenames to configure how the menu looks.

[Features](#features) · [Getting started](#getting-started) · [Usage](#using-the-menu) · [Deployment](#deployment)

- Download from MS Store: CHANGEME
- Download from GitHub: CHANGEME

---

## Features

- **Folder-driven menu.** The folder structure becomes the menu structure. No separate menu editor or database.
- **Shared and personal tools.** Administrators can provide shared folders; each user has a separate personal folder.
- **Shortcuts and nested menus.** Use folders and shortcut suffixes to control how entries appear.
- **Search across all roots.** Find files by name and path, however deep they are.
- **Keyboard and mouse controls.** Open entries, elevate a launch, remove personal entries, or open their location.
- **Enterprise deployment.** Configure shared roots and settings through the registry and Group Policy.
- **Lightweight, no external dependencies.** Built with C# / .NET Framework 4.6.2 and WinForms. Single executable ~150 KB

## Getting started

1. Run `trutility.exe`.
2. Open the tray menu and choose **Settings** if you want to change its defaults.
3. Add files, shortcuts, or folders to your personal trutility folder: `%LOCALAPPDATA%\trutility\`
4. Open the tray menu. The items you added appear automatically.

Administrators can also populate the shared folder:

`C:\ProgramData\trutility\`

Shared folders appear above the divider and are read-only from trutility. Your personal entries appear below it.

## How the menu is built

### Watched folders

trutility scans two kinds of folder:

|Root|Default location|Ownership|Purpose|
|---|---|---|---|
|Shared|`C:\ProgramData\trutility\`|Administrator-managed.|Visible machine-wide, for all users|
|Personal|`%LOCALAPPDATA%\trutility\`|User-managed|Visible only to owner, your personal location|

Shared folders appear at the top, followed by a thick divider, then the personal folder. Roots are kept separate: two folders with the same name remain two distinct menu entries. A personal entry never replaces a shared one, and neither shared root replaces another.

An administrator can configure multiple shared roots. See [Deployment](#deployment).

### Example

A folder layout such as:

<!-- CHANGEME -->

```text
C:\ProgramData\trutility\
    Windows Tools\
        eventvwr.msc
        services.msc
    server1.lnk

%LOCALAPPDATA%\trutility\
    notes.txt
    Scripts\
        backup.ps1
```

produces a menu conceptually like this:

```text
[ Search…                         ]
──────────────────────────────────
server1
──────────────────────────────────
Windows Tools       ▶
    eventvwr.msc
    services.msc
══════════════════════════════════
notes.txt
──────────────────────────────────
Scripts             ▶
    backup.ps1
──────────────────────────────────
Settings
Exit
```

Files and folders are shown separately within each folder. By default, files come first, followed by a thin divider and then subfolders. The order can be changed in Settings.

## Organizing entries

Append a suffix to a file or folder name to change how it appears. For files and shortcuts, the suffix goes before the extension. Suffixes are not displayed. For example, `putty$^_.lnk` appears as `putty` in the menu.

|Suffix|Effect|
|---|---|
|`$^`|Add a separator above the entry.|
|`$_`|Add a separator below the entry.|
|`$^_`|Add separators above and below the entry.|
|`$F`|Force a folder shortcut to open as a submenu, regardless of its size. Adds a menu level.|
|`$X`|Replace a folder, or a shortcut to a folder, with its contents. Removes a menu level.|

Suffixes can be combined on one entry, for example `$F` with `$^`.

If a shortcut's target is unavailable, the entry is omitted from the menu.

### Folder shortcuts

By default, a shortcut to a folder opens as a submenu if its target contains no more than 250 items. The count includes files and folders in all subfolders. Above that limit, the shortcut remains a normal entry that opens the target in Explorer. This prevents shortcuts to large shares or entire drives from flooding the menu and causing performance issues.

Use `$F` to force a folder shortcut to open as a submenu regardless of size. The automatic expansion behavior and item limit can be changed in Settings.

### Flattening folders with `$X`

Use `$X` to keep an organizational folder on disk without adding that folder as a menu level.

<!-- CHANGEME -->

For example:

```text
Tools$X\
    server1.lnk
    Remote\
```

appears as:

```text
server1
Remote       ▶
```

The contents appear exactly where the marked folder would have appeared, retaining their own order. Mark multiple levels to flatten them all: `Tools$X\Remote$X\`.

On a `.lnk` or `.url` shortcut, `$X` implies `$F`. For example, `Finance$X.lnk` inserts the target folder's contents directly into the menu without a `Finance` submenu.

## Using the menu

|Action|Result|
|---|---|
|Click or Enter|Launch the item.|
|**Shift** + (Click or Enter)|Run as Administrator.|
|**Ctrl** + Click|Remove an entry from your personal folder. It is sent to the Recycle Bin.|
|Middle-click|Open the containing folder in Explorer|

Ctrl+Click is available only for entries in your personal folder. Shared roots will ignore this action.

- A confirmation is shown before deletion by default. The Advanced tab in Settings can disable confirmation separately for files and folders.

## Searching

The search box appears at the top of the menu and searches every watched root, at any depth. Enter at least two characters. Results appear one second after you stop typing by default; the delay is configurable in Settings.

<!-- CHANGEME -->

trutility search results

### Search behavior

- Every search word must appear in the file name or one of its parent folder names. Words can appear in any order. For example, `fin pay` can find `payroll.xlsx` inside `Finance`.
- Names beginning with the typed text are shown first.
- Results from a Shared folder remain above the thick divider; personal results remain below it.
- The path is shown next to each result: Hover over the item to see the full location.
- Search results are files only. Folder names contribute to matching, but folders are not listed as results.

## Deployment

trutility supports shared roots and machine-wide settings. Shared folders are read-only from the application, but their visibility can be controlled through share permissions. For department-specific tools, use a shortcut to a network share or define roots in the registry.

### Department shares using shortcuts

Place a `$F` shortcut to each department's tools share in the shared folder:

```text
C:\ProgramData\trutility\
    Finance$F.lnk  →  \\fs01\dept\finance\tools
    Helpdesk$F.lnk →  \\fs01\dept\helpdesk\tools
```

Grant read access to each share only to the corresponding department.

- Department members see a submenu containing their share's contents.
- Other users see no entry. If a `$F` target cannot be read, it is omitted rather than displayed as a broken shortcut.
- Users cannot remove the shortcut because the shared folder is read-only to them.

Use `Finance$X.lnk` instead to place the share's contents directly in the menu without a `Finance` submenu.

Deploy the same shortcuts to every machine. Share permissions determine who can see each department's tools. Moving a user between departments requires changing group membership, not changing their PC.

### Registry-defined roots

Use registry roots when shortcuts are not sufficient. They support:

- **Per-user targeting:** roots can follow a user rather than a machine.
- **Explicit ordering:** roots have an `Order` value; shortcuts sort alphabetically.
- **Per-user paths:** environment variables such as `%USERNAME%` can be expanded.
- **Automatic removal:** Group Policy can remove a root when the policy no longer applies.

#### Define a root

Create one subkey per root, named after the root:

```text
HKEY_CURRENT_USER\Software\Policies\trutility\Roots\Finance
    Path     REG_EXPAND_SZ   \\fs01\dept\finance\tools
    Label    REG_SZ          Finance
    Order    REG_DWORD       20
```

|Value|Required|Meaning|
|---|---|---|
|`Path`|Yes|Folder to scan. `%VARIABLES%` are expanded.|
|`Label`|No|Places the root under a submenu with this name. Without it, its contents appear at the top level.|
|`Order`|No|Sort position, ascending. Defaults to 100; ties are sorted alphabetically. Values such as 10, 20, and 30 leave room for later additions.|
|`Enabled`|No|Set to `0` to disable the root without deleting its key. This also applies to the built-in `Default` root.|

#### Target roots with Group Policy

Create the key through a user-scoped GPO linked to the department's OU or security group. Each department writes to its own subkey, so roots do not overwrite one another. A user in both Finance and Helpdesk receives both roots, in `Order` sequence.

#### Root precedence

Sources are processed from lowest to highest priority. A later source overrides a root with the same name, value by value. Values not set by the later source are retained from the earlier source.

```text
Built-in default                              → root named "Default"
HKLM\SOFTWARE\trutility\GlobalRoot            → legacy single-root value
HKLM\SOFTWARE\trutility\Roots\                → installer / machine setup
HKCU\SOFTWARE\Policies\trutility\Roots\       → user GPO — department targeting
HKLM\SOFTWARE\Policies\trutility\Roots\       → machine GPO — final say
```

`HKCU\SOFTWARE\trutility` is intentionally excluded from this list. Users cannot define a shared root for themselves through that location.

> **Note:** Registry roots are not a security boundary. A standard user can usually write to their own `HKCU\SOFTWARE\Policies` key, so they may be able to add roots there. To control who can see a folder's contents, use share and NTFS permissions on the folder itself.

### Machine-wide settings

Administrators can configure settings under `HKLM\SOFTWARE\trutility`.

|Value|Type|Effect|
|---|---|---|
|`UserRoot`|`REG_SZ`|Pins the location of the user's personal folder. The user still controls its contents.|
|`AddToShell`|`REG_DWORD`|Enables or disables the Explorer context-menu entry.|
|`PinToTray`|`REG_DWORD`|Controls whether the tray icon is pinned.|
|`ShowExtensions`|`REG_DWORD`|Controls whether file extensions are shown.|
|`EnableSearch`|`REG_DWORD`|Shows or hides the search box.|
|`FocusSearch`|`REG_DWORD`|Controls whether the search box receives keyboard focus when the menu opens.|
|`ExpandFolderShortcuts`|`REG_DWORD`|Controls automatic submenu expansion for folder shortcuts without `$F`.|
|`AutoExpandLimit`|`REG_DWORD`|Maximum item count for automatic expansion. `0` restores the default of 250.|
|`SortBy`|`REG_DWORD`|Sort order: `0` name, `1` date (newest first), `2` extension.|
|`ReverseSort`|`REG_DWORD`|Reverses the selected sort order.|
|`FoldersFirst`|`REG_DWORD`|Places subfolders before files.|
|`HideImages`, `HideWebPages`, `HideConfigFiles`, `HideProgramFiles`, `HideLogsAndMisc`, `HideMedia`, `HideFonts`|`REG_DWORD`|Enables or disables the corresponding file-type category filter.|
|`ExtensionFilter`|`REG_SZ`|Custom file types, such as `png, jpg`. Empty means none.|
|`ExtensionFilterMode`|`REG_DWORD`|`0` hides custom types; `1` shows only custom types and ignores built-in categories.|
|`FilterMenu`|`REG_DWORD`|Controls filtering in the menu tree.|
|`FilterSearch`|`REG_DWORD`|Controls filtering in search results.|
|`SearchDelayMs`|`REG_DWORD`|Delay after typing, in milliseconds. `0` restores the default of 1000.|
|`ConfirmDeleteFiles`|`REG_DWORD`|Controls confirmation before deleting a file or shortcut.|
|`ConfirmDeleteFolders`|`REG_DWORD`|Controls confirmation before deleting a real folder.|
|`Hotkey`|`REG_SZ`|Menu hotkey, such as `Ctrl+Alt+S`. Empty means no hotkey.|
|`HotkeyMenuPosition`|`REG_DWORD`|Position: `0` bottom right, `1` bottom left, `2` top right, `3` top left, `4` mouse cursor.|

Machine-wide settings apply to users who have not already changed the corresponding setting themselves. If a user has an existing value under `HKCU\SOFTWARE\trutility`, that value takes precedence over the machine-wide setting and the control is not managed. To re-assert a machine-wide value, remove the corresponding value from `HKCU\SOFTWARE\trutility`.

## Contributing

> Found a problem? [Open an issue](https://github.com/vkuz-dev/trutility/issues).

## License

GNU Affero General Public License v3.0. See LICENSE.