'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '..');
const temporary = path.join(root, 'build/tmp/tools-network');
const output = process.env.FQ_VALIDATION_DIR || path.join(root, 'build/tools-network-validation');
fs.mkdirSync(temporary, {recursive:true});
fs.mkdirSync(output, {recursive:true});
let stalled = '';
const sockets = new Set();
const results = [];
const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  request.resume();
  const json = value => { response.writeHead(200, {'Content-Type':'application/json'}); response.end(JSON.stringify(value)); };
  if (url.pathname === '/api/' + stalled) {
    response.writeHead(200, {'Content-Type':stalled === 'screen' ? 'image/jpeg' : 'application/json', 'Content-Length':'1024'});
    response.write(stalled === 'screen' ? Buffer.from([255,216,255]) : '{');
    return;
  }
  if (url.pathname === '/api/login') return json({token:'test-session'});
  if (url.pathname === '/api/status') return json({remote:true,share:true,platform:'windows'});
  if (url.pathname === '/api/displays') return json({items:[{id:0,name:'Test'}]});
  if (url.pathname === '/api/list') return json({items:[]});
  if (url.pathname === '/api/input' || url.pathname === '/api/logout') return json({ok:true});
  if (url.pathname === '/api/screen') {
    response.writeHead(200, {'Content-Type':'image/gif'});
    return response.end(Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7','base64'));
  }
  const asset = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
  if (['index.html','app.js','app.css'].includes(asset)) {
    response.writeHead(200, {'Content-Type':asset.endsWith('.js') ? 'application/javascript' : asset.endsWith('.css') ? 'text/css' : 'text/html'});
    return response.end(fs.readFileSync(path.join(root,'assets/tools',asset)));
  }
  response.writeHead(404); response.end();
});
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
(async () => {
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  let browser;
  try {
    browser = await chromium.launchPersistentContext(path.join(temporary,'profile'), {executablePath:process.env.EDGE_PATH,headless:true,viewport:{width:1280,height:800},downloadsPath:temporary});
    for (const scenario of ['list','screen','input']) {
      stalled = scenario;
      const page = await browser.newPage();
      const errors = []; page.on('pageerror', error => errors.push(error.message));
      try {
        await page.goto(`http://127.0.0.1:${server.address().port}/${scenario === 'list' ? '' : '#remote'}`);
        await page.locator('#code').fill('123456'); await page.locator('#connect').click();
        if (scenario === 'input') {
          await page.waitForFunction(() => document.querySelector('#screen').naturalWidth > 0);
          await page.locator('#keyboard').click();
          await page.locator('#remote-text').fill('Network test'); await page.locator('#type-text').click();
        }
        await page.waitForFunction(() => !document.querySelector('#status').hidden && document.querySelector('#status').classList.contains('error'), null, {timeout:13000});
        if (scenario !== 'list') assert.equal(await page.locator('#remote').isVisible(),false);
        else assert.equal(await page.locator('#refresh').isEnabled(),true);
        assert.equal(errors.length,0,errors.join('\n'));
        results.push({scenario,passed:true});
      } catch (error) { results.push({scenario,passed:false,error:error.message}); }
      finally { await page.close(); for (const socket of sockets) socket.destroy(); }
    }
  } finally {
    if (browser) await browser.close();
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => server.close(resolve));
  }
  fs.writeFileSync(path.join(output,'browser-network.json'),JSON.stringify(results,null,2)+'\n');
  console.log(JSON.stringify(results,null,2));
  if (results.length !== 3 || results.some(result => !result.passed)) process.exitCode=1;
})().catch(error => { console.error(error); process.exitCode=1; });
