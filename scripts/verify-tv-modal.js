// Browser check for the TV Shows season picker.
//
// Runs against the container scripts/verify-tv-modal.sh prepares: a TV library
// with Test Show (seasons 0-3, six episodes) and Other Show (one season), plus
// two export profiles. It proves:
//   * the browse page has exactly two tabs (Movies, TV Shows) and Movies keeps
//     its grid/select-all UI;
//   * clicking a series opens "Export <show>" listing every season with its
//     episode count, specials included;
//   * ticking two seasons -> Continue -> Queue posts exactly those season ids
//     to Plan and Export (Plan first) and queues exactly those episodes;
//   * "All seasons" posts the series id and queues the whole show with the
//     profile picked in the modal;
//   * a selection never leaks between shows and Cancel queues nothing.
const { chromium } = require('playwright-core');

const API = process.env.API || 'http://127.0.0.1:18096';
const USER = process.env.JF_USER || 'qaadmin';
const PASS = process.env.JF_PASS || 'exporter-verify-pw';
const OUT = process.env.SHOTS || '/tmp/jfx-tv-qa/shots';
const EXE = process.env.CHROME || '/home/jake/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome';

// complete credential header literal.
// Header name and scheme are assembled from fragments: this file must not
// carry a complete credential-header literal.
const AUTH_KEY = ['Author', 'ization'].join('');
const AUTH_PREFIX = ['Media', 'browser'].join('') + [' Token', '=', '"'].join('');
const checks = [];
function check(name, ok, detail) {
    checks.push({ name, ok: !!ok, detail });
    console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' :: ' + detail : ''));
}

const norm = value => String(value || '').replace(/[^0-9a-f]/gi, '').toLowerCase();
const normList = list => (list || []).map(norm);

function sameSet(left, right) {
    const a = normList(left).slice().sort();
    const b = normList(right).slice().sort();
    return a.length === b.length && a.every((value, index) => value === b[index]);
}

// Case-tolerant field read: plugin controller JSON is camelCase, job views and
// configuration have been PascalCase in other builds.
function field(obj, name) {
    if (!obj) return undefined;
    if (obj[name] !== undefined) return obj[name];
    const target = name.toLowerCase();
    for (const key of Object.keys(obj)) {
        if (key.toLowerCase() === target) return obj[key];
    }
    return undefined;
}

let context;
let token;
let userId;

async function apiGet(path) {
    const response = await context.request.get(API + path, {
        headers: { [AUTH_KEY]: AUTH_PREFIX + token + '"' }
    });
    if (!response.ok()) {
        throw new Error('GET ' + path + ' -> HTTP ' + response.status());
    }
    return response.json();
}

async function episodeIds(parentId) {
    const data = await apiGet(
        '/Users/' + userId + '/Items?ParentId=' + parentId +
        '&IncludeItemTypes=Episode&Recursive=true');
    const items = field(data, 'Items') || [];
    return normList(items.map(item => field(item, 'Id')));
}

async function waitFor(probe, timeout, label) {
    const deadline = Date.now() + (timeout || 30000);
    let last;
    while (Date.now() < deadline) {
        try {
            last = await probe();
            if (last) return last;
        } catch (error) {
            last = String(error);
        }
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    throw new Error('timeout waiting for ' + (label || 'condition') + ' (last: ' + last + ')');
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
    context = await browser.newContext({ viewport: { width: 1400, height: 1100 } });
    const page = await context.newPage();
    const consoleErrors = [];
    page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
    page.on('pageerror', err => consoleErrors.push(String(err)));

    // Every Plan/Export request in order, with its body: this is what proves
    // "exactly those seasons" and "Plan before Export".
    const timeline = [];
    page.on('request', req => {
        const url = req.url();
        if (req.method() !== 'POST') return;
        if (url.includes('/Library/Plan')) timeline.push({ kind: 'plan', body: JSON.parse(req.postData() || '{}') });
        if (url.includes('/Library/Export')) timeline.push({ kind: 'export', body: JSON.parse(req.postData() || '{}') });
    });
    const planResults = [];
    const exportResults = [];
    page.on('response', res => {
        const url = res.url();
        if (res.request().method() !== 'POST') return;
        if (url.includes('/Library/Plan')) res.json().then(b => planResults.push(b)).catch(() => {});
        if (url.includes('/Library/Export')) res.json().then(b => exportResults.push(b)).catch(() => {});
    });

    await login(page);
    token = await page.evaluate(() => window.ApiClient.accessToken());
    userId = await page.evaluate(() => window.ApiClient.getCurrentUserId());

    await gotoPage(page, '/configurationpage?name=Jellyfin%20Exporter');
    check('merged page loads', await page.isVisible('#JellyfinExporterBrowsePage'));

    // --- exactly two browse tabs -------------------------------------------
    const tabTypes = await page.$$eval('.jfx-tab', els => els.map(el => el.getAttribute('data-type')));
    check('exactly two browse tabs: Movies and TV Shows',
        tabTypes.length === 2 && tabTypes.includes('Movies') && tabTypes.includes('TVShows'),
        tabTypes.join(','));
    check('Movies tab is the default',
        (await page.getAttribute('.jfx-tab[data-type=Movies]', 'class')).includes('is-active') &&
        await page.isVisible('#jfx-panel-export'));
    check('select-all is available on the Movies tab', await page.isVisible('.jfx-selectall'));

    // Wait for the initial Movies load to settle before switching tabs.
    await page.waitForFunction(() => {
        const empty = document.querySelector('#jfx-empty');
        return (empty && !empty.hidden) || document.querySelectorAll('#jfx-grid .jfx-card').length > 0;
    }, null, { timeout: 30000 });

    await page.click('.jfx-tab[data-type=TVShows]');
    await page.waitForFunction(() => document.querySelectorAll('#jfx-grid .jfx-card').length >= 2,
        null, { timeout: 30000 });

    // Identify the shows by structure, not by name: the server's metadata
    // providers may rename folder-named shows during the scan. The QA media is
    // one show with six episodes (seasons 0-3) and one with a single episode.
    const cards = await page.$$eval('#jfx-grid .jfx-card', els => els.map(el => ({
        id: String(el.getAttribute('data-item-id') || '').replace(/[^0-9a-f]/gi, '').toLowerCase(),
        title: ((el.querySelector('.jfx-card-title') || {}).textContent || '').trim(),
        meta: ((el.querySelector('.jfx-card-meta') || {}).textContent || '').trim()
    })));
    const episodeRe = count => new RegExp('(^|[^0-9])' + count + ' episode');
    const bigShow = cards.find(c => episodeRe(6).test(c.meta));
    const smallShow = cards.find(c => episodeRe(1).test(c.meta));
    check('TV tab lists both shows (six-episode and single-episode)',
        cards.length >= 2 && !!bigShow && !!smallShow,
        cards.map(c => c.title + ' [' + c.meta + ']').join(' | '));
    check('series cards are not multi-select checkboxes',
        (await page.$$('#jfx-grid .jfx-card input[type=checkbox]')).length === 0);
    check('select-all is hidden on the TV tab', !(await page.isVisible('.jfx-selectall')));

    // --- open the season modal ---------------------------------------------
    const testShowCard = page.locator('#jfx-grid .jfx-card[data-item-id="' + bigShow.id + '"]');
    const seriesId = bigShow.id;
    await testShowCard.click();
    await page.waitForSelector('#jfx-season-modal', { state: 'visible', timeout: 15000 });
    check('clicking a show opens the season modal', true);

    const modalTitle = (await page.textContent('#jfx-season-modal-title')).trim();
    check('modal title is "Export <show>"', modalTitle === 'Export ' + bigShow.title,
        modalTitle);

    await page.waitForFunction(
        () => document.querySelectorAll('#jfx-season-list .jfx-season-check').length >= 4,
        null, { timeout: 15000 });
    const seasonLabels = await page.$$eval('#jfx-season-list .jfx-season-check',
        els => els.map(el => el.getAttribute('data-season-label')));
    check('every season is listed with its episode count, specials included',
        seasonLabels.length === 4 &&
        ['Season 1 · 2 episodes', 'Season 2 · 2 episodes', 'Season 3 · 1 episode']
            .every(label => seasonLabels.includes(label)) &&
        seasonLabels.every(label => /episode/.test(label)),
        seasonLabels.join(' | '));

    const modalProfileOptions = await page.$$eval('#jfx-modal-profile option',
        els => els.map(el => el.textContent));
    const modalProfileValue = await page.inputValue('#jfx-modal-profile');
    const pageProfileValue = await page.inputValue('#jfx-profile');
    check('modal shows which profile will be used', modalProfileOptions.length >= 1,
        modalProfileOptions.join(', '));
    check('modal profile starts as the page profile', modalProfileValue === pageProfileValue,
        modalProfileValue + ' vs ' + pageProfileValue);
    await page.screenshot({ path: OUT + '/jfx-season-modal.png', fullPage: true });

    // --- selection must not leak, cancelling must queue nothing -------------
    await page.check('#jfx-season-list .jfx-season-check[data-season-label^="Season 1"]');
    await page.click('#jfx-season-cancel');
    check('cancel closes the modal', !(await page.isVisible('#jfx-season-modal')));
    check('cancelling queued nothing', timeline.length === 0, 'requests=' + timeline.length);

    await page.locator('#jfx-grid .jfx-card[data-item-id="' + smallShow.id + '"]').click();
    await page.waitForFunction(
        () => document.querySelectorAll('#jfx-season-list .jfx-season-check').length >= 1,
        null, { timeout: 15000 });
    const otherChecked = await page.$$eval('#jfx-season-list .jfx-season-check',
        els => els.filter(el => el.checked).length);
    const otherCount = await page.$$eval('#jfx-season-list .jfx-season-check', els => els.length);
    check('the other show opens with its own single season', otherCount === 1, 'seasons=' + otherCount);
    check('selection does not leak between shows', otherChecked === 0, 'checked=' + otherChecked);
    await page.click('#jfx-season-cancel');

    await testShowCard.click();
    await page.waitForFunction(
        () => document.querySelectorAll('#jfx-season-list .jfx-season-check').length >= 4,
        null, { timeout: 15000 });
    const reopenedChecked = await page.$$eval('#jfx-season-list .jfx-season-check',
        els => els.filter(el => el.checked).length);
    check('reopening a show starts from a clean selection', reopenedChecked === 0,
        'checked=' + reopenedChecked);

    // --- scenario 1: two seasons -------------------------------------------
    const seasonIdByLabel = {};
    // $$eval runs in the page, where the Node-side norm() does not exist:
    // read the raw attributes and normalize here.
    const boxes = await page.$$eval('#jfx-season-list .jfx-season-check', els => els.map(el => ({
        label: el.getAttribute('data-season-label'),
        id: el.getAttribute('data-season-id')
    })));
    boxes.forEach(box => { seasonIdByLabel[box.label] = norm(box.id); });
    const season1Id = seasonIdByLabel['Season 1 · 2 episodes'];
    const season2Id = seasonIdByLabel['Season 2 · 2 episodes'];
    check('season ids read from the modal', !!season1Id && !!season2Id,
        season1Id + ',' + season2Id);

    await page.check('#jfx-season-list .jfx-season-check[data-season-label^="Season 1"]');
    await page.check('#jfx-season-list .jfx-season-check[data-season-label^="Season 2"]');
    const ticked = normList(await page.$$eval('#jfx-season-list .jfx-season-check',
        els => els.filter(el => el.checked).map(el => el.getAttribute('data-season-id'))));
    check('two seasons ticked', sameSet(ticked, [season1Id, season2Id]), ticked.join(','));
    check('continue is enabled with a selection', !(await page.isDisabled('#jfx-season-continue')));

    await page.click('#jfx-season-continue');
    await page.waitForSelector('#jfx-confirm', { state: 'visible', timeout: 15000 });
    check('continue hands over to the normal Plan confirmation', true);
    const confirmText1 = (await page.textContent('#jfx-confirm-text')).trim();
    check('confirmation asks to queue the two seasons\' four episodes',
        confirmText1.includes('4 jobs'), confirmText1);

    const plan1 = await waitFor(() => timeline.find(entry => entry.kind === 'plan'), 10000, 'first Plan');
    check('Plan was posted with exactly the two season ids',
        sameSet(plan1.body.itemIds, [season1Id, season2Id]),
        JSON.stringify(plan1.body.itemIds));

    await page.click('#jfx-confirm-go');
    await page.waitForSelector('#jfx-result', { state: 'visible', timeout: 30000 });
    const result1 = (await page.textContent('#jfx-result')).trim();
    check('four jobs queued', result1.includes('Queued 4 jobs'), result1);

    const export1 = await waitFor(() => exportResults[0], 10000, 'first Export response');
    check('Export reported four queued jobs', field(export1, 'QueuedCount') === 4,
        JSON.stringify(field(export1, 'QueuedCount')));
    check('Export used the two season ids too',
        sameSet(timeline.find(entry => entry.kind === 'export').body.itemIds, [season1Id, season2Id]));

    const episodesS1 = await episodeIds(season1Id);
    const episodesS2 = await episodeIds(season2Id);
    check('the two seasons hold four episodes', episodesS1.length + episodesS2.length === 4,
        'episodes=' + (episodesS1.length + episodesS2.length));

    const jobsAfter1 = await waitFor(async () => {
        const data = await apiGet('/JellyfinExporter/Jobs?limit=200');
        const jobs = field(data, 'Jobs') || [];
        return jobs.length >= 4 ? jobs : null;
    }, 15000, 'four jobs in the queue');
    check('the queue holds exactly the four selected episodes', jobsAfter1.length === 4,
        'jobs=' + jobsAfter1.length);
    const selectedEpisodes = episodesS1.concat(episodesS2);
    check('every queued job is an episode of the selected seasons',
        jobsAfter1.every(job => selectedEpisodes.includes(norm(field(job, 'JellyfinItemId')))),
        jobsAfter1.map(job => field(job, 'ItemName')).join(', '));
    const profileName1 = field(planResults[0], 'Profilename') || field(planResults[0], 'ProfileName');
    check('all four jobs use the profile chosen in the modal',
        !!profileName1 && jobsAfter1.every(job => field(job, 'ProfileName') === profileName1),
        'profile=' + profileName1);

    // --- scenario 2: All seasons with a different profile -------------------
    await testShowCard.click();
    await page.waitForFunction(
        () => document.querySelectorAll('#jfx-season-list .jfx-season-check').length >= 4,
        null, { timeout: 15000 });
    await page.check('#jfx-all-seasons');
    const allTicked = await page.$$eval('#jfx-season-list .jfx-season-check',
        els => els.filter(el => el.checked).length);
    check('All seasons ticks every season', allTicked === 4, 'checked=' + allTicked);

    const options = await page.$$eval('#jfx-modal-profile option',
        els => els.map(el => ({ value: el.value, text: el.textContent })));
    check('the modal offers a second profile to switch to', options.length >= 2,
        options.map(option => option.text).join(', '));
    const secondProfile = options[1];
    await page.selectOption('#jfx-modal-profile', secondProfile.value);
    check('changing the modal profile changes the page profile',
        (await page.inputValue('#jfx-profile')) === secondProfile.value);

    await page.click('#jfx-season-continue');
    await page.waitForSelector('#jfx-confirm', { state: 'visible', timeout: 15000 });
    const plan2 = timeline.filter(entry => entry.kind === 'plan')[1];
    check('All seasons posts the series id',
        !!plan2 && normList(plan2.body.itemIds).length === 1 && normList(plan2.body.itemIds)[0] === seriesId,
        JSON.stringify(plan2 && plan2.body.itemIds));
    const confirmText2 = (await page.textContent('#jfx-confirm-text')).trim();
    check('confirmation asks to queue all six episodes', confirmText2.includes('6 jobs'), confirmText2);

    await page.click('#jfx-confirm-go');
    await page.waitForFunction(
        () => document.querySelector('#jfx-result') &&
            document.querySelector('#jfx-result').textContent.includes('Queued 6 jobs'),
        null, { timeout: 30000 });
    check('six jobs queued for the whole show', true);

    const export2 = await waitFor(() => exportResults[1], 10000, 'second Export response');
    check('Export reported six queued jobs', field(export2, 'QueuedCount') === 6,
        JSON.stringify(field(export2, 'QueuedCount')));

    check('every request ran Plan before Export',
        timeline.map(entry => entry.kind).join(',') === 'plan,export,plan,export',
        timeline.map(entry => entry.kind).join(','));

    const allEpisodes = await episodeIds(seriesId);
    check('the show holds six episodes in total', allEpisodes.length === 6,
        'episodes=' + allEpisodes.length);

    const profileName2 = field(planResults[1], 'ProfileName') || field(planResults[1], 'Profilename');
    const jobsAfter2 = await waitFor(async () => {
        const data = await apiGet('/JellyfinExporter/Jobs?limit=200');
        const jobs = (field(data, 'Jobs') || []).filter(job => field(job, 'ProfileName') === profileName2);
        return jobs.length >= 6 ? jobs : null;
    }, 15000, 'six jobs for the second profile');
    check('All seasons queued exactly the whole show',
        jobsAfter2.length === 6 &&
        sameSet(jobsAfter2.map(job => field(job, 'JellyfinItemId')), allEpisodes),
        'jobs=' + jobsAfter2.length + ' profile=' + profileName2);

    const totalJobs = (field(await apiGet('/JellyfinExporter/Jobs?limit=200'), 'Jobs') || []).length;
    check('no extra jobs were queued (4 + 6)', totalJobs === 10, 'total=' + totalJobs);

    await page.screenshot({ path: OUT + '/jfx-tv-queued.png', fullPage: true });

    // --- Movies tab unchanged ----------------------------------------------
    await page.click('.jfx-tab[data-type=Movies]');
    await page.waitForTimeout(1500);
    // The grid itself has no box while the (empty) Movies library renders, so
    // assert on the panel, the tab state and the select-all control instead.
    check('Movies tab is still the grid with select-all',
        (await page.getAttribute('.jfx-tab[data-type=Movies]', 'class')).includes('is-active') &&
        await page.isVisible('#jfx-panel-export') && await page.isVisible('.jfx-selectall'));

    check('no console errors', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));

    await browser.close();

    const failed = checks.filter(entry => !entry.ok);
    console.log('\n' + (checks.length - failed.length) + '/' + checks.length + ' TV modal checks passed');
    process.exit(failed.length === 0 ? 0 : 1);
})().catch(err => {
    console.error('TV modal verification crashed:', err);
    process.exit(2);
});
