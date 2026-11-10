const assert = require('node:assert/strict');
const { execFileSync, spawn } = require('node:child_process');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { createServer } = require('node:http');
const { once } = require('node:events');
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

function browserTest(name, exercise, seed = async () => {}, allowance = {}) {
  test(name, { timeout: 60_000 }, () => withDashboard(exercise, seed, allowance));
}

async function withDashboard(exercise, seed, allowance) {
  const dll = path.join(root, 'src/Factory/bin/Release/net10.0/Factory.dll');
  const home = await mkdtemp(path.join(tmpdir(), 'factory-browser-'));
  let server, stopped, browser, fixture;
  try {
    fixture = await seed(home);
    server = spawn('dotnet', [dll, 'dashboard', '--listen', 'http://127.0.0.1:0'], {
      cwd: home,
      env: { ...process.env, ...fixture?.env, FACTORY_HOME: home, 'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' },
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
    await page.route('**/api/usage', (route) => route.fulfill({ json: allowance }));
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
    await exercise({ page, nav, form, inspector, api, home, connection, fixture });
    assert.deepEqual(errors, []);
  } finally {
    try {
      await browser?.close();
    } finally {
      server?.kill();
      await stopped;
      await fixture?.dispose?.();
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

browserTest('keeps Mission Control layout, actions and version aligned', async ({ page, nav }) => {
  const dll = path.join(root, 'src/Factory/bin/Release/net10.0/Factory.dll');
  const expectedVersion = execFileSync('dotnet', [dll, 'version'], { encoding: 'utf8' }).trim().split('+')[0];
  assert.equal(await page.locator('#sidebar-version-number').innerText(), 'v' + expectedVersion);

  const createGeometry = await page.locator('#top-create').evaluate(button => {
    const outer = button.getBoundingClientRect();
    const glyph = button.querySelector('svg').getBoundingClientRect();
    return {
      width: outer.width, height: outer.height,
      offsetX: Math.abs((outer.left + outer.right - glyph.left - glyph.right) / 2),
      offsetY: Math.abs((outer.top + outer.bottom - glyph.top - glyph.bottom) / 2)
    };
  });
  assert.equal(createGeometry.width, createGeometry.height);
  assert.ok(createGeometry.offsetX < 1 && createGeometry.offsetY < 1, 'Create glyph must be centered');

  assert.equal(await page.locator('.page-head .head-actions').count(), 0, 'Do not render an empty header action group');
  assert.equal(await page.locator('.overview-intelligence').evaluate(element => getComputedStyle(element).marginTop), '17px');
  const runQueue = page.locator('.overview-home-hero [data-act="queue-run"]');
  assert.match(await runQueue.evaluate(element => getComputedStyle(element).backgroundImage), /linear-gradient/);
  await runQueue.hover();
  assert.equal(await runQueue.evaluate(element => getComputedStyle(element, '::before').animationName), 'mc-overview-sheen');

  await nav.getByRole('button', { name: 'Goal planner' }).click();
  assert.equal(await page.locator('.planner-filter-card > .card-body').evaluate(element => getComputedStyle(element).paddingTop), '20px');
  await nav.getByRole('button', { name: 'Capacity' }).click();
  assert.equal(await page.locator('section.card').filter({ hasText: 'Project allocations' }).count(), 0,
    'Unreported project allocations must not create a dead card');
  assert.ok(await page.locator('#main').evaluate(main =>
    [...main.querySelectorAll('.stacked-card')].every(element => element.parentElement === main)),
    'Capacity spacing should not rely on wrapper elements');
  assert.equal(await page.locator('.capacity-note').count(), 1);

  await nav.getByRole('button', { name: 'Settings' }).click();
  assert.equal(await page.locator('.settings-edit-button').evaluate(element => getComputedStyle(element).marginTop), '20px');

  await nav.getByRole('button', { name: 'Queue' }).click();
  const inFlight = page.locator('#main > .stacked-card').filter({ has: page.getByRole('heading', { name: 'In flight' }) });
  assert.equal(await inFlight.count(), 1);
  assert.equal(await inFlight.evaluate(element => getComputedStyle(element).marginTop), '17px');
  assert.equal(await page.locator('#main > section.card').count(), 2, 'Queue must not need spacing-only wrappers');

  await nav.getByRole('button', { name: 'Overview' }).click();
  await page.setViewportSize({ width: 390, height: 780 });
  const fits = await page.locator('.overview-home-hero').evaluate(hero => {
    const bounds = hero.getBoundingClientRect();
    return [...hero.querySelectorAll('.head-actions .button')].every(button => {
      const rect = button.getBoundingClientRect();
      return rect.left >= bounds.left && rect.right <= bounds.right + 1;
    });
  });
  assert.ok(fits, 'Overview actions must remain inside the hero on mobile');
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await runQueue.hover();
  assert.equal(await runQueue.evaluate(element => getComputedStyle(element, '::before').display), 'none');
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
  await inspector.getByText('Weekly: 65% remaining', { exact: true }).waitFor();
  await inspector.getByText('Shared controller Codex account · stale', { exact: true }).waitFor();
  await inspector.getByRole('row', { name: /local-ollama/ }).getByText('Unavailable', { exact: true }).waitFor();
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
}, async (home) => {
  const fixture = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  execFileSync('dotnet', [fixture, '--browser-recovery-fixture', home], { timeout: 15_000 });
  await writeFile(path.join(home, 'settings.json'), JSON.stringify({ providers: [
    { id: 'codex', kind: 'codex', tokenLimit: 3000 }, { id: 'local-ollama', kind: 'ollama', model: 'fixture:small' }
  ] }));
}, { weekly: { usedPercent: 35, remainingPercent: 65 }, stale: true });

browserTest('limits recovery to Budget Reconciler grants and the selected project', async ({ page, form, api, connection, fixture }) => {
  const { reconciler, operatorToken, projectId, otherId } = fixture.access;
  const snapshot = await api('state');
  const providerJob = snapshot.jobs.find((job) => job.mode === 'remote');
  const turn = snapshot.remoteProviderTurns[0];
  const foreignGoal = snapshot.goals.find((goal) => goal.projectId === otherId);
  const foreignHold = snapshot.budgetReservations.find((hold) => hold.goalId === foreignGoal.id);
  const foreignProviderHold = snapshot.providerBudgetReservations.find((hold) => hold.projectId === otherId);
  const unauthorizedPaths = [];
  page.on('request', (request) => {
    const pathname = new URL(request.url()).pathname;
    if (request.headers().authorization?.startsWith('Bearer factory-viewer-') &&
        pathname.startsWith('/api/') && !pathname.startsWith('/api/viewer/')) unauthorizedPaths.push(pathname);
  });
  async function connect(token) {
    await page.locator('#brand-connection').click();
    await connection.getByLabel('API token').fill(token);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await connection.waitFor({ state: 'hidden' });
  }
  const main = page.locator('#main');
  const roleNotice = main.getByRole('heading', { name: 'Budget Reconciler access required' });
  const select = main.getByLabel('Project', { exact: true });
  const settle = main.getByRole('button', { name: 'Settle goal reservation' });
  const settleProvider = main.getByRole('button', { name: 'Settle provider reservation' });
  const recover = main.getByRole('button', { name: 'Recover executor receipt' });
  await connect(operatorToken);
  await roleNotice.waitFor();
  assert.deepEqual(await select.locator('option').allTextContents(), ['Provider evidence fixture']);
  assert.equal(await settle.count(), 0);
  assert.equal(await settleProvider.count(), 0);
  assert.equal(await recover.count(), 0);
  assert.equal(await page.locator('#top-create').isVisible(), false);

  await connect(reconciler);
  await settle.waitFor();
  await settleProvider.waitFor();
  await recover.waitFor();
  assert.deepEqual(await select.locator('option').allTextContents(), ['Provider evidence fixture', 'Read-only project']);
  assert.doesNotMatch(await main.innerText(), /Read-only project goal|Unrelated project|fixture-lease-secret|A{64}/);
  assert.doesNotMatch(await main.innerText(), /foreign-provider/);
  assert.equal(await settleProvider.count(), 1);
  await select.selectOption(otherId);
  await roleNotice.waitFor();
  assert.equal(await settle.count(), 0);
  assert.equal(await settleProvider.count(), 0);
  assert.equal(await recover.count(), 0);
  await select.selectOption(projectId);
  await settle.waitFor();

  const route = (project) => new URL('/api/viewer/projects/' + project, page.url()).href;
  const scopedPost = (path, data) => page.request.post(path, { headers: { Authorization: 'Bearer ' + reconciler }, data });
  const decision = { reviewed: true, reason: 'Verified fixture evidence' };
  const foreignSettlement = await scopedPost(route(projectId) + '/goals/' + foreignGoal.id + '/budget/reconcile',
    { ...decision, reservationId: foreignHold.id, finalTokensIn: 20, finalTokensOut: 0 });
  assert.equal(foreignSettlement.status(), 404);
  const foreignProviderSettlement = await scopedPost(route(projectId) + '/provider-reservations/' + foreignProviderHold.id + '/reconcile',
    { ...decision, finalInputTokens: 20, finalOutputTokens: 0 });
  assert.equal(foreignProviderSettlement.status(), 404);
  const foreignReceipt = await scopedPost(route(otherId) + '/jobs/' + providerJob.id + '/provider-turns/' + turn.id + '/recover', decision);
  assert.equal(foreignReceipt.status(), 403);
  assert.equal(fixture.receiptRequests, 0);

  await recover.click();
  await form.getByLabel('Evidence and review justification').fill(decision.reason);
  await form.getByLabel('I independently reviewed the evidence').check();
  await form.getByRole('button', { name: 'Recover executor receipt' }).click();
  await page.getByText('The configured executor has no durable receipt.').waitFor();
  assert.equal(await form.isVisible(), true);
  fixture.receipt = {
    turnId: turn.id, leaseId: snapshot.remoteProviderLeases[0].id, providerId: 'fixture-provider', role: 'builder',
    inputCommit: '', observedModel: 'requested-v1', inputTokens: 70, outputTokens: 35, finalUsage: true,
    success: false, finalText: '', finalTextIsFallback: false, outputTree: '', patch: ''
  };
  await form.getByRole('button', { name: 'Recover executor receipt' }).click();
  await form.waitFor({ state: 'hidden' });
  await main.getByRole('heading', { name: 'No held executor turns' }).waitFor();

  async function enterTotals(input, output) {
    await form.getByLabel('Verified input tokens').fill(input);
    await form.getByLabel('Verified output tokens').fill(output);
    await form.getByLabel('Evidence and review justification').fill(decision.reason);
    await form.getByLabel('I independently reviewed the evidence').check();
  }
  await settle.click();
  await enterTotals('20', '0');
  await form.getByRole('button', { name: 'Settle goal reservation' }).click();
  await page.getByText('Reconcile outstanding provider turns before settling their goal reservation').waitFor();
  await form.getByRole('button', { name: 'Cancel' }).click();

  await settleProvider.click();
  await enterTotals('25', '5');
  await form.getByRole('button', { name: 'Settle provider reservation' }).click();
  await page.getByText(/explicitly confirm the verified overrun/i).waitFor();
  await form.getByLabel(/I verified usage above the reserved tokens/).check();
  await form.getByRole('button', { name: 'Settle provider reservation' }).click();
  await form.waitFor({ state: 'hidden' });
  await main.getByRole('heading', { name: 'No held provider reservations' }).waitFor();

  await settle.click();
  await enterTotals('25', '5');
  await form.getByRole('button', { name: 'Settle goal reservation' }).click();
  await form.waitFor({ state: 'hidden' });
  await main.getByRole('heading', { name: 'No held goal reservations' }).waitFor();
  const settled = await api('state');
  assert.equal(settled.remoteProviderTurns[0].state, 'reconciled');
  assert.equal(settled.budgetReservations.find((hold) => hold.id === foreignHold.id).state, 'held');
  assert.equal(settled.providerBudgetReservations.find((hold) => hold.id === foreignProviderHold.id).state, 'held');
  assert.equal(settled.budgetReservations.find((hold) =>
    settled.goals.some((goal) => goal.id === hold.goalId && goal.projectId === projectId)).consumed, 30);
  assert.deepEqual(unauthorizedPaths, []);
}, (home) => seedProviderEvidence(home, true));

browserTest('recovers a held executor receipt without exposing lease credentials', async ({ page, nav, form, inspector, api, fixture }) => {
  const snapshot = await api('state');
  const [job] = snapshot.jobs;
  const turns = await api(`jobs/${job.id}/provider-turns`);
  assert.equal(turns.length, 1);
  assert.equal(turns[0].consumed, 90);
  assert.equal(turns[0].canRecoverReceipt, true);
  for (const response of [snapshot, turns]) {
    assert.doesNotMatch(JSON.stringify(response), /fixture-lease-secret|A{64}/);
  }
  await nav.getByRole('button', { name: 'Jobs', exact: false }).click();
  await page.getByRole('row', { name: /Blocked provider fixture/ }).getByRole('button', { name: 'Inspect' }).click();
  await inspector.getByRole('heading', { name: 'Remote provider turns' }).waitFor();
  const evidence = inspector.getByRole('row', { name: /fixture-provider/ });
  await evidence.getByText('requested-v1', { exact: true }).waitFor();
  await evidence.getByText('512 / 90', { exact: true }).waitFor();
  await evidence.getByText('Executor response was interrupted', { exact: true }).waitFor();
  assert.doesNotMatch(await inspector.innerText(), /fixture-lease-secret|A{64}/);

  fixture.receipt = {
    turnId: turns[0].id, leaseId: snapshot.remoteProviderLeases[0].id, providerId: 'fixture-provider', role: 'builder',
    inputCommit: '', observedModel: 'requested-v1', inputTokens: 70, outputTokens: 35, finalUsage: true,
    success: false, finalText: '', finalTextIsFallback: false, outputTree: '', patch: '', reportedCostMicros: 125000
  };
  await evidence.getByRole('button', { name: 'Recover receipt' }).click();
  await form.getByLabel('Recovery evidence').fill('Verified the stopped fixture job and its durable receipt.');
  await form.getByLabel('I independently reviewed the interrupted job').check();
  await form.getByRole('button', { name: 'Reconcile receipt' }).click();
  await form.waitFor({ state: 'hidden' });
  await evidence.getByText('reconciled', { exact: true }).waitFor();
  await evidence.getByText('512 / 105', { exact: true }).waitFor();
  const [settled] = await api(`jobs/${job.id}/provider-turns`);
  assert.equal(settled.reportedCostMicros, 125000);
  assert.equal(settled.observedModel, 'requested-v1');
  assert.equal(settled.canRecoverReceipt, false);
  assert.equal((await api('state')).jobs[0].state, 'blocked');
}, seedProviderEvidence);

browserTest('ignores an older admin login after connecting with a project token', async ({ page, connection, fixture }) => {
  let finishAdminRequest;
  const finishAdmin = new Promise((resolve) => { finishAdminRequest = resolve; });
  const delayedAdmin = await page.evaluate(() => sessionStorage.getItem('factory-token'));
  const isDelayedAdminRequest = (request) =>
    request.url().endsWith('/api/state') && request.headers().authorization === 'Bearer ' + delayedAdmin;
  await page.route('**/api/state', async (route) => {
    if (!isDelayedAdminRequest(route.request())) return route.continue();
    await finishAdmin;
    await route.fulfill({ status: 401, json: { error: 'Invalid administrator token' } });
  });

  try {
    await Promise.all([page.waitForRequest(isDelayedAdminRequest), page.reload({ waitUntil: 'domcontentloaded' })]);
    await page.locator('#brand-connection').click();
    await connection.getByLabel('API token').fill(fixture.access.reconciler);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await connection.waitFor({ state: 'hidden' });
    await page.getByRole('heading', { name: 'Project recovery' }).waitFor();

    const adminResponse = page.waitForResponse((response) => isDelayedAdminRequest(response.request()));
    const wrongStream = page.waitForRequest((request) =>
      request.url().endsWith('/api/events/stream') &&
      request.headers().authorization === 'Bearer ' + fixture.access.reconciler,
    { timeout: 1500 }).catch((error) => {
      if (error.name !== 'TimeoutError') throw error;
      return null;
    });
    finishAdminRequest();
    assert.equal((await adminResponse).status(), 401);
    assert.ok(!(await wrongStream), 'A stale admin login started the event stream with the project token');
    await page.getByRole('button', { name: 'Connection settings, connected' }).waitFor();
    await page.getByRole('button', { name: 'Settle goal reservation' }).waitFor();
  } finally {
    finishAdminRequest();
  }
}, (home) => seedProviderEvidence(home, true));

browserTest('identifies otherwise identical provider holds in the list and settlement dialog', async ({ page, form, connection, fixture }) => {
  const secondId = '12345678-1234-1234-1234-123456789abc';
  let firstId;
  await page.route('**/api/viewer/projects/*/provider-reservations', async (route) => {
    const response = await route.fetch();
    const [hold] = await response.json();
    firstId = hold.id;
    await route.fulfill({ response, json: [hold, { ...hold, id: secondId }] });
  });
  await page.locator('#brand-connection').click();
  await connection.getByLabel('API token').fill(fixture.access.reconciler);
  await connection.getByRole('button', { name: 'Connect', exact: true }).click();
  await connection.waitFor({ state: 'hidden' });
  const holds = page.getByRole('row').filter({ hasText: 'manual-provider' });
  assert.equal(await holds.count(), 2);
  for (const id of [firstId, secondId]) {
    const row = holds.filter({ hasText: 'Hold ID: ' + id });
    await row.getByRole('button', { name: 'Settle provider reservation' }).click();
    const subtitle = form.locator('#modal-subtitle');
    assert.ok((await subtitle.innerText()).includes('Hold ID: ' + id));
    await form.getByRole('button', { name: 'Cancel' }).click();
  }
}, (home) => seedProviderEvidence(home, true));

browserTest('controls and reconciles only jobs in the selected Operator project', async ({ page, form, connection, api, fixture }) => {
  async function connect(token) {
    await page.locator('#brand-connection').click();
    await connection.getByLabel('API token').fill(token);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await connection.waitFor({ state: 'hidden' });
  }
  const operations = page.getByRole('heading', { name: 'Project job operations' });
  const projectRoute = (id) => new URL('/api/viewer/projects/' + id, page.url()).href;
  const request = (token, resource, options = {}) => page.request.fetch(resource,
    { ...options, headers: { Authorization: 'Bearer ' + token } });
  await connect(fixture.budgetToken);
  assert.equal(await operations.count(), 0);
  assert.equal((await request(fixture.budgetToken, projectRoute(fixture.projectId) + '/jobs/operations')).status(), 403);

  await connect(fixture.operatorToken);
  await operations.waitFor();
  const select = page.getByLabel('Project', { exact: true });
  await select.selectOption(fixture.otherId);
  await page.getByRole('heading', { name: 'Budget Reconciler access required' }).waitFor();
  assert.equal(await operations.count(), 0);
  assert.equal((await request(fixture.operatorToken, projectRoute(fixture.otherId) + '/jobs/operations')).status(), 403);
  await select.selectOption(fixture.projectId);
  await operations.waitFor();
  assert.equal(await page.getByText('Foreign work package', { exact: true }).count(), 0);
  const projection = await request(fixture.operatorToken, projectRoute(fixture.projectId) + '/jobs/operations');
  assert.equal(projection.status(), 200);
  assert.doesNotMatch(await projection.text(), /leaseToken|leaseOwner|local-fixture|checkpoint-worktree|Foreign work package/);
  const foreign = projectRoute(fixture.projectId) + '/jobs/' + fixture.foreignJobId;
  assert.equal((await request(fixture.operatorToken, foreign + '/action', { method: 'POST', data: { action: 'pause' } })).status(), 404);
  assert.equal((await request(fixture.operatorToken, foreign + '/reconcile',
    { method: 'POST', data: { reviewed: true, reason: 'Verified the fixture checkpoint' } })).status(), 404);

  const row = (title) => page.getByRole('row').filter({ has: page.getByText(title, { exact: true }) });
  const local = row('Local work package');
  const running = row('Running local work package');
  const remote = row('Running remote work package');
  const interrupted = row('Interrupted local work package');
  async function action(target, label) {
    await target.getByRole('button', { name: label, exact: true }).click();
    await form.getByRole('button', { name: label, exact: true }).click();
    await form.waitFor({ state: 'hidden' });
  }
  assert.equal(await remote.getByRole('button').count(), 0);
  await action(running, 'Stop job');
  await running.getByText('Requested: stopped · waiting for the runner').waitFor();
  await running.getByText('preparing', { exact: true }).waitFor();
  assert.equal(await running.getByRole('button').count(), 0);
  await action(local, 'Pause job');
  await local.getByText('paused', { exact: true }).waitFor();
  await action(local, 'Stop job');
  await local.getByText('stopped', { exact: true }).waitFor();
  await local.getByRole('button', { name: 'Resume job' }).click();
  await form.getByRole('button', { name: 'Resume job' }).click();
  await page.getByText('Confirm the review and record what you inspected.').waitFor();
  await form.getByLabel('Evidence and review justification').fill('Reviewed the prior worktree');
  await form.getByLabel('I reviewed the worktree and any external changes').check();
  await form.getByRole('button', { name: 'Resume job' }).click();
  await form.waitFor({ state: 'hidden' });
  await local.getByText('queued', { exact: true }).waitFor();

  await interrupted.getByRole('button', { name: 'Review and requeue' }).click();
  await form.getByText(fixture.checkpoint, { exact: true }).waitFor();
  await form.getByLabel('Evidence and review justification').fill('Verified stopped runner and durable checkpoint');
  await form.getByLabel('I reviewed the stopped process and clean worktree checkpoint').check();
  const dirtyFile = path.join(fixture.worktree, 'untracked.txt');
  await writeFile(dirtyFile, 'Untracked fixture');
  await form.getByRole('button', { name: 'Review and requeue' }).click();
  await page.getByText('Recovery requires a stopped local runner and a clean, matching checkpoint.').waitFor();
  await rm(dirtyFile);
  const oldJob = (await api('state')).jobs.find((job) => job.title === 'Interrupted local work package');
  const git = (...args) => execFileSync('git', args, { cwd: fixture.worktree, encoding: 'utf8', timeout: 10_000 }).trim();
  git('-c', 'user.name=Factory Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false',
    '-c', 'core.hooksPath=' + path.join(fixture.home, 'no-hooks'), 'commit', '--allow-empty', '-m', 'New recovery checkpoint');
  const newCheckpoint = git('rev-parse', 'HEAD');
  const dll = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  execFileSync('dotnet', [dll, '--browser-advance-job-checkpoint', fixture.home, oldJob.id, newCheckpoint], { timeout: 15_000 });
  const staleResponse = page.waitForResponse((response) => response.url().endsWith('/jobs/' + oldJob.id + '/reconcile') &&
    response.request().method() === 'POST');
  await form.getByRole('button', { name: 'Review and requeue' }).click();
  assert.equal((await staleResponse).status(), 409);
  await page.getByText('The recovery checkpoint changed. Refresh and review the job again.').waitFor();
  assert.equal((await api('state')).jobs.find((job) => job.id === oldJob.id).state, 'blocked');
  await form.getByRole('button', { name: 'Cancel' }).click();
  await page.getByRole('button', { name: 'Refresh projects' }).click();
  await interrupted.getByRole('button', { name: 'Review and requeue' }).click();
  await form.getByText(newCheckpoint, { exact: true }).waitFor();
  await form.getByLabel('Evidence and review justification').fill('Verified stopped runner and replacement checkpoint');
  await form.getByLabel('I reviewed the stopped process and clean worktree checkpoint').check();
  await form.getByRole('button', { name: 'Review and requeue' }).click();
  await form.waitFor({ state: 'hidden' });
  await interrupted.getByText('queued', { exact: true }).waitFor();
  const { jobs } = await api('state');
  assert.equal(jobs.find((job) => job.title === 'Interrupted local work package').stage, 'reconciled');
  assert.equal(jobs.find((job) => job.title === 'Running local work package').requestedState, 'project:stopped');
  assert.equal(jobs.find((job) => job.id === fixture.foreignJobId).state, 'queued');
}, async (home) => {
  const worktree = path.join(home, 'checkpoint-worktree');
  await mkdir(worktree);
  const git = (...args) => execFileSync('git', args, { cwd: worktree, encoding: 'utf8', timeout: 10_000 }).trim();
  git('init', '-b', 'master');
  git('-c', 'user.name=Factory Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false',
    '-c', 'core.hooksPath=' + path.join(home, 'no-hooks'), 'commit', '--allow-empty', '-m', 'Fixture checkpoint');
  const dll = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  const fixture = JSON.parse(execFileSync('dotnet',
    [dll, '--browser-project-jobs-fixture', home, worktree, git('rev-parse', 'HEAD')], { encoding: 'utf8', timeout: 15_000 }));
  return { ...fixture, worktree, home };
});

browserTest('plans and adopts only the reviewed revision in the granted project', async ({ page, nav, form, inspector, connection, api, fixture }) => {
  async function connect(token) {
    await page.locator('#brand-connection').click();
    await connection.getByLabel('API token').fill(token);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await connection.waitFor({ state: 'hidden' });
    await nav.getByRole('button', { name: 'Goal planner' }).click();
  }
  const route = (project) => new URL('/api/viewer/projects/' + project, page.url()).href;
  const request = (token, resource, options = {}) => page.request.fetch(resource,
    { ...options, headers: { Authorization: 'Bearer ' + token } });
  const create = page.getByRole('button', { name: 'Create draft goal', exact: true });
  await connect(fixture.viewer);
  await page.getByText('Planner access required to change goals or review plans.').waitFor();
  assert.equal(await create.count(), 0);
  const forbidden = await request(fixture.viewer, route(fixture.projectId) + '/goals',
    { method: 'POST', data: { objective: 'Viewer cannot create this goal' } });
  assert.equal(forbidden.status(), 403);

  await connect(fixture.planner);
  await create.click();
  await form.getByLabel('Outcome / objective').fill('Plan a scoped change');
  await form.getByLabel('Acceptance criteria (one per line)').fill('Record the verified outcome');
  await form.getByRole('button', { name: 'Create draft goal' }).click();
  await form.waitFor({ state: 'hidden' });
  const goalRow = page.getByRole('row').filter({ hasText: 'Plan a scoped change' });
  await goalRow.waitFor();
  const goal = (await api('state')).goals.find((item) => item.projectId === fixture.projectId);
  const planRoute = route(fixture.projectId) + '/plans/' + goal.id;
  const foreign = await request(fixture.planner, route(fixture.projectId) + '/plans/' + fixture.foreignGoalId);
  assert.equal(foreign.status(), 404);
  const foreignSave = await request(fixture.planner, route(fixture.projectId) + '/plans/' + fixture.foreignGoalId,
    { method: 'PUT', data: { packages: [] } });
  assert.equal(foreignSave.status(), 404);
  const selector = page.getByLabel('Project', { exact: true });
  await selector.selectOption(fixture.otherId);
  await page.getByText('Other project goal', { exact: true }).waitFor();
  assert.equal(await create.count(), 0);
  assert.equal(await page.getByRole('button', { name: 'Edit plan' }).count(), 0);
  assert.equal(await goalRow.count(), 0);
  await selector.selectOption(fixture.projectId);
  await goalRow.getByRole('button', { name: 'Edit draft goal' }).click();
  const criteria = form.getByLabel('Acceptance criteria (one per line)');
  assert.equal(await criteria.inputValue(), 'Record the verified outcome');
  const readDraft = await (await request(fixture.planner, planRoute)).json();
  const goalRoute = route(fixture.projectId) + '/goals/' + goal.id;
  const concurrent = await request(fixture.planner, goalRoute, { method: 'PUT',
    data: { objective: goal.objective, acceptanceCriteria: ['Keep the newer criteria'], goalRevision: readDraft.goalRevision } });
  assert.equal(concurrent.status(), 200);
  await criteria.fill('Outdated change from the first dialog');
  await form.getByRole('button', { name: 'Update draft goal' }).click();
  await page.getByText('The draft goal changed. Reopen it before saving.').waitFor();
  assert.deepEqual((await api('state')).goals.find((item) => item.id === goal.id).acceptanceCriteria, ['Keep the newer criteria']);
  await form.getByRole('button', { name: 'Cancel' }).click();
  await goalRow.getByRole('button', { name: 'Edit draft goal' }).click();
  await form.waitFor({ state: 'visible' });
  assert.equal(await criteria.inputValue(), 'Keep the newer criteria');
  await criteria.fill('Record the verified outcome\nDocument the project boundary');
  await form.getByRole('button', { name: 'Update draft goal' }).click();
  await form.waitFor({ state: 'hidden' });
  await goalRow.getByRole('button', { name: 'Edit plan' }).click();
  await inspector.getByRole('button', { name: 'Add package' }).click();
  await form.getByLabel('Package title').fill('Initial reviewed package');
  await form.getByLabel('Description / acceptance criteria').fill('Record the scoped result');
  await form.getByLabel('Expected file paths, one per line').fill('scoped.txt');
  await form.getByRole('button', { name: 'Save changes' }).click();
  await inspector.getByRole('button', { name: 'Save plan' }).click();
  await inspector.getByText('Initial reviewed package', { exact: true }).waitFor();
  await inspector.getByRole('button', { name: 'Adopt reviewed plan' }).click();
  const prior = await (await request(fixture.planner, planRoute)).json();
  await form.getByText(prior.plan.revision, { exact: true }).waitFor();
  const packageUpdate = { ...prior.plan.packages[0], title: 'Replacement reviewed package' };
  const replacement = await request(fixture.planner, planRoute, { method: 'PUT',
    data: { packages: [packageUpdate], goalRevision: prior.goalRevision, revision: prior.plan.revision } });
  assert.ok(replacement.ok(), await replacement.text());
  const nextPlan = await replacement.json();
  await form.getByRole('button', { name: 'Adopt reviewed plan' }).click();
  await page.getByText('Confirm the review before adopting this revision.').waitFor();
  await form.getByLabel(/I reviewed this revision/).check();
  await form.getByRole('button', { name: 'Adopt reviewed plan' }).click();
  await page.getByText('Review the current saved plan revision.').waitFor();
  assert.equal((await api('state')).jobs.length, 0);
  await form.getByRole('button', { name: 'Cancel' }).click();
  await inspector.getByRole('button', { name: 'Close inspector' }).click();
  await goalRow.getByRole('button', { name: 'Review plan' }).click();
  await inspector.getByText('Replacement reviewed package', { exact: true }).waitFor();
  await inspector.getByRole('button', { name: 'Adopt reviewed plan' }).click();
  await form.getByText(nextPlan.revision, { exact: true }).waitFor();
  await form.getByLabel(/I reviewed this revision/).check();
  await form.getByRole('button', { name: 'Adopt reviewed plan' }).click();
  await form.waitFor({ state: 'hidden' });
  await goalRow.getByText('active', { exact: true }).waitFor();
  const state = await api('state');
  assert.equal(state.jobs.length, 1);
  assert.equal(state.jobs[0].title, 'Replacement reviewed package');
  assert.equal(state.jobs[0].projectId, fixture.projectId);
  assert.equal(state.goals.find((item) => item.id === fixture.foreignGoalId).state, 'draft');
}, seedProjectPlanning);

browserTest('reviews only the selected project and creates goals from the current finding', async ({ page, nav, form, connection, api, fixture }) => {
  async function connect(token) {
    await page.locator('#brand-connection').click();
    await connection.getByLabel('API token').fill(token);
    await connection.getByRole('button', { name: 'Connect', exact: true }).click();
    await connection.waitFor({ state: 'hidden' });
    await nav.getByRole('button', { name: 'Project review' }).click();
  }
  const route = (id) => new URL('/api/viewer/projects/' + id + '/analysis', page.url()).href;
  const request = (token, url, method = 'GET', data) => page.request.fetch(url,
    { method, data, headers: { Authorization: 'Bearer ' + token } });
  const analyze = page.getByRole('button', { name: 'Analyze project' });
  const finding = page.locator('.entity-card').filter({ hasText: 'No root README.md detected' });
  const create = finding.getByRole('button', { name: 'Review & create draft goal' });
  const own = route(fixture.projectId);
  const other = route(fixture.otherId);
  await connect(fixture.planner);
  await analyze.click();
  await finding.waitFor();
  const first = (await (await request(fixture.planner, own)).json()).review;
  assert.equal(first.projectId, fixture.projectId);
  const evidence = { reviewId: first.id, findingId: 'missing-readme' };
  assert.equal((await request(fixture.planner, other, 'POST', {})).status(), 403);
  assert.equal((await request(fixture.planner, other + '/goals', 'POST', evidence)).status(), 403);
  const picker = page.getByLabel('Repository', { exact: true });
  await picker.selectOption(fixture.otherId);
  await page.getByText('No review loaded').waitFor();
  assert.equal(await analyze.count(), 0);
  assert.equal(await finding.count(), 0);
  await picker.selectOption(fixture.projectId);
  await create.click();
  await form.waitFor({ state: 'visible' });
  assert.equal((await request(fixture.planner, own, 'POST', {})).status(), 200);
  await form.getByRole('button', { name: 'Create draft goal' }).click();
  await page.getByText('The review changed. Reopen the current finding.').waitFor();
  assert.equal((await api('state')).goals.length, 1);
  await form.getByRole('button', { name: 'Cancel' }).click();

  await connect(fixture.viewer);
  await finding.waitFor();
  assert.equal(await analyze.count(), 0);
  assert.equal(await create.count(), 0);
  assert.equal((await request(fixture.viewer, other)).status(), 404);
  assert.equal((await request(fixture.viewer, own, 'POST', {})).status(), 403);
  assert.equal((await request(fixture.viewer, own + '/goals', 'POST', evidence)).status(), 403);
  await connect(fixture.planner);
  await create.click();
  execFileSync('git', ['-c', 'user.name=Factory Fixture', '-c', 'user.email=fixture@example.invalid',
    '-c', 'commit.gpgsign=false', '-c', 'core.hooksPath=' + path.join(fixture.repo, 'no-hooks'),
    'commit', '--allow-empty', '-m', 'Advance master'], { cwd: fixture.repo, timeout: 10_000 });
  await form.getByRole('button', { name: 'Create draft goal' }).click();
  await page.getByText('Master advanced. Run analysis again.').waitFor();
  await form.getByRole('button', { name: 'Cancel' }).click();
  const analyzed = page.waitForResponse(response => response.url() === own && response.request().method() === 'POST');
  await analyze.click();
  assert.equal((await analyzed).status(), 200);
  await page.getByText('Project review saved', { exact: true }).last().waitFor();
  await create.click();
  await form.getByRole('button', { name: 'Create draft goal' }).click();
  await form.waitFor({ state: 'hidden' });
  await page.getByRole('heading', { name: 'Project goal planner' }).waitFor();
  await page.getByRole('cell', { name: 'Create an operational README for this repository', exact: true }).waitFor();
  const state = await api('state');
  const goal = state.goals.find(item => item.projectId === fixture.projectId);
  assert.equal(goal.state, 'draft');
  assert.equal(state.jobs.length, 0);
  assert.equal(state.goals.filter(item => item.projectId === fixture.otherId).length, 1);
  assert.ok(state.events.some(event => event.type === 'review_goal_created' && event.message.includes(goal.id)));
}, seedProjectPlanning);

browserTest('streams only selected project activity and closes a revoked connection', async ({ page, nav, connection, fixture, home }) => {
  await page.locator('#brand-connection').click();
  await connection.getByLabel('API token').fill(fixture.viewer);
  await connection.getByRole('button', { name: 'Connect', exact: true }).click();
  await connection.waitFor({ state: 'hidden' });
  await nav.getByRole('button', { name: 'Activity' }).click();
  const main = page.locator('#main');
  const picker = main.getByLabel('Project', { exact: true });
  const row = (title) => main.getByRole('row').filter({ hasText: title });
  await row('First activity job').waitFor();
  assert.equal(await row('Second activity job').count(), 0);
  assert.doesNotMatch(await main.innerText(), /Hidden activity|private internal fixture message|global event/);
  await picker.selectOption(fixture.secondId);
  await row('Second activity job').waitFor();
  assert.equal(await row('First activity job').count(), 0);
  await picker.selectOption(fixture.firstId);
  await row('First activity job').waitFor();

  const streamUrl = (id) => new URL('/api/viewer/projects/' + id + '/events/stream', page.url());
  const headers = { Authorization: 'Bearer ' + fixture.viewer };
  const foreign = await fetch(streamUrl(fixture.hiddenId), { headers, signal: AbortSignal.timeout(10_000) });
  assert.equal(foreign.status, 404);
  const global = await fetch(new URL('/api/events/stream', page.url()), { headers, signal: AbortSignal.timeout(10_000) });
  assert.equal(global.status, 401);
  const stream = await fetch(streamUrl(fixture.firstId), { headers, signal: AbortSignal.timeout(20_000) });
  assert.equal(stream.status, 200);
  assert.match(stream.headers.get('content-type'), /^text\/event-stream/);
  const reader = stream.body.getReader(), decoder = new TextDecoder();
  let wire = '';
  while (!wire.includes('\n\n')) {
    const part = await reader.read();
    assert.equal(part.done, false);
    wire += decoder.decode(part.value, { stream: true });
  }
  assert.match(wire, /First activity job/);
  assert.doesNotMatch(wire, /Second activity job|Hidden activity job|private internal|global event/);

  const tests = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  execFileSync('dotnet', [tests, '--browser-append-project-activity', home, fixture.firstJobId], { timeout: 15_000 });
  await row('First activity job').filter({ hasText: 'job control' }).waitFor();
  assert.doesNotMatch(await main.innerText(), /private live event details/);
  const factory = path.join(root, 'src/Factory/bin/Release/net10.0/Factory.dll');
  execFileSync('dotnet', [factory, 'viewer', 'revoke', '--subject', fixture.subjectId, '--project', 'First activity project'],
    { env: { ...process.env, FACTORY_HOME: home }, timeout: 15_000 });
  for (;;) {
    const part = await reader.read();
    if (part.done) break;
    wire += decoder.decode(part.value, { stream: true });
  }
  assert.match(wire, /event: access-revoked/);
  await main.getByText(/Project access changed/).waitFor();
  assert.equal(await row('First activity job').count(), 0);
  assert.equal((await fetch(streamUrl(fixture.firstId), { headers, signal: AbortSignal.timeout(10_000) })).status, 404);
  await main.getByRole('button', { name: 'Refresh activity' }).click();
  await row('Second activity job').waitFor();
  assert.deepEqual(await picker.locator('option').allTextContents(), ['Second activity project']);
}, async (home) => {
  const dll = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  return JSON.parse(execFileSync('dotnet', [dll, '--browser-project-activity-fixture', home], { encoding: 'utf8', timeout: 15_000 }));
});

async function seedProjectPlanning(home) {
  const repo = path.join(home, 'planning-repository');
  await mkdir(repo);
  const git = (...args) => execFileSync('git', args, { cwd: repo, timeout: 10_000 });
  git('init', '-b', 'master');
  git('-c', 'user.name=Factory Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false',
    '-c', 'core.hooksPath=' + path.join(home, 'no-hooks'), 'commit', '--allow-empty', '-m', 'Fixture master');
  const dll = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
  const fixture = JSON.parse(execFileSync('dotnet', [dll, '--browser-project-planning-fixture', home, repo],
    { encoding: 'utf8', timeout: 15_000 }));
  return { ...fixture, repo };
}

async function seedProviderEvidence(home, scoped = false) {
  const token = 'b'.repeat(64);
  const fixture = { env: { FACTORY_REMOTE_EXECUTOR_TOKEN: token }, receipt: null, receiptRequests: 0 };
  const executor = createServer(async (request, response) => {
    fixture.receiptRequests++;
    let body = '';
    for await (const chunk of request) body += chunk;
    const query = JSON.parse(body);
    if (request.method !== 'POST' || request.url !== '/api/executor/receipt' ||
        request.headers.authorization !== `Bearer ${token}` || query.grantDigest !== 'A'.repeat(64) ||
        query.turnId !== fixture.receipt?.turnId) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(fixture.receipt));
  });
  executor.listen(0, '127.0.0.1');
  await once(executor, 'listening');
  fixture.dispose = () => new Promise((resolve) => executor.close(resolve));
  try {
    const dll = path.join(root, 'tests/Factory.Tests/bin/Release/net10.0/Factory.Tests.dll');
    const output = execFileSync('dotnet', [dll, scoped ? '--browser-project-recovery-fixture' : '--browser-provider-evidence-fixture', home,
      `http://127.0.0.1:${executor.address().port}/`], { timeout: 15_000, encoding: 'utf8' });
    if (scoped) fixture.access = JSON.parse(output);
    return fixture;
  } catch (error) {
    await fixture.dispose();
    throw error;
  }
}
