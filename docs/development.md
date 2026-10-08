# Development

Use the .NET 8 SDK or later on Windows x64. The plugin targets .NET 8 and
N.I.N.A.'s 3.2.0.9001 plugin API. Read the [contributor guide](../AGENTS.md)
first.

## Build and test

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
./build-package.ps1
```

`build-package.ps1` writes `artifacts/StarfrontTargetSchedulerCollab-<version>.zip`
and a plugin manifest with its checksum. The archive holds the plugin's two
assemblies, the license and the readme. N.I.N.A. already ships its own
libraries and System.Data.SQLite.

The tests replay Target Scheduler's own schema scripts to build a real
database. The collaboration fixtures are the AstroCollab spec's examples and a
reply captured from a live Starfront server. See
[the fixture notes](../tests/StarfrontCollab.Tests/Fixtures/README.md).

## Interop check

`tools/InteropCheck` runs the core library against a collaboration server on
this computer. It refuses any other server. It registers a telescope, starts a
project, joins it, applies tonight's work to a throwaway Target Scheduler
database, reports two frames and says goodbye.

Start a throwaway Starfront server from a checkout of
[`bray-sfro/starfront`](https://github.com/bray-sfro/starfront):

```powershell
$env:ASTROCOLLAB_DATA = "$env:TEMP\collab-test"
$env:ASTROCOLLAB_ADMIN_TOKEN = [Guid]::NewGuid().ToString('N')
Set-Content "$env:TEMP\collab-test-token.txt" $env:ASTROCOLLAB_ADMIN_TOKEN -NoNewline
python server/run.py --port 8800
```

Then run the check from this repository:

```powershell
dotnet run --project tools/InteropCheck -c Release -- http://127.0.0.1:8800 "$env:TEMP\collab-test-token.txt"
```

## N.I.N.A. smoke test

```powershell
./build-package.ps1
./tools/start-nina-smoke.ps1 -PluginZip artifacts/StarfrontTargetSchedulerCollab-0.1.0.0.zip
```

A test-only startup hook points N.I.N.A.'s data folder at
`artifacts/nina-smoke-<id>`. Profiles, plugins, logs and the Target Scheduler
database live there. The script waits for the plugin to start, prints its log
lines and closes N.I.N.A. Pass `-KeepOpen` to look at the options page.

## Logo

```powershell
dotnet run --project tools/Logo -c Release -- assets/starfront-targetscheduler-collab.png 512
```

## Release

1. Set the version in `packaging/manifest.template.json` and both `.csproj`
   files. Move `docs/releases/unreleased.md` to `docs/releases/<version>.md`.
2. Run the checks and `./build-package.ps1`.
3. Publish the archive as a GitHub release tagged `v<version>`:
   `gh release create v<version> artifacts/StarfrontTargetSchedulerCollab-<version>.zip --notes-file docs/releases/<version>.md`
4. Copy `artifacts/StarfrontTargetSchedulerCollab-<version>.manifest.json` to
   `manifests/s/Starfront TargetScheduler Collab/3.2.0.9001/manifest.json` in
   [`theatrus/nina-plugins-registry`](https://github.com/theatrus/nina-plugins-registry)
   and push to `main`.
5. Check the checksum of the published archive and that the manifest appears
   at `https://nina-plugins.pulsarfab.com/manifests.json`.

Never replace an asset on an existing release. Code signing is not set up yet.

## Validation record

On 2026-10-07:

- 69 unit tests passed, formatting was verified, and the package held the
  expected four files.
- The interop check passed every step against a local Starfront server
  (0.2.23, `bray-sfro/starfront@4cfa896`).
- Against the live Starfront server (0.2.29) with a test telescope, the
  packaged plugin ran in an isolated N.I.N.A. 3.2.0.9001 with Target
  Scheduler 5.9.6 and ASCOM simulators:
  - presence went `offline`, `tracking` at M31, then `exposing`, and cleared on exit
  - tonight's single-target test project became one target and five plans
  - Target Scheduler slewed and shot 2-second frames from those plans
  - every check-in reported the growing accepted count, and the server accepted it
- The screenshots in `docs/images/` come from that run.

Not yet run: a night on real equipment, and the Discord browser sign-in
against a live server. Telescope tokens and enrollment were tested.
