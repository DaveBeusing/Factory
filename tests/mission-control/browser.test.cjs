const assert = require('node:assert/strict');
const { execFileSync, spawn } = require('node:child_process');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
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

const root = path.resolve(__dirname, '../..');

function browserTest(name, exercise, seed = async () => {}) {
  test(name, { timeout: 60_000 }, () => withDashboard(exercise, seed));
}

async function withDashboard(exercise, seed) {
  const dll = path.join(root, 'src/Factory/bin/Release/net10.0/Factory.dll');
  const home = await mkdtemp(path.join(tmpdir(), 'factory-browser-'));
  let server, stopped, browser;
  try {
    await seed(home);
    server = spawn('dotnet', [dll, 'dashboard', '--listen', 'http://127.0.0.1:0'], {
      cwd: home,
      env: { ...process.env, FACTORY_HOME: home, 'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' },
      stdio: ['ignore', 'pipe', 'inherit']
    });
    stopped = new Promise((resolve) => server.once('close', resolve));
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
    const nav = page.getByRole('navigation', { name: 'Primary navigation' });
    const form = page.locator('#form-modal');
    const inspector = page.getByRole('complementary', { name: 'Details' });
    const api = async (resource, options = {}) => {
      const response = await page.request.fetch(`${url}/api/${resource}`, {
        ...options, headers: { Authorization: `Bearer ${apiToken}` }
      });
      if (!response.ok()) assert.fail(`${options.method || 'GET'} ${resource}: ${response.status()} ${await response.text()}`);
      return response.json();
    };
    await exercise({ page, nav, form, inspector, api, home, connection });
    assert.deepEqual(errors, []);
  } finally {
    try {
      await browser?.close();
    } finally {
      server?.kill();
      await stopped;
      await rm(home, { recursive: true, force: true });
    }
  }
}

browserTest('connects through the sidebar and opens planning and capacity views', async ({ page, nav, form, connection }) => {
  await page.locator('#brand-connection i.connected').waitFor();
  await page.getByRole('button', { name: 'Connection settings, connected' }).click();
  await connection.getByRole('button', { name: 'Close dialog' }).click();

  assert.equal(await page.locator('#top-create').textContent(), '');
  await page.locator('#top-create [data-icon="plus"] svg').waitFor();
  assert.equal(await page.locator('#top-create + #open-search').count(), 1);
  assert.equal(await page.locator('.topbar #open-connection, .topbar .connection-pill').count(), 0);
  await page.getByRole('button', { name: 'Create new' }).click();
  await form.getByRole('button', { name: 'New Goal' }).waitFor();
  await form.getByRole('button', { name: 'Cancel' }).click();

  await nav.getByRole('button', { name: 'Project review' }).click();
  await page.getByText('No registered projects', { exact: true }).waitFor();
  await nav.getByRole('button', { name: 'Goal planner' }).click();
  await page.getByRole('heading', { name: 'Goal planner' }).waitFor();
  await nav.getByRole('button', { name: 'Capacity' }).click();
  await page.getByRole('heading', { name: 'Provider capacity' }).waitFor();
});

browserTest('creates a goal from project findings and rejects adoption of a stale plan', async ({ page, nav, form, inspector, api, home }) => {
  const repo = path.join(home, 'repository');
  await mkdir(repo);
  const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', timeout: 10_000 }).trim();
  git('init', '-b', 'master');
  await writeFile(path.join(repo, 'sample.txt'), 'Fixture source\n');
  git('add', 'sample.txt');
  git('-c', 'user.name=Factory Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false',
    '-c', 'core.hooksPath=' + path.join(home, 'no-hooks'), 'commit', '-m', 'Initial fixture');

  await nav.getByRole('button', { name: 'Project review' }).click();
  await page.getByRole('button', { name: 'Register project' }).click();
  await form.getByLabel('Project name').fill('Browser project');
  await form.getByLabel('Git repository root').fill(repo);
  await form.getByRole('button', { name: 'Register project' }).click();
  await form.waitFor({ state: 'hidden' });
  await page.getByRole('button', { name: 'Analyze project' }).click();
  await page.getByRole('heading', { name: 'Review findings' }).waitFor();
  const finding = page.locator('.entity-card').filter({ hasText: 'No root README.md detected' });
  await finding.getByRole('button', { name: 'Review & create draft goal' }).click();
  await form.getByRole('button', { name: 'Create draft goal' }).click();
  await form.waitFor({ state: 'hidden' });
  await inspector.getByRole('button', { name: 'Close inspector' }).click();

  const state = await api('state');
  const [goal] = state.goals;
  assert.equal(state.goals.length, 1);
  assert.equal(goal.projectId, state.projects[0].id);
  assert.equal(goal.state, 'draft');
  const review = await api(`projects/${goal.projectId}/analysis`);
  assert.equal(review.commit, git('rev-parse', 'master'));

  await page.getByRole('button', { name: 'Build plan' }).click();
  await inspector.getByRole('button', { name: 'Add package' }).click();
  await form.getByLabel('Package title').fill('Document fixture startup');
  await form.getByLabel('Description / acceptance criteria').fill('Add startup instructions and an example command.');
  await form.getByLabel('Expected file paths, one per line').fill('README.md');
  await form.getByRole('button', { name: 'Save changes' }).click();
  await form.waitFor({ state: 'hidden' });
  await inspector.getByRole('button', { name: 'Save plan' }).click();
  const adopt = inspector.getByRole('button', { name: 'Adopt reviewed plan' });
  await adopt.waitFor();

  // Simulate another tab changing acceptance criteria after the plan was reviewed.
  await api(`goals/${goal.id}`, { method: 'PUT', data: {
    projectId: goal.projectId, objective: goal.objective, tokenBudget: goal.tokenBudget,
    acceptanceCriteria: [...goal.acceptanceCriteria, 'Document configuration defaults']
  } });
  page.once('dialog', (dialog) => dialog.accept());
  await adopt.click();
  await page.locator('#toast-stack').getByText(/Goal changed/).first().waitFor();
  assert.equal((await api('state')).jobs.length, 0);

  await inspector.getByRole('button', { name: 'Close inspector' }).click();
  await page.getByRole('button', { name: 'Edit plan' }).click();
  await inspector.getByText('Document configuration defaults', { exact: true }).waitFor();
  await inspector.getByRole('button', { name: 'Save plan' }).click();
  await adopt.waitFor();
  page.once('dialog', (dialog) => dialog.accept());
  await adopt.click();
  await adopt.waitFor({ state: 'hidden' });
  const adopted = await api('state');
  assert.equal(adopted.goals[0].state, 'active');
  assert.equal(adopted.jobs.length, 1);
  assert.equal(adopted.jobs[0].title, 'Document fixture startup');
  assert.equal(adopted.jobs[0].goalId, goal.id);
});

browserTest('recovers a foreign-host hold and settles linked provider and goal budgets', async ({ page, nav, form, inspector, api }) => {
  const { goals: [goal] } = await api('state');
  const budget = () => api(`goals/${goal.id}/budget`);
  await nav.getByRole('button', { name: 'Goal planner' }).click();
  await page.getByRole('button', { name: 'Goal & budget' }).click();
  await inspector.getByRole('button', { name: 'Review linked provider turns in Capacity' }).click();
  await page.getByRole('button', { name: 'Review stopped host' }).click();
  await form.getByLabel(/I verified the original runner/).check();
  await form.getByLabel('Shutdown evidence').fill('Fixture runner shutdown verified by the operator.');
  await form.getByRole('button', { name: 'Mark reservation held' }).click();
  await form.waitFor({ state: 'hidden' });
  const held = await budget();
  assert.equal(held.available, 900);
  assert.equal(held.reservations[0].pendingProviderTurns, 1);
  assert.equal((await api('providers')).heldReservations.length, 1);

  await page.getByRole('button', { name: 'Reconcile', exact: true }).click();
  await form.getByLabel('Verified input tokens').fill('610');
  await form.getByLabel('Verified output tokens').fill('30');
  await form.getByLabel('Independent evidence and justification').fill('Verified late provider report for the fixture turn.');
  await form.getByLabel('I verified the final usage outside this dashboard').check();
  page.once('dialog', (dialog) => dialog.accept());
  await form.getByRole('button', { name: 'Reconcile usage' }).click();
  await page.locator('#toast-stack').getByText(/explicitly confirm the verified overrun/).first().waitFor();
  assert.equal((await budget()).consumed, 120);

  await form.getByLabel(/I verified usage above the reserved tokens/).check();
  page.once('dialog', (dialog) => dialog.accept());
  await form.getByRole('button', { name: 'Reconcile usage' }).click();
  await form.waitFor({ state: 'hidden' });
  const linked = await budget();
  assert.equal(linked.consumed, 640);
  assert.equal(linked.reservations[0].pendingProviderTurns, 0);
  assert.equal(linked.reservations[0].state, 'held');

  await nav.getByRole('button', { name: 'Goal planner' }).click();
  await page.getByRole('button', { name: 'Goal & budget' }).click();
  await inspector.getByRole('button', { name: 'Review held usage' }).click();
  assert.equal(await form.getByLabel('Final input tokens').inputValue(), '610');
  assert.equal(await form.getByLabel('Final output tokens').inputValue(), '30');
  await form.getByLabel('Evidence and review justification').fill('Verified linked ledger totals for the fixture goal.');
  await form.getByLabel('I independently verified the final usage').check();
  await form.getByLabel(/I verified usage above the reserved tokens/).check();
  page.once('dialog', (dialog) => dialog.accept());
  await form.getByRole('button', { name: 'Confirm reconciliation' }).click();
  await form.waitFor({ state: 'hidden' });
  const settled = await budget();
  assert.equal(settled.consumed, 640);
  assert.equal(settled.available, 860);
  assert.equal(settled.reservations[0].state, 'released');
  assert.equal(settled.jobs[0].usageComplete, true);
}, (home) => {
  const fixture = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  execFileSync('dotnet', [fixture, '--browser-recovery-fixture', home], { timeout: 15_000 });
});
