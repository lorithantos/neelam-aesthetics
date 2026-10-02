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

The projects target `net10.0`, the house standard, and App Service runs .NET 10.

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

- **Managed identity only, and no connection string in GitHub.** Managed
  identity on the server, the developer's own `az login` locally. No account
  keys, SAS or client secrets: `CredentialGuard` refuses them at startup, the
  storage account refuses shared keys, and Azure Policy denies key
  authentication across the resource group. A connection string carrying no
  secret (Application Insights') is fine where nobody would expect it to be
  passed on, such as an App Service setting, but never in anything that reaches
  GitHub: files, history, PRs. `RepositoryTests` scans what git would commit.
- **A deleted save leaves no record.** No versioning, soft delete, change feed,
  index or storage logging. This is about blob history, so a deleted save's
  contents cannot be recovered. Telemetry naming a blob is fine: customer data
  does not live in blob names or metadata.
- **Security settings in `infra/main.bicep` are pinned by
  `InfrastructureTests`.** Changing one means changing its test, deliberately.
- **Design calls belong to the owner.** The open decisions in `WIP.md` are
  theirs: bring options with a recommendation, do not settle them.

## Tooling notes

- Build and test .NET through Janet (`dotnet_check` / `janet check`), never bare
  `dotnet build` or `dotnet test`: it refreshes the code graph and returns
  structured results. Use `testFilter` for a narrowed run.
- Bicep CLI and Azure CLI are installed with winget (`Microsoft.Bicep`,
  `Microsoft.AzureCLI`), and `az` uses the same `bicep` from PATH
  (`bicep.use_binary_from_path=true`). Check Bicep with `bicep build` and
  `bicep lint infra/main.bicep`; Janet does not cover Bicep yet.
- `az` needs `az login` before a what-if, a deployment or `janet az token`. The
  owner runs it themselves (`! az login`).
- `gh` is not installed. Find GitHub repos through the REST API.
- Commit messages: look at `git log --oneline` and match it -- an imperative
  subject, a prose body on why, and a closing line saying what was verified.
