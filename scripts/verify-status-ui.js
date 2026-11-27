const { chromium } = require('playwright-core');

const API = process.env.API || 'http://127.0.0.1:18100';
const USER = process.env.JF_USER || 'qaadmin';
const PASS = process.env.JF_PASS || 'qa-password-123';
const EXPECT_LIMIT = process.env.EXPECT_LIMIT || '200';
const OUT = process.env.SHOTS || '/tmp/jfx-status-qa';
const EXE = process.env.CHROME || '/home/jake/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome';

const checks = [];
function check(name, ok, detail) {
    checks.push({ name, ok: !!ok, detail });
    console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' :: ' + detail : ''));
}

async function login(page) {
    await page.goto(API + '/web/index.html', { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#txtManualName', { timeout: 30000 });
    await page.fill('#txtManualName', USER);
    await page.fill('#txtManualPassword', PASS);
    await page.click('button[type=submit]');
    await page.waitForFunction(() => window.ApiClient && ApiClient.accessToken(), null, { timeout: 30000 });
    await page.waitForTimeout(1500);
}

async function gotoPage(page, route) {
    await page.goto(API + '/web/index.html?x=' + Date.now() + '#' + route, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
}

(async () => {
    const browser = await chromium.launch({ executablePath: EXE, args: ['--no-sandbox'] });
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage();
    const consoleErrors = [];
    page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
    page.on('pageerror', err => consoleErrors.push(String(err)));

    await login(page);

    // --- Settings: history setting and the status-page link
    await gotoPage(page, '/configurationpage?name=Jellyfin%20Exporter');
    check('settings page loads', await page.isVisible('#JellyfinExporterBrowsePage'));
    check('status page button is on the page header', await page.isVisible('#jfx-open-status'));
    // The profile editor and global settings live on the Profiles tab of the
    // merged page (the plugin's single main-menu page).
    await page.click('#jfx-tab-profiles');
    check('profiles tab shows the settings form', await page.isVisible('#jfx-save'));
    check('job history setting present', await page.isVisible('#jfx-history-limit'));
    const historyValue = await page.inputValue('#jfx-history-limit');
    check('job history setting shows the saved value', historyValue === EXPECT_LIMIT, 'value=' + historyValue);
    await page.screenshot({ path: OUT + '/jfx-settings.png' });

    // --- Status page
    await gotoPage(page, '/configurationpage?name=Export%20Status');
    await page.waitForFunction(() => {
        const table = document.querySelector('#jfx-history table');
        return table && table.querySelectorAll('tbody tr').length > 0;
    }, null, { timeout: 30000 });
    check('status page loads', await page.isVisible('#JellyfinExporterStatusPage'));
    check('summary chips render', (await page.locator('#jfx-summary .jfx-chip').count()) >= 5);

    const historyRows = await page.locator('#jfx-history tbody tr').count();
    check('history table lists finished exports', historyRows >= 3, 'rows=' + historyRows);
    const historyText = await page.locator('#jfx-history').innerText();
    check('completed row shows the output path', historyText.includes('/exports/Movies/'), '');
    check('failed row shows the ffmpeg error', historyText.toLowerCase().includes('no space left on device'));

    const failedRows = await page.locator('#jfx-failed tbody tr').count();
    check('failed exports section renders', failedRows >= 1, 'rows=' + failedRows);
    check('failed row offers retry', (await page.locator('#jfx-failed tbody button', { hasText: 'Retry' }).count()) >= 1);
    await page.screenshot({ path: OUT + '/jfx-status-history.png', fullPage: true });

    // --- Start an export from the page and watch the live progress bar
    const completable = page.locator('#jfx-history tbody tr', { hasText: 'Completed' }).first();
    await completable.locator('button', { hasText: 'Retry' }).click();

    await page.waitForFunction(() => document.querySelectorAll('#jfx-active .jfx-active-card').length > 0, null, { timeout: 30000 });
    check('active card appears after retry', true);
    // The fill itself starts at width 0 (no box), so the track proves the bar rendered.
    check('active card shows a progress bar', await page.isVisible('#jfx-active .jfx-progress'));
    check('active card shows a cancel button', (await page.locator('#jfx-active button', { hasText: 'Cancel' }).count()) >= 1);

    const widths = [];
    for (let i = 0; i < 12; i++) {
        const fill = page.locator('#jfx-active .jfx-progress-fill').first();
        if (await fill.count() === 0) {
            break; // the export finished; the banner assertion below still runs
        }

        const width = await fill.evaluate(el => parseFloat(el.style.width) || 0).catch(() => null);
        if (width === null) {
            break;
        }

        widths.push(width);
        await page.screenshot({ path: OUT + '/jfx-status-running-' + i + '.png' });
        await page.waitForTimeout(1500);
    }

    const grew = widths.length >= 2 && Math.max(...widths) > Math.min(...widths);
    check('progress bar advances while the export runs', grew, 'widths=' + widths.join(','));

    await page.waitForFunction(() => {
        const banner = document.querySelector('#jfx-notices .jfx-banner-success');
        return banner && banner.textContent.includes('Export completed');
    }, null, { timeout: 180000 });
    check('completion banner appears on the page', true);

    await page.waitForFunction(() => {
        const cards = document.querySelectorAll('#jfx-active .jfx-active-card');
        return cards.length === 0;
    }, null, { timeout: 180000 });
    await page.waitForFunction(() => {
        const table = document.querySelector('#jfx-history table');
        return table && table.querySelectorAll('tbody tr').length > 0;
    }, null, { timeout: 30000 });
    await page.waitForTimeout(3500);
    const afterText = await page.locator('#jfx-history').innerText();
    check('job moved to history with a path', afterText.toLowerCase().includes('completed') && afterText.includes('/exports/Movies/'), afterText.slice(0, 160).replace(/\s+/g, ' '));
    await page.screenshot({ path: OUT + '/jfx-status-completed.png', fullPage: true });

    // --- Jellyfin's own notification surface: the activity log
    await gotoPage(page, '/dashboard/activity');
    await page.waitForTimeout(4000);
    const activityText = await page.locator('body').innerText();
    check('activity page shows the completion notification', activityText.includes('Export completed'));

    // The page paginates, so older entries are read through the same API the
    // page uses; the newest failure must still be in the log.
    const token = await page.evaluate(() => window.ApiClient.accessToken());
    const logResponse = await context.request.get(
        API + '/System/ActivityLog/Entries?limit=200',
        { headers: { Authorization: 'MediaBrowser Token="' + token + '"' } });
    const log = await logResponse.json();
    const entries = log.Items || log.items || [];
    check('activity log records the failure notification',
        entries.some(e => (e.Name || e.name) === 'Export failed'),
        entries.slice(0, 3).map(e => e.Name || e.name).join(','));
    await page.screenshot({ path: OUT + '/jfx-activity.png', fullPage: true });

    check('no console errors', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));

    await browser.close();

    const failed = checks.filter(c => !c.ok);
    console.log('\n' + (checks.length - failed.length) + '/' + checks.length + ' UI checks passed');
    process.exit(failed.length === 0 ? 0 : 1);
})().catch(err => {
    console.error('UI verification crashed:', err);
    process.exit(2);
});
