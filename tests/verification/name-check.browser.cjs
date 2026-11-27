// Issue 533 (Q192 A, D429): the registered name against the name on an approved ID. Case, spacing, accents, hyphens and
// apostrophes are ignored; middle names alone, or any other difference, open the member's one-time correction: take the
// name exactly as it is on the ID (never typed), or ask Swoovr to check it. The display name never changes, the ID's name is
// never stored or audited, ordinary name edits wait, and a date-of-birth difference keeps its own prompt (date first).
// Owned stack with the synthetic Didit stand-in; every member and every "ID" is fictional.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { api, web, createMember, memberToken, rootAdminToken, cli, sql, call, claims, localEnv, matchReady } = require('../auth/local-tokens.cjs');

const ADELAIDE = 2078025;
const canonical = v => Array.isArray(v) ? `[${v.map(canonical).join(',')}]`
  : v && typeof v === 'object' ? `{${Object.keys(v).sort().map(k => JSON.stringify(k) + ':' + canonical(v[k])).join(',')}}` : JSON.stringify(v);

(async () => {
  assert.ok(process.env.SWOOVR_OWNED_DIDIT_URL, 'needs the owned runner (synthetic Didit stand-in)');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  const output = process.env.SWOOVR_SCREENSHOTS;
  const secret = await localEnv('SWOOVR_DIDIT_WEBHOOK_SECRET');
  const workflow = await localEnv('SWOOVR_DIDIT_WORKFLOW_ID');
  const hook = async payload => {
    const response = await fetch(api + '/api/webhooks/didit', { method: 'POST', body: JSON.stringify(payload), headers: { 'Content-Type': 'application/json',
      'X-Timestamp': String(Math.floor(Date.now() / 1000)), 'X-Signature-V2': crypto.createHmac('sha256', Buffer.from(secret, 'utf8')).update(canonical(payload)).digest('hex') } });
    return response.status;
  };
  const code = r => [r.status, r.body?.code];
  try {
    const root = await rootAdminToken(browser);
    const names = ['same', 'mid', 'dif', 'both', 'comma'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('namecheck-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, ids = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]); // registered as "Fictional Example"
      assert.equal((await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Public ' + n, homeCityId: ADELAIDE } })).status, 200, n);
      await matchReady(tokens[n]);
      ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
    }
    // Approve each member's check with the "ID" given: the webhook and the stand-in's decision say the same.
    const approve = async (n, first, last, dateOfBirth) => {
      assert.equal((await call(tokens[n], '/api/me/verification', { method: 'POST', body: { client: 'web' } })).status, 200, n);
      const session = await sql(`SELECT session_id FROM id_verification_attempts WHERE account_id = '${ids[n]}' ORDER BY created_at DESC LIMIT 1`);
      const born = dateOfBirth ?? (await sql(`SELECT coalesce(date_of_birth::text, '') FROM member_profiles WHERE account_id = '${ids[n]}'`) || '1990-01-01');
      const check = { node_id: 'feature_ocr', status: 'Approved', age: 30, document_type: 'Passport', date_of_birth: born, first_name: first, ...(last ? { last_name: last } : {}) };
      await fetch(`${process.env.SWOOVR_OWNED_DIDIT_URL}/owned/decision/${session}`, { method: 'PUT',
        body: JSON.stringify({ session_id: session, status: 'Approved', vendor_data: ids[n], workflow_id: workflow, id_verifications: [check] }) });
      assert.equal(await hook({ event_id: crypto.randomUUID(), session_id: session, status: 'Approved', webhook_type: 'status.updated', created_at: Math.floor(Date.now() / 1000),
        timestamp: Math.floor(Date.now() / 1000), workflow_id: workflow, vendor_data: ids[n], decision: { id_verifications: [check] } }), 200, n);
    };
    const me = async n => (await call(tokens[n], '/api/me')).body;

    // Same once case, accents and spacing are ignored: nothing to do.
    await approve('same', ' FICTIONAL ', 'éxample');
    assert.equal((await me('same')).nameCheck, 'none'); pass();
    assert.equal(await sql(`SELECT registered_names_match_id::text FROM accounts WHERE id = '${ids.same}'`), 'true'); pass();
    assert.deepEqual(code(await call(tokens.same, '/api/me/verification/name')), [409, 'name-correction-not-open']); pass();

    // Middle names only: the one-time correction, never accepted automatically. The member sees their ID's name and takes it.
    await approve('mid', 'Fictional Maria', 'Example');
    assert.equal((await me('mid')).nameCheck, 'correctionNeeded'); pass();
    assert.ok((await me('mid')).badges.some(b => b.badge === 'idVerified'), 'ID Verified stays'); pass();
    const regBefore = (await call(tokens.mid, '/api/me/registered-names')).body;
    assert.deepEqual(code(await call(tokens.mid, '/api/me/registered-names', { method: 'PUT', body: { firstName: 'Typed', lastName: 'Name', mononym: false, expectedRevision: regBefore.revision, confirm: true } })),
      [409, 'name-check-open'], 'ordinary edits wait'); pass();
    assert.deepEqual((await call(tokens.mid, '/api/me/verification/name')).body, { firstName: 'Fictional Maria', lastName: 'Example', mononym: false, canUse: true }); pass();
    assert.equal((await call(tokens.mid, '/api/me/verification/name', { method: 'POST', body: { choice: 'typed', firstName: 'Anything' } })).status, 400, 'no free text'); pass();
    let r = await call(tokens.mid, '/api/me/verification/name', { method: 'POST', body: { choice: 'use-id' } });
    assert.deepEqual([r.status, r.body.nameCheck], [200, 'none']); pass();
    const regAfter = (await call(tokens.mid, '/api/me/registered-names')).body;
    assert.deepEqual([regAfter.firstName, regAfter.lastName, regAfter.revision], ['Fictional Maria', 'Example', regBefore.revision + 1], 'took the ID name'); pass();
    assert.equal(await sql(`SELECT display_name FROM member_profiles WHERE account_id = '${ids.mid}'`), 'Fictional Public mid', 'display name untouched'); pass();
    assert.equal(await sql(`SELECT registered_names_match_id::text FROM accounts WHERE id = '${ids.mid}'`), 'true'); pass();
    assert.deepEqual(code(await call(tokens.mid, '/api/me/verification/name', { method: 'POST', body: { choice: 'use-id' } })), [409, 'name-correction-not-open'], 'once'); pass();
    assert.equal(await sql(`SELECT count(*) FROM name_change_reviews WHERE account_id = '${ids.mid}'`), '0', 'taking the ID name opens no review'); pass();

    // A different name: the member asks for a check; an admin decides; the reason says it came from the ID.
    await approve('dif', 'Other', 'Person');
    assert.equal((await me('dif')).nameCheck, 'correctionNeeded'); pass();
    r = await call(tokens.dif, '/api/me/verification/name', { method: 'POST', body: { choice: 'review' } });
    assert.deepEqual([r.status, r.body.nameCheck], [200, 'manualReview']); pass();
    assert.equal((await call(tokens.dif, '/api/me/registered-names')).body.firstName, 'Fictional', 'the name stays until an admin decides'); pass();
    const queue = (await call(root, '/api/admin/name-reviews')).body;
    const difReview = queue.find(x => x.accountId === ids.dif);
    assert.equal(difReview?.reason, 'id-name-differs'); pass();
    assert.equal((await call(tokens.dif, '/api/admin/name-reviews')).status, 403); pass();
    r = await call(root, `/api/admin/name-reviews/${difReview.id}/resolve`, { method: 'POST', body: { outcome: 'matches', reason: 'Fictional: a known alias on the ID.' } });
    assert.equal(r.status, 200); pass();
    assert.equal((await me('dif')).nameCheck, 'none', 'the review closes the check'); pass();

    // PR 581 review: an ID name that isn't a valid registered name can't be taken, but asking Swoovr to check it still works.
    await approve('comma', 'Fictional', 'Example, Jr.');
    assert.equal((await me('comma')).nameCheck, 'correctionNeeded'); pass();
    assert.deepEqual((await call(tokens.comma, '/api/me/verification/name')).body, { firstName: 'Fictional', lastName: 'Example, Jr.', mononym: false, canUse: false }); pass();
    assert.deepEqual(code(await call(tokens.comma, '/api/me/verification/name', { method: 'POST', body: { choice: 'use-id' } })), [409, 'id-name-not-usable']); pass();
    r = await call(tokens.comma, '/api/me/verification/name', { method: 'POST', body: { choice: 'review' } });
    assert.deepEqual([r.status, r.body.nameCheck], [200, 'manualReview']); pass();

    // Never stored or audited: no ID name anywhere in the database outside the sealed identity record.
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE detail ILIKE '%Other%' OR detail ILIKE '%Person%' OR detail ILIKE '%Maria%'`), '0'); pass();
    assert.equal(await sql(`SELECT count(DISTINCT action) FROM audit_entries WHERE action IN ('name.correction-needed', 'name.corrected-to-id', 'name.review-opened') AND target_id IN ('${ids.mid}', '${ids.dif}')`), '3', 'each step audited, without names'); pass();

    // Both differ: the date of birth first, then the name, in the web app.
    await approve('both', 'Fictional Ann', 'Sample', '1985-05-05');
    const both = await me('both');
    assert.deepEqual([both.dobCheck, both.nameCheck], ['correctionNeeded', 'correctionNeeded']); pass();
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', members.both.email); await page.fill('#password', members.both.password);
    await Promise.all([page.waitForURL(url => !url.pathname.startsWith('/account/sign-in')), page.click('#kc-login')]);
    await page.goto(web + '/me/verification');
    await page.waitForSelector('[data-dob-correction=open]', { timeout: 20000 });
    assert.equal(await page.locator('[data-name-correction=open]').count(), 0, 'one dialog at a time'); pass();
    await page.fill('#dob-correction', '1985-05-05');
    await page.click('[data-dob-correction-form] button[type=submit]');
    await page.waitForSelector('[data-name-correction=open] [data-id-name]', { timeout: 20000 }); pass();
    assert.equal(await page.textContent('[data-id-name]'), 'Fictional Ann Sample'); pass();
    const [useWidth, dialogWidth] = await page.evaluate(() => [document.querySelector('[data-name-use-id]').getBoundingClientRect().width, document.querySelector('[data-name-correction] .dialog').getBoundingClientRect().width]);
    assert.ok(useWidth < dialogWidth * 0.6, 'content-width buttons'); pass();
    if (output) await page.screenshot({ path: output + '/name-correction-1280.png' });
    await page.click('[data-name-use-id]');
    await page.waitForSelector('[data-name-correction=open]', { state: 'detached' }); pass();
    const after = await me('both');
    assert.deepEqual([after.dobCheck, after.nameCheck, after.dateOfBirthVerified], ['none', 'none', true]); pass();
    await context.close();
    console.log(`PASS: name check against the ID (issue 533), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
