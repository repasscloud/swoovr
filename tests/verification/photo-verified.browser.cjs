// Issue 530 (D424, D427): Photo Verified. Off by default (the owned API has no switch): the member sees it isn't
// available. On an owned extra API with the switch on: a fresh four-character code each time, single use and bound to the
// account; one photo holding it goes through the upload checks into the Photo Identified queue (never a member link);
// Support approves against the public profile photos the member had (refused if those changed), which awards Photo
// Verified for 12 months and dates the matched photos; changing a matched photo ends it; rejection gives a member reason;
// three sends in 30 days; evidence deleted 30 days after the decision. Synthetic images and fictional adults only;
// labelled simulated screening and the Development-only disabled malware scanner on the extra API.
const assert = require('node:assert/strict');
const crypto = require('node:crypto'), fs = require('node:fs/promises'), path = require('node:path'), os = require('node:os'), net = require('node:net'), { once } = require('node:events');
const { spawn } = require('node:child_process');
if (!process.env.SWOOVR_TEST_APP_DATABASE || !process.env.SWOOVR_OWNED_RUN_MANIFEST || !process.env.Media__Local__Path) throw Error('Photo Verified requires the owned runner.');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { api, sleep, createMember, createStaff, memberToken, staffToken, rootAdminToken, cli, sql, call, claims, matchReady } = require('../auth/local-tokens.cjs');
const { synthetic } = require('../media/synthetic-images.cjs');

let EXTRA, extraProcess, extraFolder;
async function extraApi(env) {
  const manifest = JSON.parse(await fs.readFile(process.env.SWOOVR_OWNED_RUN_MANIFEST, 'utf8'));
  const probe = net.createServer(); await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve));
  EXTRA = 'http://127.0.0.1:' + probe.address().port; await new Promise(resolve => probe.close(resolve));
  extraFolder = await fs.mkdtemp(path.join(os.tmpdir(), 'swoovr-photo-verified-')); await fs.chmod(extraFolder, 0o700);
  const log = await fs.open(path.join(extraFolder, 'api.log'), 'w', 0o600);
  extraProcess = spawn('dotnet', [manifest.apiDll], { cwd: path.resolve('src/Swoovr.Api'), env: { ...process.env, ...env, ASPNETCORE_URLS: EXTRA }, stdio: ['ignore', log.fd, log.fd] }); await log.close();
  for (let n = 0; n < 150; n++) {
    if (extraProcess.exitCode !== null || extraProcess.signalCode !== null) throw Error('Owned Photo Verified API exited; inspect private log ' + extraFolder);
    try { if ((await fetch(EXTRA + '/health/live', { signal: AbortSignal.timeout(1000) })).ok) return; } catch {}
    await sleep(100);
  }
  throw Error('Owned Photo Verified API readiness timeout.');
}
async function stopExtra() {
  if (extraProcess && extraProcess.exitCode === null && extraProcess.signalCode === null) {
    const child = extraProcess, exited = once(child, 'exit'); child.kill('SIGTERM'); await Promise.race([exited, sleep(5000)]);
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await exited; }
  }
  extraProcess = null;
  if (extraFolder) { if (process.env.SWOOVR_SCREENSHOTS) await fs.copyFile(path.join(extraFolder, 'api.log'), path.join(process.env.SWOOVR_SCREENSHOTS, 'photo-verified-extra-api.log')).catch(() => {}); await fs.rm(extraFolder, { recursive: true, force: true }); extraFolder = null; }
}
const waitFor = async (predicate, ms = 60000) => { for (let i = 0; i < ms / 500; i++) { if (await predicate()) return true; await sleep(500); } return predicate(); };
const today = () => new Date().toISOString().slice(0, 10);

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const canvas = await browser.newPage();
  let passed = 0; const pass = () => passed++;
  try {
    const root = await rootAdminToken(browser);
    const names = ['pia', 'ned'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('pv-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tk = {}, id = {}, pid = {};
    for (const n of names) {
      tk[n] = await memberToken(browser, members[n]);
      const saved = await call(tk[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Pv ' + n, homeCityId: 2078025 } });
      assert.equal(saved.status, 200); pid[n] = saved.body.publicId;
      id[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tk[n]).sub}'`);
      await call(tk[n], '/api/me/modes/friends', { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tk[n]);
    }
    // Synthetic approved library photos (as the mode-photos suite does), placed on the profile.
    const jpeg = await synthetic(canvas, { kind: 'gradient', type: 'image/jpeg', seed: 530 }), hash = crypto.createHash('sha256').update(jpeg).digest('hex');
    const libraryPhoto = async account => {
      const media = crypto.randomUUID();
      await fs.mkdir(path.join(process.env.Media__Local__Path, 'photos'), { recursive: true });
      await fs.writeFile(path.join(process.env.Media__Local__Path, 'photos', media.replaceAll('-', '')), jpeg, { mode: 0o600 });
      await sql(`INSERT INTO media_items(id,account_id,status,object_key,sha256,upload_sha256,content_type,width,height,bytes,created_at,screen_attempts,known_content_checked,screener,screened_at) VALUES('${media}','${account}','Passed','photos/${media.replaceAll('-', '')}',decode('${hash}','hex'),decode('${hash}','hex'),'image/jpeg',640,480,${jpeg.length},now()-interval '2 hours',1,false,'synthetic-fixture',now())`);
      return media;
    };
    const place = (who, mediaIds) => call(tk[who], '/api/me/photos/placement', { method: 'PUT', body: { scope: 'friends', mediaIds, mainMediaId: mediaIds[0] ?? null } });
    const view = (base, who) => call(tk[who], '/api/me/verification/photo', { base });

    // ---- Off by default (the owned API): not available, and asking for a code is refused.
    let r = await call(tk.pia, '/api/me/verification/photo');
    assert.deepEqual([r.status, r.body.available, r.body.state], [200, false, 'none']); pass();
    assert.equal((await call(tk.pia, '/api/me/verification/photo/code', { method: 'POST', body: { date: today() } })).status, 503); pass();

    await extraApi({ Media__Screening__Simulate: 'uncertain', Media__Screening__Worker: 'true', Attachments__Scanning__Enabled: 'false', Verification__PhotoVerified__Enabled: 'true' });
    const at = (who, route, options = {}) => fetch(EXTRA + route, { ...options, headers: { Authorization: 'Bearer ' + tk[who], ...(options.body && !(options.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...(options.headers ?? {}) } })
      .then(async res => { const text = await res.text(); let body = null; try { body = text ? JSON.parse(text) : null; } catch { body = text; } return { status: res.status, body }; });
    const askCode = (who, date = today()) => at(who, '/api/me/verification/photo/code', { method: 'POST', body: JSON.stringify({ date }) });
    const send = async (who, code, seed) => {
      const form = new FormData();
      form.append('file', new Blob([await synthetic(canvas, { kind: 'gradient', type: 'image/jpeg', seed, label: 'FICTIONAL PAPER ' + code })], { type: 'image/jpeg' }), 'paper.jpg');
      form.append('code', code);
      return at(who, '/api/me/verification/photo', { method: 'POST', body: form });
    };

    // ---- A code needs a profile photo and today's date; it's four characters from the unambiguous alphabet.
    assert.deepEqual([(await askCode('pia')).status, (await askCode('pia')).body?.code], [409, 'photo-verification-needs-photo']); pass();
    const first = await libraryPhoto(id.pia), second = await libraryPhoto(id.pia);
    assert.equal((await place('pia', [first, second])).status, 200);
    assert.equal((await askCode('pia', '2020-01-01')).status, 400, 'not some other date'); pass();
    r = await askCode('pia');
    assert.equal(r.status, 200, JSON.stringify(r.body));
    assert.deepEqual([r.body.available, r.body.state, r.body.writtenDate, r.body.attemptsLeft], [true, 'issued', today(), 3]); pass();
    assert.match(r.body.code, /^[ACDEFHJKMNPQRTUVWXY3479]{4}$/); pass();
    const oldCode = r.body.code;
    const fresh = (await askCode('pia')).body.code;
    assert.equal((await send('pia', oldCode, 531)).body?.code, 'photo-verification-code', 'an old code is refused'); pass();
    assert.equal((await send('ned', fresh, 532)).body?.code, 'photo-verification-code', "someone else's code is refused"); pass();

    // ---- Sent: screened, then waiting for a person; never on a member link, and Pia can't ask for another code meanwhile.
    r = await send('pia', fresh, 533);
    assert.deepEqual([r.status, r.body.state, r.body.attemptsLeft], [200, 'submitted', 2]); pass();
    assert.equal((await send('pia', fresh, 534)).body?.code, 'photo-verification-code', 'single use'); pass();
    assert.equal((await askCode('pia')).body?.code, 'photo-verification-pending'); pass();
    const evidence = await sql(`SELECT media_id FROM photo_verification_attempts WHERE account_id = '${id.pia}' AND state = 'Submitted'`);
    assert.ok(await waitFor(async () => (await sql(`SELECT object_key FROM media_items WHERE id = '${evidence}'`)).startsWith('verification/')), 'kept in its own place'); pass();
    assert.equal(await sql(`SELECT purpose || '|' || status FROM media_items WHERE id = '${evidence}'`), 'verification|Uncertain'); pass();
    assert.ok(!(await call(tk.pia, '/api/me/photos')).body.library?.some?.(p => p.id === evidence), 'not in the library'); pass();

    // ---- Support reviews it: the evidence link, the code and date, and the profile photos she had.
    const staff = await createStaff('pv-sup');
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: staff.email, role: 'support' } })).status, 201);
    const sup = await staffToken(browser, staff); await call(sup, '/api/session');
    let queue = (await call(sup, '/api/admin/photo-verification')).body;
    let item = queue.find(i => i.accountId === id.pia);
    assert.deepEqual([item?.code, item?.writtenDate, item?.profilePhotos.map(p => p.id).sort(), item?.profilePhotosChanged], [fresh, today(), [first, second].sort(), false]); pass();
    assert.match(item.evidence, /\.verification\./); pass();
    assert.equal((await fetch(`${api}/${item.evidence}`, { headers: { Authorization: 'Bearer ' + sup } })).status, 200, 'the staff link serves it to its reviewer'); pass();
    assert.equal((await fetch(`${api}/${item.evidence}`)).status, 401, 'never without a staff session'); pass();
    assert.equal((await fetch(`${api}/${item.evidence}`, { headers: { Authorization: 'Bearer ' + tk.ned } })).status, 403, 'never to a member'); pass();
    const forged = item.evidence.replace('.verification.', '.member.');
    assert.notEqual((await fetch(`${api}/${forged}`)).status, 200, 'never as a member photo'); pass();
    assert.equal((await call(tk.pia, '/api/admin/photo-verification')).status, 403, "members can't see the queue"); pass();

    // ---- A stale approval is refused: a profile photo changed after she sent it.
    assert.equal((await place('pia', [first])).status, 200);
    queue = (await call(sup, '/api/admin/photo-verification')).body; item = queue.find(i => i.accountId === id.pia);
    assert.equal(item.profilePhotosChanged, true); pass();
    assert.equal((await call(sup, `/api/admin/photo-verification/${item.id}/decision`, { method: 'POST', body: { decision: 'approve' } })).body?.code, 'photo-verification-stale'); pass();
    assert.equal((await call(sup, `/api/admin/photo-verification/${item.id}/decision`, { method: 'POST', body: { decision: 'reject', reason: 'made-up' } })).status, 400); pass();
    assert.equal((await call(sup, `/api/admin/photo-verification/${item.id}/decision`, { method: 'POST', body: { decision: 'reject', reason: 'profile-photos-changed' } })).status, 200); pass();
    r = await view(undefined, 'pia');
    assert.deepEqual([r.body.state, r.body.reason, r.body.attemptsLeft], ['rejected', 'Your profile photos changed after you sent it. Send a new one.', 2]); pass();

    // ---- Again, approved: Photo Verified for 12 months, the badge, and the date on the matched photo in the viewer.
    const again = (await askCode('pia')).body.code;
    assert.equal((await send('pia', again, 535)).body.state, 'submitted');
    item = (await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia);
    await waitFor(async () => !!(await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia));
    item = (await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia);
    assert.equal((await call(sup, `/api/admin/photo-verification/${item.id}/decision`, { method: 'POST', body: { decision: 'approve' } })).status, 200); pass();
    r = await view(undefined, 'pia');
    assert.equal(r.body.state, 'verified'); pass();
    const months = (new Date(r.body.verifiedUntil) - new Date(r.body.verifiedAt)) / 86400000;
    assert.ok(months >= 364 && months <= 366, '12 months'); pass();
    assert.equal(await sql(`SELECT photo_status || '|' || photo_manual || '|' || id_status FROM member_profiles WHERE account_id = '${id.pia}'`), 'Verified|false|NotStarted', 'never ID Verified'); pass();
    const shown = (await call(tk.ned, `/api/profiles/${pid.pia}?mode=friends`)).body;
    assert.ok(shown.badges.some(b => b.badge === 'photoVerified'), 'the badge shows'); pass();
    const friends = shown.modes.find(m => m.id === 'friends'), photos = [...(friends.photos ?? []), friends.mainPhoto, shown.photo].filter(Boolean);
    assert.ok(photos.some(p => p.id === first && p.photoVerifiedAt), 'the matched photo carries the date'); pass();

    // ---- Changing a matched public photo ends it.
    const third = await libraryPhoto(id.pia);
    assert.equal((await place('pia', [third])).status, 200);
    assert.equal(await sql(`SELECT photo_status FROM member_profiles WHERE account_id = '${id.pia}'`), 'Expired'); pass();
    assert.equal(await sql(`SELECT count(*) FROM photo_verification_matches WHERE account_id = '${id.pia}'`), '0'); pass();

    // ---- Three sends in 30 days.
    const third_code = (await askCode('pia')).body;
    assert.equal(third_code.attemptsLeft, 1);
    assert.equal((await send('pia', third_code.code, 536)).body.state, 'submitted');
    const last = (await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia)
      ?? (await waitFor(async () => !!(await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia)), (await call(sup, '/api/admin/photo-verification')).body.find(i => i.accountId === id.pia));
    assert.equal((await call(sup, `/api/admin/photo-verification/${last.id}/decision`, { method: 'POST', body: { decision: 'reject', reason: 'photo-unclear' } })).status, 200);
    assert.equal((await askCode('pia')).body?.code, 'photo-verification-limit'); pass();

    // ---- Retention: evidence 30 days after the decision is deleted; the decision record stays.
    await sql(`UPDATE photo_verification_attempts SET decided_at = now() - interval '31 days' WHERE account_id = '${id.pia}' AND decided_at IS NOT NULL`);
    await cli('retention', 'purge');
    assert.equal(await sql(`SELECT count(*) FROM photo_verification_attempts WHERE account_id = '${id.pia}' AND decided_at IS NOT NULL AND evidence_deleted_at IS NULL`), '0'); pass();
    assert.equal(await sql(`SELECT count(*) FROM media_items WHERE account_id = '${id.pia}' AND purpose = 'verification' AND object_key IS NOT NULL`), '0'); pass();
    assert.equal(await sql(`SELECT count(*) FROM photo_verification_attempts WHERE account_id = '${id.pia}' AND state IN ('Approved', 'Rejected')`), '3', 'decision records kept'); pass();
    console.log(`PASS: Photo Verified (issue 530), ${passed} checks.`);
  } finally {
    await stopExtra();
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
