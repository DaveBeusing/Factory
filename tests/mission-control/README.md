# Mission Control browser tests

Build Factory from the repository root and install the browser test dependency.

```shell
dotnet build Factory.slnx -c Release
cd tests/mission-control
npm install
npx playwright install chromium
npm test
```

Set `FACTORY_BROWSER_CHANNEL=msedge` to use an installed Microsoft Edge instead of Playwright's Chromium. The tests use a temporary Factory home and an ephemeral loopback port. The account quota response is stubbed so the test can run without provider credentials.
