# Mission Control browser tests

**Playwright exercises the embedded web UI against a real, temporary Factory controller.**

[← Documentation](../../docs/README.md) · [Mission Control](../../docs/mission-control.md) · [Build guide](../../docs/getting-started.md#build-and-validate)

> [!NOTE]
> Node.js and npm are dependencies of **this browser test harness only**. Factory itself uses .NET 10 and embedded HTML/CSS/JavaScript with no frontend package manager requirement.

## Prerequisites

- .NET 10 SDK with a successful Release build.
- Node.js **22** and npm for the Playwright runner.
- Chromium installed using the Playwright CLI, or Microsoft Edge when explicitly selected.
- Git for repository fixture tests.

## Run locally

From the Factory repository root:

```powershell
dotnet build .\Factory.slnx --configuration Release
Set-Location .\tests\mission-control
npm ci
npx playwright install chromium --only-shell
npm test
```

To use an installed Microsoft Edge instead of the bundled Chromium:

```powershell
$env:FACTORY_BROWSER_CHANNEL = 'msedge'
npm test
```

## Test boundaries and coverage

The test harness starts `src/Factory/bin/Release/net10.0/Factory.dll` using an **ephemeral loopback port** and an isolated temporary `FACTORY_HOME`. It reads the generated token for that fixture, then exercises the actual controller. Account allowance API responses are stubbed so tests do not require provider-account credentials.

Coverage includes:

| Area | Representative cases |
| --- | --- |
| Navigation and connection | Sidebar authentication, event-stream health, workspace switching |
| Global UI | Create shortcut icon alignment, sidebar assembly version, responsive overview actions |
| Planning | Project review, draft goal creation, manual plan adoption, rejection of stale revision |
| Capacity and settings | Conditional empty sections, layout spacing, correct settings action geometry |
| Recovery | Linked provider/goal holds, host transitions, safe reconciliation paths |
| Project access | Scoped identities, controls, permission and recovery workflows as recorded in tests |

> [!IMPORTANT]
> Browser tests are not proof that real provider logins, OS sandboxing, public HTTPS deployment or production GitHub permissions are correctly configured. Keep those checks separate.

## CI and troubleshooting

The [Factory .NET validation workflow](../../.github/workflows/dotnet-validation.yml) runs this suite after the Windows Release build, then checks published assets, build provenance and package install/upgrade/restore. CI results apply to the tested commit, not to local modifications made afterward.

If the suite cannot start the browser, install the Playwright browser shell or select a compatible installed Edge channel. If the controller fails to launch, first run the .NET Release build from the repository root and inspect the test process output without exposing credentials.
