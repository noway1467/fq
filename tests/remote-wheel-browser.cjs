'use strict';
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '..');
const task = process.env.FQ_BROWSER_TASK || 'remote-wheel';
const data = path.join(root, 'build/test-data', task);
const output = path.join(root, 'build', task + '-validation');
const temporary = path.join(root, 'build/tmp', task);
const server = JSON.parse(fs.readFileSync(path.join(data, 'server.json'), 'utf8'));
const events = () => fs.readFileSync(path.join(data, 'input-events.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);
const results = [], errors = [];
const check = (name, value) => { assert.ok(value, name); results.push(name); };
fs.mkdirSync(output, {recursive:true});
(async () => {
  const browser = await chromium.launchPersistentContext(path.join(temporary, 'profile'), {
    executablePath:process.env.EDGE_PATH, headless:true, viewport:{width:800,height:360}, hasTouch:true, isMobile:true,
    downloadsPath:temporary
  });
  const page = await browser.newPage();
  await page.emulateMedia({reducedMotion:'reduce'});
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (['error','warning'].includes(message.type()) && !message.text().includes('favicon')) errors.push(message.text()); });
  const waitEvents = async count => { await assert.doesNotReject(async () => {
    const deadline = Date.now()+5000;
    while (events().length < count && Date.now() < deadline) await page.waitForTimeout(30);
    assert.ok(events().length >= count, 'input reaches loopback host');
  }); };
  const wheelAt = (id,deltaY,deltaMode=0,ctrlKey=false) => page.locator(id).evaluate((node,args) => {
    const r=node.getBoundingClientRect();
    return !node.dispatchEvent(new WheelEvent('wheel',{bubbles:true,cancelable:true,clientX:r.x+r.width/2,clientY:r.y+r.height/2,...args}));
  },{deltaY,deltaMode,ctrlKey});
  const touch = async (client,type,x,y) => client.send('Input.dispatchTouchEvent',{type,touchPoints:type==='touchEnd'?[]:[{x,y,id:1}]});
  try {
    await page.goto(`http://127.0.0.1:${server.port}/#remote`);
    await page.locator('#code').fill(server.remote); await page.locator('#connect').click();
    await page.waitForFunction(() => document.querySelector('#screen').naturalWidth===1200);
    for (const [width,height] of [[360,800],[768,1024],[1280,800],[800,360]]) {
      await page.setViewportSize({width,height});
      await page.waitForTimeout(80);
      check('no horizontal overflow '+width,await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth));
      check('wheel beside touchpad '+width,await page.evaluate(() => {
        const a=document.querySelector('#touchpad').getBoundingClientRect(),b=document.querySelector('#wheel-pad').getBoundingClientRect();
        return b.width>=48&&b.height>=64&&b.left>=a.right&&Math.abs(a.top-b.top)<1&&b.right<=innerWidth;
      }));
      await page.screenshot({path:path.join(output,`wheel-${width}x${height}.png`)});
    }
    await page.emulateMedia({colorScheme:'dark'});
    await page.screenshot({path:path.join(output,'wheel-dark.png')});
    await page.emulateMedia({colorScheme:'light'});
    await page.locator('#layout').click();
    check('stacked wheel and buttons fit',await page.locator('#drag').evaluate(node=>node.getBoundingClientRect().bottom<=innerHeight));
    await page.screenshot({path:path.join(output,'wheel-stacked.png')});
    await page.locator('#layout').click();
    await page.locator('#display').selectOption('1');
    const client=await page.context().newCDPSession(page);
    const pad=await page.locator('#wheel-pad').boundingBox(),x=pad.x+pad.width/2,y=pad.y+pad.height/2;
    let before=events().length;
    await touch(client,'touchStart',x,y); await touch(client,'touchMove',x,y+18); await touch(client,'touchEnd',x,y+18);
    await waitEvents(before+1);
    check('single finger down scrolls down without moving or clicking',events().slice(before).every(e=>e.type==='wheel'&&e.y<0&&e.display===1));
    before=events().length;
    await touch(client,'touchStart',x,y); await touch(client,'touchMove',x,y-18); await touch(client,'touchEnd',x,y-18);
    await waitEvents(before+1);
    check('single finger up scrolls up',events().slice(before).every(e=>e.type==='wheel'&&e.y>0));
    before=events().length;
    await touch(client,'touchStart',x,y); await touch(client,'touchEnd',x,y);
    await page.waitForTimeout(100);
    check('wheel tap never clicks remote',events().length===before);
    for (const [delta,mode,expected] of [[120,0,-120],[-120,0,120],[3,1,-120],[-3,1,120],[99999,2,-1200]]) {
      before=events().length;
      check('wheel default suppressed '+delta+'/'+mode,await wheelAt('#touchpad',delta,mode));
      await waitEvents(before+1);
      check('wheel delta normalized '+delta+'/'+mode,events()[before].y===expected);
    }
    before=events().length;
    await page.locator('#wheel-pad').focus(); await page.keyboard.press('ArrowDown'); await waitEvents(before+1);
    check('focused wheel keyboard access',events()[before].type==='wheel'&&events()[before].y===-120);
    before=events().length;
    await page.locator('#zoom').click();
    const scroll=await page.locator('#screen-wrap').evaluate(node=>node.scrollTop);
    const viewport=await page.locator('#screen-wrap').boundingBox();
    await page.mouse.move(viewport.x+viewport.width/2,viewport.y+viewport.height/2); await page.mouse.wheel(0,120);
    await waitEvents(before+2);
    check('real mouse wheel positions then scrolls remote',events()[before].type==='point'&&events()[before+1].type==='wheel'&&events()[before+1].y===-120);
    check('wheel does not pan local enlarged image',await page.locator('#screen-wrap').evaluate(node=>node.scrollTop)===scroll);
    before=events().length;
    check('ctrl wheel kept local',!await wheelAt('#screen',120,0,true));
    check('zero vertical wheel ignored',!await wheelAt('#screen',0));
    await page.waitForTimeout(100);check('local gestures send no input',events().length===before);
    before=events().length;
    await page.route('**/api/input',async route=>{await page.waitForTimeout(200);await route.continue();},{times:1});
    await page.locator('#screen').evaluate(node=>{
      const r=node.getBoundingClientRect();
      for(let i=0;i<80;i++)node.dispatchEvent(new WheelEvent('wheel',{bubbles:true,cancelable:true,clientX:r.left+r.width/2,clientY:r.top+r.height/2,deltaY:10}));
    });
    await waitEvents(before+4);await page.waitForTimeout(150);
    check('fast screen wheel coalesces without disconnect or backlog',await page.locator('#remote').isVisible()&&events().length-before===4);
    await page.locator('#zoom').click();
    const box=await page.locator('#touchpad').boundingBox();
    before=events().length;
    await client.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:box.x+25,y:box.y+30,id:1},{x:box.x+75,y:box.y+30,id:2}]});
    await client.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:box.x+25,y:box.y+50,id:1},{x:box.x+75,y:box.y+50,id:2}]});
    // 两根手指分别派发 pointermove，先等两个滚动请求到达，再检查断开后的新手势。
    await touch(client,'touchEnd',0,0);await waitEvents(before+2);
    check('existing two finger scrolling preserved',events().slice(before).every(e=>e.type==='wheel'&&e.y<0));
    before=events().length;
    await touch(client,'touchStart',x,y);await page.evaluate(()=>window.feiqStop());
    await touch(client,'touchMove',x,y+20);await touch(client,'touchEnd',x,y+20);await page.waitForTimeout(100);
    check('disconnect clears wheel gesture',events().length===before&&await page.locator('#wheel-pad').evaluate(node=>!node.classList.contains('scrolling')));
    await page.locator('#reconnect').click();await page.waitForFunction(()=>document.querySelector('#screen').naturalWidth===1200);
    before=events().length;await wheelAt('#wheel-pad',120);await waitEvents(before+1);
    check('reconnected wheel uses current display',events()[before].display===0);
    await page.evaluate(()=>window.feiqStop());
    await page.route('**/api/displays',route=>route.fulfill({json:{platform:'android',imeEnter:true,items:[{id:0,name:'手机'}]}}));
    await page.locator('#reconnect').click();await page.waitForFunction(()=>document.body.classList.contains('phone-target')&&document.querySelector('#screen').naturalWidth===1200);
    before=events().length;await wheelAt('#screen',120);await page.waitForTimeout(100);
    check('Android target hides wheel and receives no unsupported input',await page.locator('#wheel-pad').isHidden()&&events().length===before);
    await client.detach();await page.evaluate(()=>window.feiqStop());
    check('no browser errors or warnings',errors.length===0);
    console.log(`${results.length} wheel browser checks passed`);
  } catch(error) { await page.screenshot({path:path.join(output,'failure.png')}); throw error; }
  finally { fs.writeFileSync(path.join(output,'browser.json'),JSON.stringify({passed:results.length,results,errors},null,2)); await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
