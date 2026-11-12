# Maintaining the temperature extension

## Source history

The upstream project is [leandrosa81/taskbar-monitor](https://github.com/leandrosa81/taskbar-monitor).
This fork preserves the changes developed on 8 November 2026 from upstream
commit `4c441ccc0a54a745357988839b7632e7141fe803` (v0.5.4).
Commit `e4ce8b37ddeb17e5c5bb04d2db99060aebd83882` contains the original six-file
temperature extension without rewriting its source. The recovered build's EXE,
TaskbarMonitor DLL, and Newtonsoft.Json DLL matched the installed files by SHA-256
when checked on 12 November 2026.

Keep this original feature commit separate from future build fixes or feature
changes. Runtime downloads, personal settings, screenshots, and installation logs
are not source dependencies and are not included in this fork.

## How it works

- `CounterCPUTemp.cs` reads LibreHardwareMonitor's `data.json`. It prefers AMD
  Tctl/Tdie or Intel CPU Package readings, with other CPU sensors as fallbacks.
  Raw Celsius values take precedence over formatted display units.
- `CounterGPUTemp.cs` queries Windows D3DKMT for the hottest reporting GPU.
  It needs a compatible graphics driver; it does not use LibreHardwareMonitor.
- The shared temperature display uses a 0-100 Celsius graph scale, a numeric
  Celsius label, and `--°C` when no reading is available.
- `Monitor.cs`, `OptionForm.cs`, and `Options.cs` register the counters and migrate
  version-5 settings to version 6, retaining existing graph and monitor settings.
- Taskbar Monitor itself runs without elevation. LibreHardwareMonitor runs
  separately with the privileges needed to read CPU sensors.

## Build status and prerequisites

Use Windows, Visual Studio or Visual Studio Build Tools with the .NET desktop
workload, and the .NET Framework 4.7.2 targeting/developer pack. Build the Windows
11 project from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
```

The script discovers MSBuild using the current PATH or Visual Studio's
`vswhere.exe`; alternatively pass `-MSBuildPath`. It restores from NuGet, builds
Release/x64, and treats compiler and MSBuild warnings as errors. It does not
install or start the application. Output is in
`TaskbarMonitorWindows11/bin/Release`.

A separate maintenance change fixes two inherited issues found on 12 November
2026: the project referenced Newtonsoft.Json 12.0.3 while `packages.config`
declared 13.0.1, and a strict rebuild reported 13 warnings. Both dependency
declarations now use [Newtonsoft.Json 13.0.4](https://www.nuget.org/packages/Newtonsoft.Json/13.0.4).
Unused exception variables and fields were removed, along with a cleanup branch
whose private field was never assigned. Warnings are not suppressed. The
temperature counter implementations are unchanged from the preserved commit.

The Windows 11 project rebuild passes with warnings treated as errors. The
original build and the maintenance build are validated separately using the
tests below; this does not imply a new build was installed on the source machine.

The upstream installer embeds application binaries in
`TaskbarMonitorInstaller/Resources`. Its release batch file refreshes these
resources between builds. Building the solution once is not sufficient to prove
an installer contains the latest temperature code. For maintenance of the
Windows 11 app, start with its project and validate its Release output.

## Runtime setup

The original working setup used LibreHardwareMonitor 0.9.6 and its supported
sensor driver. Obtain LibreHardwareMonitor from its
[official release](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6).
The original `LibreHardwareMonitor.zip` SHA-256 was:

```text
086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001
```

In LibreHardwareMonitor, enable Remote Web Server -> Run, use port 8085, and
enable Start Minimized, Minimize To Tray, and Run On Windows Startup as needed.
The original setup registered LibreHardwareMonitor's logon task with highest
privileges. Its web server listens on all interfaces by default; reading a
loopback URL does not restrict the listener itself. Configure its binding and
firewall for the intended access.

The CPU counter defaults to `http://127.0.0.1:8085/data.json`. To change that
endpoint, exit Taskbar Monitor, back up its settings, and edit
`LibreHardwareMonitorUrl` in
`%LOCALAPPDATA%\Programs\taskbar-monitor\config.json`.

For a future installation update, first back up the existing application files
and settings. Exit Taskbar Monitor and copy the validated Windows 11 Release
output, including the matching Newtonsoft.Json DLL and EXE configuration file,
into the installation directory. Start Taskbar Monitor as the normal desktop
user. Keep its startup entry pointing to that installation directory.

The historical one-machine installation script is not included: it hardcoded
the original scratch path and local account. No reinstall is needed merely to
preserve the source in this fork.

## Validation

The recovered tests and six sensor fixtures are in `tests/temperature`. The
settings migration fixture is synthetic and does not contain a personal monitor
configuration. Tests invoke the migration in memory and do not save application
settings.

Requirements: Windows PowerShell 5.1 (`powershell.exe`, not `pwsh`), Python 3 on
PATH, and a built TaskbarMonitor DLL with its matching Newtonsoft.Json dependency
beside it. `test-counters.ps1` is an integration test that requires a GPU whose
D3DKMT driver reports temperature. It is not suitable as an unconditional test
on a VM or machine without a supported GPU. The tests use local ports 18085 and
18086; ensure both are free and run the scripts sequentially.

From the repository root, set `$dll` to the build being validated:

```powershell
$dll = Join-Path $PWD 'TaskbarMonitorWindows11\bin\Release\TaskbarMonitor.dll'
$fixtures = Join-Path $PWD 'tests\temperature\fixtures'
$config = Join-Path $fixtures 'config-v5.json'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\temperature\test-counters.ps1 -Dll $dll -Fixtures $fixtures -Config $config
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\temperature\test-late-lhm.ps1 -Dll $dll -Fixtures $fixtures
```

Coverage includes AMD/Intel sensor selection, Celsius/Fahrenheit and comma
decimals, absent sensors, defaults, settings migration, live GPU readings,
mock HTTP readings, stale-value expiry, bounded waits, and recovery when the
LibreHardwareMonitor endpoint becomes available after the counter starts.

On 12 November 2026, all 19 counter checks and the additional late-startup check
passed against both the recovered original DLL and the maintenance build. The
maintenance Release build also passed with compiler and MSBuild warnings treated
as errors. No installation was changed during this preservation and build work.

## Existing limitations and upstream updates

- CPU updates may wait up to 300 ms for a fresh HTTP reading on the background
  polling thread, delaying that shared update cycle. Requests time out after two
  seconds, failed requests back off for five seconds, and readings expire after
  ten seconds.
- GPU TEMP is added only when the initial defaults/migration probe succeeds.
  If the graphics driver is unavailable during migration, subsequent starts do
  not automatically add the omitted counter. Recover through a backed-up
  configuration edit or by recreating defaults after the driver is available.
- The existing update checks still point to the upstream project. Installing an
  upstream release can replace this extension. Build this fork when updating.
- The original verification covered the Windows 11 application on the source
  machine. It does not establish Windows 10 deskband or other-GPU compatibility.

Maintain a separate `upstream` Git remote. Review upstream changes before
merging them, rebuild this fork, rerun the tests, and validate the resulting
application before replacing an installed copy.
