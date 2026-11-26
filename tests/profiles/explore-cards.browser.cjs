// Issue 476 (D401): what an Explore card says about the person: where the viewer stands with them in this mode (issue 497)
// and whether they are a new member (issue 498).
// Owned local stack; fictional adults.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, sql, claims, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const names = ['viv', 'con', 'out', 'inn', 'non'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('cards-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, pid = {}, ids = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      pid[n] = (await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Cards ' + n, homeCityId: 2078025 } })).body.publicId;
      ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
      for (const mode of ['dating', 'friends']) await call(tokens[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    const explore = async (who, mode) => (await call(tokens[who], '/api/explore?mode=' + mode)).body.people;
    const order = async mode => (await explore('viv', mode)).map(p => p.id);
    const state = async (mode, n) => (await explore('viv', mode)).find(p => p.id === pid[n]).connection ?? null;
    const before = await order('friends');
    for (const n of names.slice(1)) assert.equal(await state('friends', n), null, n); pass();
    const ask = (from, to) => call(tokens[from], '/api/requests', { method: 'POST', body: { to: pid[to], mode: 'friends' } });
    const toCon = await ask('viv', 'con');
    assert.equal((await ask('viv', 'out')).status, 201); assert.equal((await ask('inn', 'viv')).status, 201);
    assert.equal((await call(tokens.con, `/api/requests/${toCon.body.id}/accept`, { method: 'POST' })).status, 200);
    assert.deepEqual(await Promise.all(['con', 'out', 'inn', 'non'].map(n => state('friends', n))), ['connected', 'sent', 'received', null]); pass();
    for (const n of names.slice(1)) assert.equal(await state('dating', n), null, 'another mode says nothing: ' + n); pass();
    assert.equal((await explore('inn', 'friends')).find(p => p.id === pid.viv).connection, 'sent'); pass();
    assert.equal((await explore('non', 'friends')).filter(p => p.connection).length, 0, "never other people's connections"); pass();
    assert.deepEqual(await order('friends'), before, 'the order does not change'); pass();

    // New members: for 14 days by default, then it ends on its own; the order never changes.
    assert.ok((await explore('viv', 'friends')).every(p => p.newMember === true)); pass();
    const listed = await order('friends');
    await sql(`UPDATE accounts SET created_at = now() - interval '14 days 1 minute' WHERE id = '${ids.out}'`);
    await sql(`UPDATE accounts SET created_at = now() - interval '13 days 23 hours' WHERE id = '${ids.inn}'`);
    const isNew = async n => (await explore('viv', 'friends')).find(p => p.id === pid[n]).newMember;
    assert.deepEqual([await isNew('out'), await isNew('inn')], [false, true]); pass();
    assert.deepEqual(await order('friends'), listed); pass();
    await sql(`UPDATE member_profiles SET bot = true WHERE account_id = '${ids.non}'`);
    const bot = (await explore('viv', 'friends')).find(p => p.id === pid.non);
    assert.ok(!bot || bot.newMember === false, 'never for bots'); pass();
    await sql(`UPDATE member_profiles SET bot = false WHERE account_id = '${ids.non}'`);

    // Issue 495 (D418): age and gender in Dating and Friends, orientation in Dating only, nothing in Activities or Business;
    // always under the person's own switches. Enforced by the API, and the order never changes.
    for (const n of names) for (const mode of ['activities', 'business']) await call(tokens[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
    const orientation = (await call(tokens.con, '/api/profile-options')).body.orientations[0];
    const say = body => call(tokens.con, '/api/me/identity', { method: 'PUT', body: { gender: 'woman', orientation: orientation.id, showGender: true, showOrientation: true, showAge: true, showPronouns: true, pronounId: null, datingShowMe: ['man', 'woman', 'non-binary', 'another'], ...body } });
    const facts = async mode => { const c = (await explore('viv', mode)).find(p => p.id === pid.con); return [typeof c.age, c.gender ?? null, c.orientation ?? null, c.pronouns ?? null]; };
    const orders = await Promise.all(['dating', 'friends'].map(order));
    assert.equal((await say({ pronouns: 'fae/faer' })).status, 200);
    assert.deepEqual(await facts('dating'), ['number', 'Woman', orientation.label, 'fae/faer']); pass();
    assert.deepEqual(await facts('friends'), ['number', 'Woman', null, 'fae/faer']); pass();
    for (const mode of ['activities', 'business']) { const c = (await explore('viv', mode)).find(p => p.id === pid.con); assert.deepEqual([c.age ?? null, c.gender ?? null, c.orientation ?? null, c.pronouns ?? null], [null, null, null, null], mode); pass(); }
    await say({ showGender: false, showOrientation: false, showAge: false, showPronouns: false });
    { const c = (await explore('viv', 'dating')).find(p => p.id === pid.con); assert.deepEqual([c.age ?? null, c.gender ?? null, c.orientation ?? null, c.pronouns ?? null], [null, null, null, null]); pass(); }
    assert.deepEqual(await Promise.all(['dating', 'friends'].map(order)), orders, 'the order does not change'); pass();
    await say({});

    // Issue 499 (D410): online status is opt-in and mutual, five minutes, never a time, and never from a page's own polling.
    const online = async (who, n) => (await explore(who, 'friends')).find(p => p.id === pid[n]).online;
    const turn = (who, on) => call(tokens[who], '/api/me/online-status', { method: 'PUT', body: { showOnline: on } });
    const activeAt = n => sql(`SELECT coalesce(active_at::text, '') FROM member_profiles WHERE account_id = '${ids[n]}'`);
    assert.deepEqual((await call(tokens.viv, '/api/me/online-status')).body, { available: true, showOnline: false }); pass();
    assert.equal(await activeAt('con'), '', 'nothing is kept while it is off'); pass();
    const ordered = await order('friends');
    await turn('con', true); assert.notEqual(await activeAt('con'), '');
    assert.equal(await online('viv', 'con'), false, 'viv has it off'); pass();
    await turn('viv', true);
    assert.deepEqual([await online('viv', 'con'), await online('con', 'viv'), await online('viv', 'out')], [true, true, false]); pass();
    assert.ok(!JSON.stringify(await explore('viv', 'friends')).toLowerCase().includes('activeat'), 'never a time'); pass();
    await sql(`UPDATE member_profiles SET active_at = now() - interval '6 minutes' WHERE account_id = '${ids.con}'`);
    assert.equal(await online('viv', 'con'), false); pass();
    await call(tokens.con, '/api/me/notifications');
    assert.equal(await online('viv', 'con'), false, 'the unread poll is not activity'); pass();
    // PR 526 review: an automatic refresh says so with the background header and isn't activity either.
    await fetch(process.env.SWOOVR_TEST_API_BASE + '/api/me/profile', { headers: { Authorization: 'Bearer ' + tokens.con, 'Swoovr-Version': '2026-10-28', 'X-Swoovr-Background': '1' } });
    assert.equal(await online('viv', 'con'), false, 'a marked background refresh is not activity'); pass();
    await call(tokens.con, '/api/me/profile');
    assert.equal(await online('viv', 'con'), true); pass();
    assert.deepEqual(await order('friends'), ordered, 'the order does not change'); pass();
    await sql(`UPDATE member_profiles SET bot = true WHERE account_id = '${ids.con}'`);
    assert.ok(!(await explore('viv', 'friends')).some(p => p.id === pid.con && p.online), 'never for bots'); pass();
    await sql(`UPDATE member_profiles SET bot = false WHERE account_id = '${ids.con}'`);
    await turn('con', false); assert.equal(await activeAt('con'), ''); assert.equal(await online('viv', 'con'), false); pass();
    await turn('con', true);

    const page = await (await browser.newContext({ viewport: { width: 390, height: 844 } })).newPage();
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', members.viv.email); await page.fill('#password', members.viv.password);
    await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
    for (const [theme, width] of [['light', 390], ['dark', 390], ['light', 1280]]) {
      await page.emulateMedia({ colorScheme: theme }); await page.setViewportSize({ width, height: 900 });
      await page.goto(web + '/explore?mode=friends');
      await page.waitForSelector(`.member-card:has(a[href^="/people/${pid.con}"])`, { timeout: 20000 });
      const status = n => page.locator(`.member-card:has(a[href^="/people/${pid[n]}"]) [data-tile-status]`);
      assert.deepEqual([await status('con').textContent(), await status('out').textContent(), await status('inn').textContent(), await status('non').textContent()], ['Connected', 'Request sent', 'Request received', 'New member']); pass();
      assert.deepEqual(await page.locator(`.member-card:has(a[href^="/people/${pid.con}"]) [data-tile-online]`).evaluateAll(a => a.map(e => [e.getAttribute('aria-label'), e.textContent, e.getBoundingClientRect().width > 6])), [['Online', '', true]]); pass();
      assert.equal(await page.locator('[data-tile-online]').count(), 1); pass();
      assert.ok(await page.locator('[data-tile-online]').evaluate(e => { const d = e.getBoundingClientRect(), n = e.parentElement.querySelector('a').getBoundingClientRect(); return d.top >= n.top && d.bottom <= n.bottom && d.right <= n.left; }), 'beside the name'); pass();
      const sizes = await page.locator('.member-card').evaluateAll(els => [...new Set(els.map(e => { const r = e.getBoundingClientRect(); return Math.round(r.width) + 'x' + Math.round(r.height); }))]);
      assert.equal(sizes.length, 1, 'one card size: ' + sizes); pass();
      assert.equal(await page.locator('.member-card button:not([data-card-badges])').count(), 0, 'no action buttons on cards'); pass();
      if (output) await page.screenshot({ path: `${output}/explore-status-${theme}-${width}.png`, fullPage: true });
    }
    for (const [mode, identity, aged] of [['dating', 'Woman · ' + orientation.label, true], ['friends', 'Woman', true], ['activities', null, false], ['business', null, false]]) for (const [theme, width] of [['light', 390], ['dark', 1280]]) {
      await page.emulateMedia({ colorScheme: theme }); await page.setViewportSize({ width, height: 900 });
      await page.goto(web + '/explore?mode=' + mode);
      const at = page.locator(`.member-card:has(a[href="/people/${pid.con}?mode=${mode}"])`); await at.waitFor({ timeout: 20000 });
      assert.deepEqual(await at.locator('[data-tile-identity]').allTextContents(), identity ? [identity] : [], mode); pass();
      assert.equal(/, \d\d$/.test((await at.locator('h2 a').textContent()).trim()), aged, mode + ' age'); pass();
      const sizes = await page.locator('.member-card').evaluateAll(els => new Set(els.map(e => Math.round(e.getBoundingClientRect().height))).size);
      assert.equal(sizes, 1, 'one card size'); pass();
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)); pass();
      if (output) await page.screenshot({ path: `${output}/explore-identity-${mode}-${theme}-${width}.png`, fullPage: true });
    }
    // The member's own switch, and the promise on Notifications that changes with it.
    await page.emulateMedia({ colorScheme: 'light' }); await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(web + '/me/profile'); await page.waitForSelector('[data-online-status=on]');
    await page.locator('[data-online-status]').scrollIntoViewIfNeeded();
    if (output) await page.locator('[data-online-status]').screenshot({ path: output + '/online-status-card.png' });
    await page.click('[data-show-online]'); await page.waitForSelector('[data-online-status=off]'); pass();
    assert.equal(await activeAt('viv'), ''); pass();
    await page.goto(web + '/me/notifications'); await page.waitForSelector('[data-notification-privacy]');
    assert.match(await page.textContent('[data-notification-privacy]'), /only if you both turn on online status/); pass();
    assert.doesNotMatch(await page.textContent('[data-notification-privacy]'), /never shows anyone when you're online/); pass();
    console.log(`PASS: Explore card information, ${passed} checks.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
