// Owned-stack browser evidence. Synthetic response projection checks are deliberately labelled;
// existing matching/market/travel suites remain the real API eligibility/filter evidence.
const assert = require('node:assert/strict');
const { managedEdge } = require('../auth/managed-edge.cjs');
if (!process.env.SWOOVR_TEST_APP_DATABASE) throw Error('Use owned runner.');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, matchReady } = require('../auth/local-tokens.cjs');
(async () => {
  const edge = await managedEdge(chromium); const browser = edge.browser;
  let checks = 0;
  try {
    const viewer = await createMember('pool-guidance'); await cli('beta', 'add', viewer.email);
    const token = await memberToken(browser, viewer);
    assert.equal((await call(token, '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Guidance', homeCityId: 2078025 } })).status, 200);
    await matchReady(token);
    await call(token, '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
    const peer = await createMember('pool-guidance-peer'); await cli('beta', 'add', peer.email); const peerToken = await memberToken(browser, peer);
    assert.equal((await call(peerToken, '/api/me/profile', { method:'PUT', body:{displayName:'Fictional Guidance Peer',homeCityId:2078025} })).status,200); await matchReady(peerToken);
    assert.equal((await call(peerToken,'/api/me/modes/friends',{method:'PUT',body:{enabled:true,visible:true}})).status,200);
    const page = await browser.newPage(); await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', viewer.email); await page.fill('#password', viewer.password); await page.click('#kc-login');
    await page.waitForURL(web + '/explore');
    let state = 'empty', intercepted = 0; const methods = [];
    page.on('request', request => { if (request.url().includes('/bff/')) methods.push(request.method()); });
    await page.route('**/bff/api/explore?**', async route => {
      intercepted++;
      const response = await route.fetch(); // Preserve contract headers from the running local stack.
      if (state === 'transport') return route.abort('failed');
      if (state === 'failure' || state === 'limited') return route.fulfill({ response, status: state === 'limited' ? 429 : 503, contentType: 'application/problem+json', body: JSON.stringify({ code: 'service-unavailable' }) });
      assert.equal(response.status(), 200, 'Fixture must be eligible before synthetic result projection');
      const body = await response.json(); if (state === 'short') { assert.ok(body.people.length > 0); body.people = body.people.slice(0,1); } else body.people = [];
      return route.fulfill({ response, body: JSON.stringify(body) });
    });
    await page.goto(web + '/explore'); await page.waitForSelector('[data-explore-empty]');
    assert.ok(intercepted > 0, 'Synthetic Explore interception must run'); checks++;
    assert.equal(await page.textContent('[data-explore-empty]'), 'No profiles match this view right now.'); checks++;
    // Issue 442: the mode switcher shares the heading row; the market line names no mode (the switcher shows it).
    const selectedMode = await page.locator('[data-modes] [aria-pressed=true]').innerText();
    const marketLine = await page.locator('.explore-market').innerText();
    assert.ok(!marketLine.includes(selectedMode), marketLine); checks++;
    const [title, switcher] = await page.evaluate(() => ['.explore-title-row h1', '[data-modes]'].map(q => document.querySelector(q).getBoundingClientRect().top));
    assert.ok(Math.abs(title - switcher) < 30, 'switcher on the heading row at desktop width'); checks++;
    assert.equal(await page.getAttribute('[data-travel-link]', 'href'), '/travel'); checks++;
    assert.ok((await page.textContent('[data-travel-link]')).includes('✈️')); checks++;
    assert.match(await page.textContent('[data-discovery-guidance]'), /both members' preferences applied/); checks++;
    // Review filters opens this mode's filters in a dialog, without leaving Explore and without saving anything.
    await page.click('[data-discovery-filters]');
    const mode = await page.locator('[data-modes] [aria-pressed=true]').getAttribute('data-mode');
    await page.waitForSelector(`[data-member-dialog=filters] [data-filter-form=${mode}]`); checks++;
    assert.equal(new URL(page.url()).pathname, '/explore'); checks++;
    // Issue 466 (owner-approved demo): the dialog layout. No "Changes apply" line; the details link sits in the footer with
    // Reset and Save; regular-weight slim chips; Distance and Travel after both chip lists.
    const dialog = '[data-member-dialog=filters]';
    assert.equal(await page.locator(`${dialog} [data-filter-layout=dialog]`).count(), 1); checks++;
    assert.ok(!(await page.textContent(dialog)).includes('Changes apply'), 'no "Changes apply to … when you save" line'); checks++;
    assert.equal(await page.getAttribute(`${dialog} .mf-foot [data-filter-details]`, 'href'), '/me/matching'); checks++;
    assert.equal((await page.textContent(`${dialog} [data-filter-save]`)).trim(), 'Save'); checks++;
    const look = await page.evaluate(q => {
      const d = document.querySelector(q), box = s => d.querySelector(s).getBoundingClientRect();
      return { chipWeight: getComputedStyle(d.querySelector('.mf-chip')).fontWeight, chipHeight: box('.mf-chip').height, segHeight: box('.mf-seg').height,
        ageWidth: box('#min-age').width, saveHeight: box('[data-filter-save]').height, chipsBottom: box('[data-excluded-chip]').bottom, distanceTop: box('#distance').top,
        ageTop: box('#min-age').top, idTop: box('[data-id-only]').top, footSticky: getComputedStyle(d.querySelector('.mf-foot')).position };
    }, dialog);
    assert.equal(look.chipWeight, '400', 'chips at regular weight'); checks++;
    assert.ok(look.chipHeight <= 32 && look.segHeight <= 32, 'slim chips and a one-line Any/All: ' + JSON.stringify(look)); checks++;
    assert.ok(look.ageWidth <= 80 && Math.abs(look.ageTop - look.idTop) < 20, 'compact age selects beside ID Verified only: ' + JSON.stringify(look)); checks++;
    assert.ok(look.saveHeight <= 38 && look.footSticky === 'sticky', 'compact Save in a sticky footer: ' + JSON.stringify(look)); checks++;
    assert.ok(look.distanceTop > look.chipsBottom, 'Distance and Travel below the chip lists'); checks++;
    // DEFAULT Reset: back to the saved filters, nothing sent.
    const chip = page.locator(`${dialog} [data-required-chip]:not(:disabled)`).first();
    const wasChecked = await chip.isChecked(); await chip.click(); assert.equal(await chip.isChecked(), !wasChecked); checks++;
    const noId = await page.locator(`${dialog} [data-excluded-chip]:not(:disabled):not(:checked)`).last().getAttribute('data-excluded-chip');
    const no = page.locator(`${dialog} [data-excluded-chip="${noId}"]`); await no.click(); assert.ok(await no.isChecked()); checks++;
    assert.equal(await page.evaluate(q => getComputedStyle(document.querySelector(q).closest('.mf-chip')).textDecorationLine, `${dialog} [data-excluded-chip]:checked`), 'none', 'Not for me: no strikethrough'); checks++;
    if (process.env.SWOOVR_SCREENSHOTS) {
      const shot = name => page.screenshot({ path: require('node:path').join(process.env.SWOOVR_SCREENSHOTS, name) });
      const theme = async mode => { await page.emulateMedia({ colorScheme: mode }); await page.evaluate(m => { document.documentElement.dataset.theme = m; }, mode); };
      for (const [width, height] of [[1280, 900], [390, 844]]) {
        await page.setViewportSize({ width, height });
        for (const mode of ['light', 'dark']) { await theme(mode); await shot(`explore-filters-466-${width}-${mode}.png`); }
      }
      await theme('light'); await page.setViewportSize({ width: 1280, height: 900 });
    }
    await page.click(`${dialog} [data-filter-reset]`);
    await page.waitForSelector(`${dialog} [data-filter-status=reset]`); assert.equal(await chip.isChecked(), wasChecked); assert.equal(await no.isChecked(), false); checks++;
    await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=filters]', { state: 'detached' });
    assert.equal(methods.filter(method => method !== 'GET').length, 0, 'Guidance must not write filters/modes/travel'); checks++;
    // PR 580 review: the pre-566 browser key moves to this account's key first, so a failed save still keeps the notice
    // closed on the next visit. Then put the notice back (nothing reached the account) for the checks below.
    const dismissUrl = '**/bff/api/me/explore-notice/dismiss';
    await page.route(dismissUrl, route => route.fulfill({ status: 503, body: '' }));
    await page.evaluate(() => localStorage.setItem('swoovr.explore.notice-dismissed', '1'));
    await page.reload(); await page.waitForSelector('[data-explore-empty]');
    assert.equal(await page.locator('[data-discovery-guidance]').count(), 0, 'the old key closes it'); checks++;
    const kept = await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('swoovr.explore.notice-dismissed')));
    assert.ok(kept.length === 1 && kept[0].startsWith('swoovr.explore.notice-dismissed:'), 'moved to the account key: ' + kept); checks++;
    await page.reload(); await page.waitForSelector('[data-explore-empty]');
    assert.equal(await page.locator('[data-discovery-guidance]').count(), 0, 'still closed after the save failed'); checks++;
    await page.unroute(dismissUrl);
    await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('swoovr.explore.notice-dismissed')).forEach(k => localStorage.removeItem(k)));
    await page.reload(); await page.waitForSelector('[data-explore-empty]'); await page.waitForSelector('[data-discovery-guidance]');
    // Issue 566: closing the notice is for good, on the account (not just this browser), and there's no About Explore link.
    await page.click('[data-explore-notice-close]'); assert.equal(await page.locator('[data-discovery-guidance]').count(), 0); checks++;
    await page.reload(); await page.waitForSelector('[data-explore-empty]'); assert.equal(await page.locator('[data-discovery-guidance]').count(), 0, 'dismissal remembered'); checks++;
    // PR 576 review: a saved dismissal leaves nothing in this browser that another member signing in here would pick up
    // (the account-scoped key is removed once the account has it).
    await page.waitForFunction(() => !Object.keys(localStorage).some(k => k.startsWith('swoovr.explore.notice-dismissed')), null, { timeout: 15000 });
    assert.deepEqual(await page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('swoovr.explore.notice-dismissed'))), [], 'no browser dismissal key once the account knows'); checks++;
    assert.equal(await page.locator('[data-explore-about]').count(), 0, 'no About Explore link'); checks++;
    const scopeBackgrounds = await page.evaluate(() => [...document.querySelectorAll('.explore-scope .segment')].map(b => `${b.getAttribute('aria-pressed')}:${getComputedStyle(b).backgroundColor}`));
    assert.notEqual(scopeBackgrounds[0].split(':')[1], scopeBackgrounds[1].split(':')[1], `the chosen scope is tinted: ${scopeBackgrounds}`); checks++;
    await page.evaluate(() => localStorage.clear()); await page.reload(); await page.waitForSelector('[data-explore-empty]');
    assert.equal(await page.locator('[data-discovery-guidance]').count(), 0, 'dismissal kept on the account'); checks++;
    if (process.env.SWOOVR_SCREENSHOTS) { await page.setViewportSize({ width: 1280, height: 900 }); await page.screenshot({ path: require('node:path').join(process.env.SWOOVR_SCREENSHOTS, 'explore-1280.png'), fullPage: true });
      await page.click('[data-discovery-filters]'); await page.waitForSelector('[data-member-dialog=filters] [data-filter-form]'); await page.screenshot({ path: require('node:path').join(process.env.SWOOVR_SCREENSHOTS, 'explore-filters-1280.png') }); await page.keyboard.press('Escape'); }
    state = 'short'; await page.reload(); await page.waitForSelector('.people-grid'); assert.equal(await page.locator('[data-discovery-guidance]').count(),0,'still dismissed'); checks++;
    assert.equal(await page.locator('[data-explore-empty]').count(),0); checks++;
    await page.setViewportSize({width:390,height:844});
    if(process.env.SWOOVR_SCREENSHOTS) { const fs=require('node:fs/promises'); await fs.mkdir(process.env.SWOOVR_SCREENSHOTS,{recursive:true}); await page.screenshot({path:require('node:path').join(process.env.SWOOVR_SCREENSHOTS,'explore-390.png'),fullPage:true}); }
    const previousInterceptions = intercepted;
    state = 'failure'; await page.reload(); await page.waitForSelector('[data-explore-unavailable]');
    assert.equal(await page.locator('[data-explore-empty]').count(), 0); checks++;
    assert.ok(intercepted > previousInterceptions); checks++;
    for (const failure of ['limited', 'transport']) { state = failure; await page.reload(); await page.waitForSelector('[data-explore-unavailable]'); assert.equal(await page.locator('[data-explore-empty]').count(), 0); checks++; }
    // Codex (PR 465): on Who I'd like to meet, a slow response for the mode left behind never fills the mode now selected.
    for (const [mode, age] of [['friends', 30], ['business', 40]]) {
      if (mode === 'business') await call(token, '/api/me/modes/business', { method: 'PUT', body: { enabled: true, visible: true } });
      assert.equal((await call(token, `/api/me/modes/${mode}/filters`, { method: 'PUT', body: { minAge: age, maxAge: 60, requiredChips: [], requiredMatch: 'any', excludedChips: [], idVerifiedOnly: false, visitors: 'both', maxDistanceKm: null, sort: 'recent' } })).status, 200);
    }
    await page.unroute('**/api/explore**').catch(() => {});
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(web + '/me/matching'); await page.waitForSelector('[data-filter-form=dating]');
    await page.route('**/api/me/modes/friends/filters', async route => { await new Promise(r => setTimeout(r, 1500)); await route.continue(); });
    await page.check('[data-filter-mode=friends]'); await page.check('[data-filter-mode=business]');
    await page.waitForSelector('[data-filter-form=business]'); await page.waitForTimeout(2000); // the slow Friends answer has arrived by now
    assert.equal(await page.locator('[data-filter-form]').getAttribute('data-filter-form'), 'business'); checks++;
    assert.equal(await page.inputValue('#min-age'), '40', 'Business keeps its own filters, not the late Friends ones'); checks++;
    await page.unroute('**/api/me/modes/friends/filters');
    // Codex (PR 465): a Dating save that finishes after the member switched to Friends never changes the Friends form.
    await page.check('[data-filter-mode=dating]'); await page.waitForSelector('[data-filter-form=dating]');
    await page.selectOption('#min-age', '25');
    await page.route('**/api/me/modes/dating/filters', async route => { if (route.request().method() === 'PUT') await new Promise(r => setTimeout(r, 1500)); await route.continue(); });
    await page.click('[data-filter-form=dating] button[type=submit]');
    await page.check('[data-filter-mode=friends]'); await page.waitForSelector('[data-filter-form=friends]');
    await page.waitForTimeout(2500); // the slow Dating save has finished by now
    assert.equal(await page.locator('[data-filter-form]').getAttribute('data-filter-form'), 'friends'); checks++;
    assert.equal(await page.inputValue('#min-age'), '30', 'Friends keeps its own values after the late Dating save'); checks++;
    assert.notEqual(await page.locator('[data-filter-form] .rd-status').getAttribute('data-filter-status'), 'saved', 'no Dating status on the Friends form'); checks++;
    assert.equal(await page.locator('[data-filter-form] fieldset').first().isDisabled(), false, 'the Friends form is usable'); checks++;
    await page.unroute('**/api/me/modes/dating/filters');
    await page.check('[data-filter-mode=dating]'); await page.waitForSelector('[data-filter-form=dating]');
    assert.equal(await page.inputValue('#min-age'), '25', 'the Dating save went through'); checks++;
    assert.notEqual(await page.locator('[data-filter-form] .rd-status').getAttribute('data-filter-status'), 'draft', 'no stale Dating draft after it saved'); checks++;
    // Codex (PR 465): edits made after a save started survive that save finishing (Dating: save 26, then edit to 27 meanwhile).
    await page.selectOption('#min-age', '26');
    await page.route('**/api/me/modes/dating/filters', async route => { if (route.request().method() === 'PUT') await new Promise(r => setTimeout(r, 2500)); await route.continue(); });
    await page.click('[data-filter-form=dating] button[type=submit]');
    await page.check('[data-filter-mode=friends]'); await page.waitForSelector('[data-filter-form=friends]');
    await page.check('[data-filter-mode=dating]'); await page.waitForSelector('[data-filter-form=dating]');
    await page.selectOption('#min-age', '27');
    await page.check('[data-filter-mode=friends]'); await page.waitForSelector('[data-filter-form=friends]');
    await page.waitForTimeout(3000); // the slow save of 26 has finished by now
    await page.unroute('**/api/me/modes/dating/filters');
    await page.check('[data-filter-mode=dating]'); await page.waitForSelector('[data-filter-form=dating]');
    assert.equal(await page.inputValue('#min-age'), '27', 'the newer unsaved edit is kept'); checks++;
    assert.equal(await page.locator('[data-filter-form] .rd-status').getAttribute('data-filter-status'), 'draft'); checks++;
    // Codex (PR 465): one save at a time per mode. Returning to Dating while its save is pending can't start another one.
    await page.click('[data-filter-form=dating] button[type=submit]'); await page.waitForSelector('[data-filter-status=saved]'); // save 27 first
    await page.selectOption('#min-age', '28');
    await page.route('**/api/me/modes/dating/filters', async route => { if (route.request().method() === 'PUT') await new Promise(r => setTimeout(r, 2500)); await route.continue(); });
    await page.click('[data-filter-form=dating] button[type=submit]');
    await page.check('[data-filter-mode=friends]'); await page.waitForSelector('[data-filter-form=friends]');
    await page.check('[data-filter-mode=dating]'); await page.waitForSelector('[data-filter-form=dating]');
    assert.equal(await page.locator('[data-filter-save]').getAttribute('data-filter-save'), 'pending'); checks++;
    assert.equal(await page.locator('[data-filter-save]').isDisabled(), true, 'no second Dating save while the first is pending'); checks++;
    await page.waitForSelector('[data-filter-save=ready]', { timeout: 10000 });
    await page.unroute('**/api/me/modes/dating/filters');
    assert.equal(await page.inputValue('#min-age'), '28', 'the form shows what was saved'); checks++;
    assert.equal((await call(token, '/api/me/modes/dating/filters')).body.minAge, 28, 'and the server holds it'); checks++;
    assert.equal(await page.locator('[data-filter-form] .rd-status').getAttribute('data-filter-status'), 'saved', 'says saved, not unsaved changes'); checks++;
    console.log(`PASS discovery guidance: ${checks} synthetic projection checks; no activity/density/provider claims.`);
  } finally { await edge.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
