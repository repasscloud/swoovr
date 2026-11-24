// Issue 404: a closed support request. Real member web app and sign-in; the support API's answers are stand-ins in the
// browser (the owned hosts have no helpdesk), so this covers the page, not the helpdesk. Fictional adult.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { web, createMember, memberToken, cli, call, matchReady } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const member = await createMember('support-closed');
    await cli('beta', 'add', member.email);
    const token = await memberToken(browser, member);
    await call(token, '/api/me/profile', { method: 'PUT', body: { displayName: 'Fictional Support', homeCityId: 2078025 } });
    await matchReady(token);
    const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
    await page.route(/\/api\/(me\/)?(support\/)?tickets/, route => {
      const url = route.request().url(), json = body => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
      if (/options/.test(url)) return json({ states: [{ id: 2, name: 'open' }, { id: 4, name: 'closed' }], priorities: [], attachmentPolicy: '' });
      return json({ ticket: { id: 4, reference: 'TK-4', title: 'SMS code not being received?', stateId: 4, priorityId: 2, updatedAt: '2026-11-22T10:24:00Z' }, closed: true, canReply: false,
        articles: [{ id: 1, body: 'I cannot receive an SMS code to complete account setup?', private: false, sender: 'Customer', createdAt: '2026-11-22T10:15:00Z', attachments: [], held: [] },
          { id: 2, body: 'We have identified the issue.', private: false, sender: 'Agent', createdAt: '2026-11-22T10:24:00Z', attachments: [], held: [] }] });
    });
    await page.goto(web + '/account/sign-in?intro=done');
    await page.fill('#username', member.email); await page.fill('#password', member.password);
    await Promise.all([page.waitForURL(web + '/explore'), page.click('#kc-login')]);
    for (const theme of ['light', 'dark']) for (const width of [1280, 390]) {
      await page.emulateMedia({ colorScheme: theme });
      await page.setViewportSize({ width, height: 900 });
      await page.goto(web + '/me/support/4');
      await page.waitForSelector('[data-support-closed]', { timeout: 20000 });
      assert.equal(await page.locator('[data-support-reply-body]').count(), 0); pass();
      const note = await page.locator('[data-support-closed]').boundingBox(), button = await page.locator('[data-support-again]').boundingBox();
      assert.ok(button.y >= note.y + note.height + 12, 'the button is below the note, outside it'); pass();
      assert.ok(button.width < note.width * 0.75, 'the button is never full width'); pass();
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'no sideways scroll'); pass();
      if (output) await page.screenshot({ path: `${output}/support-closed-${theme}-${width}.png` });
    }
    assert.equal(await page.locator('[data-support-closed] a').getAttribute('href'), '/me/standing'); pass();
    console.log(`PASS: closed support request page, ${passed} checks (stand-in support API; no helpdesk).`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
