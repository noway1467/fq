'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),data=path.resolve(process.env.FQ_PHASE2_BROWSER_DATA||path.join(root,'build/tmp/p2web/data')),out=path.join(root,'build/phase2-fixes/browser');
assert.ok(data.startsWith(path.join(root,'build/tmp')+path.sep),'浏览器资料必须在项目临时目录内');
fs.mkdirSync(out,{recursive:true});
const server=JSON.parse(fs.readFileSync(path.join(data,'server.json'),'utf8'));
(async()=>{
  const browser=await chromium.launchPersistentContext(path.join(root,'build/tmp/p2web/profile'),{headless:true,executablePath:process.env.EDGE_PATH,viewport:{width:900,height:700},acceptDownloads:true,downloadsPath:path.join(root,'build/tmp/p2web/downloads')});
  let passed=0;const errors=[];
  try{
    const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.port}/#share`);await page.locator('#code').fill(server.share);await page.locator('#connect').click();await page.locator('.file-row').first().waitFor();
    let checked=0,lists=0;page.on('request',request=>{if(new URL(request.url()).pathname==='/api/list')lists++;});
    await page.route('**/api/entry?*',route=>++checked===1?route.fulfill({status:404,json:{error:'文件索引已失效，请刷新目录'}}):route.continue());
    const before=lists;await page.locator('.file-row').filter({hasText:'中文 English 😀.txt'}).getByRole('link',{name:'下载',exact:false}).click();
    await page.getByText('文件链接已过期或目录已移除，已刷新列表。请重新选择文件下载。',{exact:true}).waitFor();assert.ok(lists>before);passed++;
    const downloadEvent=page.waitForEvent('download');await page.locator('.file-row').filter({hasText:'中文 English 😀.txt'}).getByRole('link',{name:'下载',exact:false}).click();const download=await downloadEvent;
    assert.equal(fs.readFileSync(await download.path(),'utf8'),'共享链接刷新后的原文件内容');passed++;
    for(const theme of ['light','dark']){
      await page.mouse.move(0,0);await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.evaluate(()=>Promise.all(document.getAnimations().map(animation=>animation.finished.catch(()=>{}))));await page.screenshot({path:path.join(out,`shared-${theme}.png`),fullPage:true});
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));passed++;
    }
    await page.locator('#search').fill('does-not-exist');await page.getByText('没有匹配的文件',{exact:true}).waitFor();passed++;
    await page.locator('#search').fill('');await page.locator('.file-row').first().waitFor();
    await page.setViewportSize({width:360,height:760});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:path.join(out,'shared-mobile.png'),fullPage:true});passed++;
    assert.deepEqual(errors,[]);passed++;fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed,failed:0,errors},null,2));console.log(`${passed} passed, 0 failed`);
  }catch(error){fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed,failed:1,error:String(error),errors},null,2));throw error;}
  finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
