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
  **One exception** (owner, 2026-10-09: "For now I will donate my credits"):
  the AI proofread's Anthropic API key, until the proofread moves to Microsoft
  Foundry and the site's identity. It lives only in the deployment's Key Vault
  (`infra/vault.bicep`), reaches the site only as the App Service Key Vault
  reference in `Proofread__AnthropicApiKey`, resolved with the site's own
  identity, and `CredentialGuard` allows it in that one setting alone. No other
  secret, and no other route for this one.
- **A deleted save's contents leave no record.** No versioning, soft delete,
  change feed, index or storage logging, so they cannot be recovered. The
  activity table does record that a save was deleted, and when, by ids only,
  never the content. Telemetry naming a blob is fine: customer data does not
  live in blob names or metadata.
- **The operator does not read client data.** Clients are kept apart in code
  by Entra: a group per client, and Operator as an app role. The operator manages clients and each
  client's look, but reading a client's drafts, templates, catalog or check policy takes a
  support grant that the client gives. Neelam is the one exception until 1.0, as a standing
  grant. Design: README "Clients and access".
- **Security settings in `infra/main.bicep` are pinned by
  `InfrastructureTests`.** Changing one means changing its test, deliberately.
- **Data over dogma.** Structure lives in data (templates, policy, catalog,
  look), not in code shaped after one client's email; and design follows
  evidence, such as the emails clients actually send, rather than assumed
  lists. Code keeps what a block type guarantees, such as typed benefits.
- **Design calls belong to the owner.** The open decisions in `WIP.md` are
  theirs: bring options with a recommendation, do not settle them.

## Tooling notes

- Build and test .NET through Janet (`dotnet_check` / `janet check`), never bare
  `dotnet build` or `dotnet test`: it refreshes the code graph and returns
  structured results. Use `testFilter` for a narrowed run.
- Bicep CLI and Azure CLI are installed with winget (`Microsoft.Bicep`,
  `Microsoft.AzureCLI`), and `az` uses the same `bicep` from PATH
  (`bicep.use_binary_from_path=true`). Check Bicep with Janet's `bicep_check`
  on both `infra\test.bicepparam` and `infra\main.bicepparam` before any
  `azure_whatif` or `azure_deploy`, and report its result with the what-if.
- `az` needs `az login` before a what-if, a deployment or `janet az token`. The
  owner runs it themselves (`! az login`).
- `gh` is not installed. Find GitHub repos through the REST API.
- Single-person repo: commit on `main` and push it. No branches for review,
  no PRs.
- Commit messages: look at `git log --oneline` and match it -- an imperative
  subject, a prose body on why, and a closing line saying what was verified.
