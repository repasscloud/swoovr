// Issue 282: the member web screens for location (D345, D348, D352; wording approved on issue 271 §12–§14). Mocked
// geolocation only: allow, deny, unsupported, low accuracy, Appear off while browsing and switching scopes; the
// location settings card; the Travel pre-arrival switches and Check in. No test or development notices (D321).
// Owned local stack with the GeoNames import; every person is fictional.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, sql, claims, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;
const audit = require('../accessibility/audit.cjs');

const ADELAIDE = 2078025, MELBOURNE = 2158177;
const HERE = { latitude: -34.9285, longitude: 138.6007 };
const text = {
  prompt: 'Share your location to use Near me. Swoovr receives readings while Near me is open and derives limited daily outcomes to help identify an outdated Home City. You can browse without appearing in nearby searches. Read our Privacy Notice for the details.',
  denied: "Location is blocked for Swoovr. You can allow it in your browser's site settings, then try again. Home market remains available without location.",
  unsupported: 'This browser cannot share your location. You can continue using Home market.',
  inaccurate: 'Near me needs a location reading accurate to within 2 km. Try again with precise location enabled, or continue using Home market.',
};

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    // web: signs in on the web, home Melbourne (not open), in Adelaide by GPS; loc: an Adelaide local; app: appears nearby.
    const names = ['web', 'loc', 'app'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('nmw-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, ids = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional NMW ' + n, homeCityId: n === 'web' ? MELBOURNE : ADELAIDE } });
      assert.equal(saved.status, 200, n);
      pid[n] = saved.body.publicId;
      ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
      await call(tokens[n], '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    await call(tokens.app, '/api/me/location-settings', { method: 'PUT', body: { appearInNearby: true, showDistance: false } });
    await call(tokens.app, '/api/me/location', { method: 'PUT', body: { ...HERE, accuracyMetres: 30, takenAt: new Date().toISOString() } });
    const regate = () => sql(`UPDATE member_locations SET accepted_at = now() - interval '6 minutes', observed_at = now() - interval '6 minutes' WHERE account_id = '${ids.web}'`);

    // A browser whose geolocation the test controls: granted or denied permission, a fixed position, and optionally no
    // geolocation at all or a coarse reading (the Permissions API only picks the wording; it never prompts).
    const open = async ({ permission = 'prompt', accuracy = 30, unsupported = false } = {}) => {
      const context = await browser.newContext({ viewport: { width: 390, height: 844 }, geolocation: { ...HERE, accuracy } });
      if (permission === 'granted') await context.grantPermissions(['geolocation'], { origin: web });
      await context.addInitScript(({ permission, accuracy, unsupported }) => {
        if (unsupported) { Object.defineProperty(navigator, 'geolocation', { value: undefined, configurable: true }); return; }
        const real = navigator.geolocation.getCurrentPosition.bind(navigator.geolocation);
        navigator.geolocation.getCurrentPosition = (ok, fail, options) => {
          if (permission === 'denied') { fail({ code: 1, message: 'denied' }); return; }
          ok({ coords: { latitude: -34.9285, longitude: 138.6007, accuracy }, timestamp: Date.now() });
        };
        if (navigator.permissions) navigator.permissions.query = async q => ({ state: permission === 'granted' ? 'granted' : permission === 'denied' ? 'denied' : 'prompt' });
        void real;
      }, { permission, accuracy, unsupported });
      const page = await context.newPage();
      await page.goto(web + '/account/sign-in?intro=done');
      await page.fill('#username', members.web.email); await page.fill('#password', members.web.password);
      await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
      await page.goto(web + '/explore?mode=friends');
      await page.waitForSelector('[data-explore=ready]');
      return { context, page };
    };
    const notice = page => page.locator('[data-near-me] p').first().innerText();
    // The accessibility gate's automated floor (ACCESSIBILITY-RELEASE-GATE.md) on each changed screen, at 390 and 320 px.
    const accessible = async (page, what) => {
      for (const width of [390, 320]) {
        await page.setViewportSize({ width, height: 844 });
        const problems = await page.evaluate(audit);
        assert.deepEqual(problems, [], `${what} at ${width}px`);
      }
      await page.setViewportSize({ width: 390, height: 844 }); pass();
    };

    // Before the prompt: the approved wording and a Share button; nothing is read until the member asks.
    let { context, page } = await open();
    assert.equal(await page.getAttribute('[data-explore-scope]', 'data-explore-scope'), 'active', 'Active location is the default'); pass();
    await page.click('[data-scope=nearMe]');
    await page.waitForSelector('[data-near-me=prompt]');
    assert.equal(await notice(page), text.prompt); pass();
    assert.equal(await sql(`SELECT count(*) FROM member_locations WHERE account_id = '${ids.web}'`), '0', 'nothing read before Share'); pass();
    await accessible(page, 'Near me before the prompt');
    if (output) await page.screenshot({ path: output + '/near-me-prompt-390.png', fullPage: true });
    // Allow: the reading is stored (rounded, sealed) and Near me lists Adelaide's people, including the member appearing nearby.
    await page.click('[data-near-me-share]');
    await page.waitForSelector('[data-explore-market]');
    assert.match(await page.textContent('[data-explore-market]'), /Near you in Adelaide/); pass();
    assert.equal(await sql(`SELECT count(*) FROM member_locations WHERE account_id = '${ids.web}'`), '1'); pass();
    await page.waitForSelector(`a[href^="/people/${pid.loc}"]`); pass();
    await page.waitForSelector(`a[href^="/people/${pid.app}"]`); pass();
    assert.equal(await page.locator('[data-near-me]').count(), 0); pass();
    if (output) await page.screenshot({ path: output + '/near-me-results-390.png', fullPage: true });
    // Switching scopes: Active location is Melbourne (not open) again, and Near me comes back without a prompt while granted.
    await page.click('[data-scope=active]');
    await page.waitForSelector('[data-explore-local=market-not-open]'); pass();
    await page.click('[data-scope=nearMe]');
    await page.waitForSelector('[data-near-me=prompt]'); pass(); // still "prompt": this browser never granted permission
    // Appear off while browsing: the member still sees Near me; they never appear to others there.
    await call(tokens.web, '/api/me/location-settings', { method: 'PUT', body: { appearInNearby: false, showDistance: false } });
    await regate();
    await page.click('[data-near-me-share]');
    await page.waitForSelector(`a[href^="/people/${pid.loc}"]`); pass();
    const fromApp = (await call(tokens.app, '/api/explore?mode=friends&scope=nearMe')).body.people.map(p => p.id);
    assert.ok(!fromApp.includes(pid.web), 'not discoverable by GPS with Appear off'); pass();
    assert.equal(await page.locator('text=/development|test mode|UAT/i').count(), 0, 'no environment notices (D321)'); pass();
    await context.close();

    // Granted already: opening Near me reads at once.
    await regate();
    ({ context, page } = await open({ permission: 'granted' }));
    await page.click('[data-scope=nearMe]');
    await page.waitForSelector('[data-explore-market]');
    assert.match(await page.textContent('[data-explore-market]'), /Near you in Adelaide/); pass();
    await context.close();

    // Denied: the blocked wording; the button stays so the member can try again after changing site settings.
    ({ context, page } = await open({ permission: 'denied' }));
    await page.click('[data-scope=nearMe]');
    await page.waitForSelector('[data-near-me=denied]');
    assert.equal(await notice(page), text.denied); pass();
    await page.click('[data-near-me-share]');
    await page.waitForSelector('[data-near-me=denied]');
    assert.equal(await page.locator('[data-near-me-share]').count(), 1, 'the button stays after a denial'); pass();
    if (output) await page.screenshot({ path: output + '/near-me-denied-390.png', fullPage: true });
    await page.click('[data-near-me-home]');
    await page.waitForSelector('[data-explore-scope=active]'); pass();
    await context.close();

    // Unsupported: the wording, and no button.
    ({ context, page } = await open({ unsupported: true }));
    await page.click('[data-scope=nearMe]');
    await page.waitForSelector('[data-near-me=unsupported]');
    assert.equal(await notice(page), text.unsupported); pass();
    assert.equal(await page.locator('[data-near-me-share]').count(), 0); pass();
    await context.close();

    // Not accurate enough (5 km): the wording; nothing is sent.
    await regate();
    const before = await sql(`SELECT accepted_at::text FROM member_locations WHERE account_id = '${ids.web}'`);
    ({ context, page } = await open({ accuracy: 5000 }));
    await page.click('[data-scope=nearMe]');
    await page.click('[data-near-me-share]');
    await page.waitForSelector('[data-near-me=inaccurate]');
    assert.equal(await notice(page), text.inaccurate); pass();
    assert.equal(await sql(`SELECT accepted_at::text FROM member_locations WHERE account_id = '${ids.web}'`), before, 'a coarse reading is never sent'); pass();

    // Privacy & safety: the two switches (approved descriptions) and Stop sharing, which deletes the stored position.
    await page.goto(web + '/me/safety');
    await page.waitForSelector('[data-location-settings]');
    assert.match(await page.textContent('[data-location-settings]'), /Let eligible members find you through Near me using your recent rounded location\./); pass();
    assert.match(await page.textContent('[data-location-settings]'), /Your coordinates and exact distance are never shown to them\./); pass();
    await page.check('[data-setting=appear-nearby]', { force: true }); await page.check('[data-setting=show-distance]', { force: true });
    await page.click('[data-save-location]'); await page.waitForSelector('[data-location-status=saved]');
    assert.deepEqual((await call(tokens.web, '/api/me/location-settings')).body, { appearInNearby: true, showDistance: true }); pass();
    await page.click('[data-stop-sharing]'); await page.waitForSelector('[data-location-status=stopped]');
    assert.equal(await sql(`SELECT count(*) FROM member_locations WHERE account_id = '${ids.web}'`), '0', 'stop sharing deletes the stored position'); pass();
    await accessible(page, 'Privacy & safety with Location');
    if (output) await page.screenshot({ path: output + '/location-settings-390.png', fullPage: true });

    // Travel: the two pre-arrival switches with their opening days, each saved on its own; Check in is the arrival action.
    const arrival = new Date(Date.now() + 20 * 86400000).toISOString().slice(0, 10), departure = new Date(Date.now() + 25 * 86400000).toISOString().slice(0, 10);
    const trip = (await call(tokens.web, '/api/me/trips', { method: 'POST', body: { cityId: ADELAIDE, arrival, departure, visible: true, itineraryId: null, discoverBeforeArrival: true } })).body;
    await page.goto(web + '/travel');
    await page.waitForSelector(`[data-trip-visible="${trip.id}"]`);
    assert.match(await page.textContent(`[data-trip="${trip.id}"]`), /Let people there discover me before arrival/); pass();
    assert.match(await page.textContent(`[data-trip="${trip.id}"]`), /Discover people there before arrival/); pass();
    assert.equal(await page.locator(`[data-trip="${trip.id}"] [data-opens-on]`).count(), 1, 'the day the window opens'); pass();
    assert.equal((await page.textContent(`[data-confirm-arrival="${trip.id}"]`)).trim(), 'Check in'); pass();
    await page.uncheck(`[data-trip-discover="${trip.id}"]`, { force: true });
    await page.waitForSelector('[data-trip-status=saved]');
    const saved = (await call(tokens.web, '/api/me/trips')).body.items.find(t => t.id === trip.id);
    assert.deepEqual([saved.visible, saved.discoverBeforeArrival], [true, false], 'each switch saves on its own'); pass();
    await accessible(page, 'Travel with the pre-arrival switches');
    if (output) await page.screenshot({ path: output + '/travel-switches-390.png', fullPage: true });
    await context.close();

    console.log(`PASS: near me web (issue 282), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
