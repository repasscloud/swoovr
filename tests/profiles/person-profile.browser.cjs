// Issues 446/447: another member's profile seen from a second fictional member, on the owned API/database/storage only.
// Synthetic gradient photos and fictional adults; no real people, no provider calls. Checks the action bar (one primary
// Connect, a Report or block menu), that opening a dialog writes nothing, keyboard focus in and out of each dialog, the
// request flow unchanged through the dialog, and no horizontal overflow at phone and desktop widths.
const assert = require('node:assert/strict'), crypto = require('node:crypto'), fs = require('node:fs/promises'), path = require('node:path'), os = require('node:os');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { createMember, memberToken, elevatedRootToken, cli, call, matchReady, sql, claims, web, api } = require('../auth/local-tokens.cjs');
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
   // ---- Issue 447: the layout and the details the viewer may see.
   const catalogue = (await call(a, '/api/modes')).body, chips = mode => catalogue.find(m => m.id === mode).intentions.slice(0, 2);
   for (const mode of ['dating', 'friends'])
    assert.equal((await call(a, '/api/me/modes/' + mode, { method: 'PUT', body: { enabled: true, visible: true, intentions: chips(mode).map(c => c.id) } })).status, 200);
   const identity = show => call(a, '/api/me/identity', { method: 'PUT', body: { gender: 'woman', showGender: true, pronounId: 'self', pronouns: 'xe/xem', showPronouns: true,
    orientation: 'straight', showOrientation: show, showAge: true, datingShowMe: ['man', 'woman', 'non-binary', 'another'] } });
   assert.equal((await identity(true)).status, 200); pass();
   // D175: orientation is Dating only, never another mode, and only when switched on.
   const apiFriends = (await call(b, `/api/profiles/${id}?mode=friends`)).body, apiDating = (await call(b, `/api/profiles/${id}?mode=dating`)).body;
   assert.ok(apiFriends.modes.every(m => m.orientation == null) && !JSON.stringify(apiFriends).includes('Straight'), 'no orientation outside Dating'); pass();
   assert.equal(apiDating.modes.find(m => m.id === 'dating').orientation, 'Straight (heterosexual)'); pass();
   assert.ok(!JSON.stringify(apiDating).includes('1990'), 'never a date of birth'); pass();
   for (const [mode, width] of [['friends', 390], ['friends', 1280], ['dating', 390], ['dating', 1280]]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${web}/people/${id}?mode=${mode}`); await page.waitForSelector('[data-person=ready]', { timeout: 20000 });
    const facts = await page.locator('[data-person-facts]').innerText();
    assert.ok(facts.includes('Woman') && facts.includes('xe/xem') && /\b3\d\b/.test(facts), facts); pass();
    assert.deepEqual(await page.locator(`[data-person-details] [data-person-mode=${mode}] .chip.is-on`).allInnerTexts(), chips(mode).map(c => c.label)); pass();
    assert.equal(await page.locator('[data-person-orientation]').count(), mode === 'dating' ? 1 : 0); pass();
    // The pending Friends request keeps Friends (and its photos) on the Dating view too (D175); Dating never shows on Friends.
    assert.equal(await page.locator('[data-person-photos]').getAttribute('data-person-photos'), '2'); pass();
    assert.equal(await page.locator('[data-shared-modes]').getAttribute('data-shared-modes'), mode === 'friends' ? 'friends' : 'dating friends'); pass();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `no horizontal overflow (${mode} ${width})`); pass();
    if (mode === 'friends') {
     const [lead, other] = await page.locator('[data-person-photos] img').evaluateAll(images => images.map(i => i.getBoundingClientRect().width));
     assert.ok(lead > other * 2, 'the lead photo leads the gallery'); pass();
    }
    if (width === 1280) { const [gallery, details] = await page.evaluate(() => ['[data-person-layout] > :first-child', '[data-person-details]'].map(q => document.querySelector(q).getBoundingClientRect())); assert.ok(details.left >= gallery.right, 'details sit beside the gallery on desktop'); pass(); }
    if (output) await page.screenshot({ path: `${output}/layout-${mode}-${width}.png`, fullPage: true });
   }
   // ---- Issue 467: the photo viewer (open, step, keyboard, swipe, focus), Report photo and the save deterrent.
   await page.setViewportSize({ width: 1280, height: 900 });
   await page.goto(`${web}/people/${id}?mode=friends`); await page.waitForSelector('[data-person=ready]', { timeout: 20000 });
   const guarded = selector => page.$$eval(selector, images => images.length > 0 && images.every(img => {
    const menu = new MouseEvent('contextmenu', { bubbles: true, cancelable: true }), drag = new DragEvent('dragstart', { bubbles: true, cancelable: true });
    img.dispatchEvent(menu); img.dispatchEvent(drag);
    return menu.defaultPrevented && drag.defaultPrevented && img.draggable === false && getComputedStyle(img).userSelect === 'none';
   }));
   const title = () => page.locator('[data-person-viewer-title]').innerText();
   const focused = () => page.evaluate(() => ({ id: document.activeElement?.id, open: document.activeElement?.getAttribute('data-person-photo-open'), inViewer: !!document.activeElement?.closest('[data-person-photo-viewer]') }));
   assert.equal(await page.locator('[data-person-photo-open]').count(), 2); pass();
   assert.ok(await guarded('[data-person-photos] img'), 'gallery photos: no context menu, no drag, no selection'); pass();
   const gallerySources = await page.locator('[data-person-photos] img').evaluateAll(images => images.map(i => i.getAttribute('src')));
   await page.click('[data-person-photo-open="0"]');
   await page.waitForSelector('[data-person-photo-viewer] [role=dialog][aria-modal=true]');
   // Issue 475 (D399): the title is just "1 of 2"; the dialog still names the person for screen readers; the upload date
   // (no time) replaces the mode on every photo, the main one included; no photo verification line before issue 372.
   assert.equal(await title(), "1 of 2"); pass();
   assert.equal(await page.getAttribute('[data-person-photo-viewer] [role=dialog]', 'aria-label'), "Fictional Sam's photo, 1 of 2"); pass();
   assert.match(await page.innerText('[data-person-viewer-uploaded]'), /^Uploaded: \d{1,2} [A-Z][a-z]{2} \d{4}$/); pass();
   assert.equal(await page.locator('[data-person-viewer-mode], [data-person-viewer-verified]').count(), 0); pass();
   assert.equal((await focused()).id, 'person-viewer-title'); pass();
   assert.equal(await page.getAttribute('[data-person-viewer-image]', 'src'), gallerySources[0]); pass();
   assert.ok(await guarded('[data-person-viewer-image]'), 'viewer photo guarded'); pass();
   assert.equal(await page.locator('[data-person-photo-viewer] button').evaluateAll(b => b.map(x => x.innerText.trim()).filter(Boolean).join('|')), '‹|›|×|Report photo', 'no owner actions'); pass();
   if (output) await page.screenshot({ path: `${output}/person-photo-viewer-1280.png` });
   await page.keyboard.press('ArrowRight'); assert.equal(await title(), "2 of 2"); pass();
   assert.equal(await page.getAttribute('[data-person-viewer-image]', 'src'), gallerySources[1]); pass();
   assert.ok(await page.isDisabled('[data-person-viewer-next]')); pass();
   await page.keyboard.press('ArrowRight'); assert.equal(await title(), "2 of 2", 'stays on the last photo'); pass();
   await page.keyboard.press('ArrowLeft'); assert.equal(await title(), "1 of 2"); pass();
   await page.click('[data-person-viewer-next]'); assert.equal(await title(), "2 of 2"); pass();
   await page.click('[data-person-viewer-prev]'); assert.equal(await title(), "1 of 2"); pass();
   // A left swipe steps forward, a right swipe back.
   const swipe = dx => page.$eval('[data-person-viewer-stage]', (stage, dx) => {
    const touch = x => new Touch({ identifier: 1, target: stage, clientX: x, clientY: 200 });
    stage.dispatchEvent(new TouchEvent('touchstart', { bubbles: true, touches: [touch(300)], changedTouches: [touch(300)] }));
    stage.dispatchEvent(new TouchEvent('touchend', { bubbles: true, touches: [], changedTouches: [touch(300 + dx)] }));
   }, dx);
   await swipe(-120); await page.waitForFunction(() => document.querySelector('[data-person-viewer-title]')?.innerText.endsWith('2 of 2')); pass();
   await swipe(120); await page.waitForFunction(() => document.querySelector('[data-person-viewer-title]')?.innerText.endsWith('1 of 2')); pass();
   for (let i = 0; i < 6; i++) { await page.keyboard.press('Tab'); assert.ok((await focused()).inViewer, 'Tab stays in the viewer'); } pass();
   await page.keyboard.press('Escape'); await page.waitForSelector('[data-person-photo-viewer]', { state: 'detached' });
   await page.waitForFunction(() => document.activeElement?.getAttribute('data-person-photo-open') === '0'); pass(); // focus back on the photo
   // Keyboard: Enter and Space on a thumbnail open that photo; Escape returns focus to it.
   await page.focus('[data-person-photo-open="1"]'); await page.keyboard.press('Enter');
   await page.waitForSelector('[data-person-photo-viewer]'); assert.equal(await title(), "2 of 2"); pass();
   await page.keyboard.press('Escape'); await page.waitForFunction(() => document.activeElement?.getAttribute('data-person-photo-open') === '1'); pass();
   await page.keyboard.press(' '); await page.waitForSelector('[data-person-photo-viewer]'); assert.equal(await title(), "2 of 2"); pass();
   await page.click('[data-person-viewer-close]'); await page.waitForFunction(() => document.activeElement?.getAttribute('data-person-photo-open') === '1'); pass();
   // CONFIRMED: Report photo uses the existing report flow and records which photo it was.
   await page.click('[data-person-photo-open="1"]'); await page.waitForSelector('[data-person-photo-viewer]');
   assert.equal(await page.getAttribute('[data-report-photo]', 'data-report-photo'), ids[1]); pass();
   await page.focus('[data-report-photo]'); await page.keyboard.press('Enter');
   await page.waitForSelector('[data-member-dialog=report-photo] #report-category'); assert.equal(await page.locator('[data-person-photo-viewer]').count(), 0); pass();
   assert.equal(await page.locator('#report-photo-dialog-heading').innerText(), "Report Fictional Sam's photo"); pass();
   if (output) await page.screenshot({ path: `${output}/person-report-photo-1280.png` });
   // PR 471 review: the report form's fields don't touch in the dialog.
   const [select, textarea] = await page.evaluate(() => ['#report-category', '#report-text'].map(q => document.querySelector(q).getBoundingClientRect().toJSON()));
   assert.ok(textarea.top - select.bottom >= 8, 'space between Reason and the note'); pass();
   await page.selectOption('#report-category', { index: 1 }); await page.click('[data-report-form] button[type=submit]');
   await page.waitForSelector('[data-member-dialog=report-photo] [data-report=sent]'); pass();
   assert.equal(await sql(`SELECT subject_type || ':' || subject_ref FROM reports WHERE subject_ref = '${ids[1]}'`), `Photo:${ids[1]}`); pass();
   assert.equal(await sql(`SELECT count(*) FROM case_evidence e JOIN reports r ON r.case_id = e.case_id WHERE r.subject_ref = '${ids[1]}' AND '${ids[1]}' = ANY(e.media_ids)`), '1'); pass();
   // PR 471 (Codex): an admin working the case can open the reported profile photo, with a recorded purpose.
   const photoCase = await sql(`SELECT case_id FROM reports WHERE subject_ref = '${ids[1]}'`), root = await elevatedRootToken(edge.browser);
   const caseView = (media, body) => call(root, `/api/admin/media/${media}/case-view`, { method: 'POST', body });
   assert.equal((await caseView(ids[0], { caseId: photoCase, purpose: 'Fictional check' })).status, 404, 'a photo the case does not name'); pass();
   const viewed = await caseView(ids[1], { caseId: photoCase, purpose: 'Fictional check of a reported profile photo' });
   assert.equal(viewed.status, 200, JSON.stringify(viewed.body)); assert.equal((await fetch(`${api}/${viewed.body.link}`, { headers: { authorization: `Bearer ${root}` } })).status, 200, "the case link serves the photo to the admin"); pass();
   assert.equal(await sql(`SELECT count(*) FROM media_review_views WHERE media_id = '${ids[1]}'`), '1'); pass();
   await page.keyboard.press('Escape'); await page.waitForSelector('[data-member-dialog=report-photo]', { state: 'detached' });
   await page.waitForFunction(() => document.activeElement?.getAttribute('data-person-photo-open') === '1'); pass();
   // Another member's photo id that isn't on a profile they may see is refused like any unavailable subject.
   const refused = await call(b, '/api/reports', { method: 'POST', body: { subjectType: 'photo', subjectId: crypto.randomUUID(), category: 'harassment', text: null, block: false } });
   assert.equal(refused.status, 404); pass();
   // Phone width: the viewer fills the screen without horizontal overflow.
   await page.setViewportSize({ width: 390, height: 844 }); await page.click('[data-person-photo-open="0"]'); await page.waitForSelector('[data-person-photo-viewer]');
   assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'viewer at 390 does not overflow'); pass();
   if (output) await page.screenshot({ path: `${output}/person-photo-viewer-390.png` });
   await page.keyboard.press('Escape'); await page.setViewportSize({ width: 1280, height: 900 });
   // Explore cards carry the same guard.
   await page.goto(`${web}/explore?mode=friends`); await page.waitForSelector('[data-tile-photo]', { timeout: 20000 });
   assert.ok(await guarded('[data-tile-photo]'), 'Explore card photos guarded'); pass();
   // Issue 443 (D398): photo and quote cards share one size; the photo is square and fills the card's width.
   const cards = await page.locator('.member-card').evaluateAll(els => els.map(e => { const r = e.getBoundingClientRect(), m = e.querySelector('.mc-media').getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height), Math.round(m.width - m.height)]; }));
   assert.ok(cards.length > 0 && cards.every(([w, h, square]) => w === cards[0][0] && h === cards[0][1] && Math.abs(square) <= 1), `equal square cards: ${JSON.stringify(cards)}`); pass();
   // Issue 496 (D407): the photo count is the photos this viewer sees in the mode, the main photo once; shown from two.
   const explored = (await call(b, '/api/explore?mode=friends')).body.people.find(p => p.id === id), seen = (await call(b, `/api/profiles/${id}?mode=friends`)).body.modes.find(m => m.id === 'friends');
   assert.equal(explored.photoCount, new Set([...seen.photos.map(p => p.id)]).size); assert.ok(explored.photoCount > 1, String(explored.photoCount)); pass();
   const countPill = page.locator(`.member-card:has(a[href^="/people/${id}"]) [data-tile-photos]`);
   assert.equal(await countPill.getAttribute('data-tile-photos'), String(explored.photoCount)); pass();
   assert.ok(await countPill.evaluate(e => { const p = e.getBoundingClientRect(), m = e.closest('.mc-media').getBoundingClientRect(); return p.right <= m.right && p.bottom <= m.bottom && p.left > m.left + m.width / 2 && p.top > m.top + m.height / 2; }), 'bottom right of the photo'); pass();
   // A checked-in traveller with a photo: the pill over the photo, inside it, and no second "Visiting" line (the API answer is adjusted here).
   await page.route(/\/api\/explore\?/, async route => { const answer = await route.fetch(), body = await answer.json(); for (const p of body.people) p.visiting = 'A Very Long Destination City Name Indeed'; await route.fulfill({ response: answer, json: body }); });
   for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 844 }); await page.goto(`${web}/explore?mode=friends`);
    const travel = page.locator('[data-tile-visiting]').first(); await travel.waitFor({ timeout: 20000 });
    assert.ok(await travel.evaluate(e => { const p = e.getBoundingClientRect(), m = e.closest('.mc-media').getBoundingClientRect(); return p.right <= m.right && p.left >= m.left && p.bottom < m.bottom; }), 'a long city stays inside the photo'); pass();
    assert.equal(await page.locator('.member-card:has([data-tile-visiting]) [data-tile-detail]').count(), 0); pass();
    if (output) await page.screenshot({ path: `${output}/explore-overlays-${width}.png` });
   }
   await page.unroute(/\/api\/explore\?/); await page.setViewportSize({ width: 1280, height: 900 });
   await page.goto(`${web}/explore?mode=friends`); await page.waitForSelector('[data-tile-photo]', { timeout: 20000 });
   assert.ok((await page.locator('.member-card .mc-chips').evaluateAll(els => els.map(e => e.children.length))).every(n => n <= 2)); pass();
   if (output) { await page.screenshot({ path: output + '/explore-cards-1280.png' }); await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: output + '/explore-cards-390.png', fullPage: true }); await page.setViewportSize({ width: 1280, height: 900 }); }
   assert.equal((await identity(false)).status, 200);
   assert.equal((await call(b, `/api/profiles/${id}?mode=dating`)).body.modes.find(m => m.id === 'dating').orientation, null, 'switched off: not shown'); pass();
   console.log(`PASS: person profile actions (issue 446), ${checks} checks: one primary Connect, Report or block menu, dialogs write nothing on open, focus in/out, Escape, request unchanged, no overflow at 390/1280; layout (issue 447): key facts, details per mode, Dating-only orientation under its switch, lead photo, side-by-side on desktop; photo viewer (issue 467): open, ←/→, buttons, swipe, Tab trap, focus return, Enter/Space, Report photo recorded, save deterrent on gallery, viewer and Explore cards.`);
  } finally { await context.close(); }
 } finally { await edge.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
