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
    await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=filters]', { state: 'detached' });
    assert.equal(methods.filter(method => method !== 'GET').length, 0, 'Guidance must not write filters/modes/travel'); checks++;
    // The notice is dismissed for this browser and comes back from About Explore.
    await page.click('[data-explore-notice-close]'); assert.equal(await page.locator('[data-discovery-guidance]').count(), 0); checks++;
    await page.reload(); await page.waitForSelector('[data-explore-empty]'); assert.equal(await page.locator('[data-discovery-guidance]').count(), 0, 'dismissal remembered'); checks++;
    await page.click('[data-explore-about]'); assert.equal(await page.locator('[data-discovery-guidance]').count(), 1); checks++;
    if (process.env.SWOOVR_SCREENSHOTS) { await page.setViewportSize({ width: 1280, height: 900 }); await page.screenshot({ path: require('node:path').join(process.env.SWOOVR_SCREENSHOTS, 'explore-1280.png'), fullPage: true });
      await page.click('[data-discovery-filters]'); await page.waitForSelector('[data-member-dialog=filters] [data-filter-form]'); await page.screenshot({ path: require('node:path').join(process.env.SWOOVR_SCREENSHOTS, 'explore-filters-1280.png') }); await page.keyboard.press('Escape'); }
    state = 'short'; await page.reload(); await page.waitForSelector('.people-grid'); assert.equal(await page.locator('[data-discovery-guidance]').count(),1); checks++;
    assert.equal(await page.locator('[data-explore-empty]').count(),0); checks++;
    await page.setViewportSize({width:390,height:844});
    if(process.env.SWOOVR_SCREENSHOTS) { const fs=require('node:fs/promises'); await fs.mkdir(process.env.SWOOVR_SCREENSHOTS,{recursive:true}); await page.screenshot({path:require('node:path').join(process.env.SWOOVR_SCREENSHOTS,'explore-390.png'),fullPage:true}); }
    const previousInterceptions = intercepted;
    state = 'failure'; await page.reload(); await page.waitForSelector('[data-explore-unavailable]');
    assert.equal(await page.locator('[data-explore-empty]').count(), 0); checks++;
    assert.ok(intercepted > previousInterceptions); checks++;
    for (const failure of ['limited', 'transport']) { state = failure; await page.reload(); await page.waitForSelector('[data-explore-unavailable]'); assert.equal(await page.locator('[data-explore-empty]').count(), 0); checks++; }
    console.log(`PASS discovery guidance: ${checks} synthetic projection checks; no activity/density/provider claims.`);
  } finally { await edge.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
