'use strict';
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..');
const temp=process.env.FQ_BROWSER_TEMP||path.join(root,'build/tmp/guest-folder-browser');
const output=process.env.FQ_VALIDATION_DIR||path.join(root,'build/guest-folder-browser');
const data=path.join(temp,'data'),serverOutput=path.join(temp,'server');
for(const folder of [temp,output,data,serverOutput])fs.mkdirSync(folder,{recursive:true});
const server=spawn(process.env.FQ_GUEST_TEST_EXE,[data,serverOutput,'server'],{windowsHide:true,env:process.env});
let log='',browser;server.stdout.on('data',chunk=>log+=chunk);server.stderr.on('data',chunk=>log+=chunk);
const exit=new Promise(resolve=>server.on('exit',resolve));
const errors=[],results=[];
const check=(name,value)=>{assert.ok(value,name);results.push(name);};
(async()=>{
  try{
    const urlFile=path.join(serverOutput,'guest-url.txt');
    for(let i=0;i<100&&!fs.existsSync(urlFile);i++){if(server.exitCode!==null)throw new Error(log);await new Promise(resolve=>setTimeout(resolve,100));}
    const url=fs.readFileSync(urlFile,'utf8').trim();
    browser=await chromium.launchPersistentContext(path.join(temp,'profile'),{executablePath:process.env.EDGE_PATH,headless:true,downloadsPath:temp});
    const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
    if(process.env.FQ_GUEST_CSS_SOURCE==='1')await page.route('**/guest.css',route=>route.fulfill({contentType:'text/css',body:fs.readFileSync(path.join(root,'assets/tools/guest.css'),'utf8')}));
    await page.goto(url);await page.locator('#choose-folder').waitFor({state:'visible'});
    const folder=path.join(temp,'资料 中文 English');fs.mkdirSync(path.join(folder,'子目录'),{recursive:true});
    fs.writeFileSync(path.join(folder,'同名.txt'),'root 😀');fs.writeFileSync(path.join(folder,'子目录','同名.txt'),'nested 中文');
    fs.writeFileSync(path.join(folder,'子目录','空文件.txt'),'');
    await page.locator('#folder-picker').setInputFiles(folder);
    await page.waitForFunction(()=>document.querySelectorAll('#uploads .meta.done').length===3);
    const received=path.join(data,'browser-received',path.basename(folder));
    check('browser folder picker preserves nested content',fs.readFileSync(path.join(received,'子目录','同名.txt'),'utf8')==='nested 中文');
    check('same basename in different directories stays separate',fs.readFileSync(path.join(received,'同名.txt'),'utf8')==='root 😀');
    check('empty file completes',fs.statSync(path.join(received,'子目录','空文件.txt')).size===0);
    await page.locator('#folder-picker').setInputFiles(folder);await page.waitForFunction(()=>document.querySelectorAll('#uploads .meta.done').length===6);
    check('second folder upload never overwrites',fs.existsSync(path.join(received,'子目录','同名 (1).txt')));
    await page.mouse.move(0,0);
    await page.emulateMedia({colorScheme:'light'});
    await page.waitForTimeout(200);
    await page.emulateMedia({colorScheme:'dark'});
    const firstDarkFrame=await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>{
      resolve(['qr-toggle','drop','choose-folder'].map(id=>({id,background:getComputedStyle(document.getElementById(id)).backgroundColor})));
    })));
    check('dark theme switches all control surfaces in one frame',firstDarkFrame.every(item=>item.background===(item.id==='choose-folder'?'rgb(29, 53, 80)':'rgb(28, 40, 54)')));
    const contrast=async selector=>page.locator(selector).first().evaluate(node=>{
      const luminance=value=>value.match(/[\d.]+/g).slice(0,3).map(Number).map(n=>{n/=255;return n<=.04045?n/12.92:((n+.055)/1.055)**2.4;}).reduce((sum,n,i)=>sum+n*[.2126,.7152,.0722][i],0);
      const style=getComputedStyle(node),a=luminance(style.color),b=luminance(style.backgroundColor);return (Math.max(a,b)+.05)/(Math.min(a,b)+.05);
    });
    for(const dark of [true,false]){
      await page.emulateMedia({colorScheme:dark?'dark':'light'});
      for(const selector of ['#qr-toggle','.download','#choose-folder']){
        await page.mouse.move(0,0);check(`readable resting control ${selector}/${dark}`,await contrast(selector)>=4.5);
        await page.locator(selector).first().hover();check(`readable hovered control ${selector}/${dark}`,await contrast(selector)>=4.5);
      }
      await page.locator('#qr-toggle').click();
      check(`readable copy button ${dark}`,await contrast('#copy')>=4.5);
      await page.locator('#copy').hover();check(`readable hovered copy button ${dark}`,await contrast('#copy')>=4.5);
      await page.locator('#copy').evaluate(button=>button.disabled=true);check(`readable disabled copy button ${dark}`,await contrast('#copy')>=4.5);
      await page.locator('#copy').evaluate(button=>button.disabled=false);
      await page.mouse.move(0,0);
      await page.screenshot({path:path.join(output,`guest-qr-${dark?'dark':'light'}.png`),fullPage:true});
      await page.locator('#qr-toggle').click();
    }
    for(const dark of [false,true])for(const [width,height] of [[360,800],[768,1024],[1280,800],[800,360]]){
      await page.emulateMedia({colorScheme:dark?'dark':'light'});await page.setViewportSize({width,height});
      await page.waitForTimeout(200);
      check(`folder UI no horizontal overflow ${width}/${dark}`,await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      await page.screenshot({path:path.join(output,`guest-${width}x${height}-${dark?'dark':'light'}.png`),fullPage:true});
    }
    check('no browser script errors',errors.length===0);
    console.log(`${results.length} guest folder browser checks passed`);
  }finally{
    if(browser)await browser.close();fs.writeFileSync(path.join(serverOutput,'stop-server'),'stop');await exit;
    fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:results.length,results,errors},null,2));
    fs.writeFileSync(path.join(output,'server.log'),log);
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
