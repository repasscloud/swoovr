// Issue 572 (D448): private favourites. Saving needs the profile to be open; only the saver sees it (no notification, no
// count, never on the other side); the list leaves out a profile that isn't open now; a block removes it; the profile menu
// and the Favourites page add and remove it. Fictional members only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, sql, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const names = ['viv', 'tam', 'bob'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('fav-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Fav ' + n, homeCityId: 2078025 } });
      assert.equal(saved.status, 200, n); pid[n] = saved.body.publicId;
      await call(tokens[n], '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    const fav = (who, whom, on = true) => call(tokens[who], `/api/people/${pid[whom]}/favourite`, { method: on ? 'PUT' : 'DELETE' });
    const list = async who => (await call(tokens[who], '/api/me/favourites')).body.map(f => f.person.id);
    const unread = async who => (await call(tokens[who], '/api/me/notifications')).body.unread;

    const before = await unread('tam');
    assert.deepEqual([(await fav('viv', 'tam')).status, (await fav('viv', 'tam')).body], [200, { favourite: true }], 'saving twice is fine'); pass();
    assert.equal((await call(tokens.viv, `/api/profiles/${pid.tam}?mode=friends`)).body.favourite, true); pass();
    assert.equal((await call(tokens.tam, `/api/profiles/${pid.viv}?mode=friends`)).body.favourite, false, 'never the other side'); pass();
    assert.deepEqual(await list('viv'), [pid.tam]); pass();
    assert.deepEqual(await list('tam'), []); pass();
    assert.equal(await unread('tam'), before, 'the member is never told'); pass();
    assert.equal((await call(tokens.viv, `/api/people/${pid.viv}/favourite`, { method: 'PUT' })).status, 404, 'not yourself'); pass();
    assert.equal((await call(tokens.viv, `/api/people/${crypto.randomUUID()}/favourite`, { method: 'PUT' })).status, 404); pass();

    // Out of the list while the profile isn't open; back when it is.
    await call(tokens.tam, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: false } });
    assert.deepEqual(await list('viv'), [], 'hidden profile drops out'); pass();
    await call(tokens.tam, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
    assert.deepEqual(await list('viv'), [pid.tam]); pass();

    // The list shows the name the linked profile shows: the mode's alias when there is one (PR 603 review).
    const shownNames = async () => (await call(tokens.viv, '/api/me/favourites')).body.map(f => f.person.displayName);
    assert.equal((await call(tokens.tam, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true, displayName: 'Fictional Alias tam' } })).status, 200);
    assert.deepEqual([await shownNames(), (await call(tokens.viv, `/api/profiles/${pid.tam}`)).body.displayName], [['Fictional Alias tam'], 'Fictional Alias tam']); pass();
    await call(tokens.tam, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true, displayName: '  ' } });
    assert.deepEqual(await shownNames(), ['Fictional Fav tam'], 'no alias: the main name'); pass();
    // Another market with no request between them: out of the list, as the profile itself doesn't open (PR 603 review).
    const tamAccount = `(SELECT account_id FROM member_profiles WHERE public_id = '${pid.tam}')`;
    const homeBefore = await sql(`SELECT home_market_id FROM member_profiles WHERE account_id = ${tamAccount}`);
    await sql(`UPDATE member_profiles SET home_market_id = (SELECT id FROM markets WHERE id <> '${homeBefore}' ORDER BY id LIMIT 1) WHERE account_id = ${tamAccount}`);
    assert.deepEqual([await list('viv'), (await call(tokens.viv, `/api/profiles/${pid.tam}`)).status], [[], 404], 'another market'); pass();
    await sql(`UPDATE member_profiles SET home_market_id = '${homeBefore}' WHERE account_id = ${tamAccount}`);
    assert.deepEqual(await list('viv'), [pid.tam]); pass();

    // A block either way removes it.
    assert.equal((await fav('bob', 'viv')).status, 200);
    assert.equal((await call(tokens.viv, '/api/blocks', { method: 'POST', body: { profileId: pid.bob } })).status, 204);
    assert.equal(await sql(`SELECT count(*) FROM member_favourites f JOIN member_profiles p ON p.account_id = f.account_id WHERE p.public_id = '${pid.bob}'`), '0', 'removed by the block'); pass();
    assert.equal((await fav('bob', 'viv')).status, 404, 'and can\'t be saved again while blocked'); pass();

    // The profile menu and the Favourites page.
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    assert.equal((await fav('viv', 'tam', false)).status, 200);
    await page.goto(web + '/account/sign-in?intro=done'); await page.fill('#username', members.viv.email); await page.fill('#password', members.viv.password);
    await Promise.all([page.waitForURL(u => !u.href.includes('/realms/')), page.click('#kc-login')]);
    // D449 (Q195): the gold star beside ⋯ on the profile; no longer a menu row.
    await page.goto(`${web}/people/${pid.tam}?mode=friends`);
    const star = page.locator('[data-person-actions] .person-favourite');
    await star.waitFor({ timeout: 20000 });
    assert.deepEqual([await star.getAttribute('aria-pressed'), await star.getAttribute('aria-label')], ['false', 'Add to favourites']); pass();
    await page.click('[data-safety-menu]');
    assert.equal(await page.locator('[data-safety-menu-list] [data-favourite]').count(), 0, 'not in the menu'); pass();
    await page.click('[data-safety-menu]');
    await star.click();
    await page.locator('[data-person-actions] .person-favourite[aria-pressed=true]').waitFor({ timeout: 10000 });
    assert.equal(await star.getAttribute('aria-label'), 'Remove from favourites'); pass();
    assert.equal(await star.evaluate(e => getComputedStyle(e.querySelector('svg')).fill), 'rgb(242, 178, 27)', 'gold when saved'); pass();
    if (output) await page.screenshot({ path: `${output}/favourite-profile-1280.png` });
    assert.deepEqual(await list('viv'), [pid.tam]); pass();
    // The Explore card's star: top-right, gold when saved, and pressing it doesn't open the profile.
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`${web}/explore?mode=friends`);
      const cardStar = page.locator(`.member-card:has(a[href^="/people/${pid.tam}"]) [data-card-favourite]`);
      await cardStar.waitFor({ timeout: 20000 });
      assert.deepEqual([await cardStar.getAttribute('aria-pressed'), await cardStar.getAttribute('aria-label')], ['true', 'Remove Fictional Fav tam from favourites']); pass();
      assert.equal(await cardStar.evaluate(e => getComputedStyle(e.querySelector('svg')).fill), 'rgb(242, 178, 27)'); pass();
      if (output) await page.screenshot({ path: `${output}/favourite-cards-${width}.png` });
      await cardStar.click();
      await page.locator(`.member-card:has(a[href^="/people/${pid.tam}"]) [data-card-favourite=off]`).waitFor({ timeout: 10000 });
      assert.equal(new URL(page.url()).pathname, '/explore', 'stays on Explore'); pass();
      assert.deepEqual(await list('viv'), []); pass();
      await cardStar.click();
      await page.locator(`.member-card:has(a[href^="/people/${pid.tam}"]) [data-card-favourite=on]`).waitFor({ timeout: 10000 });
      assert.deepEqual(await list('viv'), [pid.tam]); pass();
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    assert.deepEqual(await list('viv'), [pid.tam]); pass();
    await page.goto(web + '/me/favourites');
    await page.locator(`[data-favourite-person="${pid.tam}"]`).waitFor({ timeout: 20000 });
    // D450: the row shows name and age, and a link for each mode the profile opens in now.
    const row = page.locator(`[data-favourite-person="${pid.tam}"]`);
    assert.match(await row.locator('.cx-name').innerText(), /^Fictional Fav tam, \d+$/); pass();
    assert.equal(await row.locator('.cx-thumb').count(), 1); pass();
    assert.deepEqual(await row.locator('[data-favourite-mode]').evaluateAll(a => a.map(e => [e.dataset.favouriteMode, e.getAttribute('href'), e.textContent.trim()])),
      [['friends', `/people/${pid.tam}?mode=friends`, 'Friends']]); pass();
    assert.equal(await row.locator('.cx-name').getAttribute('href'), `/people/${pid.tam}?mode=friends`); pass();
    const api = (await call(tokens.viv, '/api/me/favourites')).body[0];
    assert.deepEqual([api.modes, typeof api.person.age], [['friends'], 'number']); pass();
    // Turn on Business for both: a second link; hide it on tam's side: gone again.
    for (const n of ['viv', 'tam']) await call(tokens[n], '/api/me/modes/business', { method: 'PUT', body: { enabled: true, visible: true } });
    assert.deepEqual((await call(tokens.viv, '/api/me/favourites')).body[0].modes.sort(), ['business', 'friends']); pass();
    // PR 605 review: the first mode is the one the row is drawn in (the first shared mode, friends, with no request between them).
    assert.equal((await call(tokens.viv, '/api/me/favourites')).body[0].modes[0], 'friends'); pass();
    await call(tokens.tam, '/api/me/modes/business', { method: 'PUT', body: { enabled: true, visible: false } });
    assert.deepEqual((await call(tokens.viv, '/api/me/favourites')).body[0].modes, ['friends'], 'a hidden mode is not listed'); pass();
    await page.reload(); await row.waitFor({ timeout: 20000 });
    for (const width of [1280, 390]) { await page.setViewportSize({ width, height: 900 }); if (output) await page.screenshot({ path: `${output}/favourites-rows-${width}.png`, fullPage: true }); }
    await page.setViewportSize({ width: 1280, height: 900 });
    assert.equal(await page.locator('[data-settings-link=favourites][aria-current=page]').count(), 1); pass();
    if (output) await page.screenshot({ path: `${output}/favourites-1280.png`, fullPage: true });
    await page.click(`[data-favourite-remove="${pid.tam}"]`);
    await page.locator('[data-favourites=none]').waitFor({ timeout: 10000 }); pass();
    assert.deepEqual(await list('viv'), []); pass();
    await context.close();
    console.log(`PASS: private favourites (issue 572), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
