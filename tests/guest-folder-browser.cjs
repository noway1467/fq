'use strict';
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const crypto=require('node:crypto');
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
const sha=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
function archiveEntries(file){
  const bytes=fs.readFileSync(file),end=bytes.length-22;
  assert.equal(bytes.readUInt32LE(end),0x06054b50,'complete ZIP EOCD');
  const count=bytes.readUInt16LE(end+10),entries=new Map();let offset=bytes.readUInt32LE(end+16);
  for(let i=0;i<count;i++){
    assert.equal(bytes.readUInt32LE(offset),0x02014b50);assert.equal(bytes.readUInt16LE(offset+8),0x0800);assert.equal(bytes.readUInt16LE(offset+10),0);
    const nameLength=bytes.readUInt16LE(offset+28),name=bytes.toString('utf8',offset+46,offset+46+nameLength);
    const local=bytes.readUInt32LE(offset+42),size=bytes.readUInt32LE(offset+24),start=local+30+bytes.readUInt16LE(local+26);
    const data=bytes.subarray(start,start+size);let crc=0xffffffff;
    for(const value of data){crc^=value;for(let bit=0;bit<8;bit++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}
    assert.equal((crc^0xffffffff)>>>0,bytes.readUInt32LE(offset+16),'downloaded entry CRC-32');
    assert.ok(!entries.has(name),'unique ZIP paths');entries.set(name,data);
    offset+=46+nameLength+bytes.readUInt16LE(offset+30)+bytes.readUInt16LE(offset+32);
  }
  assert.equal(offset,end);return entries;
}
(async()=>{
  try{
    const urlFile=path.join(serverOutput,'guest-url.txt');
    for(let i=0;i<100&&!fs.existsSync(urlFile);i++){if(server.exitCode!==null)throw new Error(log);await new Promise(resolve=>setTimeout(resolve,100));}
    const url=fs.readFileSync(urlFile,'utf8').trim();
    browser=await chromium.launchPersistentContext(path.join(temp,'profile'),{executablePath:process.env.EDGE_PATH,headless:true,downloadsPath:temp});
    const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
    page.on('console',event=>{if(['error','warning'].includes(event.type()))errors.push(event.text());});
    await page.addInitScript(()=>{
      window.fqArchiveFetches=[];const original=window.fetch;
      window.fetch=function(input,...args){
        const url=typeof input==='string'?input:input.url;
        if(new URL(url,location.href).pathname==='/archive')window.fqArchiveFetches.push('/archive');
        return original.call(this,input,...args);
      };
    });
    const archiveRequests=[];page.on('request',request=>{if(new URL(request.url()).pathname==='/archive')archiveRequests.push(request.resourceType());});
    if(process.env.FQ_GUEST_CSS_SOURCE==='1')await page.route('**/guest.css',route=>route.fulfill({contentType:'text/css',body:fs.readFileSync(path.join(root,'assets/tools/guest.css'),'utf8')}));
    await page.goto(url);await page.locator('#choose-folder').waitFor({state:'visible'});
    const status=await page.request.get(new URL('/api/guest',url).href,{headers:{'X-FQ-Guest':new URL(url).hash.slice(1)}});
    const shared=await status.json();check('archive capability advertises ZIP32 limits',shared.archive.enabled&&shared.archive.maxEntries===1000&&shared.archive.maxBytes===4294967294);
    const expected=new Map(shared.files.map(file=>[file.relativePath||file.name,fs.readFileSync(file.relativePath?path.join(data,'browser-source',file.relativePath):path.join(data,file.name))]));
    fs.writeFileSync(path.join(data,'browser-source','分享资料 中文 English','会话后新增.txt'),'must stay private');
    async function download(selector,name){
      const [result]=await Promise.all([page.waitForEvent('download'),page.locator(selector).click()]);
      assert.equal(await result.failure(),null);check(`${name} uses ZIP attachment filename`,result.suggestedFilename().endsWith('.zip'));
      const saved=path.join(temp,name+'.zip');await result.saveAs(saved);return archiveEntries(saved);
    }
    const all=await download('#download-all','all');
    check('download all completes with authorized files and matching SHA-256',all.size===expected.size&&[...expected].every(([name,bytes])=>all.has(name)&&sha(all.get(name))===sha(bytes)));
    const directory=shared.directories.find(item=>item.relativePath==='分享资料 中文 English');assert.ok(directory);
    const folderZip=await download(`[data-directory="${directory.id}"] .download`,'folder');
    const directoryExpected=[...expected].filter(([name])=>name.startsWith(directory.relativePath+'/'));
    check('download directory keeps nested Chinese and special paths',folderZip.size===directoryExpected.length&&directoryExpected.every(([name,bytes])=>folderZip.has(name)&&sha(folderZip.get(name))===sha(bytes)));
    const nested=shared.directories.find(item=>item.relativePath==='分享资料 中文 English/子目录');assert.ok(nested);
    const nestedZip=await download(`[data-directory="${nested.id}"] .download`,'nested-folder');
    check('download nested directory keeps selected directory name',nestedZip.size===1&&sha(nestedZip.get('子目录/特殊 +&%#😀.txt'))===sha(expected.get('分享资料 中文 English/子目录/特殊 +&%#😀.txt')));
    check('archive downloads use native links without fetch or xhr',archiveRequests.every(type=>!['fetch','xhr'].includes(type))&&await page.evaluate(()=>window.fqArchiveFetches.length===0));
    check('text transfer panel visible and host text rendered safely without XSS',await page.locator('#text-transfer').isVisible()&&await page.waitForFunction(()=>document.querySelectorAll('#text-received .text-entry').length===1).then(()=>true)&&await page.evaluate(()=>!window.fqTextXss&&document.querySelector('#text-received pre').textContent.includes('<img src=x onerror=window.fqTextXss=1>')));
    await page.locator('#text-draft').fill('访客网页发送 <script>window.fqTextXss=2</script> 中文 😀');
    await page.locator('#text-send').click();
    await page.waitForFunction(()=>document.querySelectorAll('#text-sent .text-entry').length===1);
    check('guest text posted to host inbox and shown as plain text',await page.evaluate(()=>!window.fqTextXss&&document.querySelector('#text-sent pre').textContent.includes('<script>window.fqTextXss=2</script>')));
    const treeGroup=page.locator('.directory-group').first();
    const treeToggle=treeGroup.locator('> .directory .tree-toggle');
    const treeChildren=treeGroup.locator('> .directory-children');
    await treeToggle.click();check('directory tree collapses on toggle',await treeChildren.isHidden());
    await page.locator('#download-search').fill('特殊 +&%#');
    check('download search expands matching ancestor temporarily',await treeChildren.isVisible()&&await page.locator('[data-file]').count()===1);
    await page.locator('#download-search').fill('');
    check('clearing download search restores previous collapsed directory state',await treeChildren.isHidden());
    await treeToggle.click();check('directory tree expands again',await treeChildren.isVisible());
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
      const parse=value=>value.match(/[\d.]+/g).map(Number);
      const effectiveBg=el=>{
        for(let cur=el;cur;cur=cur.parentElement){
          const c=parse(getComputedStyle(cur).backgroundColor);
          if(c.length<4||c[3]>0)return c.slice(0,3);
        }
        return [255,255,255];
      };
      const luminance=rgb=>rgb.map(n=>{n/=255;return n<=.04045?n/12.92:((n+.055)/1.055)**2.4;}).reduce((sum,n,i)=>sum+n*[.2126,.7152,.0722][i],0);
      const style=getComputedStyle(node),a=luminance(parse(style.color).slice(0,3)),b=luminance(effectiveBg(node));return (Math.max(a,b)+.05)/(Math.min(a,b)+.05);
    });
    for(const dark of [true,false]){
      await page.emulateMedia({colorScheme:dark?'dark':'light'});
      for(const selector of ['#qr-toggle','.download','#choose-folder']){
        await page.mouse.move(0,0);check(`readable resting control ${selector}/${dark}`,await contrast(selector)>=4.5);
        await page.locator(selector).first().hover();check(`readable hovered control ${selector}/${dark}`,await contrast(selector)>=4.5);
      }
      await page.mouse.move(0,0);
      check(`readable saved meta status ${dark}`,await contrast('#uploads .meta.done')>=4.5);
      check(`readable upload summary bytes ${dark}`,await contrast('#upload-bytes')>=4.5);
      check(`readable text success status ${dark}`,await contrast('#text-status.info')>=4.5);
      await page.evaluate(()=>{const el=document.getElementById('message');el.textContent='链接已复制到剪贴板';el.className='info';});
      check(`readable success info banner ${dark}`,await contrast('#message.info')>=4.5);
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
    let sampleState={...shared,files:[],directories:[]};
    await page.route('**/api/guest',route=>route.fulfill({contentType:'application/json',body:JSON.stringify(sampleState)}));
    await page.reload();await page.waitForFunction(()=>document.getElementById('count').textContent==='0 个文件');
    check('empty share hides archive actions',await page.locator('#download-all').isHidden()&&await page.locator('#directory-downloads').isHidden());
    const longName='长文件名 English 中文 😀 '.repeat(12)+'.txt';
    sampleState={...shared,files:[{...shared.files[0],name:longName,relativePath:'多级目录/'+longName}],directories:[{...directory,name:longName,relativePath:'目录 '+longName}]};
    await page.reload();await page.locator('#download-all').waitFor({state:'visible'});await page.setViewportSize({width:360,height:800});
    for(const dark of [false,true]){
      await page.emulateMedia({colorScheme:dark?'dark':'light'});
      check(`long Chinese and English archive rows do not overflow ${dark}`,await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      await page.screenshot({path:path.join(output,`guest-long-${dark?'dark':'light'}.png`),fullPage:true});
    }
    check('no browser script errors',errors.length===0);
    console.log(`${results.length} guest folder browser checks passed`);
  }finally{
    if(browser)await browser.close();fs.writeFileSync(path.join(serverOutput,'stop-server'),'stop');await exit;
    fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:results.length,results,errors},null,2));
    fs.writeFileSync(path.join(output,'server.log'),log);
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
