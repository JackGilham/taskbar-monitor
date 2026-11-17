# Persistent monitor selection

Per-monitor settings previously used session display names such as `DISPLAY1`
and `DISPLAY2`. Those names identify display outputs rather than a specific
physical screen. A saved disabled entry could therefore apply to a different
screen when the mapping changed. The periodic taskbar scan also skipped existing
controls, leaving a startup visibility decision unchanged after displays settled.

## Selecting a screen

Open Settings, disable **Enable on all monitors**, select each screen in the
existing display diagram, and set its **Enabled** checkbox as required. Apply or
OK now binds those settings to the monitor device interfaces, including explicit
enabled settings. Reopening Settings reads the same physical selection.

The `UsePhysicalMonitorIds` flag and `PhysicalMonitorOptions` dictionary are
additive configuration fields. Older settings keep their legacy interpretation
until an explicit save. No physical selection is guessed from a legacy file
during startup: reselect the intended screen before that first save if the old
mapping is already wrong. Disconnected physical entries remain saved.

When selective mode is active, an unknown or temporarily unidentified screen is
hidden. Its old display number cannot inherit another monitor's setting. When
the saved monitor becomes available, the next reconciliation can show it again.
**Enable on all monitors** continues to show every taskbar and permits settings
to be saved even if a physical identity is unavailable.

Windows supplies the interface through `EnumDisplayDevices` with
`EDD_GET_DEVICE_INTERFACE_NAME`. This handles changes to the `DISPLAYn` alias;
moving a cable to another GPU or connector can produce a different interface
identity and require reselection. Cloned outputs that cannot be uniquely
identified cannot be newly bound in selective mode. An unsuccessful bind leaves
the previous physical settings intact. See the
[Windows API documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw).

If displays change while Settings is open, selective saving asks you to reopen
Settings rather than binding against an outdated display list.

## Startup and Explorer recovery

A Windows Forms timer reconciles taskbars on the UI thread every four seconds.
It starts even when no taskbar exists yet, rediscovers missing taskbars, removes
stale controls, and refreshes existing controls using the current identity map.
Visibility and position update only when necessary. Losing a child window handle
does not require invoking through that destroyed handle to clean it up. Explorer
destroy callbacks return without blocking retries.

## Validation

Build using `scripts/build.ps1`, then run from the repository root with Windows
PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\monitors\test-monitor-selection.ps1 -Dll .\TaskbarMonitorWindows11\bin\Release\TaskbarMonitor.dll
```

The focused checks cover alias swaps, explicit enabled selections, missing and
late identities, unknown screens, legacy read compatibility, save/reload, copied
settings, disconnected entries, invalid/duplicate bindings, topology changes,
all-monitor fallback, and cleanup/position callbacks after window-handle loss.
They use synthetic settings and isolated controls and do not attach to Explorer
or write the user's configuration. Continue running the temperature tests
described in [the maintenance notes](temperature-maintenance.md).

These tests simulate the identity and handle-loss conditions; they do not reboot
Windows or change the actual display topology.
