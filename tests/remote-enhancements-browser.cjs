'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const {chromium} = require('playwright');
const root = path.resolve(__dirname,'..');
const output = process.env.FQ_VALIDATION_DIR || path.join(root,'build/remote-enhancements');
const temporary = process.env.FQ_BROWSER_TEMP || path.join(root,'build/tmp/remote-enhancements');
const fixtures = process.env.FQ_REMOTE_FIXTURES || path.join(root,'build/test-data/shared-remote');
fs.mkdirSync(output,{recursive:true});fs.mkdirSync(temporary,{recursive:true});
const full=fs.readFileSync(path.join(fixtures,'frame-full.fqf'));
const tile=fs.readFileSync(path.join(fixtures,'frame-tile.fqf'));
const large=fs.readFileSync(path.join(fixtures,'frame-large.fqf'));
let frame=full, clipboardText='', clipboardWrites=0, reads=0, screens=0;
let phone=false, allowRead=true, badFrame=false;
const inputs=[],results=[],errors=[];
const check=(name,value)=>{assert.ok(value,name);results.push(name);};
const server=http.createServer(async(request,response)=>{
  const url=new URL(request.url,'http://127.0.0.1');
  const chunks=[];for await(const chunk of request)chunks.push(chunk);
  const json=(status,value)=>{response.writeHead(status,{'Content-Type':'application/json'});response.end(JSON.stringify(value));};
  if(url.pathname==='/api/login'||url.pathname==='/api/reconnect')return json(200,{token:'isolated-session',mode:'remote'});
  if(url.pathname==='/api/status')return json(200,{remote:true,remotePaired:true,clipboardRead:allowRead,clipboardWrite:true});
  if(url.pathname==='/api/displays')return json(200,{platform:phone?'android':'windows',clipboardWrite:true,clipboardRead:allowRead,chord:!phone,items:[{id:0,name:'测试屏幕'}]});
  if(url.pathname==='/api/input'){inputs.push(JSON.parse(Buffer.concat(chunks)));return json(200,{ok:true});}
  if(url.pathname==='/api/logout')return json(200,{ok:true});
  if(url.pathname==='/api/clipboard'){
    if(request.method==='POST'){clipboardText=JSON.parse(Buffer.concat(chunks)).text;clipboardWrites++;return json(200,{ok:true});}
    reads++;return json(200,{text:clipboardText,empty:!clipboardText});
  }
  if(url.pathname==='/api/screen'){
    screens++;let bytes=frame;
    if(badFrame){badFrame=false;bytes=Buffer.from(tile);bytes.writeUInt16BE(65535,18);}
    else if(Number(url.searchParams.get('since'))===frame.readUInt32BE(8)){
      bytes=Buffer.from(frame.subarray(0,18));bytes[4]=0;bytes.writeUInt16BE(0,16);
      await new Promise(resolve=>setTimeout(resolve,300));
    }
    response.writeHead(200,{'Content-Type':'application/octet-stream','Content-Length':bytes.length});return response.end(bytes);
  }
  const name=url.pathname==='/'?'index.html':url.pathname.slice(1);
  if(['index.html','app.js','app.css'].includes(name)){
    response.writeHead(200,{'Content-Type':name.endsWith('.js')?'application/javascript':name.endsWith('.css')?'text/css':'text/html'});
    return response.end(fs.readFileSync(path.join(root,'assets/tools',name)));
  }
  response.writeHead(204);response.end();
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launchPersistentContext(path.join(temporary,'profile'),{executablePath:process.env.EDGE_PATH,headless:true,viewport:{width:1280,height:800},downloadsPath:temporary});
  const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
  // 像素断言会高频回读画布；仅测试上下文使用 CPU 回读模式，产品仍使用默认绘制路径。
  await page.addInitScript(()=>{const original=HTMLCanvasElement.prototype.getContext;HTMLCanvasElement.prototype.getContext=function(type,options){return original.call(this,type,type==='2d'?{...options,willReadFrequently:true}:options);};});
  page.on('console',message=>{if(['error','warning'].includes(message.type()))errors.push(message.text());});
  const pixel=async(x,y)=>page.locator('#screen').evaluate((canvas,p)=>Array.from(canvas.getContext('2d').getImageData(p.x,p.y,1,1).data),{x,y});
  const waitPixel=async(x,y,index,min)=>page.waitForFunction(p=>document.querySelector('#screen').getContext('2d').getImageData(p.x,p.y,1,1).data[p.index]>p.min,{x,y,index,min});
  const connect=async()=>{await page.locator('#code').fill('123456');await page.locator('#connect').click();await page.waitForFunction(()=>document.querySelector('#screen').naturalWidth===400);};
  try{
    await page.goto(`http://127.0.0.1:${server.address().port}/#remote`);await connect();
    check('real C# full frame decoded',(await pixel(16,16)).slice(0,3).every(value=>value>240));
    await page.evaluate(()=>document.documentElement.dataset.native='true');
    check('Android native toolbar removes zoom button',await page.locator('#zoom').isHidden());
    await page.evaluate(()=>delete document.documentElement.dataset.native);
    check('desktop browser keeps zoom button',await page.locator('#zoom').isVisible());
    const before=screens;await page.waitForTimeout(1050);check('idle polling stays bounded',screens-before<=5);
    frame=tile;await page.waitForFunction(()=>{const p=document.querySelector('#screen').getContext('2d').getImageData(16,16,1,1).data;return p[2]>150&&p[0]<90;});
    check('real C# dirty tile preserves untouched pixels',(await pixel(300,250)).slice(0,3).every(value=>value>240));
    frame=large;await page.waitForFunction(()=>{const p=document.querySelector('#screen').getContext('2d').getImageData(300,250,1,1).data;return p[1]>90&&p[0]<60;});check('full fallback replaces old pixels',(await pixel(16,16))[1]>90);
    badFrame=true;await page.waitForFunction(()=>document.querySelector('#remote-error').textContent.includes('重试'));await page.waitForFunction(()=>document.querySelector('#remote-error').hidden);
    check('malformed tile recovers with full frame',await page.locator('#remote').isVisible());
    for(const dark of [false,true]){
      await page.emulateMedia({colorScheme:dark?'dark':'light'});
      for(const [width,height] of [[360,800],[768,1024],[1280,800],[800,360]]){
        await page.setViewportSize({width,height});await page.waitForTimeout(60);
        check('new controls no horizontal overflow '+width+'/'+dark,await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
        await page.screenshot({path:path.join(output,`remote-${width}x${height}-${dark?'dark':'light'}.png`)});
      }
    }
    await page.locator('#layout').click();check('stacked mouse buttons fit with new controls',await page.locator('#drag').evaluate(node=>node.getBoundingClientRect().bottom<=innerHeight));await page.locator('#layout').click();
    await page.setViewportSize({width:1280,height:800});
    await page.locator('[data-modifier=ctrl]').click();await page.locator('[data-modifier=shift]').click();await page.locator('#chord-key').fill('a');await page.locator('#chord-send').click();
    await page.waitForFunction(()=>document.querySelector('[data-modifier=ctrl]').getAttribute('aria-pressed')==='false');
    await page.waitForTimeout(100);check('modifier builder sends one chord',inputs.some(input=>input.type==='chord'&&input.key==='a'&&input.modifiers.join(',')==='ctrl,shift'));
    const prior=inputs.length;await page.locator('#touchpad').focus();await page.keyboard.press('Control+Shift+ArrowLeft');await page.waitForTimeout(100);
    check('physical keyboard forwards modifiers',inputs.slice(prior).some(input=>input.type==='chord'&&input.key==='left'&&input.modifiers.includes('ctrl')&&input.modifiers.includes('shift')));
    const beforeMeta=inputs.length;
    await page.locator('#touchpad').evaluate(node=>{node.focus();node.dispatchEvent(new KeyboardEvent('keydown',{key:'Meta',metaKey:true,bubbles:true,cancelable:true}));node.dispatchEvent(new KeyboardEvent('keydown',{key:'d',metaKey:true,bubbles:true,cancelable:true}));});await page.waitForTimeout(100);
    check('physical Win chord does not first open Start menu',inputs.length===beforeMeta+1&&inputs[beforeMeta].key==='d'&&inputs[beforeMeta].modifiers.join(',')==='win');
    await page.locator('[data-chord="win+d"]').click();await page.waitForTimeout(100);check('system shortcut encoded without local execution',inputs.some(input=>input.key==='d'&&input.modifiers.join(',')==='win'));
    await page.locator('#keyboard').click();await page.locator('#remote-text').fill('中文 😀\nEnglish\t长文本');await page.locator('#clipboard-send').click();
    await page.waitForFunction(()=>document.querySelector('#remote-error').textContent.includes('已写入'));check('clipboard push is single explicit action',clipboardWrites===1&&clipboardText==='中文 😀\nEnglish\t长文本');
    await page.locator('#remote-text').fill('');await page.locator('#clipboard-read').click();await page.waitForFunction(()=>document.querySelector('#remote-text').value.includes('English'));check('clipboard pull fills text input',reads===1);
    allowRead=false;await page.waitForFunction(()=>document.querySelector('#clipboard-read').hidden,null,{timeout:6500});
    check('permission revocation hides read during active session',await page.locator('#remote').isVisible());
    allowRead=true;await page.waitForFunction(()=>!document.querySelector('#clipboard-read').hidden,null,{timeout:6500});
    check('permission enable restores button without reading or losing draft',reads===1&&(await page.locator('#remote-text').inputValue()).includes('English'));
    await page.locator('#remote-text').fill('');await page.evaluate(()=>Object.defineProperty(navigator,'clipboard',{value:{readText:()=>Promise.reject(new Error('denied'))},configurable:true}));
    await page.locator('#clipboard-send').click();await page.waitForFunction(()=>document.querySelector('#remote-error').textContent.includes('请先将文字粘贴到输入框'));check('WebView denied clipboard has input fallback',clipboardWrites===1);
    await page.screenshot({path:path.join(output,'keyboard-clipboard-dark.png')});
    await page.evaluate(()=>window.feiqStop());check('disconnect clears natural canvas size',await page.locator('#screen').evaluate(canvas=>canvas.naturalWidth===0&&canvas.naturalHeight===0));
    phone=true;allowRead=false;frame=full;await connect();
    check('phone hides desktop keys and clipboard read',await page.locator('#chord-controls').isHidden()&&await page.locator('#clipboard-read').isHidden()&&await page.locator('#clipboard-send').isVisible());
    await page.locator('#remote-text').fill('发往手机');await page.locator('#clipboard-send').click();await page.waitForFunction(()=>document.querySelector('#remote-error').textContent.includes('已写入'));check('phone can explicitly push clipboard',clipboardText==='发往手机'&&clipboardWrites===2);
    await page.evaluate(()=>window.feiqStop());check('no JavaScript errors or warnings',errors.length===0);
    console.log(`${results.length} remote enhancement browser checks passed`);
  }catch(error){await page.screenshot({path:path.join(output,'failure.png')});throw error;}
  finally{fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:results.length,results,errors},null,2));await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
