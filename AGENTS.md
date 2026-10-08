# Starfront TargetScheduler Collab contributor guide

This guide is for people and coding agents working in this repository.

## Writing

- Use plain, direct English. Cut words that add nothing.
- Describe what the user gets, not the commit.

## Layout

| Path | Purpose |
|---|---|
| `src/StarfrontCollab.Core/` | Wire client, tonight reader, filter folding, night and Moon arithmetic, Target Scheduler writer and reports. No N.I.N.A. types. |
| `src/StarfrontCollab.Plugin/` | N.I.N.A. manifest, options page, credential store, equipment snapshot and the two loops. |
| `tests/StarfrontCollab.Tests/` | Unit tests over the core, against a Target Scheduler database built from its own schema scripts. |
| `tools/InteropCheck/` | End-to-end check against a collaboration server on this computer. |
| `tools/NinaIsolation/`, `tools/start-nina-smoke.ps1` | Load the packaged plugin in N.I.N.A. with a throwaway data folder. |
| `tools/Logo/`, `assets/` | The logo and the tool that draws it. |
| `docs/images/` | README screenshots from an isolated N.I.N.A. with simulators. |

## Rules

### Protocol

- The server is Starfront's collaboration server (AstroCollab protocol 1).
  Its code has no license: read it to interoperate, never copy it.
- HTTPS only, except plain HTTP to this computer with explicit test consent.
  Never follow a redirect. Bearer tokens only.
- Tokens live only in Windows Credential Manager. Never put one in settings,
  logs, the UI after saving, process arguments or the environment.
- A lost enrollment reply must not be retried: the server may already have
  registered the telescope.
- Report replies are positional. Record nothing unless the whole reply reads.
- Presence is current state only. Never replay an old position.
- Every hello carries the full equipment profile: the server replaces the
  stored profile with whatever arrives.

### Mapping

- `TonightReader` follows the [AstroCollab API spec](https://astrocollabapi.com/)
  and Starfront's server. Keep the spec's examples and the live Starfront
  fixture passing when either changes.
- A share with any hold produces no demands. Never guess past a hold.
- Filter folding ignores case, spaces, dashes, underscores, slashes and a
  trailing bandpass. Template choice: exact wheel name, then same name in
  another case, then the longest default exposure.

### Target Scheduler

- Treat the Target Scheduler schema as a public contract. Require schema 22 or
  later and never create a database that is not there.
- Target Scheduler stores right ascension in hours; the wire uses degrees.
  Convert at the database boundary.
- Write lowercase hyphenated GUIDs on every row created.
- Change only rows recorded in the `starfront_collab_*` tables. Never delete
  rows, including our own. Pause plans instead.
- Preview and Apply run the same transaction; a preview rolls it back.
- Only accepted frames are reported (any frame not rejected when the grader
  is off). Unknown measurements are sent as null.

### Tests

- Never run tests against the real Target Scheduler database, a real
  N.I.N.A. data folder or a shared collaboration server.
- Add or update a regression test for every behaviour change.

## Checks

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
./build-package.ps1
```

See [docs/development.md](docs/development.md) for the interop and N.I.N.A.
smoke tests. State exactly which checks ran in every pull request.
