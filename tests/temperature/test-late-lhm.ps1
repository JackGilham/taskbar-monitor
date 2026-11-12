# Startup race: taskbar-monitor comes up before LibreHardwareMonitor's web server.
# The CPU TEMP counter should show "--" first, then pick up readings on its own once the server appears.
param([Parameter(Mandatory)] [string]$Dll, [Parameter(Mandatory)] [string]$Fixtures)
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFrom($Dll)
$bf = [Reflection.BindingFlags]'Instance,NonPublic,Public,DeclaredOnly'

$opt = [TaskbarMonitor.Options]::DefaultOptions()
$opt.LibreHardwareMonitorUrl = 'http://127.0.0.1:18085/amd-zen3.json'
$t = $asm.GetType('TaskbarMonitor.Counters.CounterCPUTemp', $true)
$cpu = [Activator]::CreateInstance($t, [object[]]@($opt))
$t.GetMethod('Initialize', $bf).Invoke($cpu, [object[]]@($null)) | Out-Null

$cpu.Update()
"t=0s   LHM not up yet       -> $($cpu.InfoSummary.CurrentStringValue)"
$before = $cpu.InfoSummary.CurrentStringValue

$server = Start-Process python -ArgumentList '-I', '-m', 'http.server', '18085', '--bind', '127.0.0.1', '--directory', ('"{0}"' -f $Fixtures) -PassThru -WindowStyle Hidden
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $after = $null
    while ($sw.Elapsed.TotalSeconds -lt 15) {
        Start-Sleep -Seconds 1
        $cpu.Update()
        if ($cpu.InfoSummary.CurrentStringValue -match '^\d') { $after = $cpu.InfoSummary.CurrentStringValue; break }
    }
    "t={0:0}s   LHM came up          -> {1}" -f $sw.Elapsed.TotalSeconds, $(if ($after) { $after } else { $cpu.InfoSummary.CurrentStringValue })
    if ($before -match '^--' -and $after -match '^45') { "PASS  recovers on its own once LibreHardwareMonitor starts" } else { "FAIL"; exit 1 }
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
}
