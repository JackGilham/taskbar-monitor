# Exercises the new temperature counters inside the built TaskbarMonitor.dll.
# Run with Windows PowerShell 5.1 (.NET Framework), e.g.:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File test-counters.ps1 -Dll <path> -Fixtures <dir> -Config <config.json>
param(
    [Parameter(Mandatory)] [string]$Dll,
    [Parameter(Mandatory)] [string]$Fixtures,
    [Parameter(Mandatory)] [string]$Config
)
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFrom($Dll)
$bf = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public,DeclaredOnly'
$failures = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { "PASS  $what  $detail" } else { $script:failures++; "FAIL  $what  $detail" }
}
function New-Counter([string]$typeName, $options) {
    $t = $asm.GetType($typeName, $true)
    $c = [Activator]::CreateInstance($t, [object[]]@($options))
    $t.GetMethod('Initialize', $bf).Invoke($c, [object[]]@($null)) | Out-Null
    $c
}

"== 1. LibreHardwareMonitor JSON parsing =="
$cpuType = $asm.GetType('TaskbarMonitor.Counters.CounterCPUTemp', $true)
$parse = $cpuType.GetMethod('TryReadCpuTemperature', $bf)
$expect = [ordered]@{
    'amd-zen3.json'          = 45.1   # Tctl/Tdie wins over CCD1 and GPU; RawValue (°C) used, not the °F display Value
    'intel.json'             = 61.0   # CPU Package wins; "Distance to TjMax" ignored
    'comma-decimal.json'     = 45.6
    'fahrenheit-no-raw.json' = 45.0   # older LHM without RawValue, display unit °F
    'null-headline.json'     = 44.0   # headline sensor has no value -> falls back to other CPU sensors
    'no-cpu.json'            = $null  # no CPU temperature -> no reading
}
foreach ($name in $expect.Keys) {
    $json = [IO.File]::ReadAllText((Join-Path $Fixtures $name), [Text.Encoding]::UTF8)
    $a = [object[]]@($json, [single]0)
    $ok = $parse.Invoke($null, $a)
    $got = if ($ok) { [math]::Round([double]$a[1], 1) } else { $null }
    Check $name ($got -eq $expect[$name]) "expected=$($expect[$name]) got=$got"
}

"== 2. Options: defaults and v5 -> v6 upgrade =="
$defaults = [TaskbarMonitor.Options]::DefaultOptions()
Check 'defaults include temperature graphs' ($defaults.CounterOptions.ContainsKey('CPU TEMP') -and $defaults.CounterOptions.ContainsKey('GPU TEMP')) (($defaults.CounterOptions.Keys) -join ', ')
$opt = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText($Config), [TaskbarMonitor.Options])
$before = $opt.OptionsVersion
$changed = [TaskbarMonitor.Options].GetMethod('_Upgrade', $bf).Invoke($opt, [object[]]@($null)) # in-memory only, no SaveToDisk
$orders = ($opt.CounterOptions.GetEnumerator() | Sort-Object { $_.Value.Order } | ForEach-Object { "$($_.Key)=$($_.Value.Order)" }) -join ', '
Check "upgrade v$before -> v$($opt.OptionsVersion)" ($changed -and $opt.OptionsVersion -eq 6 -and $opt.CounterOptions['CPU TEMP'].Order -eq 6 -and $opt.CounterOptions['GPU TEMP'].Order -eq 7) $orders
Check 'existing settings kept' ($opt.CounterOptions['CPU'].GraphType -eq 'STACKED' -and $opt.EnableOnAllMonitors -eq $false -and $opt.MonitorOptions.Count -eq 1) "CPU=$($opt.CounterOptions['CPU'].GraphType) allMonitors=$($opt.EnableOnAllMonitors) monitorOptions=$($opt.MonitorOptions.Count)"
Check 'LHM url default' ($opt.LibreHardwareMonitorUrl -eq 'http://127.0.0.1:8085/data.json') $opt.LibreHardwareMonitorUrl

"== 3. GPU TEMP on this machine (D3DKMT) =="
$gpuType = $asm.GetType('TaskbarMonitor.Counters.CounterGPUTemp', $true)
Check 'GPU IsAvailable' ([bool]$gpuType.GetMethod('IsAvailable', $bf).Invoke($null, @())) ''
$gpu = New-Counter 'TaskbarMonitor.Counters.CounterGPUTemp' $opt
$readings = foreach ($i in 1..3) { $gpu.Update(); $gpu.InfoSummary.CurrentStringValue; Start-Sleep -Milliseconds 500 }
Check 'GPU readings' (@($readings | Where-Object { $_ -match '^\d+°C$' }).Count -eq 3) ($readings -join ' ')
Check 'GPU history/graph' ($gpu.Infos[0].History.Count -eq 3 -and $gpu.Infos[0].CurrentValue -gt 0) "history=$($gpu.Infos[0].History -join ',')"
$gpu.Dispose()

"== 4. CPU TEMP over HTTP (mock LibreHardwareMonitor on 127.0.0.1:18085) =="
$server = Start-Process python -ArgumentList '-I', '-m', 'http.server', '18085', '--bind', '127.0.0.1', '--directory', ('"{0}"' -f $Fixtures) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    $opt.LibreHardwareMonitorUrl = 'http://127.0.0.1:18085/amd-zen3.json'
    $cpu = New-Counter 'TaskbarMonitor.Counters.CounterCPUTemp' $opt
    $sw = [Diagnostics.Stopwatch]::StartNew(); $cpu.Update(); $first = $sw.ElapsedMilliseconds
    "      first Update (cold HttpClient): $($cpu.InfoSummary.CurrentStringValue) in ${first}ms"
    Start-Sleep -Milliseconds 500
    $sw.Restart(); $cpu.Update(); $second = $sw.ElapsedMilliseconds
    Check 'reading' ($cpu.InfoSummary.CurrentStringValue -eq '45°C') "value=$($cpu.InfoSummary.CurrentStringValue) in ${second}ms"
    Check 'graph value' ([math]::Abs($cpu.Infos[0].CurrentValue - 45.1) -lt 0.01) "graph=$($cpu.Infos[0].CurrentValue)"

    Stop-Process -Id $server.Id -Force; $server = $null
    Start-Sleep -Seconds 1
    $cpu.Update()
    Check 'keeps last value briefly after LHM disappears' ($cpu.InfoSummary.CurrentStringValue -eq '45°C') $cpu.InfoSummary.CurrentStringValue
    $max = 0
    foreach ($i in 1..11) { Start-Sleep -Seconds 1; $sw.Restart(); $cpu.Update(); $max = [math]::Max($max, $sw.ElapsedMilliseconds) }
    Check 'shows --°C once reading is stale' ($cpu.InfoSummary.CurrentStringValue -eq '--°C') "value=$($cpu.InfoSummary.CurrentStringValue)"
    Check 'Update never blocks long while LHM is down' ($max -le 400) "slowest Update=${max}ms"
}
finally {
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}

$opt.LibreHardwareMonitorUrl = 'http://127.0.0.1:18086/data.json'   # nothing listening
$down = New-Counter 'TaskbarMonitor.Counters.CounterCPUTemp' $opt
$sw = [Diagnostics.Stopwatch]::StartNew(); $down.Update()
Check 'no LHM at all' ($down.InfoSummary.CurrentStringValue -eq '--°C' -and $down.Infos[0].CurrentValue -eq 0) "value=$($down.InfoSummary.CurrentStringValue) in $($sw.ElapsedMilliseconds)ms"

""
if ($failures -eq 0) { "ALL TESTS PASSED" } else { "$failures TEST(S) FAILED"; exit 1 }
