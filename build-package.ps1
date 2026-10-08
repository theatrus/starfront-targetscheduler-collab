[CmdletBinding()]
param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot 'artifacts'))
# Build the N.I.N.A. plugin archive and its manifest. Builds only; installs
# and publishes nothing.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build StarfrontCollab.slnx --configuration Release --no-restore -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $manifest = Get-Content -LiteralPath 'packaging/manifest.template.json' -Raw | ConvertFrom-Json
    $v = $manifest.Version
    $version = "$($v.Major).$($v.Minor).$($v.Patch).$($v.Build)"
    $output = Join-Path $PSScriptRoot 'src/StarfrontCollab.Plugin/bin/Release/net8.0-windows7.0'
    $assembly = Join-Path $output 'Starfront TargetScheduler Collab.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -ne $version) {
        throw "The manifest says $version but the plugin was built as $([Reflection.AssemblyName]::GetAssemblyName($assembly).Version)."
    }

    # Only this plugin's own assemblies: N.I.N.A. already ships its own
    # libraries and System.Data.SQLite, and a second copy would conflict.
    New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
    $stage = Join-Path $ArtifactDirectory "package-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($name in @('Starfront TargetScheduler Collab.dll', 'StarfrontCollab.Core.dll')) { Copy-Item -LiteralPath (Join-Path $output $name) -Destination $stage }
    Copy-Item -LiteralPath 'LICENSE', 'README.md' -Destination $stage
    $archive = Join-Path $ArtifactDirectory "StarfrontTargetSchedulerCollab-$version.zip"
    Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
    Remove-Item -LiteralPath $stage -Recurse -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $actual = @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $expected = @('LICENSE', 'Starfront TargetScheduler Collab.dll', 'StarfrontCollab.Core.dll', 'README.md') | Sort-Object
        if (Compare-Object $actual $expected) { throw "Unexpected files in the plugin package: $($actual -join ', ')" }
    } finally { $zip.Dispose() }

    $tag = "v$version"
    $manifest | Add-Member -NotePropertyName Installer -NotePropertyValue ([pscustomobject]@{
        URL = "https://github.com/theatrus/starfront-targetscheduler-collab/releases/download/$tag/StarfrontTargetSchedulerCollab-$version.zip"
        Type = 'ARCHIVE'
        Checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        ChecksumType = 'SHA256'
    }) -Force
    $manifestPath = Join-Path $ArtifactDirectory "StarfrontTargetSchedulerCollab-$version.manifest.json"
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    [pscustomobject]@{ Tag = $tag; Archive = $archive; Manifest = $manifestPath; Checksum = $manifest.Installer.Checksum }
} finally { Pop-Location }
