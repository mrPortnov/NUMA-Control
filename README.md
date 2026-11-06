# NUMA Control

**[English](README.md) · [Русский](README.ru.md)**

A Windows desktop application for assigning game processes to selected CPU cores. Create game profiles, choose physical cores and SMT threads, and let a lightweight tray controller reapply your settings when a game starts.

[Download the latest release](https://github.com/mrPortnov/NUMA-Control/releases/latest) · [User guide](NumaControl/USER-GUIDE.md) · [Release history](CHANGELOG.md) · [Report an issue](https://github.com/mrPortnov/NUMA-Control/issues)

![NUMA Control game library and CPU selection, standard theme](docs/screenshots/en-library-standard.png)

> **Anti-cheat compatibility:** Valve Anti-Cheat (VAC) is the only anti-cheat for which hard affinity and process priority have been verified. Fortnite was checked separately with Epic launch and Windows CPU Sets while Easy Anti-Cheat was active. High priority was denied in that check. Check your game's rules before using process scheduling tools.

NUMA Control uses documented Windows APIs to query processes, select their CPUs and request process priority. For Fortnite it uses Windows CPU Sets. It opens process handles for these operations and does not inject code, read or modify game memory, patch game files or bypass anti-cheat. [Windows CPU affinity](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask) · [Windows CPU Sets](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets) · [Process priority](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)

## What it does

Each profile stores the game executable, the selected logical processors and optional launch settings. The controller watches for matching processes and reapplies the CPU selection when necessary.

Affinity can help you experiment with a particular game and CPU configuration. It does **not** guarantee higher FPS or lower latency. Compare frame times and stability in your own workload before keeping a setting.

| Feature | Details |
|---|---|
| Interface language | Switch Russian / English in Settings; applied immediately, remembered after restart, including tray menus and dialogs |
| Game library | Profiles by executable name and optional exact path; matching is independent of temporary process IDs |
| Physical CPU map | Windows topology groups logical processors by physical core, including SMT siblings |
| Manual affinity | Select individual logical processors or use a preset; the resulting hexadecimal mask is visible |
| Background monitoring | Configurable 1, 2 or 5 second polling, per-game monitoring and a global pause |
| Launch and shortcuts | Start a game or supported launcher URI, create a game shortcut and keep the controller in the tray |
| Controller affinity | The application selects its own allowed cores separately from the game's mask |
| Optional priority | A profile can request High priority; RealTime is not used |
| Diagnostics | Memory, CPU, handles, responsiveness and local diagnostic reports |
| Recovery | Backups, profile validation, import/export and pause after recovery |
| Appearance | Standard, Windows 11 Light, Windows 11 Dark and AMI BIOS themes |

## Requirements

- Windows 10 or Windows 11, **x64**.
- **.NET Framework 4.8**. The modern .NET SDK is not required to run the app.
- A supported topology with **one processor group and 1–64 logical processors**. Applying rules is disabled for unsupported multi-group systems.
- A writable application/data location. Administrator rights are usually unnecessary; see the permissions section below.

This is a native Windows C# / WPF application. Linux, macOS and ARM64 builds are not provided. Game profiles are created by the user; a preconfigured personal library is not shipped.

## Install

Get files from [GitHub Releases](https://github.com/mrPortnov/NUMA-Control/releases). Use one distribution at a time.

| Package | Best for | How to start |
|---|---|---|
| `NUMA-Control-1.7.1-setup-x64.exe` | Recommended setup; downloads the official Microsoft .NET Framework 4.8 prerequisite only when missing | Run setup, then complete the application wizard |
| `NUMA-Control-1.7.1-setup-x64.msi` | A regular per-user installation and Windows uninstall/repair support | Run the installer, then launch NUMA Control from Start |
| `NUMA-Control-1.7.1-portable-x64.zip` | Running from an extracted folder without installation registration | Extract the entire ZIP to a writable folder and run `Open-NUMA.cmd` or `NUMA-Control.exe` |
| `SHA256SUMS.txt` | Verifying the downloaded distributions | Compare its hash with `Get-FileHash` |

### Recommended setup and .NET prerequisite

Run `NUMA-Control-1.7.1-setup-x64.exe`. It detects .NET Framework 4.8 or later and skips the download when it is present. Otherwise it downloads the official Microsoft web installer and installs the runtime before the application MSI. Internet access, administrator approval and sometimes a restart are required for the system runtime. Uninstalling NUMA Control does not remove shared .NET.

To update an earlier setup.exe installation, exit NUMA Control from the tray and run the newer setup.exe. The update preserves your profiles and leaves one NUMA Control entry in Windows Installed apps. If you installed the standalone MSI, use the newer MSI to update it.

The separate MSI and portable ZIP require .NET Framework 4.8 to be installed already. They do not download dependencies. The application itself remains offline; network access belongs to the optional prerequisite setup.

### MSI installation

1. Close NUMA Control through its tray menu.
2. Run the MSI and choose a destination. The default is `%LOCALAPPDATA%\Programs\NUMA Control`.
3. Choose whether to add an application shortcut to the desktop. Game shortcuts are created separately inside the app.
4. Complete the wizard and open the app from Start.

The installer is per-user. Repair, upgrade and uninstall use Windows Installer. Uninstall removes the program and its registered shortcuts while preserving user-generated `data`; remove that folder separately only if you want to erase your profiles.

### Portable installation

1. Extract the complete ZIP to a stable writable directory.
2. Keep `NUMA-Control.exe.config` beside the EXE.
3. Run `Open-NUMA.cmd` or the EXE. Do not run directly inside the ZIP.
4. To move your installation, close the tray app and move the whole folder, including `data`. Recreate autostart and game shortcuts afterward.

Keep the application, configuration, launcher and documentation from the ZIP together.

### Verify a download

```powershell
Get-FileHash .\NUMA-Control-1.7.1-setup-x64.exe -Algorithm SHA256
Get-FileHash .\NUMA-Control-1.7.1-setup-x64.msi -Algorithm SHA256
Get-FileHash .\NUMA-Control-1.7.1-portable-x64.zip -Algorithm SHA256
```

Compare the output with the corresponding line in `SHA256SUMS.txt`. The ZIP also contains `SHA256.txt` for its individual files. Checksums verify integrity; the current application EXE, setup EXE and MSI are **not signed with a trusted code-signing certificate**.

## First game profile

1. Start the game once, then choose **Add game → From running processes** and select the game itself. You can also use **Choose .exe file** while the game is closed.
2. Give the profile a recognizable name. Keep the exact path if multiple games use the same EXE name. Save the profile to open its CPU map.
3. Choose a preset or click individual CPU tiles. Use **Save affinity** when the selection is ready, then enable **Monitor**. Resume global monitoring if it is paused.
4. Start the game from its launcher or use **Launch game**. Read the status in the game card. **Waiting for game** means no matching process is active. **Review needed** gives the operation and error to check.

![Game profile editor with separate fields for the game and its launch target](docs/screenshots/en-profile-editor-standard.png)

### Which file or link to use

| Game source | What to enter in the profile |
|---|---|
| Direct Windows EXE | Select the game EXE. Use the optional arguments field for that EXE. |
| Steam | Match the game EXE and enter its Steam link as the launch target. Examples are `cs2.exe` with `steam://rungameid/730` and `dota2.exe` with `steam://rungameid/570`. |
| Epic Games, Battle.net or Ubisoft Connect | Select the game EXE, then choose **Use game shortcut** and import the shortcut created by the client. If it opens the game page, press **Play** in the client. |
| Fortnite | Select the running `FortniteClient-Win64-Shipping.exe` or its file. Leave the launch target and arguments blank to use the Epic link automatically. |

### Fortnite and Windows CPU Sets

Save the selected CPUs in the Fortnite profile and enable monitoring. NUMA Control applies those CPUs using **Windows CPU Sets**. **CPU Sets configured** confirms the default selection for the game process. Threads with their own selections can behave differently, and the game's hard affinity mask may still show all CPUs in Task Manager. See [Microsoft's explanation of CPU Sets](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets).

High priority is a separate request. If **Review needed** confirms CPU Sets and reports that Windows denied priority, the CPU selection has been saved for that running process. To request only the CPU selection, turn off **High game process priority** in the profile options. If Epic launch fails, use a Fortnite shortcut created by Epic or start the game from the client.

## Choosing cores

The numbers on the CPU map are Windows **logical processor numbers**, not a promise that every neighboring pair is a physical core. Grouping comes from the live Windows topology.

| Preset | Meaning |
|---|---|
| **All CPUs** | Allow every active logical processor |
| **Without edge cores** | Exclude the first and last physical core, including their SMT threads; keep all CPUs when there are two or fewer physical cores |
| **One thread per core** | Select the first Windows logical processor of each physical core; SMT is not disabled system-wide |

You can adjust any preset manually. Empty masks and incompatible masks are rejected. Existing saved masks are not silently replaced by a new preset. On supported hybrid CPUs, efficiency classes come from Windows rather than assumed P/E labels.

![CPU map with a changed selection and the Save affinity button](docs/screenshots/en-cpu-selection-standard.png)

For example, if Windows displays eight two-thread cores in order, **Without edge cores** leaves logical CPUs 2–13 selected. Check the groups on your own CPU map before applying the same selection. The hexadecimal value shows which CPU tiles are selected.

Pausing, exiting, deleting a profile or turning monitoring off leaves the running game's existing CPU assignment in place. To broaden it while the game is running, apply **All CPUs** with monitoring enabled. The controller chooses its own cores based on the games' hard affinity masks, including when a Fortnite profile uses CPU Sets.

## Language, tray and diagnostics

Change the language at **Settings → Interface language → Русский / English**. On the default Russian interface, use **Настройки → Язык интерфейса**. Existing profiles keep Russian until you choose English. The choice is stored in the profile settings; profile names and existing log entries are not translated. Windows-provided file dialogs and system error messages can follow the Windows display language.

Closing the main window keeps the application running in the tray. Use **Exit** in its tray menu to stop it completely. Autostart is opt-in and starts the app in the tray for the current Windows user.

![Settings: opt-in autostart, appearance and polling](docs/screenshots/en-settings-standard.png)

Self-diagnostics shows process resources and responsiveness. CPU use expressed as one logical CPU and as a share of the whole machine are different measurements. Reports are written locally when requested or after a detected failure. Review a report before sharing it: it may contain local paths and machine/process information.

![Self-diagnostics with real collected metrics, standard theme](docs/screenshots/en-diagnostics-standard.png)

*The memory trend appears after about a minute. A zero CPU reading while idle is normal.*

Open **Event log → Refresh** to see the latest operations. For a process access error, find the game by name or PID and read the operation beside it. A CPU Sets confirmation and a priority error can appear together. Copy only the relevant lines if you need help.

## Themes

Choose a theme at **Settings → Appearance**. The selection is saved and updates application windows, controls, navigation, tray/menu icons and the app's shortcuts. All themes provide the same features and preserve your profiles, language and affinity settings. Windows file dialogs keep the system appearance; the EXE file icon in Explorer stays the embedded standard icon.

| Theme | Appearance |
|---|---|
| **Standard** | Dark navy surfaces and violet accents; the default theme used throughout this README |
| **Windows 11 Light** | Light surfaces and blue accents |
| **Windows 11 Dark** | Dark neutral surfaces and light blue accents |
| **AMI BIOS** | Grey surfaces, saturated blue outlines, square controls and Consolas typography |

| Standard | Windows 11 Light |
|---|---|
| ![Standard theme](docs/screenshots/en-library-standard.png) | ![Windows 11 Light theme](docs/screenshots/en-library-light.png) |

| Windows 11 Dark | AMI BIOS |
|---|---|
| ![Windows 11 Dark theme](docs/screenshots/en-library-dark.png) | ![AMI BIOS theme](docs/screenshots/en-library-bios.png) |

## Data, backup and privacy

The normal data directory is **`data` beside the EXE**. If that location is not writable, the fallback is `%LOCALAPPDATA%\NumaControl`. Settings displays the location actually in use.

| File or folder | Purpose |
|---|---|
| `profiles.json` / `profiles.json.bak` | Profiles, settings and the previous valid save |
| `processor.json` | Local CPU topology snapshot, refreshed from Windows |
| `watcher.log` | Rotating local event log |
| `icons/` | Cached game icons and application graphics |
| `bug-reports/` | Manual and crash diagnostic reports |
| `legacy-backup/` | Backed-up known shortcuts during migration, if applicable |

To move profiles to another PC, use **Settings → Export profiles** on the old PC. Use **Import profiles** on the new PC, review each EXE path and CPU selection, then resume monitoring. For a full backup on the same PC, close NUMA Control through the tray and copy its data folder. Exports include local executable paths and launch arguments, so review them before sharing.

Profiles, logs and reports stay on your computer and are not sent automatically. Review reports and exports before sharing: they may contain local paths and process information.

## Permissions and troubleshooting

| Situation | What to check |
|---|---|
| The game is never detected | Use its actual EXE name; confirm the exact path if one is set; enable monitoring and clear global pause |
| Access denied | A protected or elevated game may require the controller to run at the same elevation; exit the ordinary instance first |
| Anti-cheat still denies access | Respect the restriction; the application does not bypass it and does not claim anti-cheat certification |
| Unexpected CPU usage | Check the event log, polling rate and diagnostics |
| CPU was replaced | Review paused profiles and masks against the new topology before resuming |
| Data is in an unexpected folder | Check the effective data path in Settings and whether the EXE directory is writable |
| A moved shortcut stops working | Recreate the shortcut and autostart entry from the new application folder |

Do not disable Windows security features to run the application. Unsigned packages may be restricted by Smart App Control or organizational policies.

## Documentation

[Detailed user guide](NumaControl/USER-GUIDE.md) · [Quick reference](NumaControl/README.md) · [Changelog](CHANGELOG.md)
