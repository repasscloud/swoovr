// Issue 588 (D446): a notification to every member. Admin sends (confirmed, validated, audited with ids only); Support can
// only read; it reaches every active member's centre and never a pending-deletion account; its page opens only for a
// recipient; withdrawing removes it everywhere and closes the page. Fictional data only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, createStaff, memberToken, staffToken, rootAdminToken, call, sql } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const root = await rootAdminToken(browser);
    const adm = await createStaff('bcast-adm'), sup = await createStaff('bcast-sup');
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: adm.email, role: 'admin' } })).status, 201);
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: sup.email, role: 'support' } })).status, 201);
    const st = await staffToken(browser, adm); await call(st, '/api/session');
    const support = await staffToken(browser, sup); await call(support, '/api/session');
    const members = { ann: await createMember('bcast-ann'), del: await createMember('bcast-del') };
    const tokens = { ann: await memberToken(browser, members.ann), del: await memberToken(browser, members.del) };
    for (const n of Object.keys(tokens)) assert.equal((await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Bcast ' + n, homeCityId: 2078025 } })).status, 200, n);
    const sub = n => JSON.parse(Buffer.from(tokens[n].split('.')[1], 'base64url')).sub;
    await sql(`UPDATE accounts SET status = 'PendingDeletion' WHERE subject = '${sub('del')}'`);
    const send = body => call(st, '/api/admin/broadcasts', { method: 'POST', body: { title: 'Fictional maintenance', body: 'Fictional first paragraph.\n\nFictional second paragraph.', confirm: true, ...body } });

    assert.equal((await call(tokens.ann, '/api/admin/broadcasts')).status, 403); pass();
    assert.equal((await call(support, '/api/admin/broadcasts')).status, 200); pass();
    assert.equal((await call(support, '/api/admin/broadcasts', { method: 'POST', body: { title: 'x', body: 'y', confirm: true } })).status, 403, 'Support can view only'); pass();
    for (const bad of [{ confirm: false }, { title: '' }, { title: 'x'.repeat(81) }, { body: '' }, { body: 'x'.repeat(2001) }, { title: 'Two\nlines' }])
      assert.equal((await send(bad)).status, 400, JSON.stringify(bad));
    pass();

    const active = Number(await sql(`SELECT count(*) FROM accounts WHERE realm = 'Members' AND status = 'Active'`));
    const sent = await send({});
    assert.equal(sent.status, 200, JSON.stringify(sent.body)); pass();
    const id = sent.body.id;
    assert.equal(sent.body.recipients, active, 'every active member'); pass();
    assert.equal(await sql(`SELECT count(*) FROM notifications WHERE kind = 'Announcement' AND subject_id = '${id}' AND account_id = (SELECT id FROM accounts WHERE subject = '${sub('del')}')`), '0', 'never a pending-deletion account'); pass();
    const centreRead = await call(tokens.ann, '/api/me/notifications');
    assert.equal(centreRead.status, 200, JSON.stringify(centreRead.body));
    const centre = centreRead.body.items.find(i => i.kind === 'announcement');
    assert.deepEqual([centre?.title, centre?.link, centre?.read], ['From Swoovr: Fictional maintenance', `/me/notifications/announcements/${id}`, false]); pass();
    const late = await createMember('bcast-late'); const lateToken = await memberToken(browser, late);
    assert.equal((await call(lateToken, '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Bcast late', homeCityId: 2078025 } })).status, 200);
    assert.equal((await call(lateToken, `/api/me/announcements/${id}`)).status, 404, 'only a recipient can open it'); pass();

    // The member opens it from the centre: its own read-only page, paragraphs kept, then read.
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(web + '/account/sign-in?intro=done'); await page.fill('#username', members.ann.email); await page.fill('#password', members.ann.password);
    await Promise.all([page.waitForURL(u => !u.href.includes('/realms/')), page.click('#kc-login')]);
    await page.goto(web + '/me/notifications');
    await page.locator('[data-notification-kind=announcement] a').first().click();
    await page.waitForURL(web + `/me/notifications/announcements/${id}`);
    await page.locator(`h1[data-announcement="${id}"]`).waitFor({ timeout: 20000 });
    assert.equal(await page.locator('h1').innerText(), 'Fictional maintenance'); pass();
    assert.deepEqual(await page.locator('[data-announcement-body] p').allInnerTexts(), ['Fictional first paragraph.', 'Fictional second paragraph.']); pass();
    assert.equal((await call(tokens.ann, '/api/me/notifications')).body.items.find(i => i.kind === 'announcement')?.read, true, 'opening it reads it'); pass();
    if (output) await page.screenshot({ path: `${output}/announcement-1280.png`, fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    if (output) await page.screenshot({ path: `${output}/announcement-390.png`, fullPage: true });

    // Withdraw: gone from every centre, the page closes, and it can't be sent again or edited.
    const withdrawn = await call(st, `/api/admin/broadcasts/${id}/withdraw`, { method: 'POST' });
    assert.deepEqual([withdrawn.status, withdrawn.body.canWithdraw, !!withdrawn.body.withdrawnAt], [200, false, true]); pass();
    assert.equal(await sql(`SELECT count(*) FROM notifications WHERE kind = 'Announcement' AND subject_id = '${id}'`), '0'); pass();
    assert.equal((await call(tokens.ann, '/api/me/notifications')).body.items.some(i => i.kind === 'announcement' && i.link.endsWith(id)), false); pass();
    assert.equal((await call(tokens.ann, `/api/me/announcements/${id}`)).status, 404); pass();
    await page.reload(); await page.locator('[data-announcement=unavailable]').waitFor({ timeout: 20000 }); pass();
    assert.equal((await call(st, `/api/admin/broadcasts/${id}`, { method: 'PUT', body: { title: 'x', body: 'y', confirm: true } })).status >= 404, true, 'no edits'); pass();

    // Audited with ids and counts only.
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action IN ('broadcast.sent', 'broadcast.withdrawn') AND target_id = '${id}'`), '2'); pass();
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'broadcast.%' AND detail ILIKE '%Fictional%'`), '0'); pass();
    await context.close();
    console.log(`PASS: notifications to every member (issue 588), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
