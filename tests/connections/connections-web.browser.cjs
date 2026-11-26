// Issue 539: Connections as navigable sections with compact rows. Each row has a thumbnail (or initial), name, age only where
// that mode shows it (D418: Dating and Friends, under the person's switch), the mode and badges; blocked rows have only the
// name. Requests for you open first when any wait; /connections#blocked-heading opens Blocked. All four modes. Owned local
// stack; every person is fictional.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, sql, claims, matchReady } = require('../auth/local-tokens.cjs');
const audit = require('../accessibility/audit.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;
const ADELAIDE = 2078025;
const MODES = ['dating', 'friends', 'activities', 'business'];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const names = ['me', 'ada', 'bea', 'cal', 'dex', 'blk'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('cxw-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, ids = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional CXW ' + n, homeCityId: ADELAIDE } });
      assert.equal(saved.status, 200, n);
      pid[n] = saved.body.publicId;
      ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
      for (const mode of MODES) await call(tokens[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    const ask = (from, to, mode) => call(tokens[from], '/api/requests', { method: 'POST', body: { to: pid[to], mode } });
    // ada → me in each mode (received, all four modes); me → bea (sent); cal ↔ me connected in Friends; me blocks blk.
    for (const mode of MODES) assert.equal((await ask('ada', 'me', mode)).status, 201, mode);
    assert.equal((await ask('me', 'bea', 'dating')).status, 201);
    const link = (await ask('cal', 'me', 'friends')).body.id;
    assert.equal((await call(tokens.me, `/api/requests/${link}/accept`, { method: 'POST' })).status, 200);
    assert.equal((await call(tokens.me, '/api/blocks', { method: 'POST', body: { profileId: pid.blk } })).status, 204);

    // API: ages only in Dating and Friends, under the person's switch; never in Activities or Business (D418).
    const box = async which => (await call(tokens.me, '/api/requests?box=' + which)).body.items;
    const received = await box('received');
    const ages = Object.fromEntries(received.map(r => [r.mode, r.person.age ?? null]));
    assert.equal(typeof ages.dating, 'number'); assert.equal(typeof ages.friends, 'number'); pass();
    assert.deepEqual([ages.activities, ages.business], [null, null], 'no age in Activities or Business'); pass();
    assert.equal((await call(tokens.ada, '/api/me/identity', { method: 'PUT', body: { gender: 'woman', showAge: false, showGender: true, showOrientation: false, showPronouns: false, pronounId: null, datingShowMe: ['man', 'woman', 'non-binary', 'another'] } })).status, 200);
    assert.ok((await box('received')).every(r => r.person.age == null), 'the person hid their age'); pass();
    await call(tokens.ada, '/api/me/identity', { method: 'PUT', body: { gender: 'woman', showAge: true, showGender: true, showOrientation: false, showPronouns: false, pronounId: null, datingShowMe: ['man', 'woman', 'non-binary', 'another'] } });
    assert.ok(!JSON.stringify(received).match(/dateOfBirth|date_of_birth|email/i), 'no date of birth or email'); pass();
    // PR 561 review: a mode the person no longer shows gives a name only (no photo link, no age), as their profile would 404.
    assert.equal((await call(tokens.ada, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: false } })).status, 200);
    const hidden = (await box('received')).find(r => r.mode === 'friends').person;
    assert.deepEqual([hidden.photo ?? null, hidden.age ?? null], [null, null], 'hidden mode: name only'); pass();
    assert.equal(typeof (await box('received')).find(r => r.mode === 'dating').person.age, 'number', 'other modes unchanged'); pass();
    await call(tokens.ada, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
    const blocks = (await call(tokens.me, '/api/blocks')).body;
    assert.deepEqual(Object.keys(blocks[0]).sort(), ['displayName', 'id', 'since'], 'blocked rows carry only the name'); pass();

    // Web.
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', members.me.email); await page.fill('#password', members.me.password);
    await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
    await page.goto(web + '/connections');
    await page.waitForSelector('[data-connections=ready]');
    assert.equal(await page.getAttribute('[data-connections-section]', 'data-connections-section'), 'received', 'requests first while any wait'); pass();
    assert.equal(await page.textContent('[data-section=received] [data-section-count] [aria-hidden]'), '4'); pass();
    assert.equal(String((await call(tokens.me, '/api/me/notifications')).body.pendingRequests), '4', 'the same total as the Connections tab'); pass();
    assert.deepEqual((await page.locator('[data-box=received] [data-request-mode]').evaluateAll(a => a.map(x => x.dataset.requestMode))).sort(), [...MODES].sort(), 'all four modes'); pass();
    const row = mode => page.locator(`[data-box=received] [data-request-mode=${mode}]`);
    assert.equal(await row('dating').locator('[data-row-age]').count(), 1); pass();
    assert.equal(await row('business').locator('[data-row-age]').count(), 0); pass();
    assert.equal(await row('dating').locator('.cx-thumb').count(), 1, 'a thumbnail or initial'); pass();
    assert.match(await row('business').locator('a').getAttribute('href'), /\?mode=business$/, 'opens the profile in that mode'); pass();
    // Compact actions: no full-width buttons.
    const [accept, wide] = await page.evaluate(() => { const b = document.querySelector('[data-box=received] .cx-actions .button').getBoundingClientRect().width; return [b, document.querySelector('.cx-panel').getBoundingClientRect().width]; });
    assert.ok(accept < wide * 0.3, `compact Accept (${accept} of ${wide})`); pass();
    // Left navigation on wide screens.
    const [nav, panel] = await page.evaluate(() => [document.querySelector('.cx-nav').getBoundingClientRect(), document.querySelector('.cx-panel').getBoundingClientRect()].map(r => r.left));
    assert.ok(nav < panel, 'sections on the left'); pass();
    assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at 1280'); pass();
    if (output) await page.screenshot({ path: output + '/connections-received-1280.png', fullPage: true });
    await page.click('[data-section=sent]');
    await page.waitForSelector(`[data-box=sent] a[href^="/people/${pid.bea}"]`); pass();
    await page.click('[data-section=connected]');
    await page.waitForSelector(`[data-box=connected] a[href^="/people/${pid.cal}"]`); pass();
    assert.ok(!(await page.textContent('[data-box=connected]')).includes('Match'), 'no "Match"'); pass();
    // Accept from the list: it moves to Connected and the count drops.
    await page.click('[data-section=received]');
    await row('activities').getByRole('button', { name: 'Accept' }).click();
    await page.waitForSelector('[data-connections-status=accept]');
    assert.equal(await page.textContent('[data-section=received] [data-section-count] [aria-hidden]'), '3'); pass();
    // Blocked: only the name, opened by its link.
    // The way members get there: Privacy & safety → Blocked people → Manage.
    await page.goto(web + '/me/safety');
    await page.click('a[href="/connections#blocked-heading"]');
    await page.waitForSelector(`[data-blocked="${pid.blk}"]`); pass();
    assert.equal(await page.locator(`[data-blocked="${pid.blk}"] img, [data-blocked="${pid.blk}"] a, [data-blocked="${pid.blk}"] .chip`).count(), 0, 'no photo, link or mode for someone blocked'); pass();
    // Narrow: the section switcher above the list, no sideways scroll.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(web + '/connections');
    await page.waitForSelector('[data-connections=ready]');
    for (const width of [390, 320]) { await page.setViewportSize({ width, height: 844 }); assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at ' + width); pass(); }
    if (output) { await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: output + '/connections-received-390.png', fullPage: true }); }
    await context.close();
    console.log(`PASS: connections web (issue 539), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
