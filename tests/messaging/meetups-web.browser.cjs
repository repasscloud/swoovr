// Issue 545 (Q193, D430): private 1-on-1 meet-ups planned from a chat. Plan, send, accept, suggest changes, decline,
// withdraw, call off and My meet-ups; each mode's own categories; free-text venue; one invitation waiting per chat; only the
// two members can see or answer; a block calls every open plan off; the audit never holds the place or note; Add to calendar
// is a file made on the device; I'm meeting someone is offered, never started. Owned stack; every person is fictional.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, sql, matchReady } = require('../auth/local-tokens.cjs');
const audit = require('../accessibility/audit.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;
const ADELAIDE = 2078025;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  const code = r => [r.status, r.body?.code];
  try {
    const names = ['me', 'ada', 'eve', 'blk'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('mtw-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional MTW ' + n, homeCityId: ADELAIDE } });
      assert.equal(saved.status, 200, n);
      pid[n] = saved.body.publicId;
      for (const mode of ['dating', 'business']) await call(tokens[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tokens[n]);
    }
    const connect = async (from, to, mode) => {
      const ask = await call(tokens[from], '/api/requests', { method: 'POST', body: { to: pid[to], mode } });
      assert.equal(ask.status, 201, `${from}→${to}`);
      assert.equal((await call(tokens[to], `/api/requests/${ask.body.id}/accept`, { method: 'POST' })).status, 200);
      return (await call(tokens[from], '/api/conversations', { method: 'POST', body: { to: pid[to], mode } })).body.id;
    };
    const chat = await connect('ada', 'me', 'dating');
    const blkChat = await connect('blk', 'me', 'business');
    const soon = (days, hour = 19) => { const d = new Date(); d.setDate(d.getDate() + days); d.setHours(hour, 0, 0, 0); return d.toISOString(); };
    const propose = (who, conversation, body) => call(tokens[who], `/api/conversations/${conversation}/meetups`, { method: 'POST', body });
    const answer = (who, id, action) => call(tokens[who], `/api/meetups/${id}/${action}`, { method: 'POST' });
    const card = async (who, id) => (await call(tokens[who], `/api/conversations/${chat}/messages`)).body.items.find(m => m.meetup?.id === id)?.meetup;

    // Each mode has its own categories.
    const dating = (await call(tokens.me, '/api/meetups/categories?mode=dating')).body.map(c => c.id);
    const business = (await call(tokens.me, '/api/meetups/categories?mode=business')).body.map(c => c.id);
    assert.ok(dating.includes('dinner') && !dating.includes('networking') && business.includes('networking'), 'categories per mode'); pass();
    // Refused proposals.
    const plan = { category: 'dinner', venue: 'Fictional Bistro, Adelaide CBD', startsAt: soon(3), durationMinutes: 120, note: 'Fictional: looking forward to it!' };
    assert.deepEqual(code(await propose('me', chat, { ...plan, category: 'networking' })), [400, 'invalid-meetup'], 'a category from another mode'); pass();
    assert.deepEqual(code(await propose('me', chat, { ...plan, startsAt: new Date(Date.now() - 3600e3).toISOString() })), [400, 'invalid-meetup'], 'in the past'); pass();
    assert.deepEqual(code(await propose('me', chat, { ...plan, startsAt: soon(120) })), [400, 'invalid-meetup'], 'more than 90 days ahead'); pass();
    assert.deepEqual(code(await propose('me', chat, { ...plan, venue: '   ' })), [400, 'invalid-meetup'], 'no place'); pass();
    assert.deepEqual(code(await propose('me', chat, { ...plan, durationMinutes: 7 })), [400, 'invalid-meetup'], 'odd length'); pass();
    assert.equal((await propose('eve', chat, plan)).status, 404, 'not in the chat'); pass();

    // Send: the card in the chat, pending; one invitation waits at a time.
    let r = await propose('me', chat, plan);
    assert.equal(r.status, 201); pass();
    const first = r.body.meetup;
    assert.deepEqual([r.body.kind, first.state, first.mine, first.canWithdraw, first.canAccept, first.categoryLabel], ['meetup', 'pending', true, true, false, 'Dinner']); pass();
    assert.deepEqual(code(await propose('me', chat, plan)), [409, 'meetup-pending']); pass();
    assert.deepEqual(code(await propose('ada', chat, plan)), [409, 'meetup-pending'], 'either side waits for the answer'); pass();
    assert.equal((await call(tokens.ada, '/api/me/notifications')).body.unreadChats, 1, 'counts as a delivered message (before Ada opens the chat)'); pass();
    let theirs = await card('ada', first.id);
    assert.deepEqual([theirs.mine, theirs.canAccept, theirs.canDecline, theirs.canSuggest, theirs.canWithdraw, theirs.note], [false, true, true, true, false, plan.note]); pass();
    assert.equal((await answer('eve', first.id, 'accept')).status, 404, 'only the two members'); pass();
    assert.deepEqual(code(await answer('me', first.id, 'accept')), [409, 'meetup-not-open'], "the proposer can't accept their own"); pass();

    // Suggest changes: the first is superseded and a new one goes the other way; then accept and call off.
    r = await call(tokens.ada, `/api/meetups/${first.id}/suggest`, { method: 'POST', body: { ...plan, category: 'drinks', startsAt: soon(4, 18), note: null } });
    assert.equal(r.status, 201); pass();
    const second = r.body.meetup;
    assert.equal((await card('me', first.id)).state, 'superseded'); pass();
    assert.deepEqual([(await card('me', second.id)).canAccept, (await card('me', second.id)).categoryLabel], [true, 'Drinks']); pass();
    assert.deepEqual(code(await call(tokens.ada, `/api/meetups/${first.id}/suggest`, { method: 'POST', body: plan })), [409, 'meetup-not-open'], 'a superseded plan stays as it was'); pass();
    r = await answer('me', second.id, 'accept');
    assert.deepEqual([r.status, r.body.meetup.state, r.body.meetup.canCancel], [200, 'accepted', true]); pass();
    const mine = (await call(tokens.me, '/api/me/meetups')).body;
    assert.deepEqual(mine.filter(m => m.state === 'accepted').map(m => [m.id, m.withName]), [[second.id, 'Fictional MTW ada']]); pass();
    assert.ok(!mine.some(m => m.state === 'superseded'), 'My meet-ups leaves superseded plans out'); pass();
    assert.equal((await call(tokens.eve, '/api/me/meetups')).body.length, 0, "never someone else's"); pass();
    assert.equal((await answer('ada', second.id, 'cancel')).body.meetup.state, 'cancelled'); pass();

    // Withdraw and decline.
    const third = (await propose('me', chat, { ...plan, startsAt: soon(6) })).body.meetup;
    assert.equal((await answer('me', third.id, 'withdraw')).body.meetup.state, 'withdrawn'); pass();
    r = await propose('ada', chat, { ...plan, category: 'coffee', venue: 'Fictional Reported Cafe', startsAt: soon(7, 10), note: null });
    const fourth = r.body.meetup, fourthMessage = r.body.id;
    assert.equal((await answer('me', fourth.id, 'decline')).body.meetup.state, 'declined'); pass();
    // PR 583 review: a reported card keeps its plan in the case, even with no note.
    const reported = await call(tokens.me, '/api/reports', { method: 'POST', body: { subjectType: 'message', subjectId: fourthMessage, category: 'harassment', text: null, block: false } });
    assert.equal(reported.status, 201);
    const evidence = JSON.parse(await sql(`SELECT snapshot FROM case_evidence WHERE report_id = '${reported.body.id}'`));
    const kept = evidence.messages.find(m => m.id === fourthMessage);
    assert.deepEqual([kept.kind, kept.meetup?.venue, kept.meetup?.category, kept.meetup?.status], ['meetup', 'Fictional Reported Cafe', 'coffee', 'Declined'], JSON.stringify(kept)); pass();
    // PR 583 review: invitations count toward the other member's unanswered limit, and one sent back is a reply.
    const limit = n => call(tokens.ada, '/api/me/notification-settings', { method: 'PUT', body: { unansweredLimit: n } });
    const text = () => call(tokens.me, `/api/conversations/${chat}/messages`, { method: 'POST', body: { text: 'Fictional line' } });
    assert.equal((await limit(3)).status, 200);
    for (let i = 0; i < 3; i++) assert.equal((await answer('me', (await propose('me', chat, { ...plan, startsAt: soon(10 + i) })).body.meetup.id, 'withdraw')).status, 200);
    assert.deepEqual(code(await text()), [409, 'unanswered-limit'], 'three invitations in a row reach the limit'); pass();
    assert.deepEqual(code(await propose('me', chat, { ...plan, startsAt: soon(13) })), [409, 'unanswered-limit'], 'and so does a fourth invitation'); pass();
    const back = (await propose('ada', chat, { ...plan, startsAt: soon(14) })).body.meetup;
    assert.equal((await text()).status, 201, 'their invitation is a reply'); pass();
    assert.equal((await answer('ada', back.id, 'withdraw')).status, 200);
    assert.equal((await limit(0)).status, 200);
    // PR 583 review: a note removed by staff leaves the card and its controls, so the plan can still be answered.
    r = await propose('me', chat, { ...plan, startsAt: soon(15) });
    await sql(`UPDATE messages SET body = '', moderation_removed_at = now() WHERE id = '${r.body.id}'`);
    const moderated = await card('ada', r.body.meetup.id);
    assert.deepEqual([moderated?.note, moderated?.canAccept, moderated?.canDecline, moderated?.venue], ['Removed by Swoovr.', true, true, plan.venue], 'card kept: ' + JSON.stringify(moderated ?? null)); pass();
    assert.equal((await call(tokens.ada, `/api/conversations/${chat}/messages`)).body.items.find(m => m.id === r.body.id)?.kind, 'meetup'); pass();
    assert.equal((await answer('ada', r.body.meetup.id, 'decline')).body.meetup.state, 'declined', 'answered, so the chat is free again'); pass();

    // A block calls every open plan off and nobody can answer any more.
    const blkPlan = (await propose('blk', blkChat, { category: 'networking', venue: 'Fictional Hub', startsAt: soon(5, 9) })).body.meetup;
    assert.equal((await answer('me', blkPlan.id, 'accept')).body.meetup.state, 'accepted'); pass();
    assert.equal((await call(tokens.me, '/api/blocks', { method: 'POST', body: { profileId: pid.blk } })).status, 204);
    assert.equal(await sql(`SELECT status FROM meetups WHERE id = '${blkPlan.id}'`), 'Cancelled', 'called off by the block'); pass();
    assert.equal((await answer('blk', blkPlan.id, 'cancel')).status, 404); pass();
    assert.ok(!(await call(tokens.me, '/api/me/meetups')).body.some(m => m.id === blkPlan.id), 'gone from My meet-ups'); pass();
    // The audit names the action, never the place, time or note.
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'meetup.%' AND (detail ILIKE '%Bistro%' OR detail ILIKE '%Fictional%')`), '0'); pass();
    assert.ok(Number(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'meetup.%'`)) >= 8, 'each step audited'); pass();

    // PR 583 review: a long history never crowds current plans out of My meet-ups (210 fictional old plans).
    await sql(`WITH src AS (SELECT conversation_id, mode_id, proposer_account_id p, recipient_account_id r FROM meetups WHERE id = '${second.id}'),
      ids AS (SELECT gen_random_uuid() mid, gen_random_uuid() eid, g FROM generate_series(1, 210) g),
      msg AS (INSERT INTO messages (id, conversation_id, sender_account_id, kind, body, sent_at, meetup_id)
        SELECT mid, conversation_id, p, 'Meetup', '', now() - interval '400 days' - g * interval '1 hour', eid FROM ids, src RETURNING id)
      INSERT INTO meetups (id, conversation_id, message_id, mode_id, proposer_account_id, recipient_account_id, category, venue, starts_at, status, created_at)
      SELECT eid, conversation_id, mid, mode_id, p, r, 'coffee', 'Fictional Old Cafe', now() - interval '300 days' - g * interval '1 hour', 'Cancelled', now() - interval '400 days' FROM ids, src`);
    const crowded = (await call(tokens.me, '/api/me/meetups')).body;
    assert.equal(crowded.filter(m => !['pending', 'accepted'].includes(m.state)).length, 100, 'history is bounded to the latest 100'); pass();
    assert.ok(crowded.some(m => m.id === fourth.id), 'the latest closed plans stay in the history'); pass();
    // The web plan below, sent after these, must still show under Upcoming.

    // Web: plan from the + menu; the other member accepts; the card follows live; calendar and I'm meeting someone.
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const page = await context.newPage();
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', members.me.email); await page.fill('#password', members.me.password);
    await Promise.all([page.waitForURL(url => !url.pathname.startsWith('/account/sign-in')), page.click('#kc-login')]);
    await page.goto(web + `/chats/${chat}`);
    await page.waitForSelector('[data-conversation=ready]');
    await page.click('[data-attach]');
    await page.click('[data-attach-meetup]');
    await page.waitForSelector('[data-meetup-form]');
    assert.deepEqual(await page.locator('[data-meetup-category]').evaluateAll(a => a.map(x => x.value)), dating, "the chat mode's categories"); pass();
    await page.click('label:has([data-meetup-category=coffee])');
    await page.fill('[data-meetup-venue-input]', 'Fictional Cafe, Norwood');
    const day = new Date(); day.setDate(day.getDate() + 2);
    await page.fill('[data-meetup-date]', day.toISOString().slice(0, 10));
    await page.fill('[data-meetup-time]', '10:30');
    await page.selectOption('[data-meetup-duration]', '60');
    const [send, form] = await page.evaluate(() => [document.querySelector('[data-meetup-send]').getBoundingClientRect().width, document.querySelector('[data-meetup-form]').getBoundingClientRect().width]);
    assert.ok(send < form * 0.6, 'content-width Send'); pass();
    assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor with the form open'); pass();
    if (output) await page.screenshot({ path: output + '/meetup-form-1280.png' });
    await page.click('[data-meetup-send]');
    const sent = page.locator('[data-meetup][data-meetup-state=pending]').last();
    await sent.waitFor();
    assert.equal(await sent.locator('[data-meetup-status]').innerText(), 'Waiting for a reply'); pass();
    assert.match(await sent.locator('[data-meetup-when]').innerText(), /10:30 am · 1 hour$/); pass();
    const webId = await sent.getAttribute('data-meetup');
    assert.equal((await answer('ada', webId, 'accept')).status, 200);
    const accepted = page.locator(`[data-meetup="${webId}"][data-meetup-state=accepted]`);
    await accepted.waitFor({ timeout: 15000 }); pass(); // live, without a reload
    assert.equal(await accepted.locator('[data-meetup-safety]').getAttribute('href'), '/me/meeting-safety', 'offered, never started'); pass();
    assert.equal(await sql(`SELECT count(*) FROM meeting_safety_sessions s JOIN accounts a ON a.id = s.member_id WHERE a.email = '${members.me.email}'`), '0', 'no meeting safety started'); pass();
    const [download] = await Promise.all([page.waitForEvent('download'), accepted.locator('[data-meetup-calendar]').click()]);
    const ics = require('node:fs').readFileSync(await download.path(), 'utf8');
    assert.ok(download.suggestedFilename().endsWith('.ics') && ics.includes('BEGIN:VEVENT') && ics.includes('SUMMARY:Coffee with Fictional MTW ada') && ics.includes('LOCATION:Fictional Cafe\\, Norwood'), ics); pass();
    assert.deepEqual(await page.evaluate(audit), [], 'accessibility floor with the cards'); pass();
    if (output) await page.screenshot({ path: output + '/meetup-card-1280.png' });
    await page.goto(web + '/me/meetups');
    await page.waitForSelector('[data-meetups=ready]');
    assert.equal(await page.locator(`[data-meetups-section=upcoming] [data-meetups-row="${webId}"]`).count(), 1); pass();
    for (const width of [390, 320]) { await page.setViewportSize({ width, height: 844 }); assert.deepEqual(await page.evaluate(audit), [], 'My meet-ups at ' + width); pass(); }
    if (output) await page.screenshot({ path: output + '/meetups-390.png', fullPage: true });
    // PR 583 review: a state change never brings back a card hidden for me.
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(web + `/chats/${chat}`);
    await page.waitForSelector('[data-conversation=ready]');
    const hiddenId = await page.locator('li[data-message-kind=meetup]', { has: page.locator(`[data-meetup="${webId}"]`) }).getAttribute('data-message');
    await page.click(`[data-message-menu="${hiddenId}"]`);
    await page.click(`[data-hide="${hiddenId}"]`);
    await page.locator(`li[data-message="${hiddenId}"]`).waitFor({ state: 'detached' });
    assert.equal((await answer('ada', webId, 'cancel')).status, 200);
    await page.waitForTimeout(4000);
    assert.equal(await page.locator(`li[data-message="${hiddenId}"]`).count(), 0, 'stays hidden after Ada calls it off'); pass();
    await context.close();
    console.log(`PASS: private meet-ups (issue 545), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
