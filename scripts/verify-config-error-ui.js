// Browser check for the settings page's save-failure path.
//
// Jellyfin's own `POST /Plugins/{guid}/Configuration` answers a rejected save
// with a fixed `Error processing request.` body, so the page cannot read the
// reason from that response. What it can do (and now does) is re-run
// `POST /JellyfinExporter/Profiles/Validate` for the same payload and show the
// validator's message.
//
// This script drives that path against a real server: it adds a profile whose
// output directory the server cannot write (/proc/...), lets the page's own
// pre-save Validate pass (fulfilled once, simulating the earlier check that
// passed), and then asserts the real rejected save ends in a banner naming the
// validator message and that nothing was saved.
const { chromium } = require('playwright-core');

const API = process.env.API || 'http://127.0.0.1:18110';
const USER = process.env.JF_USER || 'qaadmin';
const PASS = process.env.JF_PASS || 'cleanup-verify-pw';
const OUT = process.env.SHOTS || '/tmp/jfx-cleanup-qa-run';
const EXE = process.env.CHROME || '/home/jake/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome';
const PLUGIN_GUID = 'b0473c79-36c1-4386-9006-f941031bbea2';
const UNWRITABLE = process.env.UNWRITABLE || '/proc/jfx-export-nope';
const BEFORE_PROFILES = parseInt(process.env.BEFORE_PROFILES || '3', 10);

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
    const context = await browser.newContext({ viewport: { width: 1400, height: 1100 } });
    const page = await context.newPage();
    // The rejected save is an expected HTTP 400, which Chromium reports as a
    // console "Failed to load resource" error. Only real script errors count.
    const consoleErrors = [];
    const expectedRejection = text => /Failed to load resource/.test(text) && /40[0-9]/.test(text);
    page.on('console', msg => {
        if (msg.type() === 'error' && !expectedRejection(msg.text())) consoleErrors.push(msg.text());
    });
    page.on('pageerror', err => consoleErrors.push(String(err)));

    await login(page);

    // The page validates before saving. The first call passes (the directory
    // looked fine then); every later call goes to the server, which is how the
    // page learns the reason after the save is rejected.
    let validateCalls = 0;
    await page.route('**/JellyfinExporter/Profiles/Validate', async route => {
        validateCalls += 1;
        if (validateCalls === 1) {
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({ Valid: true, Issues: [] })
            });
            return;
        }

        await route.continue();
    });

    await gotoPage(page, '/configurationpage?name=Jellyfin%20Exporter');
    check('settings page loads', await page.isVisible('#JellyfinExporterConfigPage'));

    // Add a profile the page accepts (absolute path) but the server rejects.
    await page.click('#jfx-add-profile');
    await page.waitForSelector('#jfx-profile-form', { state: 'visible' });
    await page.fill('#jfx-name', 'Race Profile');
    await page.fill('#jfx-directory', UNWRITABLE);
    await page.click('#jfx-apply-profile');
    await page.waitForTimeout(1500);

    const profilesBefore = await page.locator('#jfx-profiles-body tr').count();
    check('the profile was added to the draft', profilesBefore === BEFORE_PROFILES + 1, 'rows=' + profilesBefore);

    await page.click('#jfx-save');

    let banner = '';
    for (let i = 0; i < 60; i++) {
        banner = (await page.locator('#jfx-config-errors').innerText().catch(() => '')) || '';
        if (banner.includes('The server rejected the configuration')) {
            break;
        }
        await page.waitForTimeout(500);
    }

    await page.screenshot({ path: OUT + '/jfx-config-rejected.png', fullPage: true });
    check('the save-failure banner appears', banner.includes('The server rejected the configuration'),
        banner.replace(/\s+/g, ' ').slice(0, 200));
    check('the banner names the HTTP status', banner.includes('HTTP 400'), banner.replace(/\s+/g, ' ').slice(0, 200));
    check('the banner carries the validator message',
        /not writable|does not exist|is not usable|must be/i.test(banner),
        banner.replace(/\s+/g, ' ').slice(0, 200));
    check('the banner is not the generic endpoint text alone',
        !/^The server rejected the configuration \(HTTP 400\):? Error processing request\.?$/.test(banner.trim()),
        banner.replace(/\s+/g, ' ').slice(0, 200));
    check('the rejected profile is highlighted in the table',
        (await page.locator('#jfx-profiles-body tr .jfx-field-error').count()) >= 1,
        'rows with a field error=' + (await page.locator('#jfx-profiles-body tr .jfx-field-error').count()));

    // Nothing was saved: the stored configuration still has the original profiles.
    const token = await page.evaluate(() => window.ApiClient.accessToken());
    const stored = await context.request.get(
        API + '/Plugins/' + PLUGIN_GUID + '/Configuration',
        { headers: { Authorization: 'MediaBrowser Token="' + token + '"' } });
    const config = await stored.json();
    const saved = (config.Profiles || config.profiles || []);
    check('the rejected save stored nothing',
        saved.length === BEFORE_PROFILES && !saved.some(p => (p.Name || p.name) === 'Race Profile'),
        'stored profiles=' + saved.length);

    // The same reason is readable through the plugin's own endpoint.
    const invalidProfiles = saved.map((profile, index) => Object.assign({}, profile, {
        OutputDirectory: index === 0 ? UNWRITABLE : (profile.OutputDirectory || profile.outputDirectory)
    }));
    const validated = await context.request.post(API + '/JellyfinExporter/Profiles/Validate', {
        headers: { Authorization: 'MediaBrowser Token="' + token + '"', 'Content-Type': 'application/json' },
        data: {
            Profiles: invalidProfiles,
            MaxConcurrentExports: config.MaxConcurrentExports || config.maxConcurrentExports || 1,
            JobHistoryLimit: config.JobHistoryLimit || config.jobHistoryLimit || 25,
            DefaultProfileId: config.DefaultProfileId || config.defaultProfileId || null
        }
    });
    const validation = await validated.json();
    // `Valid || Valid` would turn a false value into undefined: read the key that exists.
    const validFlag = Object.prototype.hasOwnProperty.call(validation, 'Valid') ? validation.Valid : validation.valid;
    const validationIssues = validation.Issues || validation.issues || [];
    check('the Validate endpoint reports the reason programmatically',
        validFlag === false && validationIssues.length > 0,
        JSON.stringify(validation).slice(0, 200));

    check('no console errors', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));

    await browser.close();

    const failed = checks.filter(c => !c.ok);
    console.log('\n' + (checks.length - failed.length) + '/' + checks.length + ' UI checks passed');
    process.exit(failed.length === 0 ? 0 : 1);
})().catch(err => {
    console.error('UI verification crashed:', err);
    process.exit(2);
});
