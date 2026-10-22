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

`StarfrontCollab.Plugin.Tests` also exercises the Advanced Sequencer instruction
and controller with mocked equipment and HTTP replies. It redirects N.I.N.A.'s
data directory before its logger starts and uses throwaway Target Scheduler
databases. These tests cover Apply with background auto-apply disabled,
equipment profile submission, request serialization, cancellation and failures.

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
./tools/start-nina-smoke.ps1 -PluginZip artifacts/StarfrontTargetSchedulerCollab-0.1.2.0.zip
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
On 2026-10-08, for 0.1.1.0:

- 77 unit tests passed.
- The packaged plugin ran against `https://collab.starfront.space` (Starfront
  0.2.35) in an isolated N.I.N.A. in Collab and Hello mode. Hello mode left the
  Target Scheduler database untouched.
- Both hostnames serve the same server. Sign-in links always name
  `collab.starfront.space`, which is why sign-in failed on the old address.
- The screenshots in `docs/images/` come from these runs.

Later on 2026-10-08, for 0.1.2.0:

- 94 unit tests passed.
- In Hello mode with a 5-minute check-in, the server showed the simulated
  mount's state and position within 30 seconds of N.I.N.A. starting, from the
  connect and unpark check-ins, with no slew. 0.1.1.0 had shown `offline` with
  no position until the next check-in. After exit the server showed `offline`.

Not yet run: a night on real equipment. The user confirmed the Discord
sign-in registers a telescope and saves its token.

## Current Starfront compatibility

The API was checked on 2026-10-15 against
[`bray-sfro/starfront@60f87d0`](https://github.com/bray-sfro/starfront/tree/60f87d016c65798ec244b161f722587004af5d18),
whose server reports **0.2.43**, wire protocol **1**. Starfront now publishes
its source under MPL-2.0; no Starfront implementation is copied here.
The upstream repository also supplies its own acquisition plugin under `nina/`.
This plugin remains the independent Target Scheduler adapter; PSF Guard is
not required and the two plugins do not share report ownership.

The loopback interop check covers enrollment with a local owner credential,
full-profile hello, presence, browsing, join, nightly work, TS preview/apply,
contribution reports, duplicate replies and goodbye. All records and frames
are synthetic and stay on the throwaway server. This does not test a real
Discord approval or replace a NINA hardware smoke test.

Readers retain `acceptOscRgb`, `acceptOscNarrowband` and `maxBortle` and hold
work for explicitly disabled OSC bands. Tasks keep the physical filter name
in `filters[].filter`; `filters[].channels` says what data bands land. Known
bands such as `OSC-RGB` and `OSC-Ha+OIII` have stable spelling, not mono credit.
The physical filter still selects the NINA wheel slot and exposure template.
Its channels are saved with the nightly plan and sent in reports, including
delayed reports. A changed same-night channel mapping is held for review.
Existing mono LRGBSHO and the server's inferred colour Clear mapping are
covered by the independent loopback check, including the actual credited band.

The per-profile Bortle selector sends an operator-declared class in full-profile
hello requests. Activation saves it with each nightly mapping in an owned
nullable column; reports use that snapshot rather than today's settings.
Unknown and legacy nights stay unknown. A same-night update keeps the first
rating. The loopback check verifies a Bortle 8 report fails a Bortle 5 limit,
while the saved Bortle 4 report succeeds.

Two extensions need separate work before claiming full feature support:

- **Custom commissioning.** Starfront infers familiar filter names from the
  hello profile. This plugin does not yet offer custom channel maps for filters
  the server cannot infer. These stay unknown.
- **File delivery.** Starfront now has an opt-in project `collectFiles` flag
  and `PUT /api/v1/projects/{id}/files/{name}`. This plugin reports metadata and
  integration totals only; it does not upload FITS/XISF or calibration files
  to Starfront. Reports and file transfer are separate operations.
