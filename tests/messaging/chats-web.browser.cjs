// Issue 541 (with issue 567's points): the Chats list. Each row is one link to the chat with a thumbnail (or initial), name,
// published age where that mode shows it, the mode the chat started in, badges, "Last updated" and the member's own unread
// count. Newest activity first. Reading a chat clears its count; the sender is never told (no read receipts). The Chats tab
// shows the total. Owned local stack; every person is fictional.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, claims, sql, matchReady } = require('../auth/local-tokens.cjs');
const audit = require('../accessibility/audit.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;
const ADELAIDE = 2078025;
const MODES = ['dating', 'friends', 'activities', 'business'];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const names = ['me', 'ada', 'bea'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('chw-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional CHW ' + n, homeCityId: ADELAIDE } });
      assert.equal(saved.status, 200, n);
      pid[n] = saved.body.publicId;
      for (const mode of MODES) await call(tokens[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    const connect = async (from, to, mode) => {
      const ask = await call(tokens[from], '/api/requests', { method: 'POST', body: { to: pid[to], mode } });
      assert.equal(ask.status, 201, `${from}→${to} ${mode}`);
      assert.equal((await call(tokens[to], `/api/requests/${ask.body.id}/accept`, { method: 'POST' })).status, 200);
    };
    await connect('ada', 'me', 'dating');
    await connect('bea', 'me', 'business');
    const open = async (who, to, mode) => (await call(tokens[who], '/api/conversations', { method: 'POST', body: { to: pid[to], mode } })).body.id;
    const withAda = await open('ada', 'me', 'dating');
    const withBea = await open('bea', 'me', 'business');
    const send = (who, conversation, text) => call(tokens[who], `/api/conversations/${conversation}/messages`, { method: 'POST', body: { text } });
    for (const text of ['Fictional hello one', 'Fictional hello two', 'Fictional hello three']) assert.equal((await send('ada', withAda, text)).status, 201);
    assert.equal((await send('me', withAda, 'Fictional reply')).status, 201, 'own messages never count');
    assert.equal((await send('bea', withBea, 'Fictional business hello')).status, 201);

    // API: counts, mode, order, faces, and no read receipt for the sender.
    const list = async who => (await call(tokens[who], '/api/conversations')).body;
    let chats = await list('me');
    const row = id => chats.find(c => c.id === id);
    assert.equal(row(withAda).unread, 3, 'three unread from Ada, own reply not counted'); pass();
    assert.equal(row(withBea).unread, 1); pass();
    assert.deepEqual([row(withAda).modeId, row(withBea).modeId], ['dating', 'business'], 'the mode each chat started in'); pass();
    assert.equal(chats[0].id, withBea, 'newest activity first'); pass();
    assert.equal(typeof row(withAda).person.age, 'number', 'published age in Dating'); pass();
    assert.equal(row(withBea).person.age ?? null, null, 'no age in Business (D418)'); pass();
    assert.ok(!JSON.stringify(chats).match(/dateOfBirth|date_of_birth|email|readAt|read_at/i), 'no date of birth, email or read times'); pass();
    assert.equal((await call(tokens.me, '/api/me/notifications')).body.unreadChats, 4, 'the Chats tab total'); pass();

    // Reading clears the reader's count only; the sender's view has no read state at all.
    assert.equal((await call(tokens.me, `/api/conversations/${withAda}/messages`)).status, 200);
    chats = await list('me');
    assert.equal(row(withAda).unread, 0, 'read on open'); pass();
    assert.equal((await call(tokens.me, '/api/me/notifications')).body.unreadChats, 1); pass();
    const adaView = (await list('ada')).find(c => c.id === withAda);
    assert.equal(adaView.unread, 1, "Ada's own count is her unread reply from me"); pass();
    assert.equal((await call(tokens.me, `/api/conversations/${withBea}/read`, { method: 'POST' })).status, 204, 'mark read'); pass();
    assert.equal((await call(tokens.ada, `/api/conversations/${withBea}/read`, { method: 'POST' })).status, 404, 'only a participant can mark a chat read'); pass();
    assert.equal((await call(tokens.me, '/api/me/notifications')).body.unreadChats, 0); pass();
    assert.equal((await send('ada', withAda, 'Fictional new one')).status, 201);
    chats = await list('me');
    assert.equal(chats[0].id, withAda, 'a new message moves the chat to the top'); pass();
    assert.equal(row(withAda).unread, 1); pass();
    assert.equal(await sql(`SELECT count(*) FROM conversation_reads WHERE conversation_id = '${withAda}'`), '1', 'only the reader has a read position'); pass();

    // Web.
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', members.me.email); await page.fill('#password', members.me.password);
    await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
    await page.waitForSelector('[data-unread-badge=chats][data-unread-chats="1"]', { timeout: 45000 }); pass();
    await page.goto(web + '/chats');
    await page.waitForSelector('[data-chats=ready]');
    const first = page.locator('[data-chat]').first();
    assert.equal(await first.getAttribute('data-chat'), withAda, 'newest first'); pass();
    assert.equal(await first.locator('[data-chat-unread-count]').textContent(), '1'); pass();
    assert.equal(await first.locator('[data-chat-mode]').textContent(), 'Dating'); pass();
    assert.match(await first.locator('[data-chat-updated]').textContent(), /^Last updated: /); pass();
    assert.equal(await first.locator('.cx-thumb').count(), 1, 'a thumbnail or initial'); pass();
    assert.equal(await first.locator('a').count(), 1, 'one link per row'); pass();
    assert.match(await first.locator('a').getAttribute('aria-label'), /Dating.*last updated.*1 unread message/); pass();
    // The whole row opens the chat: click near the row's right edge, away from the name.
    const box = await first.locator('a').boundingBox();
    await Promise.all([page.waitForURL(web + `/chats/${withAda}`), page.mouse.click(box.x + box.width - 30, box.y + box.height / 2)]); pass();
    await page.waitForSelector('[data-unread-badge=chats]', { state: 'detached', timeout: 45000 }); pass();
    // A message that arrives while the chat is open counts as read.
    assert.equal((await send('ada', withAda, 'Fictional live one')).status, 201);
    let total = -1;
    for (const until = Date.now() + 30000; Date.now() < until && total !== 0; await new Promise(r => setTimeout(r, 500)))
      total = (await call(tokens.me, '/api/me/notifications')).body.unreadChats;
    assert.equal(total, 0, 'a live message in the open chat is read'); pass();
    await page.goto(web + '/chats');
    await page.waitForSelector('[data-chats=ready]');
    assert.equal(await page.locator('[data-chat-unread-count]').count(), 0, 'nothing unread'); pass();
    // PR 578 review: one chat read elsewhere while another gets a message leaves the total at 1; the rows still follow.
    assert.equal((await send('bea', withBea, 'Fictional business two')).status, 201);
    await page.waitForSelector(`[data-chat="${withBea}"][data-chat-unread="1"]`, { timeout: 45000 }); pass();
    assert.equal((await call(tokens.me, `/api/conversations/${withBea}/messages`)).status, 200);
    assert.equal((await send('ada', withAda, 'Fictional same total')).status, 201);
    await page.waitForSelector(`[data-chat="${withAda}"][data-chat-unread="1"]`, { timeout: 75000 }); pass();
    await page.waitForSelector(`[data-chat="${withBea}"][data-chat-unread="0"]`, { timeout: 5000 }); pass();
    assert.equal(await page.locator('[data-chat]').first().getAttribute('data-chat'), withAda, 'newest activity first'); pass();
    assert.equal((await call(tokens.me, `/api/conversations/${withAda}/messages`)).status, 200);
    await page.waitForSelector('[data-chat-unread-count]', { state: 'detached', timeout: 45000 }); pass();
    // PR 578 review: Bea's photo was in review when I hid the chat and passed after: the chat comes back with it unread, and
    // the thread shows it (the same delivery-time rule for both). Seeded in SQL: a passed attachment row, no object needed.
    assert.equal((await call(tokens.me, `/api/conversations/${withBea}/hide`, { method: 'POST' })).status, 204);
    const beaAccount = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens.bea).sub}'`);
    const lateMedia = require('node:crypto').randomUUID(), latePhoto = require('node:crypto').randomUUID();
    await sql(`INSERT INTO media_items (id, account_id, status, sha256, upload_sha256, content_type, width, height, bytes, created_at, screen_attempts, known_content_checked, purpose, screened_at, conversation_id)
      VALUES ('${lateMedia}', '${beaAccount}', 'Passed', '\\x00', '\\x00', 'image/jpeg', 400, 400, 1000, now() - interval '10 minutes', 1, true, 'attachment', now() + interval '1 second', '${withBea}')`);
    await sql(`INSERT INTO messages (id, conversation_id, sender_account_id, kind, body, sent_at, media_id)
      VALUES ('${latePhoto}', '${withBea}', '${beaAccount}', 'Photo', '', now() - interval '10 minutes', '${lateMedia}')`);
    await sql(`UPDATE conversations SET last_message_at = now() + interval '1 second' WHERE id = '${withBea}'`);
    assert.equal((await list('me')).find(c => c.id === withBea)?.unread, 1, 'the chat is back with the photo unread'); pass();
    assert.ok((await call(tokens.me, `/api/conversations/${withBea}/messages`)).body.items.some(m => m.id === latePhoto), 'and the thread shows it'); pass();
    await sql(`DELETE FROM messages WHERE id = '${latePhoto}'`); await sql(`DELETE FROM media_items WHERE id = '${lateMedia}'`);
    await sql(`DELETE FROM conversation_hides WHERE conversation_id = '${withBea}'`);
    assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at 1280'); pass();
    if (output) await page.screenshot({ path: output + '/chats-1280.png', fullPage: true });
    for (const width of [390, 320]) { await page.setViewportSize({ width, height: 844 }); assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at ' + width); pass(); }
    if (output) { await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: output + '/chats-390.png', fullPage: true }); }

    // Issue 542: the conversation. Twenty unread messages: it opens at the first unread one, with the header and composer on screen.
    await page.setViewportSize({ width: 1280, height: 900 });
    const unreadIds = [];
    for (let i = 1; i <= 20; i++) unreadIds.push((await send('ada', withAda, `Fictional unread ${i}`)).body.id);
    await page.goto(web + `/chats/${withAda}`);
    await page.waitForSelector('[data-conversation=ready] [data-message]');
    await page.waitForTimeout(300);
    const view = async () => page.evaluate(first => {
      const scroller = document.querySelector('[data-chat-scroll]'), box = scroller.getBoundingClientRect();
      const target = document.querySelector(`[data-message="${first}"]`).getBoundingClientRect();
      const composer = document.querySelector('[data-composer]').getBoundingClientRect();
      return { scrolls: scroller.scrollHeight > scroller.clientHeight, firstVisible: target.top >= box.top - 1 && target.top < box.bottom,
        composerOnScreen: composer.bottom <= innerHeight + 1, headOnScreen: document.querySelector('.chat-head').getBoundingClientRect().top >= 0,
        sideways: document.documentElement.scrollWidth > innerWidth };
    }, unreadIds[0]);
    let v = await view();
    assert.ok(v.scrolls, 'the thread scrolls on its own'); pass();
    assert.ok(v.firstVisible, 'opens at the first unread message'); pass();
    assert.ok(v.composerOnScreen && v.headOnScreen, 'header and composer stay on screen'); pass();
    assert.match(await page.getAttribute('[data-chat-profile]', 'href'), new RegExp(`^/people/${pid.ada}\\?mode=dating$`), 'the header opens their profile in the chat\'s mode'); pass();
    assert.equal(await page.textContent('[data-head-mode]'), 'Dating'); pass();
    assert.equal(await page.locator('[data-head-age]').count(), 1, 'published age in the header'); pass();
    // No per-bubble action links; each message's actions open from its own menu (button, right-click), and Escape closes it.
    assert.equal(await page.locator('[data-delete], [data-hide], [data-report-message]').count(), 0, 'no always-visible message actions'); pass();
    const received = `[data-message="${unreadIds[19]}"]`;
    await page.click(`${received} [data-message-menu]`);
    assert.equal(await page.locator(`${received} [data-report-message]`).count(), 1, 'Report a received message'); pass();
    assert.equal(await page.locator(`${received} [data-delete]`).count(), 0, "can't delete their message"); pass();
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('[data-message-menu-open]').count(), 0, 'Escape closes it'); pass();
    await page.click(`${received} p`, { button: 'right' });
    assert.equal(await page.locator(`${received} [data-message-menu-open]`).count(), 1, 'right-click opens it'); pass();
    // Safety stays one tap away: Report in the header; Block and Serious safety incident in the menu.
    assert.equal(await page.locator('.chat-head [data-report-conversation]').count(), 1); pass();
    await page.click('[data-chat-menu]');
    for (const action of ['data-block-conversation', 'data-serious-link', 'data-hide-conversation', 'data-end-open'])
      assert.equal(await page.locator(`[data-chat-menu-open] [${action}]`).count(), 1, action);
    pass();
    await page.click('[data-chat-menu]');
    // Own message: Delete for both from its menu, within five minutes.
    await page.fill('[data-message-input]', 'Fictional mine'); await page.click('[data-send]');
    const mine = page.locator('[data-mine=true]', { hasText: 'Fictional mine' });
    await mine.waitFor();
    await mine.locator('[data-message-menu]').click();
    await mine.locator('[data-message-menu-open]').waitFor();
    assert.equal(await mine.locator('[data-delete]').count(), 1, 'Delete for both on my own message'); pass();
    await page.keyboard.press('Escape');
    v = await view();
    assert.ok(await page.evaluate(() => { const s = document.querySelector('[data-chat-scroll]'); return s.scrollHeight - s.scrollTop - s.clientHeight < 80; }), 'sending follows to the newest'); pass();
    // The + menu: a photo; no blanket nudity line.
    await page.click('[data-attach]');
    assert.equal(await page.locator('[data-attach-menu-open] [data-attach-photo]').count(), 1); pass();
    assert.ok(!(await page.textContent('[data-chat-scroll]') + await page.textContent('.chat-compose')).includes('No nudity'), 'no blanket nudity line'); pass();
    await page.keyboard.press('Escape');
    assert.deepEqual(await page.evaluate(audit), [], 'conversation accessibility floor at 1280'); pass();
    if (output) await page.screenshot({ path: output + '/chat-1280.png' });
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 }); await page.waitForTimeout(200);
      v = await view();
      assert.ok(v.composerOnScreen && !v.sideways, `composer on screen, no sideways scroll at ${width}`); pass();
      assert.deepEqual(await page.evaluate(audit), [], 'conversation accessibility floor at ' + width); pass();
    }
    if (output) { await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: output + '/chat-390.png' }); }
    // PR 579 review: Report (header) and End conversation (menu) add their form below the thread; it comes into view with focus.
    await page.setViewportSize({ width: 1280, height: 900 });
    const revealed = selector => page.evaluate(sel => {
      const form = document.querySelector(sel), box = document.querySelector('[data-chat-scroll]').getBoundingClientRect(), at = form?.getBoundingClientRect();
      return !!at && at.top < box.bottom && at.bottom > box.top && form.contains(document.activeElement);
    }, selector);
    await page.evaluate(() => { const s = document.querySelector('[data-chat-scroll]'); s.scrollTop = 0; });
    await page.click('[data-report-conversation]');
    await page.waitForSelector('[data-chat-report-conversation] [data-report]');
    assert.ok(await page.waitForFunction(() => document.querySelector('[data-chat-report-conversation]')?.contains(document.activeElement)).then(() => revealed('[data-chat-report-conversation]')), 'the report form is shown and focused'); pass();
    await page.evaluate(() => { const s = document.querySelector('[data-chat-scroll]'); s.scrollTop = 0; });
    await page.click('[data-chat-menu]'); await page.click('[data-end-open]');
    await page.waitForSelector('[data-end-form]');
    assert.ok(await page.waitForFunction(() => document.querySelector('[data-end-form]')?.contains(document.activeElement)).then(() => revealed('[data-end-form]')), 'the end form is shown and focused'); pass();

    // PR 579 review: more unread than one page (50): earlier pages load so it still opens at the first unread message.
    const beaId = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens.bea).sub}'`);
    await sql(`UPDATE conversation_reads SET read_at = now() - interval '31 minutes' WHERE conversation_id = '${withBea}'`);
    await sql(`INSERT INTO messages (id, conversation_id, sender_account_id, kind, body, sent_at)
      SELECT gen_random_uuid(), '${withBea}', '${beaId}', 'Text', 'Fictional backlog ' || i, now() - interval '30 minutes' + i * interval '1 second' FROM generate_series(0, 59) AS i`);
    const oldestUnread = await sql(`SELECT id FROM messages WHERE conversation_id = '${withBea}' AND body = 'Fictional backlog 0'`);
    assert.ok((await list('me')).find(c => c.id === withBea).unread > 50, 'more than a page unread'); pass();
    await page.goto(web + `/chats/${withBea}`);
    await page.waitForSelector(`[data-conversation=ready] [data-message="${oldestUnread}"]`); pass();
    await page.waitForTimeout(300);
    assert.ok(await page.evaluate(first => {
      const box = document.querySelector('[data-chat-scroll]').getBoundingClientRect(), at = document.querySelector(`[data-message="${first}"]`).getBoundingClientRect();
      return at.top >= box.top - 1 && at.top < box.bottom;
    }, oldestUnread), 'opens at the first unread message, from an earlier page'); pass();
    await context.close();
    console.log(`PASS: chats web (issues 541, 542), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
