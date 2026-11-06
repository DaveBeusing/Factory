const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, readFile, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { createInterface } = require('node:readline');
const test = require('node:test');
const { chromium } = require('playwright');

function dashboardUrl(child) {
  return new Promise((resolve, reject) => {
    const lines = createInterface({ input: child.stdout });
    const timeout = setTimeout(() => finish(new Error('Dashboard startup timed out')), 15_000);
    const onClose = (code) => finish(new Error(`Dashboard exited during startup (${code})`));
    function finish(error, url) {
      clearTimeout(timeout);
      lines.close();
      child.stdout.resume();
      child.off('close', onClose);
      child.off('error', finish);
      if (error) reject(error);
      else resolve(url);
    }
    child.once('close', onClose);
    child.once('error', finish);
    lines.on('line', (line) => {
      const address = line.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (address) finish(null, address[1]);
    });
  });
}

test('connects through the sidebar and opens planning and capacity views', { timeout: 60_000 }, async () => {
  const root = path.resolve(__dirname, '../..');
  const dll = path.join(root, 'src/Factory/bin/Release/net10.0/Factory.dll');
  const home = await mkdtemp(path.join(tmpdir(), 'factory-browser-'));
  const server = spawn('dotnet', [dll, 'dashboard', '--listen', 'http://127.0.0.1:0'], {
    cwd: home,
    env: { ...process.env, FACTORY_HOME: home, Logging__LogLevel__Microsoft_Hosting_Lifetime: 'Information' },
    stdio: ['ignore', 'pipe', 'inherit']
  });
  const stopped = new Promise((resolve) => server.once('close', resolve));
  let browser;
  try {
    const url = await dashboardUrl(server);
    const { apiToken } = JSON.parse(await readFile(path.join(home, 'settings.json'), 'utf8'));
    assert.ok(apiToken, 'The isolated dashboard should generate an API token');
    browser = await chromium.launch({ channel: process.env.FACTORY_BROWSER_CHANNEL || undefined });
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    page.setDefaultTimeout(8_000);
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    // Account quotas are external. All Mission Control APIs exercised here use the real controller.
    await page.route('**/api/usage', (route) => route.fulfill({ json: {} }));
    await page.goto(url);

    const connection = page.locator('#connection-modal');
    await connection.getByLabel('API token').fill(apiToken);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await page.getByRole('button', { name: 'Connection settings, connected' }).waitFor();
    await page.locator('#brand-connection i.connected').waitFor();
    await page.getByRole('button', { name: 'Connection settings, connected' }).click();
    await connection.getByRole('button', { name: 'Close dialog' }).click();

    assert.equal(await page.locator('#top-create').textContent(), '');
    await page.locator('#top-create [data-icon="plus"] svg').waitFor();
    assert.equal(await page.locator('#top-create + #open-search').count(), 1);
    assert.equal(await page.locator('.topbar #open-connection, .topbar .connection-pill').count(), 0);
    await page.getByRole('button', { name: 'Create new' }).click();
    const createMenu = page.locator('#form-modal');
    await createMenu.getByRole('button', { name: 'New Goal' }).waitFor();
    await createMenu.getByRole('button', { name: 'Cancel' }).click();

    const nav = page.getByRole('navigation', { name: 'Primary navigation' });
    await nav.getByRole('button', { name: 'Project review' }).click();
    await page.getByText('No registered projects', { exact: true }).waitFor();
    await nav.getByRole('button', { name: 'Goal planner' }).click();
    await page.getByRole('heading', { name: 'Goal planner' }).waitFor();
    await nav.getByRole('button', { name: 'Capacity' }).click();
    await page.getByRole('heading', { name: 'Provider capacity' }).waitFor();
    assert.deepEqual(errors, []);
  } finally {
    try {
      await browser?.close();
    } finally {
      server.kill();
      await stopped;
      await rm(home, { recursive: true, force: true });
    }
  }
});
