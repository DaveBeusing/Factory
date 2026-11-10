# Getting started with Factory

**Install the native tools, build from source, connect Mission Control and run a reviewed first goal.**

[← Documentation](README.md) · [CLI reference](cli.md) · [Configuration](configuration.md)

## Before you start

| Requirement | Purpose | When needed |
| --- | --- | --- |
| Windows x64 | Documented local packaging and scripts | Supported build path |
| .NET 10 SDK | Restore, compile, test and publish | Required |
| Git | Source inspection and isolated worktrees | Required |
| Native Codex CLI | Default provider for planning and builder execution | When using the Codex adapter |
| GitHub CLI (`gh`) | Authorized GitHub inventories, PRs and checks | GitHub-backed workflows |
| Docker | Container-based quality checks | Only if configured |
| Node.js 22 + npm | Playwright browser test harness | Tests only |

Factory's application does **not** require Python, npm or a web frontend build chain. The .NET restore uses NuGet; target repository checks may need their own toolchains.

## Install and clone

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
winget install --id Git.Git --exact --source winget
winget install --id GitHub.cli --exact --source winget

git clone https://github.com/DaveBeusing/Factory.git
Set-Location Factory
```

Open a new shell after installing tools so `PATH` is refreshed. Install and authenticate a supported native Codex CLI independently when that provider is needed. Factory includes an interactive dependency checker:

```powershell
.\Install-Dependencies.ps1
```

> [!CAUTION]
> Review external installers before running them. Do not pipe arbitrary remote scripts into a privileged shell or change machine-wide execution policy just to make an unreviewed installer run. A local policy may block the PowerShell script; organizational policy takes precedence.

Verify the tools you intend to use:

```powershell
dotnet --list-sdks
git --version
codex --version
gh --version
```

Authenticate native tools separately when using their services:

```powershell
codex login
gh auth login
```

## Build and validate

```powershell
.\Build.ps1
```

`Build.ps1` restores the solution, checks local SemVer rules, derives the preview version from first-parent Git history, builds in Release mode, runs the bundled **console test executable**, publishes a framework-dependent Windows x64 package, copies native provider assets and writes `dist/build-manifest.json`. It stops on failed steps.

For isolated compilation diagnostics, you may run:

```powershell
dotnet restore .\Factory.slnx
dotnet build .\Factory.slnx --configuration Release --no-restore
dotnet run --project .\tests\Factory.Tests\Factory.Tests.csproj --configuration Release --no-restore
```

> [!NOTE]
> Individual `dotnet build` commands are useful for troubleshooting but do **not** replace the official `Build.ps1` provenance flow. Use a full Git checkout; shallow history cannot give the same derived preview count. `dist/` is generated, ignored by Git and must not be committed.

To invoke `Factory.exe` by name for the current shell only:

```powershell
$env:PATH = "$(Resolve-Path .\dist);$env:PATH"
Factory.exe --help
```

## Start Mission Control

```powershell
.\dist\Factory.exe doctor
.\dist\Factory.exe install-skills
.\dist\Factory.exe dashboard
```

Visit **http://127.0.0.1:8787**. Connect using `apiToken` from `%USERPROFILE%\.factory\settings.json` (or your configured `FACTORY_HOME`). Mission Control never places the token in a URL; the browser retains it for the current tab session. The sidebar shows the **running Factory build version**, not a hard-coded .NET runtime version.

Use `install-skills --overwrite` only if replacing previously installed Factory skills is intentional. See [Mission Control](mission-control.md) for navigation, access modes and live status indicators.

### When Factory cannot find Codex

```powershell
Get-Command codex -CommandType Application -ErrorAction SilentlyContinue
where.exe codex
codex --version
```

If the CLI is installed outside discovered paths, set its **real executable** before starting Factory:

```powershell
$env:FACTORY_AGENT_EXECUTABLE = 'C:\path\to\codex.exe'
.\dist\Factory.exe doctor
.\dist\Factory.exe usage
```

A graphical app or a PowerShell alias is not a substitute for the native CLI executable. Restart the Factory process after environment changes. The same principle applies to an explicitly configured `FACTORY_CLAUDE_EXECUTABLE` for the read-only Claude Code adapter.

## Configure a target repository

Start with a clean checkout of the target repository's **`master`** branch. Choose quality checks appropriate for that project; a generic template might need editing before use.

```powershell
.\dist\Factory.exe init '<repository-root>' --name 'my-project' --base master --checks-json '.\examples\checks-dotnet.json'
.\dist\Factory.exe status
```

Factory requires executable quality checks for implementation work. Choose from [provided templates and configuration rules](quality.md), and do not assume a successful generic check is adequate proof of application quality.

## Plan, run and verify

1. **Create a goal** with a specific outcome and testable acceptance criteria.
2. **Propose a plan** against the current committed `master`. Inspect the preflight findings, related open issues/PRs, scope, dependency graph, owner and expected checks.
3. **Approve/adopt** only the reviewed revision. The acknowledgement flag must represent a real human review; a missing GitHub inventory is **unknown**, not zero open PRs.
4. **Run** eligible jobs, then inspect checks, reviews, leases and any failures.
5. **Integrate manually** using the repository's branch protection and authorized reviewers.
6. **Verify integration** against the actual destination base before dependent jobs run.

```powershell
.\dist\Factory.exe goal add --project 'my-project' --objective '<outcome>' --criteria '<criterion-one>;<criterion-two>'
.\dist\Factory.exe plan --goal '<goal-guid>'
# Review master, open work, dependencies, paths and evidence before continuing.
.\dist\Factory.exe adopt --goal '<goal-guid>' --plan-revision '<reviewed-revision-guid>' --mode local --reviewed-existing-work
.\dist\Factory.exe run
.\dist\Factory.exe status
# After a separate, authorized merge:
.\dist\Factory.exe verify '<job-guid>'
```

Direct jobs follow the same preflight and scope rules:

```powershell
.\dist\Factory.exe enqueue --project 'my-project' --title '<task>' --description '<acceptance and scope>' --mode local --paths 'src/Area/' --reviewed-existing-work
```

> [!IMPORTANT]
> Never use `--reviewed-existing-work` before checking source and other open work. Worktrees separate source modifications but do not sandbox the agent process. Pushing, PRs and remote workers require additional authorization and setup.

## Continue

- [CLI commands](cli.md) and [Master-first planning](planning-preflight.md)
- [Quality checks and scope enforcement](quality.md)
- [Runtime settings and backups](configuration.md)
- [Remote workers and credentials](remote-workers.md)
- [Recovery runbook](recovery.md)

**Back to:** [Documentation index](README.md)
