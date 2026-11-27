// Issue 587: the Moderation pages share one look: Appeals, Date of birth reviews and Name changes use the page head, stats
// and tables of /media; no bare "0 waiting / Oldest first" line; the shorter DOB intro. Fictional staff only.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const { admin, createStaff, rootAdminToken, fillStaffOtp, call } = require('../auth/local-tokens.cjs');
const output = process.env.SWOOVR_SCREENSHOTS;

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let passed = 0; const pass = () => passed++;
  try {
    const root = await rootAdminToken(browser);
    const adm = await createStaff('modpages-adm');
    assert.equal((await call(root, '/api/admin/invitations', { method: 'POST', body: { email: adm.email, role: 'admin' } })).status, 201);
    const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
    await page.goto(admin + '/sign-in'); await page.fill('#username', adm.email); await page.fill('#password', adm.password); await page.click('#kc-login');
    await fillStaffOtp(page, adm); await page.waitForURL(u => u.href.startsWith(admin) && !u.href.includes('/realms/'), { timeout: 30000 });
    for (const route of ['/appeals', '/dob-reviews', '/name-reviews', '/media']) {
      await page.goto(admin + route); await page.waitForSelector('main .ui-page-head h1');
      assert.equal(await page.locator('main .ui-page-head h1').count(), 1, route + ' page head'); pass();
      if (route !== '/media') { assert.ok(await page.locator('main .ui-stats').count() >= 1, route + ' stats'); pass(); }
      assert.equal(await page.locator('main').getByText('Oldest first', { exact: true }).count(), 0, route + ' no "Oldest first"'); pass();
      assert.equal(await page.locator('main p.help').count(), 0, route + ' no old help style'); pass();
      if (output) await page.screenshot({ path: `${output}/moderation${route.replace('/', '-')}-1440.png`, fullPage: true });
    }
    await page.goto(admin + '/dob-reviews');
    assert.equal(await page.locator('main .ui-page-head p').innerText(), 'ID checks that need a date of birth decision.'); pass();
    await page.goto(admin + '/appeals');
    assert.match(await page.locator('main .ui-page-head p').innerText(), /^Decided by someone other than the original decider\./); pass();
    console.log(`PASS: admin Moderation pages (issue 587), ${passed} checks.`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
