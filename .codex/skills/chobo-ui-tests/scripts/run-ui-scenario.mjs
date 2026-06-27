#!/usr/bin/env node
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { createRequire } from 'node:module';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

const data = {
  sourceCluster: { name: 'ui-source', nodes: 'clickhouse-source:9000' },
  restoreCluster: { name: 'ui-restore', nodes: 'clickhouse-restore:9000' },
  storage: {
    name: 'ui-minio',
    endpoint: 'http://backup-s3:9000',
    bucket: 'data-bucket',
    region: 'us-east-1',
    accessKey: 'chobo-access-key',
    secretKey: 'chobo-secret-key'
  },
  policy: { name: 'ui-orders-policy', database: 'backup_single_source', table: 'source_orders' },
  schedule: { name: 'ui-daily-full', timezone: 'UTC', hour: '2', minute: '0' },
  restore: { database: 'backup_single_restore', table: 'restored_orders' }
};

const largeTableData = {
  storage: { name: 'ui-large-minio' },
  policy: { name: 'ui-large-ontime-policy', database: 'large_ontime_source', table: 'ontime' },
  restore: { database: 'large_ontime_restore', table: 'ontime_restored' }
};

const plans = {
  bootstrap: ['bootstrap'],
  cluster: ['bootstrap', 'cluster'],
  storage: ['bootstrap', 'storage'],
  policy: ['bootstrap', 'cluster', 'storage', 'policy'],
  'schedule-edit': ['bootstrap', 'cluster', 'storage', 'policy', 'schedule-edit'],
  backup: ['bootstrap', 'cluster', 'storage', 'policy', 'backup'],
  restore: ['bootstrap', 'cluster', 'storage', 'policy', 'backup', 'restore'],
  details: ['bootstrap', 'cluster', 'storage', 'policy', 'backup', 'restore', 'details'],
  'logs-audit': ['bootstrap', 'cluster', 'storage', 'policy', 'backup', 'restore', 'logs-audit'],
  failure: ['bootstrap', 'failure'],
  'large-table': ['bootstrap', 'cluster', 'storage', 'policy', 'backup', 'restore', 'backup-delete-confirmation', 'gc-cleanup-ui', 'logs-audit'],
  full: ['bootstrap', 'cluster', 'storage', 'policy', 'schedule-edit', 'backup', 'schema-browser', 'restore', 'logs-audit', 'backup-delete-confirmation']
};

function parseArgs(argv) {
  const args = { scenario: 'full' };
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--env') args.env = argv[++i];
    else if (arg === '--scenario') args.scenario = argv[++i];
    else if (arg === '--base-url') args.baseUrl = argv[++i];
    else if (arg === '--ui-root') args.uiRoot = argv[++i];
    else if (arg === '--headed') args.headed = true;
    else if (arg === '--keep-open') args.keepOpen = true;
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return args;
}

async function readEnv(args) {
  let env = {};
  if (args.env) env = JSON.parse(await fs.readFile(args.env, 'utf8'));
  const baseUrl = args.baseUrl ?? env.BaseUrl;
  if (!baseUrl) throw new Error('Pass --env <ui-env.json> or --base-url <url>.');
  const repoRoot = env.RepoRoot ?? process.cwd();
  const testId = env.TestId ?? `ui-manual-${new Date().toISOString().replace(/[:.]/g, '-')}`;
  const uiRoot = args.uiRoot ?? env.UiRoot ?? path.join(repoRoot, '.artifacts', 'TestResults', testId, 'ui');
  return { ...env, EnvFile: args.env ?? env.EnvFile, BaseUrl: baseUrl.replace(/\/$/, ''), RepoRoot: repoRoot, TestId: testId, UiRoot: uiRoot };
}

async function importPlaywright() {
  try {
    return await import('playwright');
  } catch (firstError) {
    const require = createRequire(import.meta.url);
    const bundledNodeModules = path.join(os.homedir(), '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules');
    const candidateRoots = [
      process.env.CHOBO_UI_TEST_NODE_MODULES,
      ...(process.env.NODE_PATH ? process.env.NODE_PATH.split(path.delimiter) : []),
      bundledNodeModules
    ].filter(Boolean);
    try {
      const pnpmRoot = path.join(bundledNodeModules, '.pnpm');
      const entries = await fs.readdir(pnpmRoot, { withFileTypes: true });
      for (const entry of entries) {
        if (entry.isDirectory() && entry.name.startsWith('playwright@')) {
          candidateRoots.push(path.join(pnpmRoot, entry.name, 'node_modules'));
        }
      }
    } catch {}
    for (const root of candidateRoots) {
      try {
        return require(path.join(root, 'playwright'));
      } catch {}
    }
    throw new Error('The chobo-ui-tests runner requires the Playwright package. Set CHOBO_UI_TEST_NODE_MODULES or NODE_PATH to a node_modules folder containing Playwright, then rerun. Original error: ' + firstError.message);
  }
}

async function main() {
  const args = parseArgs(process.argv);
  const env = await readEnv(args);
  const scenario = args.scenario ?? env.Scenario ?? 'full';
  if (!plans[scenario]) throw new Error(`Unsupported scenario '${scenario}'.`);
  const isLargeTableScenario = scenario === 'large-table';
  if (isLargeTableScenario) {
    Object.assign(data.storage, largeTableData.storage);
    Object.assign(data.policy, largeTableData.policy);
    Object.assign(data.restore, largeTableData.restore);
  }
  const backupTimeoutMs = isLargeTableScenario ? 3 * 60 * 60 * 1000 : 120000;
  const restoreTimeoutMs = isLargeTableScenario ? 3 * 60 * 60 * 1000 : 120000;

  const screenshotsDir = path.join(env.UiRoot, 'screenshots');
  await fs.rm(screenshotsDir, { recursive: true, force: true });
  await fs.mkdir(screenshotsDir, { recursive: true });
  await fs.mkdir(path.join(env.UiRoot, 'logs'), { recursive: true });

  const consoleEntries = [];
  const networkEntries = [];
  const screenshots = [];
  const notes = [];
  const state = { authToken: env.AuthToken ?? null, backupId: null, restoreId: null };
  let shotCounter = 0;

  const { chromium } = await importPlaywright();
  let browser;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: !args.headed });
  } catch {
    browser = await chromium.launch({ headless: !args.headed });
  }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const page = await context.newPage();
  page.on('console', (msg) => consoleEntries.push(`[${msg.type()}] ${msg.text()}`));
  page.on('response', (response) => {
    const status = response.status();
    if (status >= 400) networkEntries.push(`${status} ${response.request().method()} ${response.url()}`);
  });
  page.on('pageerror', (error) => consoleEntries.push(`[pageerror] ${error.stack ?? error.message}`));

  async function screenshot(slug, note, status = 'pass') {
    shotCounter += 1;
    const file = `${String(shotCounter).padStart(3, '0')}-${slug}.png`;
    const fullPath = path.join(screenshotsDir, file);
    await page.screenshot({ path: fullPath, fullPage: false });
    screenshots.push({ file, route: routeOf(page.url()), step: slug, note, status });
    notes.push({ status, text: `${slug}: ${note}` });
  }

  async function failureShot(error) {
    const n = String(shotCounter + 1).padStart(3, '0');
    try { await page.screenshot({ path: path.join(screenshotsDir, `${n}-failure-current-screen.png`), fullPage: false }); } catch {}
    try { await page.screenshot({ path: path.join(screenshotsDir, `${n}-failure-full-page.png`), fullPage: true }); } catch {}
    notes.push({ status: 'fail', text: error.stack ?? error.message });
  }

  const api = async (method, apiPath, body, token = state.authToken) => {
    const response = await fetch(`${env.BaseUrl}/api/v1/${apiPath.replace(/^\//, '')}`, {
      method,
      headers: {
        ...(body === undefined ? {} : { 'content-type': 'application/json' }),
        ...(token ? { authorization: `Bearer ${token}` } : {})
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { json = text; }
    if (!response.ok) {
      const error = new Error(`${method} ${apiPath} failed: HTTP ${response.status} ${text}`);
      error.status = response.status;
      error.body = json;
      throw error;
    }
    return json;
  };

  async function go(route) {
    await page.goto(`${env.BaseUrl}${route}`, { waitUntil: 'domcontentloaded' });
    await page.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});
  }

  async function clickButton(name, options = {}) {
    const button = page.getByRole('button', { name });
    await button.first().click(options);
  }

  async function createExistingRestoreTarget() {
    if (!env.ComposeFile || !env.ProjectName) {
      throw new Error('Restore destructive-confirmation scenario requires ComposeFile and ProjectName to pre-create the restore target table.');
    }
    const query = `CREATE DATABASE IF NOT EXISTS ${data.restore.database}; CREATE TABLE IF NOT EXISTS ${data.restore.database}.${data.restore.table} (id UInt32, name String) ENGINE = MergeTree ORDER BY id`;
    await execFileAsync('docker', ['compose', '-f', env.ComposeFile, '-p', env.ProjectName, 'exec', '-T', 'clickhouse-restore', 'clickhouse-client', '--multiquery', '-q', query], { cwd: env.RepoRoot, timeout: 30000 });
    notes.push({ status: 'pass', text: `Pre-created ${data.restore.database}.${data.restore.table} so the restore confirmation covers an existing target table with append enabled.` });
  }

  async function controlByLabel(label, controlSelector = 'input,select,textarea') {
    const byAccessibleName = page.getByLabel(label, { exact: true }).first();
    if (await byAccessibleName.isVisible({ timeout: 1000 }).catch(() => false)) return byAccessibleName;
    const escaped = String(label).replace(/"/g, '\\"');
    const labelElement = page.locator(`label:has-text("${escaped}")`).filter({ hasText: label }).first();
    if (await labelElement.isVisible({ timeout: 3000 }).catch(() => false)) {
      return labelElement.locator(controlSelector).first();
    }
    return page.locator(`text=${label}`).locator('..').locator(controlSelector).first();
  }

  async function fillLabel(label, value) {
    const input = await controlByLabel(label, 'input,textarea');
    await input.fill(String(value));
  }

  async function selectLabel(label, labelOrValue) {
    const select = await controlByLabel(label, 'select');
    try { await select.selectOption({ label: labelOrValue }); }
    catch { await select.selectOption(labelOrValue); }
  }

  async function expectText(text, timeout = 15000) {
    const deadline = Date.now() + timeout;
    let lastCount = 0;
    while (Date.now() < deadline) {
      const matches = page.getByText(text, { exact: false });
      lastCount = await matches.count().catch(() => 0);
      for (let i = 0; i < lastCount; i++) {
        if (await matches.nth(i).isVisible().catch(() => false)) return;
      }
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    throw new Error(`Timed out waiting for visible text ${text}. Last match count: ${lastCount}`);
  }

  async function waitForStatus(pathName, status, timeoutMs = 90000) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      const items = await api('GET', pathName);
      const list = Array.isArray(items) ? items : items.items ?? [];
      last = list[0];
      const found = list.find((item) => item.status === status);
      if (found) return found;
      await new Promise((resolve) => setTimeout(resolve, 2000));
      await page.reload({ waitUntil: 'domcontentloaded' }).catch(() => {});
    }
    throw new Error(`Timed out waiting for ${pathName} status ${status}. Last first item: ${JSON.stringify(last)}`);
  }
  async function waitForItem(pathName, predicate, description, timeoutMs = 30000) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      const items = await api('GET', pathName);
      const list = Array.isArray(items) ? items : items.items ?? [];
      last = list;
      const found = list.find(predicate);
      if (found) return found;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
    throw new Error(`Timed out waiting for ${description} in ${pathName}. Last list: ${JSON.stringify(last)}`);
  }


  async function persistAuthToken() {
    if (!env.EnvFile || !state.authToken) return;
    try {
      const current = JSON.parse(await fs.readFile(env.EnvFile, 'utf8'));
      current.AuthToken = state.authToken;
      await fs.writeFile(env.EnvFile, JSON.stringify(current, null, 2));
    } catch (error) {
      notes.push({ status: 'warn', text: `Could not persist auth token for reruns: ${error.message}` });
    }
  }
  async function runBootstrap() {
    await go('/');
    await expectText(/Install Chobo|Access token|Chobo/);
    const installVisible = await page.getByRole('button', { name: /Install/i }).first().isVisible().catch(() => false);
    if (installVisible) {
      await screenshot('bootstrap-install-screen', 'First-run install screen is visible before any token exists.');
      await clickButton(/Install/i);
      const tokenBox = page.locator('[aria-label="Initial access token"]');
      await tokenBox.waitFor({ timeout: 30000 });
      state.authToken = (await tokenBox.textContent()).trim();
      if (!state.authToken || state.authToken === 'dev-static-access-token' || state.authToken === 'static-test-token') {
        throw new Error('Bootstrap token was empty or used a forbidden dev/static token.');
      }
      await persistAuthToken();
      await screenshot('bootstrap-token-created', 'One-time token is shown and can be stored before leaving the install screen.');
      await clickButton(/Ready to start/i);
    }
    await go('/');
    if (!state.authToken) throw new Error('No bootstrap token was captured; this runner needs a fresh bootstrap environment or AuthToken in env file.');
    await screenshot('login-screen', 'Login screen accepts the freshly captured bootstrap token.');
    await fillLabel('Access token', state.authToken);
    await clickButton(/Sign in/i);
    await expectText(/Dashboard|Backups|Policies/i, 30000);
    await screenshot('dashboard-after-login', 'Authenticated shell and dashboard render after signing in with the one-time token.');

    const installAgain = await fetch(`${env.BaseUrl}/api/v1/server/install`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ adminUser: '' }) });
    if (installAgain.ok) throw new Error('Install endpoint allowed a second install.');
    const anonymous = await fetch(`${env.BaseUrl}/api/v1/users`);
    if (anonymous.status !== 401) throw new Error(`Anonymous users request returned ${anonymous.status}, expected 401.`);
  }

  async function runCluster() {
    await go('/clusters');
    await clickButton(/^Add$/i);
    await fillLabel('Name', data.sourceCluster.name);
    await selectLabel('Mode', 'Single instance');
    await fillLabel('Access nodes', data.sourceCluster.nodes);
    await fillLabel('Max DOP', '1');
    await screenshot('cluster-create-source', 'Source ClickHouse form uses a reachable Docker service name and conservative Max DOP.');
    await clickButton(/Save cluster/i);
    await waitForItem('clusters', (item) => item.name === data.sourceCluster.name, data.sourceCluster.name);
    await go('/clusters');
    await screenshot('cluster-source-saved', 'Source cluster is saved and visible in the clusters table.');

    await clickButton(/^Add$/i);
    await fillLabel('Name', data.restoreCluster.name);
    await selectLabel('Mode', 'Single instance');
    await fillLabel('Access nodes', data.restoreCluster.nodes);
    await fillLabel('Max DOP', '1');
    await clickButton(/Save cluster/i);
    await waitForItem('clusters', (item) => item.name === data.restoreCluster.name, data.restoreCluster.name);
  }

  async function runStorage() {
    await go('/targets');
    await clickButton(/^Add$/i);
    await fillLabel('Name', data.storage.name);
    await fillLabel('Endpoint', data.storage.endpoint);
    await fillLabel('Bucket', data.storage.bucket);
    await fillLabel('Region', data.storage.region);
    await fillLabel('Path prefix', `ui/${env.TestId}`);
    await fillLabel('Access key', data.storage.accessKey);
    await fillLabel('Secret key', data.storage.secretKey);
    await screenshot('storage-create-minio', 'MinIO storage form uses path-style S3 settings that ChoboServer and ClickHouse can both reach.');
    await clickButton(/Save storage/i);
    await waitForItem('targets', (item) => item.name === data.storage.name, data.storage.name);
    await go('/targets');
    const bodyText = await page.locator('body').innerText();
    if (bodyText.includes(data.storage.secretKey)) throw new Error('Saved storage page exposes the MinIO secret key.');
    await screenshot('storage-minio-saved', 'MinIO target is saved and the secret key is not displayed after save.');
  }

  async function runPolicy() {
    await go('/policies');
    await clickButton(/Add policy/i);
    await fillLabel('Name', data.policy.name);
    await selectLabel('Source cluster', data.sourceCluster.name);
    await selectLabel('Backup storage', data.storage.name);
    const row = page.locator('.rule-row').first();
    await row.locator('select').nth(0).selectOption('Include');
    await row.locator('select').nth(1).selectOption('Exact');
    await row.locator('input').nth(0).fill(data.policy.database);
    await row.locator('select').nth(2).selectOption('Exact');
    await row.locator('input').nth(1).fill(data.policy.table);
    await fillLabel('Min backups to keep', '1');
    await fillLabel('Min full backups', '1');
    await expectText(`${data.policy.database}.${data.policy.table}`, 30000);
    await screenshot('policy-create-selector-preview', 'Policy selector preview includes the real source table that will be backed up.');
    await clickButton(/Save policy/i);
    await waitForItem('policies', (item) => item.name === data.policy.name, data.policy.name);
    await go('/policies');
    await screenshot('policy-saved', 'Policy is saved and visible with source cluster and backup storage context.');
  }

  async function runSchedule() {
    await go('/schedules');
    await clickButton(/Add schedule/i);
    await fillLabel('Name', data.schedule.name);
    await selectLabel('Policy', data.policy.name);
    await selectLabel('Backup type', 'Full');
    await fillLabel('Timezone', data.schedule.timezone);
    await clickButton(/Daily/i);
    await fillLabel('Hour', data.schedule.hour);
    await fillLabel('Minute', data.schedule.minute);
    await expectText(/Next runs|Validating cron|daily/i, 30000);
    await screenshot('schedule-create', 'Schedule editor shows a daily UTC full-backup schedule with validation feedback.');
    await clickButton(/Save schedule|Validate cron before saving/i);
    await waitForItem('schedules', (item) => item.name === data.schedule.name, data.schedule.name);
    await go('/schedules');
    await screenshot('schedule-saved', 'Schedule is saved and appears in the schedules table.');
  }

  async function runBackup() {
    await go('/policies');
    const row = page.locator('tbody tr').filter({ hasText: data.policy.name }).first();
    await row.waitFor({ timeout: 30000 });
    await row.getByRole('button', { name: /Run now/i }).click();
    const runDialog = page.getByRole('dialog', { name: /Run policy now/i });
    await runDialog.waitFor({ timeout: 10000 });
    await screenshot('backup-run-now-dialog', 'Run Now dialog explains full versus regular backup before queueing the policy run.');
    await runDialog.getByRole('button', { name: /Regular backup/i }).click();
    await waitForItem('backups', () => true, 'a queued backup run', 30000);
    await go('/backups');
    await screenshot('backup-queued', 'Manual policy backup was queued from the UI and the backups screen is reachable.');
    const backup = await waitForStatus('backups', 'Succeeded', backupTimeoutMs);
    state.backupId = backup.id;
    await go('/backups');
    await page.locator('tbody tr').filter({ hasText: /Succeeded/i }).first().waitFor({ timeout: 30000 });
    await screenshot('backup-succeeded-list', 'Backups list shows the run succeeded.');
    await page.getByRole('link', { name: /Details/i }).first().click();
    await expectText(/Backup detail|Tables and shards/i, 30000);
    await screenshot('backup-details', isLargeTableScenario ? 'Large backup detail remains usable and exposes size, table/shard state, related logs, and audit while handling a multi-GB table.' : 'Backup detail drawer exposes status, table/shard information, related logs, and audit sections.');
  }


  async function runSchemaBrowser() {
    if (!state.backupId) throw new Error('Schema Browser scenario requires a completed backup id.');
    await go('/schema');
    await expectText(/Schema Browser|Backup/i, 30000);
    await screenshot('schema-browser-empty-or-loading', 'Schema Browser tab is reachable and shows the backup selector before a backup is selected.');
    const backupSelect = page.locator('label select').nth(1);
    await page.waitForFunction((select) => Array.from(select.options).some((option) => option.value), await backupSelect.elementHandle(), { timeout: 30000 });
    const optionValues = await backupSelect.locator('option').evaluateAll((options) => options.map((option) => option.value).filter(Boolean));
    await backupSelect.selectOption(optionValues.includes(state.backupId) ? state.backupId : optionValues[0]);
    await expectText(data.policy.database, 30000);
    await expectText(data.policy.table, 30000);
    await page.getByRole('button', { name: new RegExp(data.policy.table) }).first().click();
    await expectText('CREATE TABLE', 30000);
    await screenshot('schema-browser-table-sql', 'Schema Browser shows a database/table tree and the captured CREATE TABLE SQL for the selected backup.');

    const allDownload = page.waitForEvent('download');
    await page.getByRole('button', { name: /Export all/i }).click();
    const all = await allDownload;
    if (!all.suggestedFilename().includes(state.backupId)) throw new Error(`Unexpected all-schema export file name: ${all.suggestedFilename()}`);

    const databaseDownload = page.waitForEvent('download');
    await page.getByRole('button', { name: /Export database/i }).click();
    const database = await databaseDownload;
    if (!database.suggestedFilename().includes(data.policy.database)) throw new Error(`Unexpected database schema export file name: ${database.suggestedFilename()}`);
    notes.push({ status: 'pass', text: 'Schema Browser export buttons produced downloadable SQL files for all schema and the selected database.' });
  }
  async function runRestore() {
    if (!isLargeTableScenario) await createExistingRestoreTarget();
    await go('/restores/start');
    await page.locator('input[type="radio"]').first().check();
    await screenshot('restore-source-backup', 'Restore wizard starts by choosing the successful backup recovery point.');
    await clickButton(/Continue/i);
    await selectLabel('Target cluster', data.restoreCluster.name);
    const singleNode = page.getByRole('button', { name: /Single node/i });
    if (await singleNode.isVisible().catch(() => false)) await singleNode.click();
    await screenshot('restore-destination', 'Restore destination uses the known-good restore ClickHouse node.');
    await clickButton(/Continue/i);
    const mappingRow = page.getByRole('row', { name: new RegExp(data.policy.table) }).first();
    await mappingRow.locator('input[type="checkbox"]').first().check();
    await mappingRow.locator('input').nth(1).fill(data.restore.database);
    await mappingRow.locator('input').nth(2).fill(data.restore.table);
    if (!isLargeTableScenario) await mappingRow.getByLabel(/Append to existing table/i).check();
    await screenshot('restore-scope-mapping', isLargeTableScenario ? 'Large restore scope maps the multi-GB OnTime table to an isolated target table without the mapping grid becoming unreadable.' : 'Restore scope maps into a pre-existing target table and enables append, making this a destructive restore path.');
    await clickButton(/Continue/i);
    await screenshot('restore-confirmation-ready', 'Restore review is ready; clicking Queue restore must show a visible destructive-action confirmation.');
    await clickButton(/Queue restore/i);
    const dialog = page.getByRole('dialog', { name: /Confirm destructive restore/i });
    await dialog.waitFor({ timeout: 10000 });
    await screenshot('restore-confirmation-dialog', isLargeTableScenario ? 'Visible in-app confirmation dialog is shown before queueing the large restore.' : 'Visible in-app confirmation dialog is shown before appending into an existing restore table.');
    await dialog.getByRole('button', { name: /Confirm restore/i }).click();
    await expectText(/Restore detail|Restores/i, 30000);
    const restore = await waitForStatus('restores', 'Succeeded', restoreTimeoutMs);
    state.restoreId = restore.id;
    await go('/restores');
    await screenshot('restore-succeeded-list', 'Restore history shows the destructive restore succeeded after confirmation.');
    await page.getByRole('link', { name: /Details/i }).first().click();
    await expectText(/Restore detail|Tables|Succeeded/i, 30000);
    await screenshot('restore-details', isLargeTableScenario ? 'Large restore detail exposes terminal status, affected table, logs, and audit after a multi-GB restore.' : 'Restore detail page exposes terminal status and affected tables.');
    await verifyRestoredRows();
  }

  async function runBackupDeleteConfirmation() {
    if (!state.backupId) {
      const backups = await api('GET', 'backups');
      state.backupId = backups[0]?.id ?? null;
    }
    if (!state.backupId) throw new Error('Backup delete confirmation scenario requires a completed backup id.');
    await go('/backups');
    const row = page.locator('tbody tr').filter({ has: page.getByRole('button', { name: /^Delete$/i }) }).first();
    await row.waitFor({ timeout: 30000 });
    await screenshot('backup-delete-confirmation-ready', 'Backup row is ready; clicking Delete must show a visible destructive-action confirmation before the API request is sent.');
    await row.getByRole('button', { name: /^Delete$/i }).click();
    let dialog = page.getByRole('dialog', { name: /Delete backup/i });
    await dialog.waitFor({ timeout: 10000 });
    await screenshot('backup-delete-confirmation-dialog-cancel', 'Visible in-app delete confirmation dialog is shown before deleting backup data.');
    await dialog.getByRole('button', { name: /Cancel/i }).click();
    await page.waitForTimeout(500);
    const afterDismiss = await api('GET', `backups/${state.backupId}`);
    if (/DeleteRequested|Deleted/.test(afterDismiss.status)) {
      throw new Error(`Backup delete proceeded after confirmation was canceled. Status: ${afterDismiss.status}`);
    }
    await screenshot('backup-delete-canceled', 'Canceling the delete confirmation leaves the backup undeleted.');

    await row.getByRole('button', { name: /^Delete$/i }).click();
    dialog = page.getByRole('dialog', { name: /Delete backup/i });
    await dialog.waitFor({ timeout: 10000 });
    await screenshot('backup-delete-confirmation-dialog-confirm', 'Visible in-app delete confirmation dialog is shown before the confirmed destructive API request.');
    await dialog.getByRole('button', { name: /Delete backup/i }).click();
    const deadline = Date.now() + 30000;
    let deleted = null;
    while (Date.now() < deadline) {
      deleted = await api('GET', `backups/${state.backupId}`);
      if (/DeleteRequested|Deleted/.test(deleted.status)) break;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
    if (!deleted || !/DeleteRequested|Deleted/.test(deleted.status)) {
      throw new Error(`Backup delete was not requested after confirmation. Last status: ${deleted?.status}`);
    }
    await go('/backups');
    await screenshot('backup-delete-confirmed', 'Accepting the delete confirmation sends confirmDestructive and the backup enters a delete state.');
  }

  async function runGcCleanupUiCheck() {
    if (!state.backupId) throw new Error('GC cleanup UI check requires a completed backup id.');
    await go('/gc');
    await expectText(state.backupId, 30000);
    await screenshot('large-gc-queue-before-run', 'Garbage Collector page shows the delete-requested large backup with a focused Run item action.');
    await page.getByRole('button', { name: /Run item/i }).first().click();
    const deadline = Date.now() + 10 * 60 * 1000;
    let backup = null;
    while (Date.now() < deadline) {
      backup = await api('GET', `backups/${state.backupId}`);
      if (backup.status === 'ManualDeleted') break;
      await new Promise((resolve) => setTimeout(resolve, 3000));
    }
    if (!backup || backup.status !== 'ManualDeleted') throw new Error(`Large backup did not reach ManualDeleted after GC. Last status: ${backup?.status}`);
    await go('/gc');
    await screenshot('large-gc-after-run', 'Garbage Collector page shows cleanup completed for the large backup and keeps GC-specific logs visible.');
    notes.push({ status: 'pass', text: `Large backup ${state.backupId} reached ManualDeleted through the GC page flow.` });
  }
  async function verifyRestoredRows() {
    if (!env.ComposeFile || !env.ProjectName) {
      notes.push({ status: 'warn', text: 'Skipped restored-row SQL verification because ComposeFile/ProjectName were not in env.' });
      return;
    }
    if (isLargeTableScenario) {
      const sourceQuery = `SELECT count(), min(FlightDate), max(FlightDate), sum(cityHash64(Year, Month, DayofMonth, FlightDate, Reporting_Airline, Flight_Number_Reporting_Airline, Origin, Dest)) FROM ${data.policy.database}.${data.policy.table} FORMAT TSV`;
      const restoreQuery = `SELECT count(), min(FlightDate), max(FlightDate), sum(cityHash64(Year, Month, DayofMonth, FlightDate, Reporting_Airline, Flight_Number_Reporting_Airline, Origin, Dest)) FROM ${data.restore.database}.${data.restore.table} FORMAT TSV`;
      const source = (await execFileAsync('docker', ['compose', '-f', env.ComposeFile, '-p', env.ProjectName, 'exec', '-T', 'clickhouse-source', 'clickhouse-client', '-q', sourceQuery], { cwd: env.RepoRoot, timeout: 300000 })).stdout.trim().replace(/\r/g, '');
      const restored = (await execFileAsync('docker', ['compose', '-f', env.ComposeFile, '-p', env.ProjectName, 'exec', '-T', 'clickhouse-restore', 'clickhouse-client', '-q', restoreQuery], { cwd: env.RepoRoot, timeout: 300000 })).stdout.trim().replace(/\r/g, '');
      if (source !== restored) throw new Error(`Large restored table summary mismatch. Source: ${source} Restored: ${restored}`);
      notes.push({ status: 'pass', text: `Large restored table matched source summary: ${restored}` });
      return;
    }
    const query = `SELECT id, name FROM ${data.restore.database}.${data.restore.table} ORDER BY id FORMAT CSV`;
    const { stdout } = await execFileAsync('docker', ['compose', '-f', env.ComposeFile, '-p', env.ProjectName, 'exec', '-T', 'clickhouse-restore', 'clickhouse-client', '-q', query], { cwd: env.RepoRoot, timeout: 30000 });
    const normalized = stdout.trim().replace(/\r/g, '');
    const expected = '1,"alpha"\n2,"beta"\n3,"gamma"';
    if (normalized !== expected) throw new Error(`Restored rows mismatch. Expected:\n${expected}\nActual:\n${normalized}`);
    notes.push({ status: 'pass', text: 'Restored rows matched expected BackupRestoreSingleNode CSV.' });
  }

  async function runDetails() {
    await go('/backups');
    await page.getByRole('link', { name: /Details/i }).first().click();
    await screenshot('details-backup-route-reload', 'Backup detail remains understandable after returning to the list and reopening details.');
    await go('/restores');
    await page.getByRole('link', { name: /Details/i }).first().click();
    await screenshot('details-restore-route-reload', 'Restore detail remains understandable after returning to history and reopening details.');
  }

  async function runLogsAudit() {
    await go('/logs');
    await expectText(/Logs|Message/i);
    await screenshot('logs-screen', 'Logs screen renders recent operational records with filters and paging controls.');
    await go('/audit');
    await expectText(/Audit|Action|Entity/i);
    await screenshot('audit-screen', 'Audit screen renders configuration and operational audit records.');
    await go('/');
    await screenshot('final-dashboard', 'Final dashboard renders after the full operational journey.');
  }

  async function runFailure() {
    await go('/targets');
    await clickButton(/^Add$/i);
    await fillLabel('Name', 'ui-bad-minio');
    await fillLabel('Endpoint', 'http://backup-s3:9999');
    await fillLabel('Bucket', data.storage.bucket);
    await fillLabel('Region', data.storage.region);
    await fillLabel('Access key', data.storage.accessKey);
    await fillLabel('Secret key', data.storage.secretKey);
    await clickButton(/Save storage/i);
    await expectText('ui-bad-minio');
    await page.getByRole('row', { name: /ui-bad-minio/ }).getByRole('button', { name: /Test/i }).click();
    await page.waitForTimeout(1500);
    await screenshot('failure-bad-storage-feedback', 'Bad MinIO endpoint remains recoverable and should surface useful error feedback.', 'warn');

    const seeded = await api('POST', 'test-hooks/seed-dashboard-failed-backup', {});
    const firstFailureLine = 'Chobo.UiTests.IntentionalDashboardFailureException: first line for dashboard failure preview';
    const expandedOnlyLine = 'InnerException: this extra diagnostic line should only appear after expanding the failure details.';
    await go('/');
    await expectText('ui-dashboard-failure-schedule', 30000);
    const failureRow = page.locator('.failure-summary-row').filter({ hasText: 'ui-dashboard-failure-schedule' }).first();
    await failureRow.waitFor({ timeout: 30000 });
    const rowText = await failureRow.textContent();
    if (rowText.includes(firstFailureLine) || rowText.includes(expandedOnlyLine)) {
      throw new Error(`Dashboard recent failures row showed raw failure text instead of only timing/status context: ${rowText}`);
    }
    await screenshot('failure-dashboard-recent-failure-time-only', 'Dashboard recent failures row shows failure timing and an info button without dumping the exception text.');

    await failureRow.getByRole('button', { name: /Open backup details/i }).click();
    await expectText(/Backup detail|Run status/i, 30000);
    await expectText(firstFailureLine, 30000);
    const previewBodyText = await page.locator('body').textContent();
    if (previewBodyText.includes(expandedOnlyLine)) {
      throw new Error('Backup detail failure preview showed text that should only appear after expanding the modal.');
    }
    await screenshot('failure-dashboard-info-opens-backup-detail', 'Recent failure info button opens the failed backup detail drawer with a shortened failure preview.');

    await page.locator('.backup-detail-drawer').getByRole('button', { name: /^Expand$/i }).click();
    await expectText(expandedOnlyLine, 30000);
    await screenshot('failure-backup-detail-expanded-error', `Backup detail Expand opens the full failure text for seeded failed backup ${seeded.backupId}.`);
  }
  try {
    for (const step of plans[scenario]) {
      if (step === 'bootstrap') await runBootstrap();
      else if (step === 'cluster') await runCluster();
      else if (step === 'storage') await runStorage();
      else if (step === 'policy') await runPolicy();
      else if (step === 'schedule-edit') await runSchedule();
      else if (step === 'backup') await runBackup();
      else if (step === 'schema-browser') await runSchemaBrowser();
      else if (step === 'restore') await runRestore();
      else if (step === 'details') await runDetails();
      else if (step === 'logs-audit') await runLogsAudit();
      else if (step === 'backup-delete-confirmation') await runBackupDeleteConfirmation();
      else if (step === 'gc-cleanup-ui') await runGcCleanupUiCheck();
      else if (step === 'failure') await runFailure();
    }
    await writeArtifacts('passed');
  } catch (error) {
    await failureShot(error);
    await writeArtifacts('failed', error);
    throw error;
  } finally {
    if (!args.keepOpen) await browser.close();
  }

  async function writeArtifacts(status, error = null) {
    await fs.writeFile(path.join(env.UiRoot, 'console.log'), consoleEntries.join('\n') + '\n');
    await fs.writeFile(path.join(env.UiRoot, 'network.log'), networkEntries.join('\n') + '\n');
    const index = ['# Chobo UI Screenshots', ''];
    screenshots.forEach((shot, indexNumber) => {
      index.push(`${indexNumber + 1}. [${shot.file}](./${shot.file}) - ${shot.status.toUpperCase()} - ${shot.route} - ${shot.note}`);
      index.push(`   ![${shot.step}](./${shot.file})`);
    });
    await fs.writeFile(path.join(screenshotsDir, 'index.md'), index.join('\n') + '\n');

    const report = [
      '# Chobo UI Test Report',
      '',
      `- Status: ${status}`,
      `- Scenario: ${scenario}`,
      `- Base URL: ${env.BaseUrl}`,
      `- Screenshots: ${screenshotsDir}`,
      `- Console issues: ${consoleEntries.filter((entry) => /error|pageerror/i.test(entry)).length}`,
      `- Network HTTP >= 400: ${networkEntries.length}`,
      '',
      '## QA Notes',
      ...notes.map((note) => `- ${note.status.toUpperCase()}: ${note.text}`),
      ...(error ? ['', '## Failure', '', '```', error.stack ?? error.message, '```'] : [])
    ];
    await fs.writeFile(path.join(env.UiRoot, 'report.md'), report.join('\n') + '\n');
    await fs.writeFile(path.join(env.UiRoot, 'result.json'), JSON.stringify({ status, scenario, env, screenshots, notes, consoleEntries, networkEntries, error: error ? String(error.stack ?? error.message) : null }, null, 2));
  }
}

function routeOf(url) {
  try {
    const parsed = new URL(url);
    return `${parsed.pathname}${parsed.search}${parsed.hash}` || '/';
  } catch { return url; }
}

main().catch((error) => {
  console.error(error.stack ?? error.message);
  process.exitCode = 1;
});
