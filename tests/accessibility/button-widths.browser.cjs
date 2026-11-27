// Issue 574 (D447): buttons are as wide as their label on every member page, in rows with space between them. Fails when a
// visible button on a member page is wider than 320 px on desktop, or when two buttons in a row touch. Menus, pickers and
// the photo viewer's action list are rows of a list, not buttons in this sense, and are left out. Fictional member only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;
const routes = ['/explore', '/connections', '/chats', '/me/profile', '/me/photos', '/me/matching', '/me/meetups', '/me/verification', '/me/safety',
  '/me/standing', '/me/notifications', '/settings/appearance', '/me/support', '/account/membership', '/account', '/account/data', '/me/notices'];
const SELECTOR = '.button, .primary-button, .primary-link, .quiet-button';
const EXEMPT = '.ph-viewer-actions, .chat-menu, .safety-menu-list, .city-results, [role=menu], [popover], .settings-nav, .home-actions, .tab-bar';

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const member = await createMember('btnw');
    await cli('beta', 'add', member.email);
    const token = await memberToken(browser, member);
    assert.equal((await call(token, '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Wren', homeCityId: 2078025 } })).status, 200);
    for (const mode of ['dating', 'friends']) await call(token, `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
    await matchReady(token);
    const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
    await page.goto(web + '/account/sign-in?intro=done'); await page.fill('#username', member.email); await page.fill('#password', member.password);
    await Promise.all([page.waitForURL(u => !u.href.includes('/realms/')), page.click('#kc-login')]);
    const problems = [];
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      for (const route of routes) {
        await page.goto(web + route); await page.waitForLoadState('networkidle'); await page.waitForTimeout(400);
        const found = await page.evaluate(([selector, exempt, wide]) => {
          const shown = [...document.querySelectorAll(selector)].filter(b => !b.closest(exempt) && b.offsetParent !== null && b.getBoundingClientRect().width > 0);
          const out = [];
          for (const b of shown) {
            const r = b.getBoundingClientRect(), label = (b.innerText || b.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 40);
            if (wide && r.width > 320) out.push(`${Math.round(r.width)}px "${label}"`);
            for (const o of shown) {
              if (o === b) continue; const q = o.getBoundingClientRect();
              const sameRow = Math.abs(q.top - r.top) < 4, gap = q.left - r.right;
              if (sameRow && gap >= 0 && gap < 6) out.push(`touching "${label}" | "${(o.innerText || '').trim().slice(0, 30)}"`);
            }
          }
          return out;
        }, [SELECTOR, EXEMPT, width >= 1000]);
        if (found.length) problems.push(`${route} @${width}: ${found.join('; ')}`); else pass();
        if (output) await page.screenshot({ path: `${output}/buttons${route.replaceAll('/', '-')}-${width}.png`, fullPage: true });
      }
    }
    assert.deepEqual(problems, [], 'natural-width buttons with space between them:\n' + problems.join('\n'));
    console.log(`PASS: natural-width buttons (issue 574), ${passed} page checks at 1280 and 390.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
