// Issues 446/447: another member's profile seen from a second fictional member, on the owned API/database/storage only.
// Synthetic gradient photos and fictional adults; no real people, no provider calls. Checks the action bar (one primary
// Connect, a Report or block menu), that opening a dialog writes nothing, keyboard focus in and out of each dialog, the
// request flow unchanged through the dialog, and no horizontal overflow at phone and desktop widths.
const assert = require('node:assert/strict'), crypto = require('node:crypto'), fs = require('node:fs/promises'), path = require('node:path'), os = require('node:os');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { createMember, memberToken, cli, call, matchReady, sql, claims, web } = require('../auth/local-tokens.cjs');
const { synthetic } = require('../media/synthetic-images.cjs');
if (!process.env.SWOOVR_TEST_API_BASE || !process.env.SWOOVR_TEST_APP_DATABASE || process.env.Media__Store !== 'local' || !process.env.Media__Local__Path?.startsWith(os.tmpdir())) throw Error('Person profile checks require the owned local media/database runner');
const output = process.env.SWOOVR_SCREENSHOTS;
let checks = 0; const pass = () => checks++;
(async () => {
 const edge = await require('../auth/managed-edge.cjs').managedEdge(chromium);
 try {
  const [owner, viewer] = await Promise.all(['person-profile-owner', 'person-profile-viewer'].map(createMember));
  await cli('beta', 'add', owner.email, viewer.email);
  const a = await memberToken(edge.browser, owner), b = await memberToken(edge.browser, viewer);
  for (const [token, name, about] of [[a, 'Fictional Sam', 'Fictional about text for a profile layout check.'], [b, 'Fictional Viewer', null]]) {
   assert.equal((await call(token, '/api/me/profile', { method: 'PUT', body: { displayName: name, homeCityId: 2078025, about } })).status, 200); await matchReady(token);
   for (const mode of ['dating', 'friends']) await call(token, '/api/me/modes/' + mode, { method: 'PUT', body: { enabled: true, visible: true } });
  }
  const id = (await call(a, '/api/me/profile')).body.publicId, account = await sql(`SELECT id FROM accounts WHERE subject='${claims(a).sub}'`);
  const context = await edge.browser.newContext({ viewport: { width: 1280, height: 900 } }), page = await context.newPage();
  try {
   // Two approved synthetic photos in Friends, the first one main.
   const ids = [crypto.randomUUID(), crypto.randomUUID()];
   await fs.mkdir(path.join(process.env.Media__Local__Path, 'photos'), { recursive: true });
   for (const [i, media] of ids.entries()) {
    const jpeg = await synthetic(page, { kind: 'gradient', type: 'image/jpeg', seed: 446 + i }), hash = crypto.createHash('sha256').update(jpeg).digest('hex');
    await fs.writeFile(path.join(process.env.Media__Local__Path, 'photos', media.replaceAll('-', '')), jpeg, { mode: 0o600 });
    await sql(`INSERT INTO media_items(id,account_id,status,object_key,sha256,upload_sha256,content_type,width,height,bytes,created_at,screen_attempts,known_content_checked,screener,screened_at) VALUES('${media}','${account}','Passed','photos/${media.replaceAll('-','')}',decode('${hash}','hex'),decode('${hash}','hex'),'image/jpeg',640,480,${jpeg.length},now()-interval '2 hours',1,false,'synthetic-fixture',now())`);
   }
   assert.equal((await call(a, '/api/me/photos/placement', { method: 'PUT', body: { scope: 'friends', mediaIds: ids, mainMediaId: ids[0] } })).status, 200); pass();

   await page.goto(web + '/account/sign-in'); await page.fill('#username', viewer.email); await page.fill('#password', viewer.password);
   await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
   const writes = []; page.on('request', r => { if (!['GET', 'HEAD', 'OPTIONS'].includes(r.method()) && /\/(bff\/)?api\//.test(r.url())) writes.push(r.method() + ' ' + new URL(r.url()).pathname); });
   await page.goto(`${web}/people/${id}?mode=friends`); await page.waitForSelector('[data-person=ready]', { timeout: 20000 });

   // Issue 446: one primary action, the safety menu closed, the old cards gone.
   assert.equal(await page.locator('[data-person-actions] [data-open-connect]').innerText(), 'Connect'); pass();
   assert.equal(await page.locator('[data-connect-modes], [data-report=idle], [data-block=idle]').count(), 0); pass();
   assert.equal(await page.locator('[data-safety-menu]').getAttribute('aria-expanded'), 'false'); pass();
   for (const width of [390, 1280]) { await page.setViewportSize({ width, height: 900 }); assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no horizontal overflow at ' + width); pass(); if (output) await page.screenshot({ path: `${output}/person-${width}.png`, fullPage: true }); }

   // Keyboard: Enter opens Connect, focus moves to its heading, Escape closes and returns focus. Nothing was written.
   await page.focus('[data-open-connect]'); await page.keyboard.press('Enter');
   await page.waitForSelector('[data-member-dialog=connect] [role=dialog][aria-modal=true]');
   assert.equal(await page.evaluate(() => document.activeElement?.id), 'connect-dialog-heading'); pass();
   assert.equal(await page.locator('[data-connect-mode=friends]').getAttribute('data-connection'), 'none'); pass();
   if (output) await page.screenshot({ path: `${output}/person-connect-dialog-1280.png` });
   for (let i = 0; i < 6; i++) { await page.keyboard.press('Tab'); assert.ok(await page.evaluate(() => !!document.activeElement?.closest('[data-member-dialog]')), 'Tab stays in the dialog'); } pass();
   await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Shift+Tab');
   assert.ok(await page.evaluate(() => !!document.activeElement?.closest('[data-member-dialog]')), 'Shift+Tab stays in the dialog'); pass();
   await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=connect]', { state: 'detached' });
   assert.equal(await page.evaluate(() => document.activeElement?.hasAttribute('data-open-connect')), true); pass();

   // The safety menu: Report, Block and the serious-incident page, all reachable in one step.
   await page.click('[data-safety-menu]');
   assert.equal(await page.locator('[data-safety-menu]').getAttribute('aria-expanded'), 'true'); pass();
   assert.equal(await page.locator('[data-safety-menu-list] li').count(), 3); pass();
   assert.equal(await page.getAttribute('[data-serious-link]', 'href'), `/me/safety/report?type=profile&id=${id}`); pass();
   if (output) await page.screenshot({ path: `${output}/person-safety-menu-1280.png` });
   await page.click('[data-report-open]');
   await page.waitForSelector('[data-member-dialog=report] #report-category'); pass();
   assert.equal(await page.evaluate(() => document.activeElement?.id), 'report-dialog-heading'); pass();
   if (output) await page.screenshot({ path: `${output}/person-report-dialog-1280.png` });
   await page.click('[data-dialog-close]'); await page.waitForSelector('[data-member-dialog=report]', { state: 'detached' });
   assert.equal(await page.evaluate(() => document.activeElement?.hasAttribute('data-safety-menu')), true); pass();
   await page.click('[data-safety-menu]'); await page.click('[data-block-open]');
   await page.waitForSelector('[data-member-dialog=block] [data-block=confirm]');
   await page.locator('[data-block=confirm]').getByRole('button', { name: 'Cancel' }).click();
   await page.waitForSelector('[data-member-dialog=block]', { state: 'detached' });
   assert.deepEqual(writes, [], 'opening and closing Connect, Report and Block wrote nothing'); pass();
   assert.deepEqual((await call(b, '/api/blocks')).body, []); pass();

   // The request flow itself is unchanged: sent from the dialog, shown in the summary afterwards.
   await page.click('[data-open-connect]'); await page.click('[data-connect-mode=friends] button');
   await page.waitForSelector('[data-connect-mode=friends][data-connection=sent]'); pass();
   assert.equal(await page.locator('[data-connect-status]').innerText(), 'Request sent.'); pass();
   assert.ok(writes.some(w => w.endsWith('/api/requests')), writes.join(',')); pass();
   // Codex (PR 463): a click on the dimmed page behind keeps Escape working.
   await page.mouse.click(5, 5);
   assert.equal(await page.locator('[data-member-dialog=connect]').count(), 1, 'a backdrop click does not close or act'); pass();
   await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=connect]', { state: 'detached' }); pass();
   assert.ok((await page.locator('[data-connect-summary]').innerText()).includes('Friends: request sent')); pass();
   // Codex (PR 463): when Message fails (the connection ended meanwhile), the reason shows beside the button.
   const received = (await call(a, '/api/requests?box=received')).body.items.find(r => r.mode === 'friends');
   assert.equal((await call(a, `/api/requests/${received.id}/accept`, { method: 'POST' })).status, 200);
   await page.reload(); await page.waitForSelector('[data-person=ready] [data-message-person]', { timeout: 20000 });
   await page.route('**/api/conversations', route => route.request().method() === 'POST' ? route.fulfill({ status: 404, contentType: 'application/problem+json', body: '{"status":404,"code":"not-found"}' }) : route.continue());
   await page.click('[data-message-person]');
   await page.waitForSelector('[data-message-status=failed]');
   assert.equal(await page.locator('[data-message-status]').innerText(), "You can message people you're connected with."); pass();
   assert.equal(await page.locator('[data-member-dialog]').count(), 0, 'shown on the page, not in a closed dialog'); pass();
   await page.unroute('**/api/conversations');
   // Codex (PR 464): a sent report replaces its form; focus returns to the dialog heading and Escape still closes it.
   await page.click('[data-safety-menu]'); await page.click('[data-report-open]');
   await page.waitForSelector('[data-member-dialog=report] #report-category');
   await page.selectOption('#report-category', { index: 1 });
   await page.focus('[data-report-form] button[type=submit]'); await page.keyboard.press('Enter');
   await page.waitForSelector('[data-member-dialog=report] [data-report=sent]');
   await page.waitForFunction(() => document.activeElement?.id === 'report-dialog-heading'); pass();
   await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=report]', { state: 'detached' }); pass();
   await page.setViewportSize({ width: 390, height: 844 }); await page.click('[data-safety-menu]');
   assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'menu open at 390 does not overflow'); pass();
   if (output) await page.screenshot({ path: `${output}/person-safety-menu-390.png`, fullPage: true });
   console.log(`PASS: person profile actions (issue 446), ${checks} checks: one primary Connect, Report or block menu, dialogs write nothing on open, focus in/out, Escape, request unchanged, no overflow at 390/1280.`);
  } finally { await context.close(); }
 } finally { await edge.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
