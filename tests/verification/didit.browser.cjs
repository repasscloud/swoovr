// Local acceptance for slice P (D126–D130, D147, D152): ID verification through Didit's hosted flow in test mode, the
// webhook on the API, the return re-read, admin outcomes, the age-assurance gate and the founder allocation job.
// Real Didit test-mode session creation happens at most twice a run (members "ana" and "web"); no verification is
// completed at Didit (that needs a person with an ID). Every other state change arrives as a webhook this test signs
// with the destination secret from .local/docker.env, exactly as Didit signs them; forged, stale, replayed,
// mis-addressed and oversized deliveries are refused. Needs the local stack, the enrolled fictional root admin, hosts
// built from this checkout with the Didit settings (see docs/delivery/LOCAL-DEVELOPMENT.md) and the GeoNames import.
// Every member is a fictional adult created for this run.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { api, web, admin, sleep, createMember, createStaff, memberToken, staffToken, fillStaffOtp, rootAdminToken, cli, sql, call, claims, localEnv, membersRealm, matchReady } = require('../auth/local-tokens.cjs');

const ADELAIDE = 2078025;
// Python's json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False) for the integer/string/null/object
// payloads below, which is what Didit's X-Signature-V2 covers.
const canonical = v => Array.isArray(v) ? `[${v.map(canonical).join(',')}]`
  : v && typeof v === 'object' ? `{${Object.keys(v).sort().map(k => JSON.stringify(k) + ':' + canonical(v[k])).join(',')}}` : JSON.stringify(v);

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  const output = process.env.SWOOVR_SCREENSHOTS;
  const secret = await localEnv('SWOOVR_DIDIT_WEBHOOK_SECRET');
  const workflow = await localEnv('SWOOVR_DIDIT_WORKFLOW_ID');
  assert.ok(secret && workflow, 'Didit settings missing from .local/docker.env');
  // The founder job (an operator command) issues tokens and mails them to the local mail sink.
  Object.assign(process.env, { Founders__Secrets__1: await localEnv('SWOOVR_FOUNDER_SECRET_V1'), Email__Smtp__Host: '127.0.0.1', Email__Smtp__Port: '1025' });
  const hook = async (payload, { sign = 'v2', headers = {}, raw } = {}) => {
    const body = raw ?? JSON.stringify(payload);
    const key = Buffer.from(secret, 'utf8');
    const signature = sign === 'v2' ? { 'X-Signature-V2': crypto.createHmac('sha256', key).update(canonical(payload)).digest('hex') }
      : sign === 'raw' ? { 'X-Signature': crypto.createHmac('sha256', key).update(body).digest('hex') }
      : sign === 'forged' ? { 'X-Signature-V2': crypto.createHmac('sha256', 'not-the-secret').update(canonical(payload)).digest('hex') } : {};
    const response = await fetch(api + '/api/webhooks/didit', { method: 'POST', body,
      headers: { 'Content-Type': 'application/json', 'X-Timestamp': String(Math.floor(Date.now() / 1000)), ...signature, ...headers } });
    const text = await response.text();
    return { status: response.status, body: text ? JSON.parse(text) : null };
  };
  const event = (session, status, vendor, extra = {}) => ({ event_id: crypto.randomUUID(), session_id: session, status, webhook_type: 'status.updated',
    created_at: Math.floor(Date.now() / 1000), timestamp: Math.floor(Date.now() / 1000), workflow_id: workflow, vendor_data: vendor, ...extra });
  const approved = (age, dateOfBirth) => ({ decision: { id_verifications: [{ node_id: 'feature_ocr', status: 'Approved', age, document_type: 'Passport', ...(dateOfBirth ? { date_of_birth: dateOfBirth } : {}) }] } });
  try {
    const root = await rootAdminToken(browser);
    const names = ['ana', 'bel', 'cat', 'dee', 'eve', 'nev', 'web', 'bot'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('verify-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tokens = {}, ids = {}, pid = {};
    for (const n of names) {
      tokens[n] = await memberToken(browser, members[n]);
      const saved = await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional ' + n[0].toUpperCase() + n.slice(1), homeCityId: ADELAIDE } });
      assert.equal(saved.status, 200, n);
      await matchReady(tokens[n]);
      await call(tokens[n], '/api/me/modes/dating', { method: 'PUT', body: { enabled: true, visible: true } });
      pid[n] = saved.body.publicId;
      ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
    }
    const code = r => [r.status, r.body?.code];
    const view = n => call(tokens[n], '/api/me/verification');
    const start = (n, client = 'web') => call(tokens[n], '/api/me/verification', { method: 'POST', body: { client } });
    const fakeAttempt = async (n, state = 'Created') => { // a session row as if Didit had created it (no provider call)
      const session = crypto.randomUUID();
      await sql(`INSERT INTO id_verification_attempts (id, account_id, session_id, workflow_id, state, provider_status, started, self_service, created_at, updated_at, registered_names_revision)
        VALUES ('${crypto.randomUUID()}', '${ids[n]}', '${session}', '${workflow}', '${state}', 'Not Started', false, true, now(), now(), (SELECT registered_names_revision FROM accounts WHERE id='${ids[n]}'))`);
      await sql(`UPDATE member_profiles SET can_request_id_verification = false WHERE account_id = '${ids[n]}'`);
      return session;
    };

    // Before any check: optional, free, labelled test mode (D147), nothing shown publicly.
    let r = await view('ana');
    assert.equal(r.status, 200); pass();
    assert.deepEqual([r.body.idStatus, r.body.attemptState, r.body.canStart, r.body.available, r.body.testMode, r.body.url], ['notStarted', null, true, true, true, null]); pass();
    assert.deepEqual(code(await start('ana', 'evil')), [400, 'invalid-request']); pass(); // the return address is never the client's
    assert.deepEqual(code(await call(tokens.ana, '/api/me/verification', { method: 'POST', body: { client: 'web', callback: 'https://evil.example/' } })).slice(0, 1), [200]);
    pass(); // an injected callback is ignored (the configured web return address is used)

    // Real Didit test-mode session for ana (created by the call above): the hosted link, the session stored against the account.
    r = await view('ana');
    assert.equal(r.body.attemptState, 'created'); assert.match(r.body.url, /^https:\/\/[a-z.]*didit\.me\//); pass();
    const anaSession = await sql(`SELECT session_id FROM id_verification_attempts WHERE account_id = '${ids.ana}'`);
    assert.match(anaSession, /^[0-9a-f-]{36}$/); pass();
    assert.equal(await sql(`SELECT can_request_id_verification FROM member_profiles WHERE account_id = '${ids.ana}'`), 'f'); pass();
    assert.equal((await start('ana')).body.url, r.body.url); pass(); // starting again shows the open session, no second one
    assert.equal(await sql(`SELECT count(*) FROM id_verification_attempts WHERE account_id = '${ids.ana}'`), '1'); pass();
    // The return re-read asks Didit: the session hasn't been started, so nothing changes.
    assert.equal((await view('ana')).body.attemptState, 'created'); pass();
    // Did Didit's own webhook reach this API through the quick tunnel? Recorded, never asserted (depends on the tunnel).
    await sleep(8000);
    const liveEvents = await sql(`SELECT count(*) FROM didit_webhook_events WHERE session_id = '${anaSession}'`);
    console.log(`INFO: live Didit webhook deliveries for the new session so far: ${liveEvents}`);

    // Webhook authentication (D152): signature first, then the signed timestamp, then the event id.
    const stateOf = n => sql(`SELECT state FROM id_verification_attempts WHERE account_id = '${ids[n]}' ORDER BY created_at DESC LIMIT 1`);
    r = await hook(event(anaSession, 'Approved', ids.ana, approved(30)), { sign: 'forged' });
    assert.equal(r.status, 401); assert.equal(await stateOf('ana'), 'Created'); pass();
    assert.equal((await hook(event(anaSession, 'Approved', ids.ana, approved(30)), { sign: 'none' })).status, 401); pass();
    const simple = event(anaSession, 'Approved', ids.ana);
    const simpleSig = crypto.createHmac('sha256', secret).update(`${simple.timestamp}:${simple.session_id}:${simple.status}:${simple.webhook_type}`).digest('hex');
    assert.equal((await hook(simple, { sign: 'none', headers: { 'X-Signature-Simple': simpleSig } })).status, 401); pass(); // the simple signature doesn't cover vendor_data
    const stale = event(anaSession, 'Approved', ids.ana, { timestamp: Math.floor(Date.now() / 1000) - 600, ...approved(30) });
    assert.equal((await hook(stale)).status, 400); assert.equal(await stateOf('ana'), 'Created'); pass(); // a fresh X-Timestamp header doesn't help
    const signedThenTampered = event(anaSession, 'Approved', ids.ana, approved(30));
    const goodSig = crypto.createHmac('sha256', secret).update(canonical(signedThenTampered)).digest('hex');
    assert.equal((await hook(null, { sign: 'none', raw: JSON.stringify({ ...signedThenTampered, vendor_data: ids.bel }), headers: { 'X-Signature-V2': goodSig } })).status, 401); pass();
    assert.equal((await hook(null, { sign: 'none', raw: '{"a":"' + 'x'.repeat(1024 * 1024) + '"}', headers: { 'X-Signature-V2': 'ab' } })).status, 413); pass();
    r = await hook(event(anaSession, 'Approved', ids.bel, approved(30))); // signed, but for another member's account
    assert.deepEqual([r.status, r.body.outcome], [200, 'vendor-mismatch']); assert.equal(await stateOf('ana'), 'Created'); pass();
    r = await hook(event(anaSession, 'Approved', ids.ana, { ...approved(30), workflow_id: crypto.randomUUID() }));
    assert.deepEqual([r.status, r.body.outcome], [200, 'workflow-mismatch']); pass();
    r = await hook(event(crypto.randomUUID(), 'Approved', ids.ana, approved(30)));
    assert.deepEqual([r.status, r.body.outcome], [200, 'unknown-session']); pass();
    r = await hook({ ...event(anaSession, 'ACTIVE', ids.ana), webhook_type: 'user.status.updated' });
    assert.deepEqual([r.status, r.body.outcome], [200, 'ignored']); pass();
    assert.equal((await hook({ ...event(anaSession, 'Approved', ids.ana), event_id: 'not-a-uuid' })).status, 400); pass();

    // Accepted deliveries: In Progress, then Approved (adult) by raw-body signature; ID Verified shows; replays change nothing.
    r = await hook(event(anaSession, 'In Progress', ids.ana));
    assert.deepEqual([r.status, r.body.outcome], [200, 'applied']); assert.equal(await stateOf('ana'), 'InProgress'); pass();
    assert.deepEqual([(await view('ana')).body.idStatus, (await view('ana')).body.attemptState], ['pending', 'inProgress']); pass();
    const approval = event(anaSession, 'Approved', ids.ana, approved(30, '1985-05-05')); // a different date from ana's profile (1990-01-01)
    assert.equal((await hook(approval, { sign: 'raw' })).body.outcome, 'applied'); pass();
    r = await view('ana');
    assert.deepEqual([r.body.idStatus, r.body.attemptState, r.body.url, r.body.canStart, r.body.refusal], ['verified', 'approved', null, false, 'alreadyVerified']); pass();
    assert.equal((await call(tokens.ana, '/api/me')).body.ageStatus, 'verified'); pass();
    // Q53 (D158) and Q58 (D166): the document's date differs, so the date of birth isn't verified and the member gets one
    // correction; ID Verified stays.
    let anaMe = (await call(tokens.ana, '/api/me')).body;
    assert.deepEqual([anaMe.dateOfBirthVerified, anaMe.dobCheck], [false, 'correctionNeeded']); pass();
    assert.equal(await sql(`SELECT date_of_birth_verified::text FROM member_profiles WHERE account_id = '${ids.ana}'`), 'false'); pass();
    assert.deepEqual(code(await call(tokens.ana, '/api/me/age', { method: 'PUT', body: { dateOfBirth: '1985-05-05' } })), [409, 'dob-correction-required']); pass();
    assert.deepEqual(code(await call(tokens.bel, '/api/me/verification/date-of-birth', { method: 'POST', body: { dateOfBirth: '1985-05-05' } })), [409, 'dob-correction-not-open']); pass(); // nothing open for bel
    for (const bad of [{}, { dateOfBirth: '2999-01-01' }, { dateOfBirth: 'nope' }])
    { assert.equal((await call(tokens.ana, '/api/me/verification/date-of-birth', { method: 'POST', body: bad })).status, 400, JSON.stringify(bad)); pass(); }
    assert.equal(await sql(`SELECT dob_check FROM member_profiles WHERE account_id = '${ids.ana}'`), 'CorrectionNeeded'); pass(); // refused tries don't use it
    // The correction: the API reads Didit's decision again. This real test-mode session was never completed, so Didit has no
    // date to compare and the date goes to an admin's review (a matching date is verified; see the checks and the owner's run).
    r = await call(tokens.ana, '/api/me/verification/date-of-birth', { method: 'POST', body: { dateOfBirth: '1985-05-05', dateOfBirthVerified: true, dobCheck: 'none' } });
    assert.deepEqual([r.status, r.body.dateOfBirth, r.body.dateOfBirthVerified, r.body.dobCheck], [200, '1985-05-05', false, 'manualReview']); pass();
    assert.ok((await call(tokens.ana, '/api/me')).body.badges.some(b => b.badge === 'idVerified')); pass(); // ID Verified kept
    assert.deepEqual(code(await call(tokens.ana, '/api/me/verification/date-of-birth', { method: 'POST', body: { dateOfBirth: '1985-05-05' } })), [409, 'dob-correction-not-open']); pass(); // once
    assert.deepEqual(code(await call(tokens.ana, '/api/me/age', { method: 'PUT', body: { dateOfBirth: '1990-01-01' } })), [409, 'dob-under-review']); pass();
    // The admin queue: admins only, ids and reasons, never a date.
    let reviews = await call(root, '/api/admin/dob-reviews');
    const anaReview = reviews.body.find(x => x.accountId === ids.ana);
    assert.deepEqual([reviews.status, anaReview?.reason, anaReview?.resolvedAt], [200, 'still-different', null]); pass();
    assert.ok(!/\d{4}-\d{2}-\d{2}"(?!T)/.test(JSON.stringify(anaReview)) && !JSON.stringify(anaReview).includes('1985')); pass();
    assert.equal((await call(tokens.ana, '/api/admin/dob-reviews')).status, 403); pass();
    assert.equal((await call(root, `/api/admin/accounts/${ids.ana}/summary`)).body.dobCheck, 'manualReview'); pass();
    for (const body of [{ outcome: 'verified' }, { outcome: 'maybe', reason: 'x' }, { outcome: '0', reason: 'x' }])
    { assert.equal((await call(root, `/api/admin/dob-reviews/${anaReview.id}/resolve`, { method: 'POST', body })).status, 400, JSON.stringify(body)); pass(); }
    assert.equal((await call(root, `/api/admin/dob-reviews/${crypto.randomUUID()}/resolve`, { method: 'POST', body: { outcome: 'verified', reason: 'x' } })).status, 404); pass();
    r = await call(root, `/api/admin/dob-reviews/${anaReview.id}/resolve`, { method: 'POST', body: { outcome: 'verified', reason: 'Fictional: checked against the document in Didit.' } });
    assert.deepEqual([r.status, r.body.outcome], [200, 'verified']); pass();
    anaMe = (await call(tokens.ana, '/api/me')).body;
    assert.deepEqual([anaMe.dateOfBirthVerified, anaMe.dobCheck], [true, 'none']); pass();
    assert.equal((await call(root, `/api/admin/dob-reviews/${anaReview.id}/resolve`, { method: 'POST', body: { outcome: 'notVerified', reason: 'x' } })).status, 409); pass();
    assert.ok(!(await call(root, '/api/admin/dob-reviews')).body.some(x => x.id === anaReview.id)); pass(); // the open queue
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'dob.%' AND detail ~ '[0-9]{4}-[0-9]{2}-[0-9]{2}'`), '0'); pass();
    assert.equal(await sql(`SELECT count(*) FROM information_schema.columns WHERE table_name = 'dob_reviews' AND data_type = 'date'`), '0'); pass();
    assert.ok((await call(tokens.ana, '/api/me')).body.badges.some(b => b.badge === 'idVerified')); pass();
    assert.ok((await call(tokens.bel, `/api/profiles/${pid.ana}`)).body.badges.some(b => b.badge === 'idVerified')); pass(); // others see the badge
    assert.deepEqual((await hook(approval)).body, { duplicate: true }); pass(); // the same event id again
    r = await hook(event(anaSession, 'Declined', ids.ana)); // a later, contradictory status for a settled attempt
    assert.equal(r.status, 200); assert.equal((await view('ana')).body.idStatus, 'verified'); pass();
    assert.equal((await view('ana')).body.attemptState, 'approved'); pass(); // and the return re-read (Didit says Not Started) can't move it back
    assert.deepEqual(code(await start('ana')), [409, 'already-verified']); pass();
    // Only the outcome is stored: no document type, age or date of birth anywhere in the attempt or the audit.
    assert.equal(await sql(`SELECT adult::text || '/' || provider_status FROM id_verification_attempts WHERE account_id = '${ids.ana}'`), 'true/Approved'); pass();
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE (action LIKE 'verification.%' OR action LIKE 'age.%') AND (detail LIKE '%Passport%' OR detail LIKE '%30%' OR detail LIKE '%${anaSession}%' OR target_id = '${anaSession}')`), '0'); pass();
    assert.equal(await sql(`SELECT count(*) FROM information_schema.columns WHERE table_name IN ('id_verification_attempts', 'didit_webhook_events') AND column_name <> 'registered_names_revision' AND column_name ~ '(document|birth|dob|selfie|image|face|name|body)'`), '0'); pass();
    assert.notEqual(await sql(`SELECT founder_eligible_at FROM member_profiles WHERE account_id = '${ids.ana}'`), ''); pass(); // D130 eligibility time recorded

    // One self-service attempt (D129): a declined check uses it; an admin may allow another.
    const belSession = await fakeAttempt('bel');
    await hook(event(belSession, 'In Progress', ids.bel));
    assert.equal((await hook(event(belSession, 'Declined', ids.bel))).body.outcome, 'applied'); pass();
    r = await view('bel');
    assert.deepEqual([r.body.idStatus, r.body.attemptState, r.body.canStart, r.body.refusal], ['rejected', 'declined', false, 'attemptUsed']); pass();
    assert.deepEqual(code(await start('bel')), [409, 'verification-attempt-used']); pass();
    assert.ok(!(await call(tokens.ana, `/api/profiles/${pid.bel}`)).body.badges.some(b => b.badge === 'idVerified')); pass();
    // An expired session that was never started isn't an attempt: the member gets it back (D129, D155).
    const catSession = await fakeAttempt('cat');
    await hook(event(catSession, 'Expired', ids.cat));
    r = await view('cat');
    assert.deepEqual([r.body.attemptState, r.body.canStart], ['expired', true]); pass();
    // ...but an abandoned real attempt (started, then stopped) uses it.
    const deeSession = await fakeAttempt('dee');
    await hook(event(deeSession, 'In Progress', ids.dee)); await hook(event(deeSession, 'Abandoned', ids.dee));
    assert.deepEqual([(await view('dee')).body.attemptState, (await view('dee')).body.canStart], ['abandoned', false]); pass();

    // An approved document showing someone under 18 locks the account (D127).
    const eveSession = await fakeAttempt('eve');
    await hook(event(eveSession, 'Approved', ids.eve, approved(16)));
    assert.deepEqual(code(await call(tokens.eve, '/api/me')), [403, 'account-locked']); pass();
    assert.equal(await sql(`SELECT status || '/' || status_reason FROM accounts WHERE id = '${ids.eve}'`), 'Locked/underage'); pass();
    assert.equal(await sql(`SELECT id_status || '/' || age_status FROM member_profiles WHERE account_id = '${ids.eve}'`), 'Rejected/Underage'); pass();
    assert.ok(!(await call(tokens.ana, '/api/explore?mode=dating')).body.people.some(p => p.id === pid.eve)); pass();

    // Admin outcomes (D129): staff only, recent sign-in, audited; members and other members' ids get nothing.
    assert.equal((await call(tokens.ana, `/api/admin/accounts/${ids.bel}/verification`)).status, 403); pass();
    assert.equal((await call(tokens.ana, `/api/admin/accounts/${ids.bel}/verification/allow-again`, { method: 'POST' })).status, 403); pass();
    assert.equal((await call(tokens.ana, `/api/admin/accounts/${ids.nev}/age-assurance`, { method: 'PUT', body: { needsVerification: true } })).status, 403); pass();
    r = await call(root, `/api/admin/accounts/${ids.bel}/verification`);
    assert.equal(r.status, 200); assert.deepEqual(r.body.attempts.map(a => [a.state, a.started, a.selfService]), [['declined', true, true]]); pass();
    assert.ok(!/url|didit\.me|birth|document/i.test(JSON.stringify(r.body))); pass();
    assert.equal((await call(root, `/api/admin/accounts/${crypto.randomUUID()}/verification`)).status, 404); pass();
    assert.equal((await call(root, `/api/admin/accounts/${ids.bel}/verification/delete`, { method: 'POST' })).status, 404); pass();
    r = await call(root, `/api/admin/accounts/${ids.bel}/verification/allow-again`, { method: 'POST' });
    assert.equal(r.status, 200); assert.equal(r.body.canRequestIdVerification, true); pass();
    assert.deepEqual([(await view('bel')).body.canStart, (await view('bel')).body.idStatus], [true, 'rejected']); pass();
    assert.deepEqual(code(await call(root, `/api/admin/accounts/${ids.ana}/verification/allow-again`, { method: 'POST' })), [409, 'invalid-request']); pass(); // already verified
    r = await call(root, `/api/admin/accounts/${ids.ana}/verification/revoke`, { method: 'POST' });
    assert.equal(r.status, 200); assert.equal(r.body.idStatus, 'rejected'); pass();
    assert.ok(!(await call(tokens.bel, `/api/profiles/${pid.ana}`)).body.badges.some(b => b.badge === 'idVerified')); pass();
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action IN ('verification.allow-again', 'verification.revoke') AND target_id IN ('${ids.bel}', '${ids.ana}')`), '2'); pass();

    // Age assurance (D127): a flagged account can use only its account and ID verification.
    r = await call(root, `/api/admin/accounts/${ids.nev}/age-assurance`, { method: 'PUT', body: { needsVerification: true } });
    assert.deepEqual([r.status, r.body.ageStatus], [200, 'needsVerification']); pass();
    for (const path of ['/api/explore?mode=dating', '/api/me/profile', '/api/connections', '/api/me/trips', '/api/modes', `/api/profiles/${pid.ana}`]) {
      assert.deepEqual(code(await call(tokens.nev, path)), [403, 'age-verification-required'], path); pass();
    }
    assert.deepEqual(code(await call(tokens.nev, '/api/requests', { method: 'POST', body: { to: pid.ana, mode: 'dating' } })), [403, 'age-verification-required']); pass();
    assert.equal((await call(tokens.nev, '/api/me')).body.ageStatus, 'needsVerification'); pass();
    r = await view('nev');
    assert.deepEqual([r.status, r.body.ageCheckRequired, r.body.canStart], [200, true, true]); pass();
    assert.ok(!(await call(tokens.ana, '/api/explore?mode=dating')).body.people.some(p => p.id === pid.nev)); pass();
    assert.equal((await call(tokens.ana, `/api/profiles/${pid.nev}`)).status, 404); pass();
    // The flag follows the verified email to a new account: delete the Keycloak user and register again with the same address.
    const realm = await membersRealm();
    const [kcUser] = await realm(`/users?email=${encodeURIComponent(members.nev.email)}&exact=true`);
    await realm(`/users/${kcUser.id}`, 'DELETE');
    await realm('/users', 'POST', { username: members.nev.email, email: members.nev.email, firstName: 'Fictional', enabled: true, emailVerified: true,
      credentials: [{ type: 'password', value: members.nev.password, temporary: false }] });
    await cli('beta', 'revoke', members.nev.email); await cli('beta', 'add', members.nev.email); // the old enrolment is bound to the old account
    const again = await memberToken(browser, members.nev);
    assert.notEqual(claims(again).sub, claims(tokens.nev).sub); pass();
    assert.equal((await call(again, '/api/me')).body.ageStatus, 'needsVerification'); pass();
    assert.deepEqual(code(await call(again, '/api/explore?mode=dating')), [403, 'age-verification-required']); pass();
    // An authorised resolution (adult override, support or admin) lifts it.
    const nevAgain = await sql(`SELECT id FROM accounts WHERE subject = '${claims(again).sub}'`);
    assert.equal((await call(root, `/api/admin/accounts/${nevAgain}/adult-override`, { method: 'POST' })).status, 204); pass();
    assert.equal((await call(again, '/api/me/profile')).status, 200); pass();

    // Bots never get an ID check (Q11 rule 54).
    await call(root, `/api/admin/accounts/${ids.bot}/bot`, { method: 'PUT', body: { bot: true } });
    assert.deepEqual(code(await start('bot')), [403, 'not-available']); pass();

    // Founders (D130): admin exceptions still need verification; the job allocates in eligibility order, never twice.
    assert.deepEqual(code(await call(root, `/api/admin/accounts/${ids.cat}/founder`, { method: 'POST' })), [409, 'founder-not-eligible']); pass();
    const restored = await fakeAttempt('dee'); // a second, admin-allowed attempt for dee ends approved
    await sql(`UPDATE id_verification_attempts SET self_service = false WHERE session_id = '${restored}'`);
    await sql(`UPDATE id_verification_attempts SET state = 'Abandoned' WHERE account_id = '${ids.dee}' AND session_id <> '${restored}'`);
    await hook(event(restored, 'Approved', ids.dee, approved(36, '1990-01-01'))); // the same date as dee's profile
    assert.equal((await view('dee')).body.idStatus, 'verified'); pass();
    let deeMe = (await call(tokens.dee, '/api/me')).body;
    assert.deepEqual([deeMe.dateOfBirthVerified, deeMe.dateOfBirth], [true, '1990-01-01']); pass(); // matched: marked verified
    assert.deepEqual(code(await call(tokens.dee, '/api/me/age', { method: 'PUT', body: { dateOfBirth: '1991-01-01' } })), [409, 'date-of-birth-locked']); pass();
    assert.equal((await call(tokens.dee, '/api/me/age', { method: 'PUT', body: { dateOfBirth: '1990-01-01' } })).status, 200); pass(); // the same date is fine
    assert.deepEqual(code(await call(tokens.ana, '/api/me/age', { method: 'PUT', body: { dateOfBirth: '1990-02-01' } })), [409, 'date-of-birth-locked']); pass(); // verified by review now
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'dob.%' AND detail ~ '[0-9]{4}-[0-9]{2}-[0-9]{2}'`), '0'); pass(); // never a date; opaque grant ids may contain decimal year-like substrings
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action = 'dob.verified' AND target_id = '${ids.dee}'`), '1'); pass();
    assert.equal((await call(root, `/api/admin/accounts/${ids.dee}/summary`)).body.dateOfBirthVerified, true); pass();
    const placesBefore = Number(await sql(`SELECT count(*) FROM founder_places`));
    const run = await cli('founders', 'allocate');
    assert.match(run, /Founders: \d+ newly eligible, \d+ places allocated, \d+ skipped, \d+ left\./); pass();
    const placesAfter = Number(await sql(`SELECT count(*) FROM founder_places`));
    assert.ok(placesAfter <= 1000 && placesAfter >= placesBefore); pass();
    const deePlace = await sql(`SELECT founder_place FROM member_profiles WHERE account_id = '${ids.dee}'`);
    if (placesBefore < 1000) { assert.match(deePlace, /^\d+$/); pass(); }
    assert.equal(await sql(`SELECT (founder_eligible_at <= founding_since AND founder_entitlement_from IS NOT NULL)::text FROM member_profiles WHERE account_id = '${ids.dee}'`), 'true'); pass();
    assert.equal(await sql(`SELECT coalesce(founder_place::text, 'none') FROM member_profiles WHERE account_id = '${ids.ana}'`), 'none'); pass(); // revoked before the run
    await cli('founders', 'allocate');
    assert.equal(await sql(`SELECT founder_place FROM member_profiles WHERE account_id = '${ids.dee}'`), deePlace); pass(); // never a second place
    await assert.rejects(sql(`INSERT INTO founder_places (place, granted_at) VALUES (1001, now())`)); pass(); // the database refuses place 1,001
    await assert.rejects(sql(`UPDATE member_profiles SET founder_place = ${deePlace} WHERE account_id = '${ids.bel}'`)); pass(); // one account per place

    // The webhook isn't on the BFF; the member route is. Then the page in actual Edge.
    const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
    const page = await context.newPage();
    const signIn = async (p, member) => {
      await p.goto(web + '/account/sign-in?intro=done');
      await p.fill('#username', member.email); await p.fill('#password', member.password);
      await Promise.all([p.waitForURL(web+'/explore'),p.click('#kc-login')]);await p.goto(web+'/account');
    };
    await signIn(page, members.web);
    const bff = (path, options = {}) => page.request.fetch(web + '/bff/' + path, { ...options, headers: { 'X-Swoovr-Bff': '1', ...(options.headers ?? {}) } });
    assert.equal((await bff('api/webhooks/didit', { method: 'POST', data: {} })).status(), 404); pass();
    assert.equal((await bff(`api/admin/accounts/${ids.web}/verification`)).status(), 404); pass();
    assert.equal((await bff('api/me/verification')).status(), 200); pass();
    await page.goto(web + '/me/verification'); // the account page no longer links here at phone width
    await page.waitForSelector('[data-verification=ready]', { timeout: 20000 });
    assert.equal(await page.locator('[data-verification-mode]').count(), 0); pass(); // no environment notice on any host (D321)
    assert.equal(await page.locator('[data-id-status]').getAttribute('data-id-status'), 'notStarted'); pass();
    if (output) await page.screenshot({ path: output + '/verification-start-390.png', fullPage: true });
    await page.click('[data-verification-start]'); // the second real Didit test-mode session of this run
    await page.waitForSelector('[data-verification-link]', { timeout: 20000 });
    const link = page.locator('[data-verification-link]');
    assert.match(await link.getAttribute('href'), /^https:\/\/[a-z.]*didit\.me\//); pass();
    assert.equal(await link.getAttribute('target'), '_blank'); pass(); // opens outside the app, in a new browser tab
    assert.equal(await page.locator('[data-attempt-state]').getAttribute('data-attempt-state'), 'created'); pass();
    if (output) await page.screenshot({ path: output + '/verification-open-390.png', fullPage: true });
    // Coming back: the return page re-reads through the API; a forged status in the address changes nothing.
    await page.goto(web + `/me/verification/return?verificationSessionId=${crypto.randomUUID()}&status=Approved`);
    await page.waitForSelector('[data-verification=ready]', { timeout: 20000 });
    assert.equal(await page.locator('[data-id-status]').getAttribute('data-id-status'), 'pending'); pass();
    const webSession = await sql(`SELECT session_id FROM id_verification_attempts WHERE account_id = '${ids.web}'`);
    await hook(event(webSession, 'In Review', ids.web));
    await page.click('[data-verification-refresh]');
    await page.waitForSelector('[data-attempt-state=inReview]'); pass();
    // Q58 (D166): approved with a different date of birth, the one-time prompt pops up; the correction goes to review
    // here because this real test-mode session holds no date to compare.
    await hook(event(webSession, 'Approved', ids.web, approved(30, '1970-03-03')));
    await page.goto(web + '/me/verification');
    await page.waitForSelector('[data-dob-correction=open]', { timeout: 20000 }); pass();
    assert.equal(await page.locator('[role=dialog][aria-modal=true]').count(), 1); pass();
    assert.ok(!(await page.locator('[data-dob-correction=open]').innerText()).includes('1970')); pass(); // the document's date is never shown
    if (output) await page.screenshot({ path: output + '/dob-correction-390.png', fullPage: true });
    await page.fill('#dob-correction', '1970-03-03');
    await page.click('[data-dob-correction-form] button[type=submit]');
    await page.waitForFunction(() => document.querySelector('[data-dob-review=open]') || document.querySelector('[data-dob-correction-status]:not([data-dob-correction-status=""])'), null, { timeout: 20000 });
    assert.equal(await page.locator('[data-dob-review=open]').count(), 1, 'correction status ' + await page.locator('[data-dob-correction-status]').getAttribute('data-dob-correction-status').catch(() => 'none')); pass();
    assert.equal(await page.locator('[data-dob-correction=open]').count(), 0); pass();
    // Issue 439: plain words, no date and no "still differs"; the badge's checked date is ordinary weight.
    const reviewText = await page.locator('[data-dob-review=open]').innerText();
    assert.ok(reviewText.includes('Your ID is verified.') && !/\d/.test(reviewText) && !/differs/i.test(reviewText), reviewText); pass();
    if (await page.locator('[data-badge-checked]').count() > 0)
      assert.equal(await page.locator('[data-badge-checked]').first().evaluate(e => getComputedStyle(e).fontWeight), '400');
    pass();
    if (output) await page.screenshot({ path: output + '/dob-review-390.png', fullPage: true });
    assert.equal((await call(root, '/api/admin/dob-reviews')).body.filter(x => x.accountId === ids.web).length, 1); pass();
    await page.goto(web + '/account');
    await page.waitForSelector('[data-dob-locked=manualReview]', { timeout: 20000 }); pass();
    // A flagged member sees why on the account page.
    const nevContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    const nevPage = await nevContext.newPage();
    await call(root, `/api/admin/accounts/${ids.cat}/age-assurance`, { method: 'PUT', body: { needsVerification: true } });
    await signIn(nevPage, members.cat);
    await nevPage.waitForSelector('[data-age-check-required=true]', { timeout: 20000 }); pass();
    if (output) await nevPage.screenshot({ path: output + '/age-check-390.png', fullPage: true });
    await nevContext.close(); await context.close();
    // D299: Admin correction reuses the encrypted D191 record; an ID badge survives mismatch/manual review.
    const dobGrant=await call(root,`/api/admin/accounts/${ids.dee}/dob-correction`,{method:'POST',body:{reason:'Fictional stored identity comparison'}});
    assert.equal(dobGrant.status,200);pass();
    const mismatched=await call(tokens.dee,'/api/me/age',{method:'PUT',body:{dateOfBirth:'1991-01-01',confirmed:true}});
    assert.equal(mismatched.status,200);assert.equal(mismatched.body.dateOfBirthVerified,false);assert.equal(mismatched.body.dobCheck,'manualReview');pass();
    assert.equal((await view('dee')).body.idStatus,'verified');pass();
    const review=(await call(root,'/api/admin/dob-reviews')).body.find(r=>r.accountId===ids.dee);
    assert(review);assert.equal((await call(root,`/api/admin/dob-reviews/${review.id}/resolve`,{method:'POST',body:{outcome:'notVerified',reason:'Fictional reviewed date correction'}})).status,200);pass();
    assert.equal((await call(root,`/api/admin/accounts/${ids.dee}/dob-correction`,{method:'POST',body:{reason:'Fictional stored identity matching'}})).status,200);pass();
    const matched=await call(tokens.dee,'/api/me/age',{method:'PUT',body:{dateOfBirth:'1990-01-01',confirmed:true}});
    assert.equal(matched.status,200);assert.equal(matched.body.dateOfBirthVerified,true);assert.equal(matched.body.dobCheck,'none');pass();
    // Issue 440 (D402): an approval that arrives without the date of birth is completed from Didit's decision, so a date
    // that differs gives the member their one correction; with no date anywhere it still goes to a person.
    if (process.env.SWOOVR_OWNED_DIDIT_URL) {
      const decided = (session, vendor, checks, stall = {}) => fetch(`${process.env.SWOOVR_OWNED_DIDIT_URL}/owned/decision/${session}`, { method: 'PUT',
        body: JSON.stringify({ session_id: session, status: 'Approved', vendor_data: vendor, workflow_id: workflow, id_verifications: checks, ...stall }) });
      for (const [n, extra] of [['bare', {}], ['legacy', { decision: { id_verification: { status: 'Approved', date_of_birth: '1971-01-01' } } }], ['none', {}]]) {
        members[n] = await createMember('verify-' + n); await cli('beta', 'add', members[n].email);
        tokens[n] = await memberToken(browser, members[n]);
        await call(tokens[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional ' + n, homeCityId: ADELAIDE } });
        await matchReady(tokens[n]);
        ids[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens[n]).sub}'`);
        const session = await fakeAttempt(n);
        if (n !== 'none') await decided(session, ids[n], [{ status: 'Approved', age: 55, date_of_birth: '1971-01-01' }]);
        assert.equal((await hook(event(session, 'Approved', ids[n], extra))).body.outcome, 'applied'); pass();
        assert.equal(await sql(`SELECT dob_check FROM member_profiles WHERE account_id = '${ids[n]}'`), n === 'none' ? 'ManualReview' : 'CorrectionNeeded', n); pass();
        assert.equal(await sql(`SELECT count(*) FROM dob_reviews WHERE account_id = '${ids[n]}'`), n === 'none' ? '1' : '0', n); pass();
      }
      // Issue 488 (D413): a document check that isn't Approved supplies nothing and the review says so; ID Verified stays, age doesn't.
      members.pending = await createMember('verify-pending'); await cli('beta', 'add', members.pending.email);
      tokens.pending = await memberToken(browser, members.pending);
      await call(tokens.pending, '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional pending', homeCityId: ADELAIDE } });
      await matchReady(tokens.pending);
      ids.pending = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tokens.pending).sub}'`);
      const pendingSession = await fakeAttempt('pending'), pendingChecks = [{ status: 'In Review', age: 55, date_of_birth: '1971-01-01' }];
      await decided(pendingSession, ids.pending, pendingChecks);
      assert.equal((await hook(event(pendingSession, 'Approved', ids.pending, { decision: { id_verifications: pendingChecks } }))).body.outcome, 'applied'); pass();
      assert.equal(await sql(`SELECT reason FROM dob_reviews WHERE account_id = '${ids.pending}'`), 'document-not-approved'); pass();
      assert.equal(await sql(`SELECT id_status || ',' || date_of_birth_verified || ',' || (age_status = 'Verified')::text FROM member_profiles WHERE account_id = '${ids.pending}'`), 'Verified,false,false'); pass();
      assert.equal(await sql(`SELECT count(*) FROM restricted.verified_identities WHERE account_id = '${ids.pending}'`), '0'); pass();
      assert.equal(await sql(`SELECT detail FROM audit_entries WHERE action = 'dob.review-opened' AND target_id = '${ids.pending}'`), 'document-not-approved'); pass();
      // The waiting count reaches admins' navigation and overview, and nobody else.
      const waiting = Number(await sql('SELECT count(*) FROM dob_reviews WHERE resolved_at IS NULL'));
      assert.ok(waiting > 0);
      assert.equal((await call(root, '/api/admin/workspace')).body.dobReviewsWaiting, waiting); pass();
      const people = {};
      for (const role of ['admin', 'support']) {
        people[role] = await createStaff('dob-' + role);
        assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: people[role].email, role } })).status, 201);
        people[role].token = await staffToken(browser, people[role]); await call(people[role].token, '/api/session');
      }
      assert.equal((await call(people.support.token, '/api/admin/workspace')).body.dobReviewsWaiting ?? null, null); pass();
      // Issue 456 (D403): the Didit decision, read live for an admin with live access and a purpose, never stored.
      const didit = (token, who, body) => call(token, `/api/admin/accounts/${ids[who]}/didit-decision`, { method: 'POST', body });
      const why = { purpose: 'Fictional date review' }, adm = people.admin.token;
      assert.equal((await didit(null, 'bare', why)).status, 401); pass();
      assert.equal((await didit(tokens.ana, 'bare', why)).status, 403); pass();
      assert.equal((await didit(people.support.token, 'bare', why)).status, 403); pass(); // Support has no date of birth queue
      assert.deepEqual(code(await didit(adm, 'bare', why)), [403, 'elevated-access-required']); pass();
      assert.equal((await call(people.support.token, '/api/admin/access/grants', { method: 'POST', body: { capability: 'didit-decision-open', purpose: 'x' } })).status, 403); pass();
      assert.equal((await call(adm, '/api/admin/access/grants', { method: 'POST', body: { capability: 'didit-decision-open', purpose: 'Fictional date review' } })).status, 201); pass();
      assert.deepEqual(code(await didit(adm, 'bare', { purpose: ' ' })), [400, 'purpose-required']); pass();
      r = await didit(adm, 'bare', why);
      assert.deepEqual([r.status, r.body.result, r.body.status], [200, 'available', 'Approved']); pass();
      assert.deepEqual([r.body.documents.length, r.body.documents[0].dateOfBirth, r.body.documents[0].sameDate], [1, '1971-01-01', false]); pass();
      assert.equal((await didit(adm, 'none', why)).body.result, 'gone'); pass(); // the stand-in has no such session
      assert.equal((await didit(adm, 'bare', { ...why, reviewId: crypto.randomUUID() })).status, 404); pass();
      const bareSession = await sql(`SELECT session_id FROM id_verification_attempts WHERE account_id = '${ids.bare}'`);
      await decided(bareSession, ids.legacy, [{ status: 'Approved', date_of_birth: '1971-01-01' }]);
      r = await didit(adm, 'bare', why);
      assert.deepEqual([r.body.result, r.body.documents], ['unavailable', []], 'another account\'s session is never shown'); pass();
      await decided(bareSession, ids.bare, [{ status: 'Approved', age: 55, document_type: 'Passport', issuing_state: 'AUS', date_of_birth: '1971-01-01' }]);
      assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action = 'didit-decision.opened' AND target_id = '${ids.bare}' AND detail IS NULL`), '2'); pass();
      assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action LIKE 'didit-decision%' AND detail IS NOT NULL`), '0'); pass();
      const adminId = await sql(`SELECT id FROM accounts WHERE subject = '${claims(adm).sub}'`);
      await call(adm, '/api/admin/access/exclusions', { method: 'POST', body: { subjectAccountId: ids.legacy, reason: 'Fictional conflict' } });
      if (await sql(`SELECT count(*) FROM staff_exclusions WHERE staff_account_id = '${adminId}' AND subject_account_id = '${ids.legacy}'`) === '0') await sql(`INSERT INTO staff_exclusions (staff_account_id, subject_account_id, reason, created_by, created_at) VALUES ('${adminId}', '${ids.legacy}', 'Fictional conflict', '${adminId}', now())`);
      assert.deepEqual(code(await didit(adm, 'legacy', why)), [403, 'conflict-of-interest']); pass();
      // A review for a member whose session the stand-in knows, to see the panel in the dialog.
      const bareAttempt = await sql(`SELECT id FROM id_verification_attempts WHERE account_id = '${ids.bare}'`), bareReview = crypto.randomUUID();
      await sql(`INSERT INTO dob_reviews (id, account_id, attempt_id, reason, created_at) VALUES ('${bareReview}', '${ids.bare}', '${bareAttempt}', 'still-different', now())`);
      // Issue 455 (D404): Support raises, an admin who didn't raise it resolves; nothing about it reaches the member.
      const raise = (token, who, reason) => call(token, `/api/admin/accounts/${ids[who]}/escalation`, { method: 'POST', body: { reason } });
      const sup = people.support.token;
      assert.equal((await raise(tokens.ana, 'bare', 'Fictional')).status, 403); pass();
      assert.equal((await raise(sup, 'bare', 'Born 1 January 1971')).status, 400); pass();
      assert.equal((await raise(sup, 'bare', 'Fictional: the document looks altered')).status, 200); pass();
      assert.equal((await raise(adm, 'bare', 'Fictional again')).status, 409); pass();
      assert.equal((await raise(adm, 'legacy', 'Fictional')).body.code, 'conflict-of-interest'); pass();
      assert.equal((await call(sup, '/api/admin/escalations')).status, 403); pass();
      assert.equal((await call(sup, `/api/admin/accounts/${ids.bare}/escalation`)).body.open.canResolve, false); pass();
      const raisedCase = (await call(adm, '/api/admin/dob-reviews')).body.find(x => x.id === bareReview).escalation;
      assert.deepEqual([raisedCase.reason, raisedCase.dobReviewId, raisedCase.canResolve], ['Fictional: the document looks altered', bareReview, true]); pass();
      assert.equal((await call(sup, `/api/admin/escalations/${raisedCase.id}/resolve`, { method: 'POST', body: { note: 'Fictional' } })).status, 403); pass();
      assert.equal((await raise(adm, 'web', 'Fictional: raised by an admin')).status, 200); pass();
      const own = (await call(adm, '/api/admin/escalations')).body.find(x => x.accountId === ids.web);
      assert.deepEqual(code(await call(adm, `/api/admin/escalations/${own.id}/resolve`, { method: 'POST', body: { note: 'Fictional' } })), [403, 'moderation-not-permitted']); pass();
      assert.equal((await call(root, `/api/admin/escalations/${own.id}/resolve`, { method: 'POST', body: { note: 'On 01/01/1971' } })).status, 400); pass();
      assert.equal((await call(root, `/api/admin/escalations/${own.id}/resolve`, { method: 'POST', body: { note: 'Fictional: looked at' } })).body.resolutionNote, 'Fictional: looked at'); pass();
      assert.equal((await call(root, `/api/admin/escalations/${own.id}/resolve`, { method: 'POST', body: { note: 'Fictional' } })).status, 409); pass();
      assert.equal(await sql(`SELECT string_agg(action, ',' ORDER BY id) FROM audit_entries WHERE action LIKE 'account.escalat%' AND target_id = '${ids.web}'`), 'account.escalated,account.escalation-resolved'); pass();
      // PR 491 review: staff with a conflict of interest can't close an escalation by deciding its review either.
      const conflictAttempt = await sql(`SELECT id FROM id_verification_attempts WHERE account_id = '${ids.legacy}'`), conflictReview = crypto.randomUUID();
      await sql(`INSERT INTO dob_reviews (id, account_id, attempt_id, reason, created_at) VALUES ('${conflictReview}', '${ids.legacy}', '${conflictAttempt}', 'still-different', now())`);
      assert.equal((await raise(sup, 'legacy', 'Fictional: please look')).status, 200); pass();
      assert.deepEqual(code(await call(adm, `/api/admin/dob-reviews/${conflictReview}/resolve`, { method: 'POST', body: { outcome: 'verified', reason: 'Fictional' } })), [403, 'conflict-of-interest']); pass();
      assert.equal(await sql(`SELECT (SELECT count(*) FROM account_escalations WHERE dob_review_id = '${conflictReview}' AND resolved_at IS NULL) + (SELECT count(*) FROM dob_reviews WHERE id = '${conflictReview}' AND resolved_at IS NULL)`), '2', 'both stay open'); pass();
      // Deciding an escalated review: not by the person who escalated it; another admin closes both.
      const legacyAttempt = await sql(`SELECT id FROM id_verification_attempts WHERE account_id = '${ids.none}'`);
      const noneReview = await sql(`SELECT id FROM dob_reviews WHERE account_id = '${ids.none}' AND resolved_at IS NULL`); assert.ok(legacyAttempt && noneReview);
      assert.equal((await raise(adm, 'none', 'Fictional: second opinion')).status, 200); pass();
      const decide = (token, id) => call(token, `/api/admin/dob-reviews/${id}/resolve`, { method: 'POST', body: { outcome: 'notVerified', reason: 'Fictional decision' } });
      assert.deepEqual(code(await decide(adm, noneReview)), [403, 'moderation-not-permitted']); pass();
      assert.equal((await decide(root, noneReview)).status, 200); pass();
      assert.equal(await sql(`SELECT count(*) FROM account_escalations WHERE dob_review_id = '${noneReview}' AND resolved_at IS NULL`), '0'); pass();
      // PR 505 review: an admin closes the escalation while another decides its review: the first resolution is kept, recorded once.
      const raceReview = crypto.randomUUID(), rootId = await sql(`SELECT id FROM accounts WHERE subject = '${claims(root).sub}'`);
      await sql(`INSERT INTO dob_reviews (id, account_id, attempt_id, reason, created_at) VALUES ('${raceReview}', '${ids.none}', '${legacyAttempt}', 'still-different', now())`);
      assert.equal((await raise(sup, 'none', 'Fictional: race')).status, 200); pass();
      const raceCase = await sql(`SELECT id FROM account_escalations WHERE dob_review_id = '${raceReview}' AND resolved_at IS NULL`);
      const first = sql(`BEGIN; UPDATE account_escalations SET resolved_at = now(), resolved_by = '${rootId}', resolution_note = 'Fictional: first' WHERE id = '${raceCase}'; SELECT pg_sleep(4); COMMIT;`);
      await new Promise(resolve => setTimeout(resolve, 1500));
      const decided2 = await Promise.all([decide(adm, raceReview), decide(root, raceReview)]); await first;
      assert.deepEqual(decided2.map(x => x.status).sort(), [200, 409], 'one decision'); pass();
      assert.equal(await sql(`SELECT resolution_note FROM account_escalations WHERE id = '${raceCase}'`), 'Fictional: first'); pass();
      assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action = 'account.escalation-resolved' AND detail = '${raceCase}'`), '0', 'the decision records no second resolution'); pass();
      for (const path of ['/api/me/notifications', '/api/me/notices', '/api/me/standing']) assert.ok(!JSON.stringify((await call(tokens.bare, path)).body).includes('altered'), path); pass();
      // Issue 488 (D413): the ID document, proxied live for an open review. Synthetic pictures only.
      const picture = name => `${process.env.SWOOVR_OWNED_DIDIT_URL}/owned/image/${name}?signature=synthetic-signature`;
      const idChecks = (front, back) => [{ status: 'Approved', age: 55, date_of_birth: '1971-01-01', front_image: front && picture(front), back_image: back && picture(back), portrait_image: picture('portrait.png') }];
      const idImage = (token, who, reviewId, purpose = 'Fictional ID check') => call(token, `/api/admin/accounts/${ids[who]}/id-image`, { method: 'POST', body: { purpose, reviewId } });
      const grantId = token => call(token, '/api/admin/access/grants', { method: 'POST', body: { capability: 'id-image-open', purpose: 'Fictional ID check' } });
      await decided(bareSession, ids.bare, idChecks('front.png', 'back.png'));
      assert.equal((await idImage(null, 'bare', bareReview)).status, 401); pass();
      assert.equal((await idImage(tokens.ana, 'bare', bareReview)).status, 403); pass();
      assert.equal((await idImage(sup, 'bare', bareReview)).status, 403); pass();
      assert.equal((await grantId(sup)).status, 403); pass();
      assert.deepEqual(code(await idImage(adm, 'bare', bareReview)), [403, 'elevated-access-required'], 'the Didit result access is not enough'); pass();
      assert.equal((await grantId(adm)).status, 201); pass();
      assert.deepEqual(code(await idImage(adm, 'bare', bareReview, ' ')), [400, 'purpose-required']); pass();
      r = await idImage(adm, 'bare', bareReview);
      assert.deepEqual([r.status, r.body.result, r.body.images.map(i => i.side + ' ' + i.contentType)], [200, 'available', ['Front image/png', 'Back image/png']]); pass();
      assert.equal(Buffer.from(r.body.images[0].data, 'base64').subarray(1, 4).toString(), 'PNG'); pass();
      assert.ok(!JSON.stringify(r.body).includes('/owned/'), 'no provider address reaches the browser'); pass();
      const raw = await fetch(`${api}/api/admin/accounts/${ids.bare}/id-image`, { method: 'POST', headers: { Authorization: 'Bearer ' + adm, 'Content-Type': 'application/json', 'Swoovr-Version': '2026-10-28' }, body: JSON.stringify({ purpose: 'Fictional ID check', reviewId: bareReview }) });
      assert.deepEqual([raw.status, raw.headers.get('cache-control')], [200, 'no-store']); pass();
      assert.deepEqual(code(await idImage(adm, 'legacy', conflictReview)), [403, 'conflict-of-interest']); pass(); // declared, and linked accounts use the same rule
      await sql(`INSERT INTO staff_member_links (staff_account_id, member_account_id, linked_at, linked_by) VALUES ('${adminId}', '${ids.eve}', now(), '${adminId}')`);
      assert.deepEqual(code(await idImage(adm, 'eve', bareReview)), [403, 'conflict-of-interest'], 'their own linked account'); pass();
      await sql(`DELETE FROM staff_member_links WHERE staff_account_id = '${adminId}'`);
      assert.equal((await idImage(adm, 'web', bareReview)).status, 404, 'another member\'s review'); pass();
      assert.equal((await idImage(adm, 'bare', crypto.randomUUID())).status, 404); pass();
      assert.equal((await idImage(adm, 'none', noneReview)).status, 404, 'a decided review'); pass();
      const said = async (expected, vendor, checks, stall) => { await decided(bareSession, vendor, checks, stall); assert.deepEqual([(r = await idImage(adm, 'bare', bareReview)).body.result, r.body.images], [expected, []], expected); pass(); };
      await said('unavailable', ids.legacy, idChecks('front.png'));
      await said('refused', ids.bare, idChecks('front.bad'));
      await said('refused', ids.bare, idChecks('front.png', 'back.big'));
      await said('refused', ids.bare, [{ status: 'Approved', front_image: 'https://media.example.test/front.png' }]);
      await said('none', ids.bare, [{ status: 'Approved', front_image: null }]);
      await said('unavailable', ids.bare, [], { owned_status: 503 });
      await said('gone', ids.bare, [], { owned_status: 404 });
      const slow = Date.now(); await said('unavailable', ids.bare, [], { owned_delay_ms: 20000 }); assert.ok(Date.now() - slow < 10000); pass();
      await decided(bareSession, ids.bare, idChecks('front.png', 'back.png'));
      // An expired access no longer opens it.
      await sql(`UPDATE restricted.access_grants SET expires_at = now() - interval '1 minute' WHERE account_id = '${adminId}' AND capability = 'id-image-open'`).catch(() => sql(`UPDATE access_grants SET expires_at = now() - interval '1 minute' WHERE account_id = '${adminId}' AND capability = 'id-image-open'`));
      assert.deepEqual(code(await idImage(adm, 'bare', bareReview)), [403, 'elevated-access-required']); pass();
      // An unapproved check's document opens too (Q185 A).
      assert.equal((await grantId(adm)).status, 201);
      const pendingReview = await sql(`SELECT id FROM dob_reviews WHERE account_id = '${ids.pending}'`);
      await decided(pendingSession, ids.pending, [{ ...idChecks('front.png')[0], status: 'In Review' }]);
      assert.equal((await idImage(adm, 'pending', pendingReview)).body.result, 'available'); pass();
      // Audited with ids only, in the sensitive-access log, and nowhere else.
      assert.equal(await sql(`SELECT string_agg(DISTINCT detail, ',') FROM audit_entries WHERE action = 'id-image.opened' AND target_id = '${ids.bare}'`), bareReview); pass();
      assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action = 'id-image.opened' AND target_id = '${ids.bare}'`), '10'); pass();
      assert.equal((await call(sup, `/api/admin/dob-reviews`)).status, 403); pass();
      if (process.env.SWOOVR_MEETING_OWNED_LOGS) for (const name of ['api.log', 'admin.log', 'web.log'])
        assert.ok(!/owned\/image|synthetic-signature/.test(require('node:fs').readFileSync(`${process.env.SWOOVR_MEETING_OWNED_LOGS}/${name}`, 'utf8')), name + ' holds no image link');
      pass();
      const queue = Number(await sql('SELECT count(*) FROM dob_reviews WHERE resolved_at IS NULL'));
      for (const role of ['admin', 'support']) {
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 } }), page = await context.newPage();
        await page.goto(admin + '/');
        await page.fill('#username', people[role].email); await page.fill('#password', people[role].password); await page.click('#kc-login');
        await fillStaffOtp(page, people[role]);
        await page.waitForSelector('[data-dashboard]', { timeout: 20000 });
        assert.equal(await page.locator('.admin-sidebar [data-nav-dob-count]').first().getAttribute('data-nav-dob-count', { timeout: 2000 }).catch(() => null), role === 'admin' ? String(queue) : null, role); pass();
        assert.equal(await page.locator('[data-dob-reviews-waiting]').count(), role === 'admin' ? 1 : 0, role); pass();
        if (output) await page.screenshot({ path: `${output}/admin-overview-${role}-1280.png` });
        if (role === 'admin') {
          for (const [name, url, size] of [['dob-dialog-1280', `/dob-reviews?review=${bareReview}`, { width: 1280, height: 900 }], ['dob-dialog-390', `/dob-reviews?review=${bareReview}`, { width: 390, height: 844 }], ['account-verification-1280', `/accounts/${ids.bare}/verification`, { width: 1280, height: 900 }]]) {
            await page.setViewportSize(size);
            await page.goto(admin + url);
            await page.click('[data-didit-decision=closed] summary');
            if (output) await page.screenshot({ path: `${output}/${name}-closed.png` });
            await page.selectOption('[data-didit-decision] select[name=purpose]', 'Date of birth review');
            await page.fill('[data-didit-decision] textarea[name=detail]', 'Fictional check');
            await page.click('[data-didit-show]');
            await page.waitForSelector('[data-didit-decision=available]', { timeout: 20000 }); pass();
            assert.match(await page.textContent('[data-didit-dob]'), /1 January 1971/); pass();
            if (output) await page.screenshot({ path: `${output}/${name}-open.png` });
          }
          for (const theme of ['light', 'dark']) for (const width of [1280, 390]) {
            await page.emulateMedia({ colorScheme: theme });
            await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
            await page.goto(`${admin}/dob-reviews?review=${bareReview}`);
            await page.evaluate(t => { try { localStorage.setItem('swoovr.admin.theme', t); } catch { } }, theme); await page.reload();
            assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), theme); pass();
            await page.click('[data-id-image=closed] summary'); await page.fill('[data-id-image] textarea', 'Fictional look');
            const answered = page.waitForResponse(x => x.request().method() === 'POST' && x.url().includes('/dob-reviews'));
            await page.click('[data-id-image-show]');
            assert.equal((await answered).headers()['cache-control'].includes('no-store'), true); pass();
            await page.waitForSelector('[data-id-image=available] img', { timeout: 20000 });
            assert.deepEqual(await page.locator('[data-id-image] img').evaluateAll(all => all.map(i => [i.naturalWidth, i.src.startsWith('data:image/png;base64,')])), [[320, true], [320, true]]); pass();
            assert.ok(!(await page.content()).includes('/owned/image')); pass();
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no sideways scroll'); pass();
            await page.locator('[data-id-image]').scrollIntoViewIfNeeded();
            if (output) await page.screenshot({ path: `${output}/id-image-${theme}-${width}.png` });
            await page.click('[data-photo-close]'); await page.waitForSelector('[data-review-dialog]', { state: 'detached' });
            assert.equal(await page.locator('[data-id-image] img').count(), 0, 'closed with the dialog'); pass();
            await page.goBack(); assert.equal(await page.locator('[data-id-image] img').count(), 0, 'going back does not bring it up'); pass();
          }
          await page.evaluate(() => { try { localStorage.removeItem('swoovr.admin.theme'); } catch { } }); await page.emulateMedia({ colorScheme: 'light' });
          await page.setViewportSize({ width: 1280, height: 900 });
          await page.goto(admin + '/dob-reviews?why=document-not-approved');
          assert.equal(await page.locator('[data-dob-review]').count(), 1); pass();
          assert.match(await page.textContent('[data-dob-review]'), /Document check not approved in Didit/); pass();
          if (output) await page.screenshot({ path: output + '/dob-not-approved-1280.png' });
          for (const [name, url] of [['escalations', '/escalations'], ['dob-escalated-filter', '/dob-reviews?escalated=yes'], ['account-escalated', `/accounts/${ids.bare}`], ['account-escalate', `/accounts/${ids.web}`]]) {
            await page.goto(admin + url); if (output) await page.screenshot({ path: `${output}/${name}-1280.png` });
          }
          await page.click('[data-escalate]'); await page.fill('[name=escalationReason]', 'Fictional: from the account page'); await page.click('[data-confirm=escalate]');
          await page.waitForSelector('[data-escalated]'); pass();
          await page.goto(admin + '/dob-reviews?escalated=yes'); assert.equal(await page.locator('[data-dob-review]').count(), await page.locator('[data-dob-escalated]').count()); pass();
          assert.ok(await page.locator('[data-dob-escalated]').count() > 0); pass();
          await page.goto(admin + '/escalations'); await page.click(`[data-escalation="${raisedCase.id}"] summary`);
          await page.fill(`[data-escalation="${raisedCase.id}"] textarea`, 'Fictional: reviewed'); await page.click(`[data-escalation="${raisedCase.id}"] [data-escalation-resolve]`);
          await page.waitForSelector('[data-result=ok]'); pass();
          assert.equal(await page.locator(`[data-escalation="${raisedCase.id}"]`).count(), 0); pass();
          // PR 490 review: Ctrl+Enter submits the form in focus once, and never saves the decision from another form.
          const posts = [], keyed = async (focus, fill) => {
            await page.goto(`${admin}/dob-reviews?review=${bareReview}`);
            await page.check('[data-resolve=notVerified]', { force: true }); await page.fill('[data-dob-form] textarea[name=reason]', 'Fictional: half written');
            if (fill) await fill();
            await page.focus(focus);
            posts.length = 0;
            await page.route('**/*', route => { const r = route.request(); if (r.method() !== 'POST') return route.continue(); posts.push(decodeURIComponent((r.postDataBuffer()?.toString() ?? '').replace(/\+/g, ' '))); return route.abort(); });
            await page.keyboard.press('Control+Enter'); await page.waitForTimeout(1500); await page.unroute('**/*');
            return posts;
          };
          let sent = await keyed('[data-didit-decision] textarea[name=detail]', async () => { await page.click('[data-didit-decision=closed] summary'); await page.fill('[data-didit-decision] textarea[name=detail]', 'Fictional check'); });
          assert.equal(sent.length, 1, 'one request from the Didit form'); pass();
          assert.ok(sent[0].includes('didit-') && !sent[0].includes('half written'), 'the Didit form, not the decision'); pass();
          sent = await keyed('[data-dob-form] textarea[name=reason]');
          assert.deepEqual([sent.length, sent[0]?.includes('half written')], [1, true], 'the decision, once'); pass();
          sent = await keyed('[data-review-dialog] [data-photo-close]');
          assert.deepEqual([sent.length, sent[0]?.includes('half written')], [1, true], 'outside a form, the decision'); pass();
          // PR 490 review: a slow or failing Didit shows the retry state inside the admin app's own 10-second limit.
          await page.setViewportSize({ width: 1280, height: 900 });
          for (const stall of [{ owned_delay_ms: 20000 }, { owned_status: 503 }]) {
            await decided(bareSession, ids.bare, [], stall);
            await page.goto(`${admin}/accounts/${ids.bare}/verification`);
            await page.click('[data-didit-decision=closed] summary');
            await page.selectOption('[data-didit-decision] select[name=purpose]', 'Date of birth review');
            await page.fill('[data-didit-decision] textarea[name=detail]', 'Fictional check');
            const asked = Date.now();
            await page.click('[data-didit-show]');
            await page.waitForSelector('[data-didit-decision=unavailable]', { timeout: 20000 }); pass();
            assert.ok(Date.now() - asked < 10000, 'answered before the admin app gives up'); pass();
            assert.match(await page.textContent('[data-didit-decision]'), /can't be reached right now/); pass();
            assert.equal(await page.locator('[data-didit-show]').count(), 1, 'can be tried again'); pass();
          }
          await page.setViewportSize({ width: 1280, height: 900 }); await page.goto(admin + '/');
          await page.setViewportSize({ width: 390, height: 844 }); await page.reload(); await page.waitForSelector('[data-dashboard]');
          if (output) await page.screenshot({ path: output + '/admin-overview-admin-390.png' });
          await sql(`UPDATE dob_reviews SET resolved_at = now() WHERE resolved_at IS NULL`);
          await page.goto(admin + '/');
          await page.waitForSelector('[data-dob-reviews-waiting="0"]'); pass();
          assert.equal(await page.locator('[data-nav-dob-count]').count(), 0, 'hidden at 0'); pass();
        }
        await context.close();
      }
    }
    console.log(`PASS: ${process.env.SWOOVR_OWNED_DIDIT_SIMULATED==='1'?'synthetic local Didit adapter (no live provider acceptance)':'Didit ID verification'} acceptance, ${passed} checks (test-mode label, ${process.env.SWOOVR_OWNED_DIDIT_SIMULATED==='1'?'mocked session creation bound to the account':'real Didit session creation bound to the account'}, one open session, configured return address only, webhook signature before timestamp before dedupe, forged/unsigned/simple-only/tampered/stale/oversized/replayed deliveries, wrong vendor_data, workflow and unknown sessions, forward-only states, re-read on return, outcome-only storage, one self-service attempt, unused sessions returned, under-18 lock, admin allow-again/revoke audited, age-assurance gate everywhere and across a re-registration, adult override, bots refused, founder job order/limit/no second place, BFF allow-list, web start/return/refresh in Edge).`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
