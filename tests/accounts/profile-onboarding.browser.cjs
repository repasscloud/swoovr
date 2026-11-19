// Issue 389 (D382): the first-sign-in profile steps after account setup, on the real owned Keycloak/API/BFF.
// Fictional adults only; no production data, no simulated provider acceptance.
const assert=require('node:assert/strict');
const {chromium}=require(process.env.SWOOVR_PLAYWRIGHT_PATH||'playwright');
const {createMember,memberToken,cli,call,web,sql}=require('../auth/local-tokens.cjs');
if(!process.env.SWOOVR_TEST_API_BASE||!process.env.SWOOVR_TEST_APP_DATABASE)throw Error('Requires owned runner');
const ADELAIDE=2078025;
let passed=0;const pass=()=>passed++;
const signIn=async(context,member)=>{const page=await context.newPage();await page.goto(web+'/account/sign-in?intro=done');
  await page.fill('#username',member.email);await page.fill('#password',member.password);
  await Promise.all([page.waitForURL(web+'/welcome'),page.click('#kc-login')]);return page;};
(async()=>{
 const edge=await require('../auth/managed-edge.cjs').managedEdge(chromium);
 try{
  assert.equal(await sql(`SELECT count(*) FROM geo_cities WHERE geo_name_id=${ADELAIDE}`),'1','GeoNames fixture city');
  const member=await createMember('profile-onboarding');await cli('beta','add',member.email);
  const token=await memberToken(edge.browser,{...member,onboarded:false});
  assert.equal((await call(token,'/api/me/age',{method:'PUT',body:{dateOfBirth:'1991-03-04',confirmed:true}})).status,200);
  let me=await call(token,'/api/me');assert.equal(me.body.setupStep,'profile');pass();
  let state=await call(token,'/api/me/onboarding');
  assert.deepEqual(state.body.pending,['profile','about','photos','modes','identity']);pass();
  // The optional steps can't be skipped out of order, and nothing completes early.
  assert.equal((await call(token,'/api/me/onboarding/steps/photos',{method:'POST',body:{}})).status,409);
  assert.equal((await call(token,'/api/me/onboarding/complete',{method:'POST'})).status,409);pass();
  // Q181.5 A: contact details never reach a public tile.
  for(const text of ['call me 0412 345 678','me@example.test','insta @fictional_person'])
    assert.equal((await call(token,'/api/me/picture-text',{method:'PUT',body:{text}})).body?.code,'picture-text-contact',text);
  pass();

  const first=await edge.browser.newContext();
  try{
   const page=await signIn(first,member);
   for(const route of ['/explore','/me/profile','/connections']){await page.goto(web+route);await page.waitForURL(web+'/welcome');}
   await page.waitForSelector('[data-onboarding-step=profile]');
   assert.match(await page.locator('.onboarding-eyebrow').innerText(),/Step 1 of 5/i);
   // Q181.2 A: the registered first name, shown and editable.
   assert.equal(await page.inputValue('#onboarding-display-name'),'Fictional');pass();
   assert.match(await page.locator('[data-home-city-help]').innerText(),/Only you can see it.*different region.*30 days/s);pass();
   await page.fill('#onboarding-display-name','Fictional Robin');
   await page.fill('#onboarding-home-city','Adelaide');await page.click(`[data-city="${ADELAIDE}"]`);
   await page.click('[data-onboarding-step=profile] button[type=submit]');
   await page.waitForSelector('[data-onboarding-step=about]');
   await page.fill('#onboarding-about','Fictional bio for an acceptance test.');
   assert.equal(await page.locator('[data-about-count]').innerText(),'37/500');
   assert.equal(await page.isChecked('[data-onboarding-share-about]'),true,'consent box ticked on this screen');pass();
   await page.click('[data-onboarding-step=about] button[type=submit]');
   await page.waitForSelector('[data-onboarding-step=photos]');
   assert.match(await page.locator('[data-photo-allowance]').innerText(),/up to 10/);pass();
  }finally{await first.close();}

  // Resume: a new session lands on the first step not done.
  me=await call(token,'/api/me');assert.equal(me.body.setupStep,'photos');
  const second=await edge.browser.newContext();
  try{
   const page=await signIn(second,member);
   await page.waitForSelector('[data-onboarding-step=photos]');assert.match(await page.locator('.onboarding-eyebrow').innerText(),/Step 3 of 5/i);pass();
   // The "no photos" branch: the member's own words as a text tile, previewed before saving.
   await page.click('[data-onboarding-words-instead]');
   await page.waitForSelector('[data-onboarding-step=picture-text]');
   await page.fill('#onboarding-picture-text','Coffee first, then mountains.');
   assert.match(await page.locator('[data-picture-text-preview]').innerText(),/Coffee first, then mountains\./);pass();
   assert.match(await page.locator('[data-onboarding-step=picture-text]').innerText(),/doesn't use AI/);pass();
   await page.click('[data-onboarding-step=picture-text] button[type=submit]');
   await page.waitForSelector('[data-onboarding-step=modes]');
   await page.check('[data-onboarding-mode=dating] input');await page.check('[data-onboarding-mode=friends] input');
   await page.click('[data-onboarding-step=modes] button[type=submit]');
   await page.waitForSelector('[data-onboarding-step=identity]');
   assert.equal(await page.locator('[data-gender-required]').count(),1,'Dating needs a gender');
   // 181.8 A: identity can't be continued past without a gender while Dating is on.
   assert.equal((await call(token,'/api/me/onboarding/steps/identity',{method:'POST',body:{}})).status,409);pass();
   assert.equal(await page.isChecked('[data-show=pronouns]'),true);assert.equal(await page.isChecked('[data-show=orientation]'),false);pass();
   await page.selectOption('#onboarding-gender','trans-woman');
   await page.selectOption('#onboarding-pronouns','self');await page.fill('#onboarding-pronouns-own','xe/xem');
   assert.equal(await page.inputValue('#onboarding-orientation'),'','orientation Not set by default');
   await page.click('[data-onboarding-step=identity] button[type=submit]');
   await page.waitForSelector('[data-onboarding-step=datingShowMe]');
   const choices=await page.locator('[data-onboarding-show-me]').evaluateAll(n=>n.map(e=>e.dataset.onboardingShowMe));
   assert.deepEqual(choices,['man','woman','non-binary','another'],'Q181.1 A: show me keeps four choices');pass();
   await page.check('[data-onboarding-show-me=man] input');
   await Promise.all([page.waitForURL(web+'/explore?mode=dating'),page.click('[data-onboarding-step=datingShowMe] button[type=submit]')]);pass();
   await page.goto(web+'/me/profile');assert.equal(new URL(page.url()).pathname,'/me/profile','onboarding no longer redirects');pass();
  }finally{await second.close();}

  me=await call(token,'/api/me');assert.equal(me.body.setupStep,null);
  state=await call(token,'/api/me/onboarding');assert.ok(state.body.completed);assert.deepEqual(state.body.pending,[]);pass();
  const identity=(await call(token,'/api/me/identity')).body;
  assert.equal(identity.gender,'trans-woman');assert.equal(identity.pronounId,'self');assert.equal(identity.pronouns,'xe/xem');
  assert.equal(identity.showPronouns,true);assert.equal(identity.orientation,null);assert.deepEqual(identity.datingShowMe,['man']);pass();
  const profile=(await call(token,'/api/me/profile')).body;
  assert.equal(profile.displayName,'Fictional Robin');assert.equal(profile.homeCity.id,ADELAIDE);assert.equal(profile.pictureText,'Coffee first, then mountains.');
  const dating=profile.modes.find(m=>m.id==='dating');assert.equal(dating.enabled,true);assert.equal(dating.shareCoreAbout,true,'Q181.4 A consent applied');pass();
  // PR #392 review: a member who left after the last save but before /complete succeeded (or whose profile already
  // had every step) is recorded as complete on their next visit, so clearing data later can't reopen onboarding.
  const account=(await sql(`SELECT account_id FROM member_profiles WHERE display_name='Fictional Robin'`)).trim();
  await sql(`UPDATE member_profiles SET onboarding_completed_at=NULL WHERE account_id='${account}'`);
  assert.equal((await call(token,'/api/me')).body.setupStep,null);
  assert.equal(await sql(`SELECT count(*) FROM member_profiles WHERE account_id='${account}' AND onboarding_completed_at IS NOT NULL`),'1','flag persisted without /complete');
  assert.equal(await sql(`SELECT count(*) FROM audit_entries WHERE action='onboarding.completed' AND target_id='${account}'`),'2','one audit entry per completion');pass();
  // PR #392 review: the about step's "every mode" answer applies to modes already on (dating, friends here), both ways.
  const shared=v=>sql(`SELECT count(*) FROM member_modes WHERE account_id='${account}' AND enabled AND share_core_about=${v}`);
  assert.equal((await call(token,'/api/me/onboarding/steps/about',{method:'POST',body:{shareAbout:false}})).status,200);
  assert.equal(await shared('false'),'2','unticking hides the bio in existing modes');
  // PR #392 review: the consent change is captured as a profile version (profile-at-report shows what others saw).
  const hidden=m=>sql(`SELECT content_json::jsonb->>'shownAbout' IS NULL FROM profile_versions WHERE account_id='${account}' AND mode_id='${m}' ORDER BY captured_at DESC LIMIT 1`);
  assert.equal(await hidden('dating'),'t');assert.equal(await hidden('friends'),'t','latest version shows the bio hidden');
  assert.equal((await call(token,'/api/me/onboarding/steps/about',{method:'POST',body:{shareAbout:true}})).status,200);
  assert.equal(await shared('true'),'2','ticking shows the bio in existing modes');
  assert.equal(await hidden('dating'),'f','latest version shows the bio again');pass();
  // PR #392 review: a client without the pronoun switch (the current app) hides pronouns with "Show my gender and pronouns" off.
  const older={gender:'trans-woman',pronouns:'xe/xem',orientation:null,showGender:false,showOrientation:false,showAge:true,datingShowMe:['man']};
  assert.equal((await call(token,'/api/me/identity',{method:'PUT',body:older})).body.showPronouns,false);
  assert.equal((await call(token,'/api/me/identity',{method:'PUT',body:{...older,showGender:true}})).body.showPronouns,false,'turning it on keeps the switch');
  assert.equal((await call(token,'/api/me/identity',{method:'PUT',body:{...older,showGender:true,showPronouns:true}})).body.showPronouns,true);pass();
  // Once complete, clearing a home city never sends the member back to onboarding.
  await call(token,'/api/me/profile',{method:'PUT',body:{displayName:'Fictional Robin',about:profile.about}});
  assert.equal((await call(token,'/api/me')).body.setupStep,null);pass();
  // PR #392 review: after onboarding the text picture is still editable and removable on /me/profile.
  const third=await edge.browser.newContext();
  try{
   const page=await third.newPage();await page.goto(web+'/account/sign-in?intro=done');
   await page.fill('#username',member.email);await page.fill('#password',member.password);
   await Promise.all([page.waitForURL(u=>u.href.startsWith(web)&&!u.pathname.startsWith('/account/sign-in')),page.click('#kc-login')]);
   await page.goto(web+'/me/profile');await page.waitForSelector('[data-picture-text-settings]');
   assert.equal(await page.inputValue('#picture-text'),'Coffee first, then mountains.');
   await page.fill('#picture-text','call me 0412 345 678');await page.click('[data-picture-text-save]');
   await page.waitForSelector('[data-picture-text-status=picture-text-contact]');pass();
   await page.fill('#picture-text','Mountains first, then coffee.');await page.click('[data-picture-text-save]');
   await page.waitForSelector('[data-picture-text-status=saved]');
   assert.equal((await call(token,'/api/me/profile')).body.pictureText,'Mountains first, then coffee.');
   await page.click('[data-picture-text-remove]');await page.waitForSelector('[data-picture-text-remove]',{state:'detached'});
   assert.equal(await page.locator('[data-picture-text-status]').innerText(),'Removed.');
   assert.equal((await call(token,'/api/me/profile')).body.pictureText??null,null);pass();
  }finally{await third.close();}
  console.log(`PASS: ${passed} first sign-in profile onboarding checks (gate, pre-filled name, accurate home-city wording, about with consent, resume, text picture with contact refusal and preview, modes, Dating gender rule, pronoun Self-describe, show-me choices, redirect to the first enabled mode, completion flag, completion persisted without /complete, every-mode bio answer on existing modes with profile versions, older-client pronoun switch, text picture edit and removal on /me/profile).`);
 }finally{await edge.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
