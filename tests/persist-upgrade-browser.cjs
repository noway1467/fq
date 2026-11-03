'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),data=path.join(root,'build/test-data/persist-upgrade'),output=path.join(root,'build/persist-upgrade-validation');
const profile=path.join(root,'build/tmp/persist-upgrade/browser-profile');
const server=JSON.parse(fs.readFileSync(path.join(data,'server.json'),'utf8'));fs.mkdirSync(output,{recursive:true});
const results=[],errors=[];
const check=(name,condition)=>{assert.ok(condition,name);results.push(name);};
(async()=>{
  for(let run=0;run<2;run++){
    const browser=await chromium.launchPersistentContext(profile,{executablePath:process.env.EDGE_PATH,headless:true,viewport:{width:800,height:360},hasTouch:true});
    try{
      const page=await browser.newPage();page.on('pageerror',e=>errors.push(e.message));
      for(const mode of ['share','remote']){
        await page.goto(`http://127.0.0.1:${server.port}/#${mode}`);
        if(run===0){await page.locator('#code').fill(server[mode]);await page.locator('#connect').click();}
        else{check(mode+' code stays empty on browser restart',await page.locator('#code').inputValue()==='');await page.locator('#reconnect').click();}
        await page.locator(mode==='share'?'#files':'#remote').waitFor({state:'visible'});
        if(mode==='share')await page.locator('.file-row').first().waitFor();else await page.waitForFunction(()=>document.getElementById('screen').naturalWidth>0);
        check(`${mode} connected ${run===0?'before':'after'} browser restart`,true);
        const cookie=(await browser.cookies()).find(c=>c.name===`fq_pair_${mode}_${server.port}`);
        check(`${mode} pairing cookie persists and is HttpOnly`,cookie&&cookie.httpOnly&&cookie.expires>Date.now()/1000);
        if(run===1)await page.screenshot({path:path.join(output,mode+'-restored.png')});
        const logout=page.waitForResponse(r=>r.url().endsWith('/api/logout'));await page.locator(mode==='share'?'#disconnect':'#remote-disconnect').click();await logout;
      }
    }finally{await browser.close();}
  }
  check('no page errors',errors.length===0);fs.writeFileSync(path.join(root,'build/persist-upgrade-browser.json'),JSON.stringify({passed:results.length,results,errors},null,2));console.log(results.length+' browser persistence checks passed');
})().catch(e=>{console.error(e);fs.writeFileSync(path.join(root,'build/persist-upgrade-browser.json'),JSON.stringify({passed:results.length,results,errors,failure:e.message},null,2));process.exitCode=1;});
