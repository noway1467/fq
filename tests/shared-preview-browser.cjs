'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const {folderFixtures,zipContents,waitForListing,textContrast}=require('./shared-browser-helpers.cjs');
const root=path.resolve(__dirname,'..'),data=path.resolve(process.env.FQ_BROWSER_DATA||path.join(root,'build/test-data/r0822/browser'));
const output=path.resolve(process.env.FQ_VALIDATION_DIR||path.join(root,'build/release-0.8.22/browser')),temp=path.resolve(process.env.FQ_BROWSER_TEMP||path.join(root,'build/tmp/r0822/browser'));
assert.ok([data,output,temp].every(file=>file.startsWith(root+path.sep)),'浏览器验证资料必须位于项目内');
fs.mkdirSync(output,{recursive:true});fs.mkdirSync(temp,{recursive:true});
const server=JSON.parse(fs.readFileSync(path.join(data,'server.json'),'utf8'));
(async()=>{
  const context=await chromium.launchPersistentContext(path.join(temp,'profile'),{executablePath:process.env.EDGE_PATH,headless:true,downloadsPath:temp});
  const page=await context.newPage(),errors=[],checks=[];
  await page.emulateMedia({reducedMotion:'reduce'});
  page.on('pageerror',error=>errors.push(error.message));
  const check=(name,value)=>{assert.ok(value,name);checks.push(name);};
  try{
    folderFixtures(data);
    await page.goto(`http://127.0.0.1:${server.port}/#share`);await page.locator('#code').fill(server.share);await page.locator('#connect').click();
    const open=async name=>{
      const opened=context.waitForEvent('page');await page.getByRole('link',{name,exact:true}).click();
      const tab=await opened;await tab.waitForLoadState('domcontentloaded');return tab;
    };
    const image=await open('查看 图片 sample.png');
    await image.waitForFunction(()=>document.querySelector('img')?.naturalWidth===480);
    check('图片在浏览器独立页面显示',image!==page&&await image.locator('img').isVisible());
    check('文件页面隔离 opener',await image.evaluate(()=>window.opener===null));
    await image.screenshot({path:path.join(output,'image-native.png')});await image.close();
    for(const theme of ['light','dark'])for(const width of [360,768,1280]){
      await page.emulateMedia({colorScheme:theme});await page.setViewportSize({width,height:800});
      check(`文件列表无横向溢出 ${theme} ${width}`,await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      const contrast=await textContrast(page,'.file-name,.file-size,.btn-play,.btn-download,#breadcrumbs button,#sort,#order,#refresh,#download-folder,#count,#page-title');
      check(`文件文字对比度至少 4.5 ${theme} ${width} ${JSON.stringify(contrast.filter(item=>item.ratio<4.5))}`,contrast.length>0&&contrast.every(item=>item.ratio>=4.5));
      await page.screenshot({path:path.join(output,`files-${theme}-${width}.png`),fullPage:true});
    }
    const text=await open('说明.txt');
    check('点击文件名直接打开纯文本且HTML不执行',await text.locator('pre').innerText()==='中文 English <script>window.bad=true</script>'&&await text.evaluate(()=>!window.bad));
    await text.screenshot({path:path.join(output,'text-native.png')});await text.close();
    const empty=await open('查看 empty.txt');check('空文本独立打开',await empty.locator('body').innerText()==='');await empty.close();
    const video=await open('播放 playback.webm');
    await video.waitForFunction(()=>document.querySelector('video')?.readyState>=2);
    await video.locator('video').evaluate(video=>video.play());
    await video.waitForFunction(()=>document.querySelector('video').currentTime>.3);
    await video.locator('video').evaluate(video=>{video.currentTime=2;});
    await video.waitForFunction(()=>!document.querySelector('video').seeking&&document.querySelector('video').currentTime>=2);
    check('浏览器原生视频播放器可播放和跳转',true);await video.screenshot({path:path.join(output,'video-native.png')});await video.close();
    const pdf=page.getByRole('link',{name:'查看 document.pdf',exact:true});
    const response=await page.request.get(new URL(await pdf.getAttribute('href'),page.url()).href);
    check('PDF提供浏览器查看且不是强制附件',response.ok()&&response.headers()['content-type']==='application/pdf'&&!response.headers()['content-disposition']&&await pdf.getAttribute('target')==='_blank');
    const pdfTab=await open('查看 document.pdf');await pdfTab.locator('embed[type="application/pdf"]').waitFor();check('PDF浏览器查看器已打开',true);await pdfTab.close();
    check('共享列表不再含页底播放器或预览区',await page.locator('#player-panel,video,#preview-image,#preview-text').count()===0);
    await page.getByRole('button',{name:'进入 演示子目录',exact:true}).click();await waitForListing(page);const nativeLink=new URL(await page.locator('#download-folder').getAttribute('href'),page.url());check('共享目录提供带真实 ID 的原生下载链接',nativeLink.searchParams.get('archive')==='store'&&nativeLink.searchParams.get('id')&&nativeLink.searchParams.get('token'));
    const downloaded=page.waitForEvent('download');await page.locator('#download-folder').click();const archive=await downloaded;await archive.saveAs(path.join(output,'shared-directory.zip'));const files=zipContents(fs.readFileSync(path.join(output,'shared-directory.zip')));check('目录 ZIP 的 UTF-8 名称与文件内容一致',files.get('目录说明.txt').equals(fs.readFileSync(path.join(data,'shared/演示子目录/目录说明.txt'))));
    await page.locator('#breadcrumbs button').first().click();await waitForListing(page);await page.getByRole('button',{name:'进入 空目录',exact:true}).click();await waitForListing(page);check('空目录状态与分页控件一致',await page.locator('.empty').innerText()==='这个文件夹是空的'&&await page.locator('#load-more').isHidden());await page.locator('#breadcrumbs button').first().click();await waitForListing(page);
    fs.writeFileSync(path.join(data,'add-folder'),'add');
    await page.getByRole('button',{name:'进入 additional',exact:true}).waitFor({timeout:12000});
    check('增加目录自动刷新且无需重新连接',await page.locator('#files').isVisible()&&await page.locator('#login').isHidden());
    await page.getByRole('button',{name:'进入 shared',exact:true}).click();await page.getByRole('link',{name:'查看 说明.txt',exact:true}).waitFor();
    fs.writeFileSync(path.join(data,'remove-folder'),'remove');
    await page.locator('.file-name',{hasText:'remaining.txt'}).waitFor({timeout:12000});
    check('移除当前目录自动返回可访问目录',await page.locator('#login').isHidden());
    await page.locator('#disconnect').click();await page.locator('#reconnect').click();await page.locator('.file-name',{hasText:'remaining.txt'}).waitFor();
    check('目录变化后配对仍可重连',true);
    await page.locator('#search').fill('不存在');await waitForListing(page);check('空搜索状态',await page.locator('.empty').innerText()==='没有匹配的文件');
    assert.deepEqual(errors,[]);checks.push('无脚本错误');
    fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:checks.length,checks},null,2));console.log(`PASS ${checks.length} browser checks`);
  }catch(error){await page.screenshot({path:path.join(output,'failure.png'),fullPage:true});throw error;}finally{await context.close();fs.writeFileSync(path.join(data,'stop'),'stop');}
})().catch(error=>{console.error(error);process.exitCode=1;});
