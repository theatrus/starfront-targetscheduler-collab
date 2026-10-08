# Test fixtures

## AstroCollab examples

These files match the
[AstroCollab API spec](https://github.com/theatrus/astrocollab-api) examples
at commit `35a6f068c6015367b4dbb69c1c4de5b7a0747d90` (line endings aside):

| Fixture | Spec example |
|---|---|
| `starfront-health.json` | `examples/health.response.json` |
| `starfront-hello.json` | `examples/hello.request.json` |
| `starfront-projects.json` | `examples/openProjects.response.json` |
| `starfront-tonight.json` | `examples/tonight.response.json` |

They are synthetic examples with no credentials or private data. The spec's
MIT license is in `ASTROCOLLAB-LICENSE.txt`.

## Live Starfront reply

`starfront-live-tonight.json` is a `GET /api/v1/agent/task` reply from a live
Starfront server (0.2.29) on 2026-10-07, for a test telescope on a test
project. Only the `note` field was changed, to `joined by a test telescope`.
It holds no tokens.

## Target Scheduler schema

`ts_schema/` holds Target Scheduler's database scripts, identical to
`NINA.Plugin.TargetScheduler/Database/Initial/initial_schema.sql` and
`NINA.Plugin.TargetScheduler/Database/Migrate/{1..23}.sql` in
[`tcpalmer/nina.plugin.targetscheduler`](https://github.com/tcpalmer/nina.plugin.targetscheduler)
at commit `17b36a4f8580c687ad18f8b94127d7ca1a2a702e` (line endings aside).
Tests replay them to build a real Target Scheduler database at schema 23.
They remain under the Mozilla Public License 2.0; see `ts_schema/LICENSE`.

No Starfront implementation code is copied into this repository.
