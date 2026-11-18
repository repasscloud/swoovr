// #300 (D380): the recipient's optional unanswered-message limit, end to end on disposable owned resources. The API
// enforces it (Off by default, 3/5/10 only, the message at the limit sends, the next is refused with a neutral
// 409 unanswered-limit, one reply resets it, Off lifts it, other conversations are independent), and the web chat
// disables its composer at the limit and opens it again when the reply arrives live. Run with the explicit
// unanswered-limit selector in tests/auth/run-owned.cjs; every member is a fictional adult.
const assert = require('node:assert/strict');
const {chromium} = require(process.env.SWOOVR_PLAYWRIGHT_PATH || 'playwright');
const {managedEdge} = require('../auth/managed-edge.cjs');
const {web,ownedRunId,createMember,memberToken,cli,sql,call,claims,matchReady} = require('../auth/local-tokens.cjs');
assert(ownedRunId, 'Requires disposable owned runner');
const NEUTRAL = "You've reached the unanswered message limit for this conversation. You can send more when they reply.";
(async()=>{
 const edge=await managedEdge(chromium), browser=edge.browser;
 let passed=0;const pass=()=>passed++;const code=r=>[r.status,r.body?.code];
 try {
  const names=['a','b','c'];
  const members=await Promise.all(names.map(n=>createMember('unans-'+n)));
  await cli('beta','add',...members.map(m=>m.email));
  const tk={},pid={};
  for(const [i,n] of names.entries()){
   tk[n]=await memberToken(browser,members[i]);
   const p=await call(tk[n],'/api/me/profile',{method:'PUT',body:{displayName:'Fictional Unanswered '+n,homeCityId:2078025}});
   assert.equal(p.status,200);pid[n]=p.body.publicId;
   await call(tk[n],'/api/me/modes/friends',{method:'PUT',body:{enabled:true,visible:true}});
   await matchReady(tk[n]);
  }
  const connect=async(from,to)=>{
   const r=await call(tk[from],'/api/requests',{method:'POST',body:{to:pid[to],mode:'friends'}});assert.equal(r.status,201);
   assert.equal((await call(tk[to],`/api/requests/${r.body.id}/accept`,{method:'POST'})).status,200);
   const c=await call(tk[from],'/api/conversations',{method:'POST',body:{to:pid[to]}});assert.ok([200,201].includes(c.status));return c.body.id;
  };
  const ab=await connect('a','b'), ac=await connect('a','c');
  const send=(who,id,text='Fictional line')=>call(tk[who],`/api/conversations/${id}/messages`,{method:'POST',body:{text}});
  const settings=(who,body)=>call(tk[who],'/api/me/notification-settings',{method:'PUT',body});

  // Off by default; only Off (0), 3, 5 or 10; another save that omits it keeps it.
  assert.equal((await call(tk.b,'/api/me/notification-settings')).body.unansweredLimit,0);pass();
  for(const bad of [1,4,7,11,-3]){assert.deepEqual(code(await settings('b',{unansweredLimit:bad})),[400,'invalid-settings']);pass();}
  assert.equal((await settings('b',{unansweredLimit:3})).body.unansweredLimit,3);pass();
  assert.equal((await settings('b',{privatePreviews:true})).body.unansweredLimit,3);pass();
  assert.equal(await sql(`SELECT unanswered_limit FROM notification_settings ns JOIN accounts a ON a.id=ns.account_id WHERE a.subject='${claims(tk.b).sub}'`),'3');pass();

  // At the limit: the third sends, the fourth is refused neutrally and stores nothing.
  for(let n=1;n<=3;n++){assert.equal((await send('a',ab,'Fictional '+n)).status,201);pass();}
  const refused=await send('a',ab,'Fictional 4');
  assert.deepEqual(code(refused),[409,'unanswered-limit']);pass();
  assert.ok(!/\d/.test(refused.body.detail??'')&&!/\d/.test(refused.body.title??''));pass(); // never the number
  assert.equal(await sql(`SELECT count(*) FROM messages WHERE conversation_id='${ab}' AND body='Fictional 4'`),'0');pass();
  assert.equal((await call(tk.a,`/api/conversations/${ab}`)).body.awaitingReply,true);pass();
  // Reading the chat isn't a reply; deleting one of A's messages doesn't make room.
  await call(tk.b,`/api/conversations/${ab}/messages`);
  const mine=(await call(tk.a,`/api/conversations/${ab}/messages`)).body.items.find(m=>m.mine);
  assert.equal((await call(tk.a,`/api/messages/${mine.id}`,{method:'DELETE'})).status,204);
  assert.deepEqual(code(await send('a',ab)),[409,'unanswered-limit']);pass();
  // Other conversations follow their own recipient (C is Off).
  assert.equal((await send('a',ac)).status,201);pass();
  // One reply resets it.
  assert.equal((await send('b',ab,'Hi')).status,201);pass();
  assert.equal((await call(tk.a,`/api/conversations/${ab}`)).body.awaitingReply,false);pass();
  for(let n=0;n<3;n++)assert.equal((await send('a',ab)).status,201);
  assert.deepEqual(code(await send('a',ab)),[409,'unanswered-limit']);pass();
  // Raising it frees them; Off lifts it; on again counts the run already there.
  await settings('b',{unansweredLimit:10});assert.equal((await send('a',ab)).status,201);pass();
  await settings('b',{unansweredLimit:3});assert.deepEqual(code(await send('a',ab)),[409,'unanswered-limit']);pass();
  await settings('b',{unansweredLimit:0});assert.equal((await send('a',ab)).status,201);pass();
  await settings('b',{unansweredLimit:5});assert.deepEqual(code(await send('a',ab)),[409,'unanswered-limit']);pass();

  // Web: A's composer is disabled with the neutral line, and opens when B's reply arrives live.
  const context=await browser.newContext();const page=await context.newPage();
  await page.goto(web+'/account/sign-in?intro=done');await page.fill('#username',members[0].email);await page.fill('#password',members[0].password);
  await Promise.all([page.waitForURL(web+'/explore'),page.click('#kc-login')]);
  await page.goto(web+'/chats/'+ab);await page.waitForSelector('[data-conversation="ready"][data-live="on"]');
  assert.equal(await page.getAttribute('[data-composer]','data-composer-state'),'awaiting-reply');pass();
  assert.equal((await page.textContent('[data-awaiting-reply]')).trim(),NEUTRAL);pass();
  assert.ok(await page.isDisabled('[data-message-input]'));pass();
  assert.equal((await send('b',ab,'Fictional reply')).status,201);
  await page.waitForSelector('[data-composer][data-composer-state="open"]');pass();
  assert.ok(!(await page.isDisabled('[data-message-input]')));pass();
  assert.equal(await page.locator('[data-awaiting-reply]').count(),0);pass();

  // Web: B's setting is in Notifications next to photos in chat, and saves.
  const bContext=await browser.newContext();const bPage=await bContext.newPage();
  await bPage.goto(web+'/account/sign-in?intro=done');await bPage.fill('#username',members[1].email);await bPage.fill('#password',members[1].password);
  await Promise.all([bPage.waitForURL(web+'/explore'),bPage.click('#kc-login')]);
  await bPage.goto(web+'/me/notifications');await bPage.waitForSelector('[data-setting="unanswered-limit"]');
  assert.equal(await bPage.inputValue('[data-setting="unanswered-limit"]'),'5');pass();
  await bPage.selectOption('[data-setting="unanswered-limit"]','10');await bPage.click('[data-save-settings]');
  await bPage.waitForSelector('[data-settings-status="saved"]');
  assert.equal((await call(tk.b,'/api/me/notification-settings')).body.unansweredLimit,10);pass();
  await context.close();await bContext.close();
  console.log(`PASS: unanswered-message limit (${passed} checks).`);
 } finally { await edge.close(); }
})().catch(error=>{console.error(error);process.exit(1);});
