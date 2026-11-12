#requires -Version 5.1
[CmdletBinding()]
param([string]$MSBuildPath)

$ErrorActionPreference = 'Stop'
$WarningPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $MSBuildPath) {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command) {
        $MSBuildPath = $command.Source
    }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
            if ($LASTEXITCODE -ne 0) {
                throw 'Visual Studio discovery failed.'
            }
        }
    }
}

if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'Install Visual Studio Build Tools with .NET desktop build tools and the .NET Framework 4.7.2 targeting pack, or provide -MSBuildPath.'
}

$project = Join-Path $repoRoot 'TaskbarMonitorWindows11\TaskbarMonitorWindows11.csproj'
$packages = Join-Path $repoRoot 'packages'
& $MSBuildPath $project /restore /t:Rebuild /p:RestorePackagesConfig=true "/p:RestoreRepositoryPath=$packages" /p:RestoreSources=https://api.nuget.org/v3/index.json /p:Configuration=Release /p:Platform=AnyCPU /p:TreatWarningsAsErrors=true /warnaserror /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    throw "Restore/build failed with exit code $LASTEXITCODE."
}
