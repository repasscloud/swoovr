// Issue 572 (D448): private favourites. Saving needs the profile to be open; only the saver sees it (no notification, no
// count, never on the other side); the list leaves out a profile that isn't open now; a block removes it; the profile menu
// and the Favourites page add and remove it. Fictional members only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, call, sql, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const names = ['viv', 'tam', 'bob'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('fav-' + n)])));
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
    await page.goto(`${web}/people/${pid.tam}?mode=friends`);
    await page.click('[data-safety-menu]');
    assert.equal((await page.locator('[data-favourite=off]').innerText()).trim(), 'Add to favourites'); pass();
    if (output) await page.screenshot({ path: `${output}/favourite-menu-1280.png` });
    await page.click('[data-favourite=off]');
    await page.click('[data-safety-menu]');
    await page.locator('[data-favourite=on]').waitFor({ timeout: 10000 });
    assert.equal((await page.locator('[data-favourite=on]').innerText()).trim(), 'Remove from favourites'); pass();
    assert.deepEqual(await list('viv'), [pid.tam]); pass();
    await page.goto(web + '/me/favourites');
    await page.locator(`[data-favourite-person="${pid.tam}"]`).waitFor({ timeout: 20000 });
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
