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
    assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at 1280'); pass();
    if (output) await page.screenshot({ path: output + '/chats-1280.png', fullPage: true });
    for (const width of [390, 320]) { await page.setViewportSize({ width, height: 844 }); assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor at ' + width); pass(); }
    if (output) { await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: output + '/chats-390.png', fullPage: true }); }
    await context.close();
    console.log(`PASS: chats web (issue 541), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
