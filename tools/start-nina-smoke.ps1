[CmdletBinding()]
param(
    [string]$NinaDirectory = "$env:ProgramFiles\N.I.N.A. - Nighttime Imaging 'N' Astronomy",
    [Parameter(Mandatory)][string]$PluginZip,
    [int]$Seconds = 90,
    [switch]$KeepOpen
)
# Start N.I.N.A. with this plugin in a throwaway data folder, wait until the
# plugin reports that it started, and print what it logged. The real profiles,
# plugins and Target Scheduler database are never touched.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$nina = Join-Path (Resolve-Path -LiteralPath $NinaDirectory) 'NINA.exe'
$zip = (Resolve-Path -LiteralPath $PluginZip).Path

$hookOutput = Join-Path $repo 'artifacts/nina-hook'
dotnet build (Join-Path $PSScriptRoot 'NinaIsolation/NinaIsolation.csproj') --configuration Release --output $hookOutput -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Isolation hook build failed.' }

$root = Join-Path $repo "artifacts/nina-smoke-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$root = (Resolve-Path -LiteralPath $root).Path
$token = [Guid]::NewGuid().ToString('N')
Set-Content -LiteralPath (Join-Path $root '.collab-test-root') -Value $token -NoNewline
$hook = Join-Path $root 'NinaIsolation.dll'
Copy-Item -LiteralPath (Join-Path $hookOutput 'NinaIsolation.dll') -Destination $hook
# Creating the versioned plugin folder also stops N.I.N.A. migrating old plugins in.
$plugins = Join-Path $root 'Plugins/3.0.0/Starfront TargetScheduler Collab'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath $plugins

$start = New-Object System.Diagnostics.ProcessStartInfo $nina
$start.WorkingDirectory = Split-Path $nina
$start.UseShellExecute = $false
$start.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = $hook
$start.EnvironmentVariables['STARFRONT_COLLAB_NINA_ROOT'] = $root
$start.EnvironmentVariables['STARFRONT_COLLAB_NINA_TOKEN'] = $token
$process = [System.Diagnostics.Process]::Start($start)
try {
    $ready = Join-Path $root 'isolation-ready.txt'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath $ready) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (!(Test-Path -LiteralPath $ready) -or (Get-Content -LiteralPath $ready -Raw) -ne $root) {
        throw "N.I.N.A. did not confirm isolation; nothing was loaded from the real data folder. See $root."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $started = $null
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline -and !$started) {
        Start-Sleep -Seconds 2
        $started = Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue |
            Select-String -SimpleMatch 'Starfront TargetScheduler Collab started' | Select-Object -First 1
    }
    $lines = Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -Filter '*.log' -ErrorAction SilentlyContinue |
        Select-String -Pattern 'Starfront TargetScheduler Collab|StarfrontCollab|6687bd68' | ForEach-Object { $_.Line }
    [pscustomobject]@{
        Started = [bool]$started
        ProcessId = $process.Id
        TestRoot = $root
        Log = $lines
    }
    if (!$started) { throw "The plugin did not report that it started within $Seconds s. See the logs under $root." }
} finally {
    if (!$KeepOpen -and !$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
