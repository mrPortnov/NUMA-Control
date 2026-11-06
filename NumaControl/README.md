# NUMA Control 1.7.0 — quick reference

**[English](README.md) · [Русский](README.ru.md)**

NUMA Control assigns selected CPU cores to game processes using the standard Windows affinity APIs. It provides profiles, physical-core/SMT selection, background monitoring, a tray menu, four themes and local diagnostics. Affinity does not guarantee higher FPS: compare frame times and stability in your own games.

> **Anti-cheat warning:** Tested only with Valve Anti-Cheat (VAC). Compatibility with other anti-cheat systems has not been tested or confirmed. VAC testing is not certification or a guarantee against restrictions by the game. Check the rules of your game before using process affinity or priority tools.

NUMA Control uses documented Windows APIs to query processes and change their CPU affinity ([SetProcessAffinityMask](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask)) and, optionally, priority ([SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)). It does not inject DLLs or code, read or modify game memory, patch game files or bypass anti-cheat. It opens process handles for these Windows operations; this does not imply approval by an anti-cheat provider.

## Install and start

Requires Windows 10/11 x64, .NET Framework 4.8 and one processor group with 1–64 logical CPUs.

- **Recommended setup:** `NUMA-Control-1.7.0-setup-x64.exe` detects .NET Framework 4.8 or later. If missing, it downloads the official Microsoft prerequisite and installs it before the application. The runtime may need internet, administrator approval and a restart. Existing 4.8/4.8.1 is skipped; application removal does not remove shared .NET.
- **MSI:** `NUMA-Control-1.7.0-setup-x64.msi` installs for the current user, normally under `%LOCALAPPDATA%\Programs\NUMA Control`. It requires .NET already installed. Repair, upgrade and uninstall use Windows Installer and preserve user-generated `data`.
- **Portable:** extract `NUMA-Control-1.7.0-portable-x64.zip` completely to a stable writable folder. Keep `NUMA-Control.exe.config` beside the EXE and run `Open-NUMA.cmd` or `NUMA-Control.exe`.

The app and distribution packages currently have no trusted code-signing signature. Compare a download's SHA-256 with `SHA256SUMS.txt` from the same release. Do not disable Windows protection to bypass an organizational policy.

## Choose a language

Open **Settings → Interface language → Русский / English**. On the initial Russian interface, use **Настройки → Язык интерфейса**. The change applies immediately and is remembered after restart. Existing profiles default to Russian until you choose English. User names, paths and older log entries remain unchanged; system dialogs may use the Windows language.

## Add your first game

1. Choose **Add game**, select the game's actual EXE or choose a running process.
2. Give it a name. For a common filename such as `Game.exe`, include the exact path to distinguish games.
3. Choose allowed logical processors and save the affinity.
4. Enable **Monitor**, then launch the game normally or use **Launch game**.
5. Check the waiting/applied/error status and correct the profile if needed.

Examples: `cs2.exe`, `dota2.exe`, `Game.exe`. Steam launch targets include `steam://rungameid/730` and `steam://rungameid/570`; `astrumplay://play/0.1177` is another supported target. The game's matching EXE and its launch target are separate fields. Arbitrary shell commands are rejected.

For Epic Games, Battle.net and Ubisoft Connect, select the game EXE and import its launcher shortcut with **Use game shortcut**. **Set up Fortnite** fills both fields for an installed Epic copy. Check the profile status for the result of CPU affinity and priority operations.

## CPU presets

| Preset | Selection |
|---|---|
| All CPUs | Every active logical processor |
| Without edge cores | Excludes the first and last physical cores with all their SMT threads; keeps all CPUs on systems with two or fewer physical cores |
| One thread per core | The first logical processor of each physical core in Windows order; it neither ranks SMT siblings nor disables SMT system-wide |

Any preset can be adjusted by clicking individual processors. Empty and incompatible masks are rejected. New profiles start without edge cores when more than two physical cores exist; existing masks are retained. Numbers follow Windows logical CPU numbering, grouped by detected physical topology.

**Pause, exit, disabling monitoring and deleting a profile leave an already assigned game mask in place until that process exits.** Apply All CPUs explicitly to broaden a running game's mask. Other Windows processes and drivers are not pinned to the excluded cores.

The controller selects its own cores outside running games' actual affinity masks. A physical core is considered occupied when any of its SMT threads is available to a game. With no free physical core, the controller falls back to the last physical core and shows the overlap. After the last known game exits, its original controller affinity is restored.

## Everyday controls

- **Monitor:** enables one profile; global pause takes precedence.
- **Pause / Resume:** controls game-rule assignment while diagnostics and controller core selection continue.
- **Launch game:** enables the profile and clears global pause; an already matching running game is not launched again.
- **Desktop shortcut:** creates a shortcut using the permanent profile ID. Rename/mask changes preserve the ID; recreate shortcuts after moving the application.
- **Options:** edits or deletes a profile. High priority is enabled for newly created profiles; disabling that option leaves an already assigned priority until process exit. RealTime is never used.

Closing the window keeps the app in the tray; choose **Exit** there to stop it. Opt-in Windows autostart uses `--tray` and does not elevate privileges. Default polling is 2 seconds, with 1/2/5 second choices in Settings. Empty idle libraries may sleep longer; changing settings wakes the monitor.

## Appearance and diagnostics

Settings offers **Standard**, **Windows 11 Light**, **Windows 11 Dark** and **AMI BIOS**. Appearance changes are saved and do not change game rules. AMI BIOS uses grey/blue rectangular controls and Consolas. System file dialogs follow Windows styling.

Self-diagnostics shows real CPU, memory, handles, threads and heartbeat data. CPU needs an initial measurement interval; the memory trend needs about a minute of history. One logical CPU percentage and whole-machine CPU percentage have different denominators. Reports stay local and can include paths in exception messages or the log tail: review them before sharing.

## Data, backup and recovery

Default location: **`data` beside the EXE**; fallback: `%LOCALAPPDATA%\NumaControl` when that folder is not writable. Settings shows the effective location.

| File/folder | Contents |
|---|---|
| `profiles.json`, `.bak` | Profiles, selected language/theme, intervals and the previous valid save |
| `processor.json` | CPU topology snapshot, checked against live Windows information at startup |
| `watcher.log`, `.previous` | Rotating local log |
| `icons/` | Cached local game icons and controller icon variants |
| `bug-reports/` | Manual/fatal reports and the separate `last-failure.json` marker |
| `legacy-backup/` | Known old shortcuts backed up during migration, when applicable |

Back up the whole data folder with the app closed, or export profiles in Settings. Import validates the file, asks for replacement confirmation and pauses monitoring. Current language/theme are retained. Exports include paths and arguments. Corrupt files and available backups are preserved separately before recovery; review the paused library before resuming. CPU/topology changes also pause monitoring for review.

A migration card appears only when a previous monitor or shortcut is found. Disabling previous monitoring sends the normal stop signal and backs up recognized shortcuts without deleting old EXEs or enabling new autostart. Unknown shortcut targets require manual handling.

## Limits and help

Protected or elevated games may reject affinity changes. Match the game's elevation if needed, after exiting the ordinary controller instance. Protection is not bypassed. The app does not inject DLLs or read/modify game memory and does not claim anti-cheat certification.

Read the [complete user guide](USER-GUIDE.md) for every control, diagnostics thresholds and recovery behavior. [GitHub Releases](https://github.com/mrPortnov/NUMA-Control/releases) provides downloads and release notes; the [repository](https://github.com/mrPortnov/NUMA-Control) provides screenshots and the changelog.
