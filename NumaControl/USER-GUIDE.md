# NUMA Control 1.7.0 — user guide

**[English](USER-GUIDE.md) · [Русский](USER-GUIDE.ru.md)**

NUMA Control assigns selected Windows logical processors to game processes and reapplies the rule when needed. One tray controller manages your profiles, chooses its own cores and monitors its health. It does not allocate NUMA memory, disable SMT in firmware or reserve cores for Windows. Higher FPS or lower latency is not guaranteed; compare frame times and stability in your own games.

> **Anti-cheat warning:** Tested only with Valve Anti-Cheat (VAC). Compatibility with other anti-cheat systems has not been tested or confirmed. VAC testing is not certification or a guarantee against restrictions by the game. Check the rules of your game before using process affinity or priority tools.

NUMA Control uses documented Windows APIs to query processes and change their CPU affinity ([SetProcessAffinityMask](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask)) and, optionally, priority ([SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)). It does not inject DLLs or code, read or modify game memory, patch game files or bypass anti-cheat. It opens process handles for these Windows operations; this does not imply approval by an anti-cheat provider.

## Requirements and installation

Requires **Windows 10/11 x64**, **.NET Framework 4.8** and **one processor group containing 1–64 logical processors**. The modern .NET SDK, Python, Node and source code are unnecessary for normal use. Unsupported multi-group systems cannot apply game masks.

Download from [GitHub Releases](https://github.com/mrPortnov/NUMA-Control/releases). Use one distribution at a time.

| Distribution | Installation |
|---|---|
| `NUMA-Control-1.7.0-setup-x64.exe` | Recommended. Detects .NET Framework 4.8 or later; if missing, downloads the official Microsoft web installer and installs the prerequisite before the application MSI. The runtime may require internet, administrator approval and a restart. Existing 4.8/4.8.1 is skipped. |
| `NUMA-Control-1.7.0-setup-x64.msi` | Per-user application installation. Requires .NET already installed; does not download it. Default destination: `%LOCALAPPDATA%\Programs\NUMA Control`. Start-menu shortcuts are created; the application desktop shortcut is optional. |
| `NUMA-Control-1.7.0-portable-x64.zip` | Extract the whole archive to a stable writable folder. Keep `NUMA-Control.exe.config` beside the EXE. Run `Open-NUMA.cmd` or `NUMA-Control.exe`, outside the ZIP. |

The app, setup EXE and MSI are currently unsigned. Compare downloaded files with `SHA256SUMS.txt` from the same release; the portable ZIP also contains a per-file `SHA256.txt`. Checksums check integrity, not publisher identity. Respect Windows and organizational protection policies.

Close the app through its tray menu before updating, repairing, moving or uninstalling. Windows Installer supports repair and upgrade of the installed version. Uninstall via Windows Settings or the Start-menu uninstall shortcut. It removes program files and shortcuts owned by that copy, including recognized autostart/game shortcuts, while preserving user-generated `data`. Shortcuts belonging to a different portable copy are preserved. Shared .NET is not removed. Remove retained data separately only if you intend to erase it.

To move a portable installation, move the entire folder including `data`. To move from portable to MSI, close both copies, back up `data`, then copy it to the installed location or use profile export/import. Imported game icons will be located again. Recreate autostart and game shortcuts for the new application path. Do not run different copies with separate libraries simultaneously.

## Starting, hiding and exiting

| Action | Result |
|---|---|
| Open the EXE or portable `Open-NUMA.cmd` | Starts monitoring and opens the library; an existing instance receives the command. The CMD only opens the EXE. |
| `--show` | Opens the window explicitly. |
| `--tray` | Starts without opening the window; used by Windows autostart. |
| `--launch <ID>` | Enables that profile, resumes monitoring and launches its target. Game shortcuts use the permanent profile ID. |
| Double-click the tray icon | Opens the existing window or recreates it. |
| Minimize | Hides the window in the tray; an unsaved CPU selection remains in that hidden window. |
| Maximize | Enlarges the window; press again to restore its size. |
| Close the window | Releases the interface and continues in the tray; prompts about an unsaved CPU selection first. |
| Tray menu → Exit | Stops the controller completely. Already assigned game masks remain until their processes exit. |

WPF/.NET libraries can stay loaded after the window closes. Reopening keeps the selected game and page within the current session. Windows autostart is opt-in.

## Language and navigation

Choose **Settings → Interface language → Русский / English**. On the initial Russian interface, use **Настройки → Язык интерфейса**. The change immediately updates pages, editors, dialogs, tooltips, diagnostics and tray menus and is remembered after restart. Existing data defaults to Russian until changed. User names, paths and older log entries are retained as entered. Native file dialogs and system errors may use the Windows language.

| Control | Purpose |
|---|---|
| Game list | Selects a profile and shows its status. Hover over shortened names to read them fully. |
| Add game | Opens the profile editor. A new library is empty. |
| Library | Shows the selected game, its status and CPU selection. |
| Settings | Language, themes, autostart, polling, profile transfer, CPU and data-location information. |
| Event log | Recent detections, assignments, configuration changes and errors. |
| Self-diagnostics | Resources, controller cores, responsiveness and report actions. |
| Pause / Resume | Stops/resumes assignment of all game masks and priorities. Health monitoring and controller core selection continue. Resume after a failure also acknowledges recovery and clears the failure marker. |
| Bottom status line | Shows the latest action result for about five seconds; a new action restarts the timer. A tooltip exposes long text while visible. |

The upper cards show enabled monitors, physical/logical CPU topology and **cores used by the controller**. A game's process count refers to processes where the rule was applied, not installed games. Controller status is Auto before a game, Dedicated cores when free physical cores exist, or Shared core when falling back to the last physical core. Tooltips show CPU numbers and the reason.

Topology is read live from Windows at every startup and recorded in `processor.json`. A saved snapshot does not override detection. A CPU/topology change preserves existing masks but pauses monitoring for review.

Icons come from local EXEs or locally registered Steam game icons and are cached in `data\icons`. Missing or inaccessible icons fall back to initials without blocking profiles. Images are not downloaded.

Use Tab/Shift+Tab to navigate, Space for buttons/toggles, Enter for a dialog's main action and Escape to cancel where supported. Long pages support the wheel, scrollbar and Page Up/Page Down.

## Game card and launching

| Control | Behavior |
|---|---|
| Name and process | Identifies the profile and whether matching uses only the name or an exact path. |
| Monitor | Enables/disables this rule; global pause takes precedence. A failed save restores the persisted switch state. |
| Status | Waiting, assigned, paused, PID or access error. |
| Launch game | Enables the profile and clears global pause. Other enabled profiles resume too. An already matching running game is not launched twice. |
| Desktop shortcut | Creates or updates a `.lnk` using the permanent profile ID. Different profiles with the same display name get distinct shortcuts. |
| Options | Edits or deletes the profile. |

A missing target or unavailable launcher produces a message; monitoring remains enabled and can detect a normal launcher start. Renaming a profile or changing its mask preserves its ID and shortcut. Deleting the profile invalidates its shortcut. Moving the controller requires recreating shortcuts.

The matching **game EXE** and **launch target** are independent. For example, monitor `cs2.exe` while launching `steam://rungameid/730`. Set an exact game path for common filenames. Supported targets include absolute EXE paths and game links for Steam, Astrum, Epic Games, Battle.net and Ubisoft Connect.

A directly launched game can receive its mask at startup. Imported shortcuts and Epic Games, Battle.net and Ubisoft Connect links go through Windows Shell. The observer checks the matching game process as it appears and reports the operation that failed if Windows denies access.

### Launcher games and Fortnite

1. In the launcher, create a desktop shortcut for the game. In NUMA Control, choose the actual game EXE.
2. Choose **Use game shortcut**, open the `.lnk` or `.url`, then review the launch target and arguments before saving.
3. Choose **Launch game**. If Battle.net or another client opens the game page, press **Play** in that client. The observer waits for the game process.

#### Configure Fortnite

1. Open Fortnite from Epic and select `FortniteClient-Win64-Shipping.exe` under **Add game → From running processes**. You can select the same file with **Choose .exe file** while the game is closed.
2. Leave the launch target and arguments blank and choose **Save game**. Existing profiles that use the exact game EXE as their launch target also use the Epic link if there are no launch arguments.
3. In the library, choose the CPUs you want, then choose **Save affinity**. Turn on **Monitor** and resume global monitoring if it is paused.
4. Start the game through Epic or choose **Launch game**. If the local Epic link cannot be found, import the Fortnite shortcut created by Epic or start the game in the client.

For Fortnite, NUMA Control uses **Windows CPU Sets**. Windows records a default CPU selection for threads without their own selection. An existing hard affinity mask still takes precedence. Task Manager's hard affinity view may therefore show all CPUs even when the game's default CPU Sets match your selection. The controller uses the hard affinity mask when selecting its own cores. [Microsoft CPU Sets documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets)

**CPU Sets configured** confirms the selection. If **Review needed** confirms CPU Sets and reports an error setting High priority, the CPU selection was applied and the priority request failed. For CPU selection alone, turn off **High game process priority** in the profile options. Use **All CPUs → Save affinity** with monitoring enabled to broaden the selection. Close and reopen Fortnite to start a fresh process.

The [illustrated walkthrough](https://github.com/mrPortnov/NUMA-Control/blob/main/README.md#first-game-profile) shows the profile editor and CPU map.

## CPU map and saved masks

CPU numbers start at zero and are **Windows logical processor numbers**. Groups show SMT siblings of a detected physical core. On hybrid CPUs, efficiency classes come from Windows. Physical-core labels are ordered by their lowest logical processor; they are not permanent BIOS IDs.

Highlighted CPUs are allowed for the game. Clicking changes a draft and updates the displayed hexadecimal mask/count. The hex value is informational, not a text input.

| Preset | Selection |
|---|---|
| All CPUs | Every active logical processor. The controller may then share the last physical core with the game. |
| Without edge cores | Excludes the first and last physical cores, including all SMT siblings; keeps all CPUs when there are two or fewer physical cores. |
| One thread per core | The first Windows logical processor in each physical core. This does not rank siblings or disable SMT system-wide. |

Adjust presets manually if needed. New profiles use Without edge cores when more than two physical cores exist; existing masks are preserved. Save is unavailable when nothing changed, no CPU is selected or the topology is unsupported. Empty/incompatible masks are rejected. Saving while paused or with Monitor off stores the mask; assignment waits until monitoring is enabled.

Leaving an edited selection prompts to save, discard or cancel navigation. A failed save keeps the draft. **Pausing, switching Monitor off, deleting a profile and exiting do not undo an already assigned game mask.** Apply All CPUs explicitly while monitoring to broaden the running game's mask, or restart the game. Excluded cores are not reserved for Windows, drivers or other processes.

### Cores used by the controller

The controller uses the union of running known games' **actual** affinity masks. If any SMT thread is available to a game, its entire physical core counts as occupied. The controller selects all threads on the remaining free physical cores. With none free, it uses the last physical core and reports the overlap. This is based on allowed masks, not measured load percentages.

Actual masks are still read while rules are paused or disabled; unreadable protected processes use the available fallback information. Deleting a profile stops assigning its game rule but an already known running game can remain tracked for controller core selection until it exits. After the last known game ends, the controller restores its own original affinity.

## Adding and editing profiles

Up to **100 profiles** can be stored. Choose cores on the library page after saving the profile editor.

| Field/action | Meaning |
|---|---|
| Display name | Up to 80 characters. |
| Process name | Executable name without `.exe` or a directory; file/process selection fills it automatically. |
| Exact game EXE path | Optional additional match. Leave empty for name-only matching; recommended for common names. |
| Launch target | Full EXE path or supported URI. For Fortnite, the empty target uses the local Epic link. Other profiles fall back to their game path. |
| Launch arguments | Used for EXE targets only. Maximum 4,096 characters. |
| High process priority | Requests High using Windows APIs. Enabled for new profiles; older profiles without this field keep their former behavior. RealTime is never used. |
| Automatically monitor | Initial/current value of the profile's Monitor switch. |
| Save game / Enter | Validates fields, overlapping rules and disk writes. On failure, the editor and entered data remain open. |
| Cancel / Escape / close | Discards editor changes. |
| Delete profile | Requires confirmation; removes the library entry, leaving game files and existing desktop shortcuts. Cancel keeps the editor. |

Two profiles with the same process name require different exact paths. Overlapping name/path rules are rejected to prevent competing assignments.

High priority can reduce other applications' responsiveness under load and does not guarantee higher FPS. Turning it off, pausing or disabling Monitor stops future assignments; an already assigned High priority remains until process exit or manual change in Task Manager.

### Running-process picker

Search filters the displayed name, PID and available path without case sensitivity. Refresh keeps the filter but clears selection. Select a row and choose Add selected, or double-click; Cancel returns without changing the editor. A PID is not saved as a matching rule. If a protected path is unavailable, select by name and enter the exact path manually afterward.

Windows Open/Save dialogs confirm a path or cancel the operation; they are not installation or elevation prompts.

## Settings and all four themes

| Theme | Appearance |
|---|---|
| Standard | Default dark navy surfaces and violet accents. |
| Windows 11 Light | Light surfaces and blue accents. |
| Windows 11 Dark | Neutral dark surfaces and light blue accents. |
| AMI BIOS | Grey/blue palette, square controls and Consolas throughout; small labels are one point larger. |

The saved theme updates application windows, controls, navigation, window/tray/menu icons and shortcuts pointing to this copy. It preserves language, profiles and masks. The EXE icon in Explorer stays the embedded standard icon. System file dialogs follow Windows styling. Theme screenshots are in the [English repository README](https://github.com/mrPortnov/NUMA-Control#themes).

| Setting | Behavior |
|---|---|
| Start with Windows | Creates/removes a current-user Startup shortcut pointing directly to the EXE with `--tray`. It does not elevate privileges. |
| Polling: 1 / 2 / 5 seconds | Sets game-process polling in idle and active states; default is 2 seconds. Older custom intervals remain until changed. An empty idle library may wait longer, up to about 30 seconds; changes wake the monitor. |
| Export profiles | Writes the library and settings to JSON. Includes paths and launch arguments; review before sharing. |
| Import / restore | Validates JSON/schema, language, masks, overlap and CPU availability; asks before replacement, backs up the former version and pauses monitoring. Current interface language and theme are retained. A `.bak` file can also be selected. |
| About | Version, processor snapshot path and application information. |

After moving the application, turn autostart off and on and recreate game shortcuts. A **Previous monitoring** card appears only when an old watcher or Startup shortcut is detected. After confirmation, disabling it sends the normal stop signal and backs up recognized shortcuts in `legacy-backup`; it does not delete old EXEs or enable new autostart. Refresh updates detection. Unknown targets, including old PowerShell shortcuts, need manual handling using the old distribution's controls.

## Event log

Shows up to 250 recent records, newest first, with an empty-state message when appropriate. Refresh reloads the view. The local file rotates at approximately 1 MiB, keeping the previous file. Paths and error details may be recorded, so inspect the log before sharing it.

## Self-diagnostics and reports

Read diagnostics after it has collected samples. CPU needs an initial interval; a private-memory trend needs about a minute. Refresh updates available readings, at most once per second, rather than creating missing history.

| Block | Interpretation |
|---|---|
| Memory | Private memory, working set and managed .NET memory, in MiB; private-memory change over roughly one minute. A positive change alone is not proof of a leak. |
| CPU | Latest approximately five-second CPU interval and an average over available history, up to about 30 seconds; percentages for one logical CPU and the whole machine. Zero while idle is valid. |
| Handles and threads | Process handle/thread counts and enabled/total profile counts. |
| Controller cores | Chosen logical CPUs, free physical cores or shared-core fallback and its reason. |
| Responsiveness | UI/engine heartbeat age, engine alive state, consecutive errors, latest scan/cycles and uptime. Heartbeat age is not mouse-input latency. |
| Save report | Writes a manual JSON snapshot without exiting or creating a fatal marker. |
| Open reports folder | Opens the local folder in Explorer; nothing is uploaded. |

CPU example: 3 CPU-seconds over 5 wall-seconds is **60% of one logical CPU**, or **3.75% of a 16-CPU machine**. The denominator is the machine's total logical CPU count, not the controller's current affinity count. Threads can migrate. Task Manager comparisons need the same period and whole-machine denominator. Opening WPF windows can create short bursts; averages and the protection policy's current interval are different readings.

### Automatic protection

These are built-in detection thresholds, not target resource consumption:

| Condition | Trigger |
|---|---|
| Private memory | At least 512 MiB continuously for 30 seconds. |
| Working set | At least 768 MiB continuously for 30 seconds. |
| Handles | At least 6,000 continuously for 30 seconds. |
| CPU, hidden window | At least 50% of one logical CPU for 60 seconds. |
| CPU, visible window | At least 90% of one logical CPU for 120 seconds. |
| UI heartbeat | Older than 45 seconds. |
| Required monitor engine | Unexpectedly stopped or heartbeat older than 90 seconds. |
| Repeated engine errors | Five consecutive cycle errors. |
| Controller affinity failure / unhandled exception | Fatal self-affinity failure or an unhandled UI/tray/thread/task exception. |

A separate leak heuristic uses a five-minute history after a one-minute warm-up. It looks for at least 96 MiB of private-memory growth or 2,000 additional handles, with growth across at least 60% of adjacent samples. A one-time WPF allocation is not by itself a leak. Window-visibility changes reset the trend warm-up; long sampling gaps, such as suspend/resume, clear sustained counters and restart the history grace period.

Ordinary invalid input, a missing EXE, a rejected profile file or expected access/write errors show a user message rather than automatically treating the whole application as failed.

### Failure and next launch

For a detected critical failure, the controller stops monitoring, attempts to save a report and `last-failure.json`, and closes. Restart it manually. The next launch remains paused so you can review the cause and profiles; Resume clears the marker while retaining reports.

The bounded JSON includes the reason/exception, app/Windows/CPU information, masks, a configuration summary, resource history and log tail. It does not contain a memory dump, game memory, a complete profile export or launch arguments. Exceptions and logs can still expose local paths and process details. Review every report before sending it.

Reports normally use `data\bug-reports`. If writing fails, `data\recovery\<id>\bug-reports` beside the EXE and then `%TEMP%\NumaControl-recovery\<id>\bug-reports` are tried. Up to 20 manual/failure reports are retained together; the failure marker is separate. Detection is heuristic and cannot cover every bug. Power loss, forced termination or a failed disk may prevent a report.

## Data, backup and recovery

The default is **`data` beside the EXE**, with `%LOCALAPPDATA%\NumaControl` as fallback if that directory is not writable. Settings shows the effective location. A first portable start can copy earlier AppData data if no local profiles exist, leaving the original intact.

| File/folder | Purpose |
|---|---|
| `profiles.json` | Profiles and settings, including language, theme, polling and priority options. |
| `profiles.json.bak` | Previous valid saved configuration. |
| `processor.json` | Live-detected CPU topology snapshot. |
| `watcher.log`, `watcher.log.previous` | Current/previous rotating event log. |
| `icons/` | Local game icons and theme-specific controller icons. |
| `bug-reports/` | Manual/failure JSON reports and `last-failure.json`. |
| `legacy-backup/` | Recognized previous-monitor shortcuts, when migration is used. |
| Corrupt/recovery copies | Preserved source files when configuration recovery is necessary. |

Close the app before copying the whole data folder as a backup. A backup of the previous valid save is retained. Corrupt JSON is preserved instead of silently overwritten; an available valid backup is recovered and monitoring is paused. If both copies are unusable, preserve them, recover/import a known-good copy and review the library before resuming. Imported unavailable CPUs are rejected; existing masks after a CPU change can be edited while paused.

The app works offline and does not automatically send reports. Optional setup downloads .NET, and launching a game/URI delegates to the installed game client, which has its own network behavior. Windows Installer registration and explicitly requested Startup/desktop shortcuts use the normal Windows locations; those are separate from app data.

## Troubleshooting and safe sharing

| Problem | What to check |
|---|---|
| Access denied | The game may be elevated or protected. If needed, exit the ordinary controller and run it at the same elevation. An administrator can still be denied by game protection. |
| Incompatible mask / changed CPU | Review logical CPUs and save a valid mask before resuming. Multi-group systems are unsupported. |
| Launcher target does not open | Check the installed client and import a fresh game shortcut. You can start the game from the client while monitoring waits. |
| Shortcut no longer works | Check for a deleted profile or moved application and recreate it. |
| Unexpected resource use | Inspect diagnostics/logs and polling rate; distinguish warm-up, interface measurement overhead and sustained growth. |
| Windows blocks the package | Check release origin and checksum. Unsigned software may be blocked by Smart App Control or organizational rules; the app does not change those protections. |

NUMA Control does not inject DLLs, read/modify game memory or bypass anti-cheat. Anti-cheat compatibility is not certified.

For an [issue](https://github.com/mrPortnov/NUMA-Control/issues), provide version, Windows version, physical/logical CPU count, minimal steps, expected/observed behavior and the relevant mask. Remove private paths, arguments and unrelated data from any shared export, log or report. Never upload credentials, tokens or personal libraries to the public repository.

See the [quick reference](README.md), [repository](https://github.com/mrPortnov/NUMA-Control) and [release history](https://github.com/mrPortnov/NUMA-Control/blob/main/CHANGELOG.md) for downloads, screenshots and version information.
