// D384 actual-browser acceptance against the managed Docker stack (./scripts/local-platform.sh up):
// a photo upload is malware-scanned by the shared ClamAV before decoding (EICAR refused), a clean synthetic photo is
// screened by services/image-moderation, and the admin Overview shows the Image processing panel.
// Needs installed Edge and Playwright (SWOOVR_PLAYWRIGHT_PATH). Fictional members and synthetic images only; the
// photo is deleted again. Screenshots: .local/platform/image-processing-overview*.png.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { managedEdge } = require('../auth/managed-edge.cjs');
const { synthetic } = require('../media/synthetic-images.cjs');
// The standard EICAR anti-virus test file (harmless by design).
const EICAR = 'X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*';
const WEB = 'http://localhost:5081', ADMIN = 'http://localhost:5082', BFF = { 'X-Swoovr-Bff': '1' };
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
  const { parseEnv } = await import('../../scripts/local/platform.mjs');
  const { totp } = await import('../../scripts/auth/totp.mjs');
  const env = parseEnv(await fs.readFile('.local/platform/docker.env', 'utf8'));
  const fixture = JSON.parse(await fs.readFile('.local/platform/admin.json', 'utf8'));
  const edge = await managedEdge(chromium);
  try {
    const members = await edge.browser.newContext();
    const member = await members.newPage();
    await member.goto(WEB + '/account/sign-in?intro=done');
    await member.waitForSelector('#username');
    await member.fill('#username', process.env.SWOOVR_IMAGE_CHECK_MEMBER || 'fictional.alex@example.test');
    await member.fill('#password', env.SWOOVR_LOCAL_MEMBER_PASSWORD);
    await member.click('#kc-login');
    await member.waitForLoadState();
    if (await member.locator('[name=accept]').count()) await member.locator('[name=accept]').click();
    await member.waitForURL(url => url.origin === WEB, { timeout: 30000 });
    // First sign-in on a fresh local stack: verify a fictional mobile (ACMA 0491 570 test range); the code goes to Mailpit.
    const phone = await (await members.request.get(WEB + '/bff/api/me/phone', { headers: BFF })).json();
    if (!phone.verified && !phone.phoneVerified) {
      const number = '+61491570' + String(100 + Math.floor(Math.random() * 800));
      const since = Date.now();
      const started = await members.request.post(WEB + '/bff/api/me/phone', { headers: BFF, data: { number } });
      assert.ok(started.ok(), 'fictional mobile verification started: ' + started.status() + ' ' + await started.text());
      let code;
      for (let i = 0; i < 30 && !code; i++) {
        await sleep(1000);
        const list = await (await fetch('http://127.0.0.1:8025/api/v1/messages?limit=10')).json();
        for (const m of list.messages ?? []) {
          if (Date.parse(m.Created) < since - 5000) continue;
          const text = (await (await fetch('http://127.0.0.1:8025/api/v1/message/' + m.ID)).json()).Text ?? '';
          code = text.match(/\b(\d{6})\b/)?.[1]; if (code) break;
        }
      }
      assert.ok(code, 'verification code found in Mailpit');
      const confirmed = await members.request.post(WEB + '/bff/api/me/phone/confirm', { headers: BFF, data: { number, code } });
      assert.ok(confirmed.ok(), 'fictional mobile verified: ' + confirmed.status() + ' ' + await confirmed.text());
    }
    // Fictional fixture only (as tests/auth/local-tokens.cjs): confirm the registered names photo routes require.
    const names = await members.request.get(WEB + '/bff/api/me/registered-names', { headers: BFF });
    if (names.ok() && !(await names.json()).confirmed)
      assert.ok((await members.request.put(WEB + '/bff/api/me/registered-names', { headers: BFF, data: { firstName: 'Fictional', lastName: 'Example', mononym: false, expectedRevision: (await names.json()).revision, confirm: true } })).ok(), 'fictional registered names confirmed');
    const send = async (bytes, name, type) => members.request.post(WEB + '/bff/api/me/photos', { headers: BFF, multipart: { file: { name, mimeType: type, buffer: bytes } } });
    const refused = await send(Buffer.from(EICAR), 'eicar.jpg', 'image/jpeg');
    const refusedBody = await refused.text();
    assert.equal(refused.status(), 422, 'EICAR refused by the malware scan: ' + refused.status() + ' ' + refusedBody);
    assert.equal(JSON.parse(refusedBody).code, 'photo-refused');
    const accepted = await send(await synthetic(member, { kind: 'gradient', width: 640, height: 480, seed: 384, label: 'D384 CHECK' }), 'd384.png', 'image/png');
    assert.equal(accepted.status(), 202, 'clean synthetic photo accepted for screening: ' + await accepted.text());
    const photoId = (await accepted.json()).id;
    let state = 'checking';
    for (let i = 0; i < 90 && state === 'checking'; i++) {
      await sleep(2000);
      const mine = await (await members.request.get(WEB + '/bff/api/me/photos', { headers: BFF })).json();
      state = mine.photos.find(p => p.id === photoId)?.state ?? 'missing';
    }
    assert.equal(state, 'approved', 'the synthetic photo was screened and approved by image-moderation');

    const staff = await edge.browser.newContext();
    const page = await staff.newPage();
    await page.goto(ADMIN + '/');
    await page.fill('#username', env.SWOOVR_LOCAL_ADMIN_EMAIL);
    await page.fill('#password', env.SWOOVR_LOCAL_ADMIN_PASSWORD);
    await page.click('#kc-login');
    await page.waitForSelector('#otp');
    while (Math.floor(Date.now() / 30000) <= (fixture.lastOtpStep ?? -1)) await sleep(500);
    fixture.lastOtpStep = Math.floor(Date.now() / 30000);
    await fs.writeFile('.local/platform/admin.json', JSON.stringify(fixture, null, 2) + '\n', { mode: 0o600 });
    await page.fill('#otp', totp(fixture.totpSecret));
    await page.click('#kc-login');
    await page.waitForURL(ADMIN + '/', { timeout: 30000 });
    const panel = page.locator('[data-image-processing]');
    assert.equal(await panel.count(), 1, 'Image processing panel shown on the Overview');
    assert.equal(await panel.getAttribute('data-image-processing'), 'healthy');
    assert.equal(await page.locator('[data-screening-service]').innerText(), 'Answering');
    assert.equal(await page.locator('[data-malware-scanner]').innerText(), 'Answering');
    assert.ok(Number(await page.locator('[data-processing-passed]').innerText()) >= 1, 'the approved photo is counted');
    await page.setViewportSize({ width: 1280, height: 1000 });
    await panel.screenshot({ path: '.local/platform/image-processing-overview.png' });
    await page.setViewportSize({ width: 390, height: 1000 });
    await panel.screenshot({ path: '.local/platform/image-processing-overview-phone.png' });
    await staff.close();
    assert.ok((await members.request.delete(WEB + '/bff/api/me/photos/' + photoId, { headers: BFF })).ok(), 'test photo removed');
    await members.close();
    console.log('PASS: D384 photo malware refusal (EICAR), real screening to approved, and the Overview Image processing panel against Docker hosts.');
  } finally { await edge.close(); }
})().catch(e => { console.error(e.message); process.exitCode = 1; });
