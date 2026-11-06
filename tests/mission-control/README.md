# Mission Control browser tests

Build Factory from the repository root. Use Node.js 22 and npm for the browser tests.

```shell
dotnet build Factory.slnx -c Release
cd tests/mission-control
npm ci
npx playwright install chromium --only-shell
npm test
```

Set `FACTORY_BROWSER_CHANNEL=msedge` to use an installed Microsoft Edge instead of Playwright's Chromium. The tests use a temporary Factory home and an ephemeral loopback port. The account quota response is stubbed so the test can run without provider credentials.

The suite covers sidebar authentication and navigation, project review through goal creation and manual plan adoption, rejection of a stale plan, and recovery of linked provider and goal reservations after a host change. GitHub runs the suite after the .NET build and tests.
