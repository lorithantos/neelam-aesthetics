# Neelam Aesthetics -- campaign safety

A tool that stops Neelam Aesthetics' marketing emails going out with mistakes:
templates and drafts, rule checks and an AI proofread as a gate, and an export
to paste into Square Marketing. ASP.NET / Blazor Server on Azure App Service,
Azure Blob Storage, Bicep.

`README.md` is the design. `WIP.md` is where things stand: what the owner has
decided, what is built, and what is still open. **This file does not restate
either.** Read `WIP.md` before starting work.

## Start of session

Run Janet startup yourself from this directory, then read what it lists under
`read` and treat its `rules` as in force:

```powershell
& "$env:JanetBase\scripts\Invoke-JanetStartup.ps1"
```

Its thread report narrows to the `neelam-aesthetics` area, which holds this
repo's backlog. Set `--area neelam-aesthetics` on anything you add. Thread
items are local to this machine; a cloud session cannot see them, so anything a
cloud session must know goes in `WIP.md` as well.

## Build and test

```powershell
janet check --target Neelam.sln     # build + test, structured JSON; exit 0 only if all green
dotnet test Neelam.sln              # fallback without janet
```

The projects target `net8.0` and the solution is a classic `.sln`, because the
cloud sessions run SDK 8. Local SDK 10 builds them as-is; do not retarget.

No test touches the network. Keep it that way: real-service checks are manual
probes, described in `WIP.md`.

## Code graph

Graph first for C# structure. The RazorGraph graph id is `neelam`, built from
`C:\repos\neelam-aesthetics\Neelam.sln` with `build_solution` (about two
seconds). Rebuild after changing C#, and check `builtAt` against your edits.

If the `mcp__razorgraph__*` tools are not in the session, the server is still
reachable over HTTP:

```powershell
& "$env:JanetBase\scripts\Invoke-McpTool.ps1" -Tool build_solution -Arguments @{ path='C:\repos\neelam-aesthetics\Neelam.sln'; graphId='neelam' }
```

## Rules that are the owner's, not preferences

- **No connection strings at all.** Managed identity on the server, the
  developer's own `az login` locally. No account keys, SAS, client secrets or
  connection strings, even ones holding no secret. `CredentialGuard` refuses
  them at startup, the storage account refuses shared keys, and Azure Policy
  denies key authentication across the resource group. Only the server has to
  prove this; local runs need not.
- **A deleted save leaves no record.** No versioning, soft delete, change feed,
  index or storage logging.
- **Security settings in `infra/main.bicep` are pinned by
  `InfrastructureTests`.** Changing one means changing its test, deliberately.
- **Design calls belong to the owner.** The open decisions in `WIP.md` are
  theirs: bring options with a recommendation, do not settle them.

## Tooling notes

- Bicep CLI is not installed here. Download the signed standalone
  `bicep-win-x64.exe` from the Azure/bicep GitHub releases into the scratchpad,
  then `bicep build` and `bicep lint infra/main.bicep`.
- Neither `gh` nor `az` is installed. Find GitHub repos through the REST API;
  for Azure, `janet az token` works only after `az login`.
- Commit messages: look at `git log --oneline` and match it -- an imperative
  subject, a prose body on why, and a closing line saying what was verified.
