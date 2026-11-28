'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {chromium} = require('playwright');

const root = path.resolve(__dirname, '..');
const temporary = process.env.FQ_BROWSER_TEMP || path.join(root, 'build/tmp/remote-disconnect-browser');
const output = process.env.FQ_VALIDATION_DIR || path.join(root, 'build/remote-drop-audit-20261121');
fs.mkdirSync(temporary, {recursive:true});
fs.mkdirSync(output, {recursive:true});
let logins = 0;
const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  request.resume();
  const json = (status, value, headers = {}) => {
    response.writeHead(status, {'Content-Type':'application/json', ...headers});
    response.end(JSON.stringify(value));
  };
  if (url.pathname === '/api/login' || url.pathname === '/api/reconnect') {
    const token = 'isolated-session-' + ++logins;
    return json(200, {token, mode:'remote'}, {
      'Set-Cookie': [`fq_remote=${token}; Path=/; HttpOnly; SameSite=Strict`, 'fq_pair_remote=isolated-pair; Path=/; HttpOnly; SameSite=Strict']
    });
  }
  if (url.pathname === '/api/status') return json(200, {remote:true, remotePaired:true});
  if (url.pathname === '/api/displays') return json(200, {platform:'windows', items:[{id:0, name:'测试屏幕'}]});
  if (url.pathname === '/api/list') return json(200, {items:[]});
  if (url.pathname === '/api/screen') {
    const image = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64');
    response.writeHead(200, {'Content-Type':'image/gif'});
    return response.end(image);
  }
  if (url.pathname === '/api/input' || url.pathname === '/api/logout') return json(200, {ok:true});
  if (url.pathname === '/' || url.pathname === '/index.html' || ['/app.js','/app.css'].includes(url.pathname)) {
    const name = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    response.writeHead(200, {'Content-Type':name.endsWith('.js')?'application/javascript':name.endsWith('.css')?'text/css':'text/html'});
    return response.end(fs.readFileSync(path.join(root, 'assets/tools', name)));
  }
  response.writeHead(404);
  response.end();
});

async function connect(page, first = false) {
  if (first) await page.locator('#code').fill('123456');
  await page.locator(first?'#connect':'#reconnect').click();
  await page.locator('#remote').waitFor({state:'visible'});
  await page.waitForFunction(() => document.querySelector('#screen').naturalWidth > 0);
}

async function countRequest(page, pathname) {
  return page.evaluate(name => performance.getEntriesByType('resource').filter(item => new URL(item.name).pathname === name).length, pathname);
}

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launchPersistentContext(path.join(temporary, 'profile'), {
    executablePath:process.env.EDGE_PATH,
    headless:true,
    viewport:{width:800,height:360}
  });
  const results = [];
  const check = (name, value, details = '') => { assert.ok(value, name+(details?': '+details:'')); results.push(name); };
  try {
    const page = await browser.newPage();
    page.on('pageerror', error => { throw error; });
    await page.goto(`http://127.0.0.1:${server.address().port}/#remote`);
    await connect(page, true);

    let statuses = 0;
    await page.route('**/api/status', async route => {
      statuses++;
      if (statuses === 1) await route.fulfill({status:503, contentType:'application/json', body:'{"error":"temporary status outage"}'});
      else await route.continue();
    });
    const probeSnapshot=await page.evaluate(async()=>{await window.feiqCheck();const warning=document.querySelector('#remote-error');return {visible:!warning.hidden,text:warning.textContent};});
    check('temporary status failure keeps live remote session', await page.locator('#remote').isVisible());
    check('temporary status failure reports the probe without logging out',probeSnapshot.visible&&probeSnapshot.text.includes('保持中'));
    await page.unroute('**/api/status');

    const beforeFrames = await countRequest(page, '/api/screen');
    let transientFrames = 0;
    await page.route('**/api/screen?*', async route => {
      transientFrames++;
      if (transientFrames === 1) await route.abort('connectionreset');
      else await route.continue();
    });
    await page.waitForFunction(async previous => {
      const count = performance.getEntriesByType('resource').filter(item => new URL(item.name).pathname === '/api/screen').length;
      return count >= previous + 2;
    }, beforeFrames);
    await page.waitForFunction(() => document.querySelector('#remote-error').hidden);
    check('single frame transport reset retries and recovers', transientFrames >= 2 && await page.locator('#remote').isVisible());
    await page.unroute('**/api/screen?*');

    let authFailures = 0;
    await page.route('**/api/screen?*', async route => {
      if (authFailures++ === 0) await route.fulfill({status:401, contentType:'application/json', body:'{"error":"session expired"}'});
      else await route.continue();
    });
    const beforeReconnects = await countRequest(page, '/api/reconnect');
    const reconnectRequest=page.waitForRequest(request=>new URL(request.url()).pathname==='/api/reconnect',{timeout:10000});
    await reconnectRequest;
    await page.locator('#remote-error').waitFor({state:'hidden'});
    const reconnectCount=await countRequest(page,'/api/reconnect'),remoteVisible=await page.locator('#remote').isVisible(),statusText=await page.locator('#remote-error').innerText();
    check('expired screen session automatically reconnects with saved pairing', reconnectCount===beforeReconnects+1&&remoteVisible,JSON.stringify({beforeReconnects,reconnectCount,remoteVisible,statusText}));
    await page.unroute('**/api/screen?*');

    let inputRequests=0;
    await page.close();
    const inputPage=await browser.newPage();
    await inputPage.goto(`http://127.0.0.1:${server.address().port}/#remote`);
    await connect(inputPage,true);
    await inputPage.route('**/api/input',async route=>{
      inputRequests++;
      if(inputRequests===1)await route.abort('connectionreset');
      else await route.continue();
    });
    const beforeInputReconnects=await countRequest(inputPage,'/api/reconnect');
    const inputReconnect=inputPage.waitForRequest(request=>new URL(request.url()).pathname==='/api/reconnect',{timeout:10000});
    await inputPage.locator('#keyboard').click();
    await inputPage.locator('#remote-text').fill('不自动重放的输入');
    await inputPage.locator('#type-text').click();
    await inputReconnect;
    await inputPage.waitForFunction(previous=>performance.getEntriesByType('resource').filter(item=>new URL(item.name).pathname==='/api/reconnect').length===previous+1,beforeInputReconnects);
    check('uncertain input is not automatically replayed',inputRequests===1 && await inputPage.locator('#remote').isVisible());
    check('reconnect reports that input was not resent',await inputPage.locator('#remote-error').innerText().then(text=>text.includes('操作未自动重发')));
    await inputPage.close();

    const racePage=await browser.newPage();
    await racePage.goto(`http://127.0.0.1:${server.address().port}/#remote`);
    await connect(racePage,true);
    let releaseStatus;
    let statusEntered;
    const enteredStatus = new Promise(resolve => { statusEntered = resolve; });
    await racePage.route('**/api/status', async route => {
      if (!releaseStatus) {
        const gate = new Promise(resolve => { releaseStatus = resolve; });
        statusEntered();
        await gate;
      }
      await route.fulfill({status:200, contentType:'application/json', body:'{"remote":false,"remotePaired":false}'});
    }, {times:1});
    const checking = racePage.evaluate(() => window.feiqCheck());
    await enteredStatus;
    await racePage.evaluate(() => window.feiqStop());
    await connect(racePage);
    check('new paired session connects while previous status request is pending', await racePage.locator('#remote').isVisible());
    releaseStatus();
    await checking;
    await racePage.waitForTimeout(80);
    check('stale status response cannot end new session', await racePage.locator('#remote').isVisible());
    check('remote reconnect sends no stale logout', await countRequest(racePage, '/api/logout') === 1);

    await racePage.evaluate(() => window.feiqStop());
    await racePage.close();

    const budgetPage=await browser.newPage();
    await budgetPage.goto(`http://127.0.0.1:${server.address().port}/#remote`);await connect(budgetPage,true);
    const loginsBeforeBudget=logins;
    await budgetPage.route('**/api/screen?*',route=>route.fulfill({status:401,contentType:'application/json',body:'{"error":"session invalidated"}'}));
    await budgetPage.waitForFunction(()=>document.querySelector('#status').textContent.includes('已停止自动恢复'));
    check('persistent frame 401 stops after four paired recovery attempts',logins-loginsBeforeBudget===4&&await budgetPage.locator('#login').isVisible());
    await budgetPage.unroute('**/api/screen?*');await connect(budgetPage);
    check('manual reconnect remains available after recovery limit',await budgetPage.locator('#remote').isVisible()&&await budgetPage.locator('#reconnect').isEnabled());
    for(let outage=0;outage<2;outage++){
      const beforeOutage=logins;
      const reconnected=budgetPage.waitForResponse(response=>new URL(response.url()).pathname==='/api/reconnect');
      await budgetPage.route('**/api/screen?*',route=>route.fulfill({status:401,contentType:'application/json',body:'{"error":"session expired"}'}),{times:1});
      await reconnected;await budgetPage.waitForFunction(()=>document.querySelector('#screen').naturalWidth>0);
      check('a valid frame resets recovery budget '+outage,logins-beforeOutage===1&&await budgetPage.locator('#remote').isVisible());
    }
    await budgetPage.evaluate(()=>window.feiqStop());await budgetPage.close();

    const latePage=await browser.newPage();
    await latePage.addInitScript(()=>{const original=window.setTimeout;window.setTimeout=(callback,milliseconds,...args)=>original(callback,milliseconds===250||milliseconds===5000?60000:milliseconds,...args);});
    let releaseInput,inputEntered,holdInput=true,failProbe=false;
    const pendingInput=new Promise(resolve=>inputEntered=resolve);
    await latePage.route('**/api/*',async route=>{
      const pathname=new URL(route.request().url()).pathname;
      if(pathname==='/api/input'&&holdInput){holdInput=false;const pending=new Promise(resolve=>releaseInput=resolve);inputEntered();await pending;await route.fulfill({status:200,contentType:'application/json',body:'{"ok":true}'});}
      else if(pathname==='/api/status'&&failProbe)await route.fulfill({status:503,contentType:'application/json',body:'{"error":"temporary probe failure"}'});
      else await route.continue();
    });
    await latePage.goto(`http://127.0.0.1:${server.address().port}/#remote`);await connect(latePage,true);
    await latePage.locator('#keyboard').click();await latePage.locator('#remote-text').fill('旧会话输入');await latePage.locator('#type-text').click();await pendingInput;
    await latePage.evaluate(()=>window.feiqStop());await connect(latePage);
    failProbe=true;
    await latePage.evaluate(()=>window.feiqCheck());check('new session warning is visible before old input returns',await latePage.locator('#remote-error').isVisible());
    const oldResponse=latePage.waitForResponse(response=>new URL(response.url()).pathname==='/api/input');releaseInput();await oldResponse;await latePage.waitForTimeout(80);
    check('old input success cannot clear new session warning',await latePage.locator('#remote-error').isVisible()&&(await latePage.locator('#remote-error').innerText()).includes('保持中'));
    await latePage.evaluate(()=>window.feiqStop());await latePage.close();

    const stoppedPage=await browser.newPage();
    await stoppedPage.goto(`http://127.0.0.1:${server.address().port}/#remote`);
    let releaseReconnect,reconnectEntered;
    const pendingReconnect=new Promise(resolve=>reconnectEntered=resolve);
    await stoppedPage.route('**/api/reconnect',async route=>{
      const gate=new Promise(resolve=>releaseReconnect=resolve);reconnectEntered();await gate;await route.continue();
    },{times:1});
    await stoppedPage.locator('#reconnect').click();await pendingReconnect;
    assert.ok(await stoppedPage.locator('#connect').isDisabled()&&await stoppedPage.locator('#reconnect').isDisabled());
    await stoppedPage.evaluate(()=>window.feiqStop());
    assert.ok(await stoppedPage.locator('#connect').isEnabled()&&await stoppedPage.locator('#reconnect').isEnabled(),'external stop immediately unlocks connection buttons');
    const stoppedResponse=stoppedPage.waitForResponse(response=>new URL(response.url()).pathname==='/api/reconnect');releaseReconnect();await stoppedResponse;await stoppedPage.waitForTimeout(80);
    assert.ok(await stoppedPage.locator('#connect').isEnabled()&&await stoppedPage.locator('#reconnect').isEnabled()&&await stoppedPage.locator('#login').isVisible(),'old connect response cannot lock buttons or reopen the stopped session');
    const recoveredRequest=stoppedPage.waitForRequest(request=>new URL(request.url()).pathname==='/api/reconnect');
    await stoppedPage.evaluate(()=>window.feiqCheck());await recoveredRequest;
    await stoppedPage.waitForFunction(()=>document.querySelector('#screen').naturalWidth>0);
    check('external stop during reconnect unlocks buttons and foreground check restores pairing',await stoppedPage.locator('#remote').isVisible()&&await stoppedPage.locator('#reconnect').isEnabled());
    await stoppedPage.evaluate(()=>window.feiqStop());await stoppedPage.close();

    for(const mode of ['remote','share']){
      const loadingPage=await browser.newPage(),pathname=mode==='remote'?'/api/displays':'/api/list';
      let releaseLoad,loadEntered;
      const pendingLoad=new Promise(resolve=>loadEntered=resolve);
      await loadingPage.route('**'+pathname+'*',async route=>{
        const gate=new Promise(resolve=>releaseLoad=resolve);loadEntered();await gate;await route.continue().catch(()=>{});
      },{times:1});
      await loadingPage.goto(`http://127.0.0.1:${server.address().port}/#${mode}`);
      await loadingPage.locator('#code').fill('123456');await loadingPage.locator('#connect').click();await pendingLoad;
      assert.ok(await loadingPage.locator('#connect').isDisabled(),'connection remains pending while loading '+pathname);
      const settled=Promise.race([
        loadingPage.waitForResponse(response=>new URL(response.url()).pathname===pathname),
        loadingPage.waitForEvent('requestfailed',request=>new URL(request.url()).pathname===pathname)
      ]);
      await loadingPage.locator(mode==='remote'?'#remote-disconnect':'#disconnect').click();
      assert.ok(await loadingPage.locator('#connect').isEnabled()&&await loadingPage.locator('#reconnect').isEnabled(),'disconnect immediately unlocks buttons during '+pathname);
      releaseLoad();await settled;await loadingPage.waitForTimeout(80);
      assert.ok(await loadingPage.locator('#connect').isEnabled()&&await loadingPage.locator('#reconnect').isEnabled()&&await loadingPage.locator('#login').isVisible(),'old load response cannot lock buttons during '+pathname);
      await loadingPage.locator('#reconnect').click();await loadingPage.locator(mode==='remote'?'#remote':'#files').waitFor({state:'visible'});
      await loadingPage.waitForFunction(()=>!document.querySelector('#connect').disabled);
      check('disconnect during '+pathname+' leaves manual reconnect available',await loadingPage.locator('#reconnect').isEnabled());
      await loadingPage.evaluate(()=>window.feiqStop());await loadingPage.close();
    }
    fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify({passed:results.length, results}, null, 2)+'\n');
    console.log(`${results.length} remote disconnect browser checks passed`);
  } finally {
    await browser.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => {console.error(error);process.exitCode=1;});
