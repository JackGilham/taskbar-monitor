#requires -Version 5.1
param([Parameter(Mandatory)] [string]$Dll)
$ErrorActionPreference = 'Stop'
$WarningPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Dll).Path)
$null = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Dll).Path) 'Newtonsoft.Json.dll'))
$passed = 0
function Check([string]$name, [bool]$result) {
    if (-not $result) { throw "FAIL $name" }
    $script:passed++
    "PASS $name"
}
function Map($entries) {
    $map = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $entries.Keys) { $map.Add($key, $entries[$key]) }
    return ,$map
}
function Json($value) { [Newtonsoft.Json.JsonConvert]::SerializeObject($value) }

$options = New-Object TaskbarMonitor.Options
$options.CounterOptions = New-Object 'System.Collections.Generic.Dictionary[string,TaskbarMonitor.CounterOptions]'
$options.EnableOnAllMonitors = $false
$disabled = New-Object TaskbarMonitor.MonitorOptions
$disabled.Enabled = $false
$disabled.Position = [TaskbarMonitor.MonitorOptions+DisplayPosition]::LEFT
$options.MonitorOptions.Add('DISPLAY1', $disabled)
$original = Json $options
Check 'legacy file starts without physical binding' (-not $options.UsePhysicalMonitorIds)
Check 'legacy disabled screen retains its setting' (-not $options.GetMonitorOptions('DISPLAY1', 'PRIMARY-ID').Enabled)
Check 'legacy enabled/right default is retained' ($options.GetMonitorOptions('DISPLAY2', 'SECONDARY-ID').Enabled)
Check 'reading legacy settings does not rewrite them' ((Json $options) -eq $original)

$ids = Map @{ DISPLAY1 = 'PRIMARY-ID'; DISPLAY2 = 'SECONDARY-ID' }
$options.BindMonitorIdentities($ids)
Check 'explicit save enables physical binding' $options.UsePhysicalMonitorIds
Check 'enabled/right screen is saved explicitly' ($options.PhysicalMonitorOptions.ContainsKey('SECONDARY-ID') -and $options.PhysicalMonitorOptions['SECONDARY-ID'].Enabled)
Check 'disabled screen positioning survives binding' ($options.PhysicalMonitorOptions['PRIMARY-ID'].Position -eq 'LEFT')
Check 'secondary stays enabled after display names swap' ($options.GetMonitorOptions('DISPLAY1', 'SECONDARY-ID').Enabled)
Check 'primary stays disabled after display names swap' (-not $options.GetMonitorOptions('DISPLAY2', 'PRIMARY-ID').Enabled)
Check 'identity matching ignores case' ($options.GetMonitorOptions('DISPLAY9', 'secondary-id').Enabled)
Check 'unknown physical screen does not inherit enabled alias' (-not $options.GetMonitorOptions('DISPLAY2', 'NEW-ID').Enabled)
Check 'missing identity stays hidden' (-not $options.GetMonitorOptions('DISPLAY2', $null).Enabled)
Check 'late identity becomes visible without rewriting settings' ($options.GetMonitorOptions('DISPLAY7', 'SECONDARY-ID').Enabled)

$copy = New-Object TaskbarMonitor.Options
$options.CopyTo($copy)
$copy.PhysicalMonitorOptions['SECONDARY-ID'].Enabled = $false
Check 'settings preview uses a deep copy' $options.PhysicalMonitorOptions['SECONDARY-ID'].Enabled
Check 'settings preview copies identity mode' $copy.UsePhysicalMonitorIds
$restored = [Newtonsoft.Json.JsonConvert]::DeserializeObject((Json $options), [TaskbarMonitor.Options])
Check 'save/reload retains selected physical screen' ($restored.GetMonitorOptions('DISPLAY1', 'SECONDARY-ID').Enabled)
Check 'save/reload retains disabled physical screen' (-not $restored.GetMonitorOptions('DISPLAY2', 'PRIMARY-ID').Enabled)

$options.BindMonitorIdentities((Map @{ DISPLAY8 = 'PRIMARY-ID' }))
Check 'saving while target is disconnected preserves it' $options.PhysicalMonitorOptions['SECONDARY-ID'].Enabled
Check 'settings reads return detached values' (& {
    $read = $options.GetMonitorOptions('DISPLAY9', 'SECONDARY-ID'); $read.Enabled = $false
    return $options.PhysicalMonitorOptions['SECONDARY-ID'].Enabled
})
foreach ($invalid in @((Map @{ DISPLAY1 = $null }), (Map @{ DISPLAY1 = 'SAME'; DISPLAY2 = 'same' }), (Map @{}))) {
    $before = Json $options
    $failed = $false
    try { $options.BindMonitorIdentities($invalid) } catch { $failed = $true }
    Check 'invalid identity set is rejected without changing settings' ($failed -and (Json $options) -eq $before)
}
Check 'dialog detects changed display identity' (-not [TaskbarMonitor.MonitorIdentity]::SameDisplays($ids, (Map @{ DISPLAY1 = 'SECONDARY-ID'; DISPLAY2 = 'PRIMARY-ID' })))
Check 'dialog detects disconnected screen' (-not [TaskbarMonitor.MonitorIdentity]::SameDisplays($ids, (Map @{ DISPLAY1 = 'PRIMARY-ID' })))
Check 'dialog accepts identical mapping regardless of case' ([TaskbarMonitor.MonitorIdentity]::SameDisplays($ids, (Map @{ DISPLAY1 = 'primary-id'; DISPLAY2 = 'secondary-id' })))

# Exercise all-monitor saving without constructing a settings preview or touching real configuration.
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$formType = $assembly.GetType('TaskbarMonitor.OptionForm', $true)
$form = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($formType)
$allOptions = New-Object TaskbarMonitor.Options
$options.CopyTo($allOptions)
$allOptions.EnableOnAllMonitors = $true
$beforeAllSave = Json $allOptions
$formType.GetField('Options', $flags).SetValue($form, $allOptions)
$formType.GetField('monitorIdentities', $flags).SetValue($form, (Map @{ 'UNAVAILABLE-DISPLAY' = $null }))
$saved = $formType.GetMethod('PrepareMonitorSave', $flags).Invoke($form, @())
Check 'all-monitor settings can save during identity failure' $saved
Check 'all-monitor fallback preserves prior physical bindings' ((Json $allOptions) -eq $beforeAllSave)

# No manager constructor: do not attach any test controls to Explorer or load real settings.
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$managerType = $assembly.GetType('TaskbarMonitor.TaskbarManager', $true)
$manager = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($managerType)
$list = New-Object 'System.Collections.Generic.List[TaskbarMonitor.Taskbar]'
$managerType.GetField('TaskbarList', $flags).SetValue($manager, $list)
$window = New-Object System.Windows.Forms.Form
$control = New-Object TaskbarMonitor.SystemWatcherControl
try {
    $taskbar = [TaskbarMonitor.Taskbar]::new($true)
    $taskbar.TargetWnd = $window.Handle
    $taskbar.TaskbarMonitorControl = $control
    $list.Add($taskbar)
    $managerType.GetField('MainTaskbar', $flags).SetValue($manager, $taskbar)
    Check 'handle-loss fixture has no marshaling handle' (-not $control.IsHandleCreated -and -not $control.IsDisposed)
    $managerType.GetMethod('UpdatePosition', $flags).Invoke($manager, @($taskbar, $false)) | Out-Null
    Check 'queued position callback tolerates a lost handle' (-not $control.IsDisposed -and -not $control.IsHandleCreated)
    $managerType.GetMethod('RemoveControl', $flags).Invoke($manager, @($taskbar)) | Out-Null
    Check 'lost-handle control is disposed without Invoke' ($control.IsDisposed -and $list.Count -eq 0 -and $null -eq $manager.MainControl)
    Check 'startup retry uses UI timer' ($managerType.GetField('timer', $flags).FieldType -eq [System.Windows.Forms.Timer])
}
finally {
    $control.Dispose()
    $window.Dispose()
}

"ALL $passed MONITOR CHECKS PASSED"
