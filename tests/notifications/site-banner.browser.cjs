// Issue 588 (D445): site-wide banners. Admin publishes (audited), Support only reads; one banner on per placement; the
// optional window and "switch off always wins"; the public endpoints (app and website) return only text and tone; the app
// shows the live banner signed in and signed out, in each tone, and it can't be dismissed. Fictional data only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, admin, api, createMember, createStaff, memberToken, staffToken, rootAdminToken, fillStaffOtp, call, sql } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  const ids = [];
  let st;
  try {
    const root = await rootAdminToken(browser);
    const adm = await createStaff('banner-adm'), sup = await createStaff('banner-sup');
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: adm.email, role: 'admin' } })).status, 201);
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: sup.email, role: 'support' } })).status, 201);
    st = await staffToken(browser, adm); await call(st, '/api/session');
    const support = await staffToken(browser, sup); await call(support, '/api/session');
    const member = await createMember('banner-mem'); const mt = await memberToken(browser, member);
    const create = body => call(st, '/api/admin/site-banners', { method: 'POST', body: { tone: 'info', inApp: true, onWebsite: false, startsAt: null, endsAt: null, ...body } });
    const turn = (id, on, token = st) => call(token, `/api/admin/site-banners/${id}/${on ? 'enable' : 'disable'}`, { method: 'POST' });
    const publicBanner = async placement => { const r = await fetch(`${api}/api/site-banner?placement=${placement}`); return [r.status, r.status === 200 ? await r.json() : null, r.headers.get('cache-control')]; };

    // Only staff; Support reads, never writes; a member can't read the list.
    assert.equal((await call(mt, '/api/admin/site-banners')).status, 403); pass();
    assert.equal((await call(support, '/api/admin/site-banners')).status, 200); pass();
    assert.equal((await call(support, '/api/admin/site-banners', { method: 'POST', body: { text: 'Fictional', tone: 'info', inApp: true, onWebsite: false } })).status, 403, 'Support can view only'); pass();
    // Validation.
    for (const bad of [{ text: '' }, { text: 'x'.repeat(201) }, { text: 'Two\nlines' }, { tone: 'pink' }, { inApp: false, onWebsite: false },
      { startsAt: '2026-12-02T00:00:00Z', endsAt: '2026-12-01T00:00:00Z' }])
      assert.equal((await create({ text: 'Fictional banner', ...bad })).status, 400, JSON.stringify(bad));
    pass();

    // Saved switched off: nothing public yet.
    const app = await create({ text: 'Fictional maintenance tonight from 10 pm', tone: 'warning' });
    assert.equal(app.status, 200, JSON.stringify(app.body)); ids.push(app.body.id);
    assert.deepEqual([app.body.enabled, app.body.state, app.body.canEdit], [false, 'off', true]); pass();
    assert.equal((await publicBanner('app'))[0], 204); pass();
    // Switched on: the app placement only, text and tone only, briefly cached.
    assert.equal((await turn(app.body.id, true)).body.state, 'live'); pass();
    const [status, body, cache] = await publicBanner('app');
    assert.deepEqual([status, body], [200, { text: 'Fictional maintenance tonight from 10 pm', tone: 'warning' }]); pass();
    assert.match(cache ?? '', /public, max-age=60/); pass();
    assert.equal((await publicBanner('website'))[0], 204, 'not on the website'); pass();
    assert.equal((await fetch(`${api}/api/v1/site-banner`)).status, 204); pass();
    // One banner on per placement; a website-only banner can be on beside it.
    const second = await create({ text: 'Fictional second app banner' }); ids.push(second.body.id);
    const taken = await turn(second.body.id, true);
    assert.deepEqual([taken.status, taken.body?.code], [409, 'site-banner-placement-taken']); pass();
    const site = await create({ text: 'Fictional website notice', tone: 'critical', inApp: false, onWebsite: true }); ids.push(site.body.id);
    assert.equal((await turn(site.body.id, true)).status, 200); pass();
    const v1 = await fetch(`${api}/api/v1/site-banner`);
    assert.deepEqual([v1.status, await v1.json(), v1.headers.get('access-control-allow-origin')], [200, { text: 'Fictional website notice', tone: 'critical' }, '*']); pass();
    // Editing an enabled banner into a placement that's taken is refused too.
    const clash = await call(st, `/api/admin/site-banners/${site.body.id}`, { method: 'PUT', body: { text: 'Fictional website notice', tone: 'critical', inApp: true, onWebsite: true } });
    assert.deepEqual([clash.status, clash.body?.code], [409, 'site-banner-placement-taken']); pass();

    // The app shows it signed out and signed in, above the top bar, with no way to dismiss it.
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    const seen = async (path, text) => { for (let i = 0; i < 40; i++) { await page.goto(web + path); if ((await page.locator('[data-site-banner]').count()) && (await page.locator('[data-site-banner]').innerText()).includes(text)) return true; await page.waitForTimeout(1000); } return false; };
    assert.ok(await seen('/terms', 'Fictional maintenance tonight'), 'signed out'); pass();
    assert.equal(await page.locator('[data-site-banner]').getAttribute('data-tone'), 'warning'); pass();
    assert.equal(await page.locator('[data-site-banner] button, [data-site-banner] a').count(), 0, 'nothing to dismiss'); pass();
    if (output) await page.screenshot({ path: `${output}/site-banner-warning-signed-out-1280.png` });
    await page.goto(web + '/account/sign-in?intro=done'); await page.fill('#username', member.email); await page.fill('#password', member.password);
    await Promise.all([page.waitForURL(u => !u.href.includes('/realms/')), page.click('#kc-login')]);
    assert.ok(await seen('/me/profile', 'Fictional maintenance tonight'), 'signed in'); pass();
    assert.ok(await page.locator('[data-site-banner]').evaluate(b => b.getBoundingClientRect().bottom <= document.querySelector('.app-header').getBoundingClientRect().top + 1), 'above the top bar'); pass();
    if (output) await page.screenshot({ path: `${output}/site-banner-warning-signed-in-1280.png` });
    for (const [tone, width, scheme] of [['info', 390, 'light'], ['critical', 390, 'dark']]) {
      assert.equal((await call(st, `/api/admin/site-banners/${app.body.id}`, { method: 'PUT', body: { text: `Fictional ${tone} banner`, tone, inApp: true, onWebsite: false } })).status, 200);
      await page.setViewportSize({ width, height: 844 }); await page.emulateMedia({ colorScheme: scheme });
      assert.ok(await seen('/me/profile', `Fictional ${tone} banner`), tone); pass();
      assert.equal(await page.locator('[data-site-banner]').getAttribute('data-tone'), tone); pass();
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no sideways scroll'); pass();
      if (output) await page.screenshot({ path: `${output}/site-banner-${tone}-${scheme}-${width}.png` });
    }

    // The window: a future start doesn't show yet; switching off always wins.
    const later = new Date(Date.now() + 86400000).toISOString();
    const scheduled = await call(st, `/api/admin/site-banners/${app.body.id}`, { method: 'PUT', body: { text: 'Fictional later banner', tone: 'info', inApp: true, onWebsite: false, startsAt: later, endsAt: null } });
    assert.equal(scheduled.body.state, 'scheduled'); pass();
    assert.equal((await publicBanner('app'))[0], 204, 'not before its start'); pass();
    assert.equal((await call(st, `/api/admin/site-banners/${app.body.id}`, { method: 'PUT', body: { text: 'Fictional now banner', tone: 'info', inApp: true, onWebsite: false, startsAt: null, endsAt: later } })).body.state, 'live'); pass();
    assert.equal((await turn(app.body.id, false)).body.state, 'off'); pass();
    assert.equal((await publicBanner('app'))[0], 204, 'off wins'); pass();
    assert.equal((await turn(second.body.id, true)).status, 200, 'the placement is free again'); pass();
    assert.equal((await turn(second.body.id, false)).status, 200);

    // Audited with ids only, never the text.
    assert.ok(Number(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'site-banner.%'`)) >= 8); pass();
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'site-banner.%' AND detail ILIKE '%Fictional%'`), '0'); pass();

    // Admin: the Announcements page lists them with their state and offers the form.
    const staffContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const staffPage = await staffContext.newPage();
    await staffPage.goto(admin + '/sign-in'); await staffPage.fill('#username', adm.email); await staffPage.fill('#password', adm.password); await staffPage.click('#kc-login');
    await fillStaffOtp(staffPage, adm); await staffPage.waitForURL(u => u.href.startsWith(admin) && !u.href.includes('/realms/'), { timeout: 30000 });
    await staffPage.goto(admin + '/announcements'); await staffPage.waitForSelector('[data-site-banners]');
    assert.equal(await staffPage.locator(`[data-site-banner="${site.body.id}"]`).getAttribute('data-site-banner-state'), 'live'); pass();
    assert.equal(await staffPage.locator('[data-site-banner-form=new]').count(), 1); pass();
    assert.equal(await staffPage.locator('[data-nav-announcements][aria-current=page]').count(), 1); pass();
    if (output) await staffPage.screenshot({ path: `${output}/site-banner-admin-1440.png`, fullPage: true });
    await staffContext.close(); await context.close();
    console.log(`PASS: site banners (issue 588), ${passed} checks.`);
  } finally {
    if (st) for (const id of ids) await call(st, `/api/admin/site-banners/${id}/disable`, { method: 'POST' }).catch(() => {});
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
