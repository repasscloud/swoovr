// Issue 546 (D423, D428): adult photos sent straight to chat. Screening always runs; for a pair where it may be delivered
// (sender and recipient ID Verified, recipient's Receive explicit media on for a mode they're connected in) a photo the
// automatic check flags for nudity is rated explicit and delivered without routine review. Otherwise it waits in review
// with the Chat destination; staff may approve it as adult content, and then it reaches the recipient only if they may
// receive it now. A refused explicit photo never reaches the recipient (no message, count or notification); the sender
// sees a neutral notice. Each view checks again (182.4). Labelled simulated screening on an owned extra API; synthetic
// images and fictional adults only.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises'), path = require('node:path'), os = require('node:os'), net = require('node:net'), { once } = require('node:events');
const { spawn } = require('node:child_process');
if (!process.env.SWOOVR_TEST_APP_DATABASE || !process.env.ConnectionStrings__Swoovr || !process.env.SWOOVR_OWNED_RUN_MANIFEST) throw Error('Explicit chat photos require the owned runner.');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { sleep, createMember, createStaff, memberToken, staffToken, rootAdminToken, cli, sql, call, claims, matchReady } = require('../auth/local-tokens.cjs');
const { synthetic, upload } = require('../media/synthetic-images.cjs');

const ADELAIDE = 2078025;
let EXTRA, extraProcess, extraFolder;
async function extraApi(env) {
  const manifest = JSON.parse(await fs.readFile(process.env.SWOOVR_OWNED_RUN_MANIFEST, 'utf8'));
  const probe = net.createServer(); await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve));
  EXTRA = 'http://127.0.0.1:' + probe.address().port; await new Promise(resolve => probe.close(resolve));
  extraFolder = await fs.mkdtemp(path.join(os.tmpdir(), 'swoovr-explicit-extra-')); await fs.chmod(extraFolder, 0o700);
  const log = await fs.open(path.join(extraFolder, 'api.log'), 'w', 0o600);
  extraProcess = spawn('dotnet', [manifest.apiDll], { cwd: path.resolve('src/Swoovr.Api'), env: { ...process.env, ...env, ASPNETCORE_URLS: EXTRA }, stdio: ['ignore', log.fd, log.fd] }); await log.close();
  for (let n = 0; n < 150; n++) {
    if (extraProcess.exitCode !== null || extraProcess.signalCode !== null) throw Error('Owned explicit-chat API exited; inspect private log ' + extraFolder);
    try { if ((await fetch(EXTRA + '/health/live', { signal: AbortSignal.timeout(1000) })).ok) return; } catch {}
    await sleep(100);
  }
  throw Error('Owned explicit-chat API readiness timeout.');
}
async function stopExtra() {
  if (extraProcess && extraProcess.exitCode === null && extraProcess.signalCode === null) {
    const child = extraProcess, exited = once(child, 'exit'); child.kill('SIGTERM'); await Promise.race([exited, sleep(5000)]);
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await exited; }
  }
  extraProcess = null; if (extraFolder) { await fs.rm(extraFolder, { recursive: true, force: true }); extraFolder = null; }
}
const waitFor = async (predicate, ms = 60000) => { for (let i = 0; i < ms / 500; i++) { if (await predicate()) return true; await sleep(500); } return predicate(); };

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const canvas = await browser.newPage();
  let passed = 0; const pass = () => passed++;
  try {
    const root = await rootAdminToken(browser);
    // snd sends (ID Verified); yes: ID Verified and opted in; off: ID Verified, not opted in; noid: not ID Verified;
    // unv: a sender who isn't ID Verified.
    const names = ['snd', 'yes', 'off', 'noid', 'unv'];
    const members = Object.fromEntries(await Promise.all(names.map(async n => [n, await createMember('xc-' + n)])));
    await cli('beta', 'add', ...Object.values(members).map(m => m.email));
    const tk = {}, id = {}, pid = {};
    for (const n of names) {
      tk[n] = await memberToken(browser, members[n]);
      const saved = await call(tk[n], '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Xc ' + n, homeCityId: ADELAIDE } });
      assert.equal(saved.status, 200, n); pid[n] = saved.body.publicId;
      id[n] = await sql(`SELECT id FROM accounts WHERE subject = '${claims(tk[n]).sub}'`);
      for (const mode of ['friends', 'activities']) await call(tk[n], `/api/me/modes/${mode}`, { method: 'PUT', body: { enabled: true, visible: true } });
      await matchReady(tk[n]);
    }
    for (const n of ['snd', 'yes', 'off']) await sql(`UPDATE member_profiles SET id_status = 'Verified', id_checked_at = now() WHERE account_id = '${id[n]}'`); // simulated for this local test
    const connect = async (from, to, mode = 'friends') => {
      const r = await call(tk[from], '/api/requests', { method: 'POST', body: { to: pid[to], mode } });
      assert.equal(r.status, 201, `${from}->${to} ${JSON.stringify(r.body)}`);
      assert.equal((await call(tk[to], `/api/requests/${r.body.id}/accept`, { method: 'POST' })).status, 200);
    };
    for (const other of ['yes', 'off', 'noid']) await connect('snd', other);
    await connect('unv', 'yes');
    const start = async (who, to) => (await call(tk[who], '/api/conversations', { method: 'POST', body: { to: pid[to] } })).body.id;
    const c = { yes: await start('snd', 'yes'), off: await start('snd', 'off'), noid: await start('snd', 'noid'), unv: await start('unv', 'yes') };
    const list = async (who, conversation) => (await call(tk[who], `/api/conversations/${conversation}/messages`)).body?.items ?? [];
    const unread = async (who, conversation) => (await call(tk[who], '/api/conversations')).body.find(x => x.id === conversation)?.unread ?? 0;

    // Receive explicit media: only for ID Verified members (unchanged, D389); on for the mode they're connected in.
    assert.equal((await call(tk.noid, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'friends', receive: true } })).status, 403); pass();
    assert.equal((await call(tk.yes, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'friends', receive: true } })).status, 200); pass();
    // "off" opts in only for a mode they aren't connected in with snd: it doesn't count (D428).
    assert.equal((await call(tk.off, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'activities', receive: true } })).status, 200); pass();

    const staff = await createStaff('xc-adm');
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: staff.email, role: 'support' } })).status, 201);
    const sup = await staffToken(browser, staff); await call(sup, '/api/session');
    assert.equal((await call(sup, '/api/admin/media/safety-ack', { method: 'POST', body: { version: '2026-11-02', confirmed: true } })).status, 200);
    const decide = (media, decision, reasonCode) => call(sup, `/api/admin/media/${media}/decision`, { method: 'POST', body: { decision, reasonCode } });

    // Labelled simulations, owned extra API only: screening returns "uncertain, nudity" for every photo, and the malware scan
    // is the Development-only disabled scanner (the owned runner has no ClamAV on the host). Malware scanning is covered by
    // its own checks; this suite is about who receives what.
    await extraApi({ Media__Screening__Simulate: 'uncertain', Media__Screening__Worker: 'true', Attachments__Scanning__Enabled: 'false' });
    let seed = 600;
    const send = async (who, conversation) => {
      const r = await upload(`${EXTRA}/api/conversations/${conversation}/photos`, await synthetic(canvas, { kind: 'gradient', type: 'image/jpeg', seed: seed++ }), { token: tk[who] });
      assert.equal(r.status, 202, JSON.stringify(r.body));
      return { message: r.body.id, media: await sql(`SELECT media_id FROM messages WHERE id = '${r.body.id}'`) };
    };
    const state = async (who, conversation, message) => (await list(who, conversation)).find(m => m.id === message);

    // ---- Eligible pair: rated explicit and delivered without review; it opens blurred (Explicit) for both.
    const auto = await send('snd', c.yes);
    assert.ok(await waitFor(async () => (await state('snd', c.yes, auto.message))?.photoState === 'approved'), 'delivered without review'); pass();
    assert.equal(await sql(`SELECT status || '|' || content_rating FROM media_items WHERE id = '${auto.media}'`), 'Passed|Explicit'); pass();
    assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action = 'media.explicit-auto' AND target_id = '${auto.media}'`), '1'); pass();
    let seen = await state('yes', c.yes, auto.message);
    assert.deepEqual([seen?.explicit, !!seen?.photo?.link], [true, true]); pass();
    assert.equal((await state('snd', c.yes, auto.message)).explicit, true); pass();
    assert.ok(!(await call(sup, '/api/admin/media/review?kind=attachment')).body.items.some(i => i.id === auto.media), 'never queued'); pass();

    // ---- Opting out after delivery stops it loading (182.4); turning it back on brings it back.
    await call(tk.yes, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'friends', receive: false } });
    seen = await state('yes', c.yes, auto.message);
    assert.deepEqual([seen?.explicit, seen?.photo ?? null], [true, null]); pass();
    assert.ok((await state('snd', c.yes, auto.message)).photo, 'the sender still sees their own'); pass();
    await call(tk.yes, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'friends', receive: true } });
    assert.ok((await state('yes', c.yes, auto.message))?.photo); pass();

    // ---- Recipient not opted in for a connected mode: review with the Chat destination; never reaches them.
    const unreadBefore = await unread('off', c.off);
    const held = await send('snd', c.off);
    assert.ok(await waitFor(async () => (await state('snd', c.off, held.message))?.photoState === 'in-review')); pass();
    const queued = (await call(sup, '/api/admin/media/review?kind=attachment')).body.items.find(i => i.id === held.media);
    assert.deepEqual([queued?.destination, queued?.ownerIdVerified], ['chat', true]); pass();
    assert.equal((await decide(held.media, 'approve', 'APPROVED_CHAT_EXPLICIT')).status, 200); pass();
    assert.ok(await waitFor(async () => (await state('snd', c.off, held.message))?.photoState === 'not-accepted'), 'the sender is told, neutrally'); pass();
    assert.ok((await state('snd', c.off, held.message)).photo, 'and can still see their own photo'); pass();
    assert.equal(await state('off', c.off, held.message), undefined, 'no message for the recipient'); pass();
    assert.equal(await unread('off', c.off), unreadBefore, 'no unread count'); pass();
    assert.equal(await sql(`SELECT count(*) FROM notifications WHERE account_id = '${id.off}' AND kind = 'Message'`), '0', 'no notification'); pass();
    // Opting in later doesn't deliver a refused photo.
    await call(tk.off, '/api/me/explicit-media', { method: 'PUT', body: { mode: 'friends', receive: true } });
    assert.equal(await state('off', c.off, held.message), undefined); pass();

    // ---- Recipient not ID Verified: an approved standard false positive is delivered normally, with no explicit flag.
    const plain = await send('snd', c.noid);
    assert.ok(await waitFor(async () => (await state('snd', c.noid, plain.message))?.photoState === 'in-review')); pass();
    assert.equal((await decide(plain.media, 'approve', 'APPROVED_FALSE_POSITIVE')).status, 200);
    assert.ok(await waitFor(async () => !!(await state('noid', c.noid, plain.message))?.photo), 'delivered'); pass();
    assert.equal((await state('noid', c.noid, plain.message)).explicit ?? null, null); pass();

    // ---- A sender who isn't ID Verified: never auto-rated, and staff can't approve it as adult content.
    const unverified = await send('unv', c.unv);
    assert.ok(await waitFor(async () => (await state('unv', c.unv, unverified.message))?.photoState === 'in-review'), 'not delivered automatically'); pass();
    assert.equal((await decide(unverified.media, 'approve', 'APPROVED_CHAT_EXPLICIT')).status, 409); pass();
    assert.equal(await sql(`SELECT content_rating FROM media_items WHERE id = '${unverified.media}'`), 'Standard'); pass();
    assert.equal((await decide(unverified.media, 'reject', 'REJECT_EXPLICIT_NO_ID')).status, 200); pass();

    // ---- The export: the recipient's chat lists the explicit photo by id and rating, never a refused one.
    const exportOf = async who => { const r = await call(tk[who], '/api/me/export', { method: 'POST' }); return (await fetch(`${process.env.SWOOVR_TEST_API_BASE}/${r.body.path}`, { headers: { Authorization: 'Bearer ' + tk[who] } })).json(); };
    const yesExport = await exportOf('yes');
    const exportedPhoto = yesExport.conversations.flatMap(x => x.messages).find(m => m.photo === auto.media);
    assert.deepEqual([exportedPhoto?.photoRating, exportedPhoto?.dataBase64], ['explicit', undefined]); pass();
    const offExport = await exportOf('off');
    assert.ok(!offExport.conversations.flatMap(x => x.messages).some(m => m.photo === held.media), 'a refused photo is not in their export'); pass();
    console.log(`PASS: explicit chat photos (issue 546), ${passed} checks.`);
  } finally {
    await stopExtra();
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
