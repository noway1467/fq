'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { performance } = require('node:perf_hooks');

class Element {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.hidden = false;
    this.classList = { add() {}, remove() {} };
  }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  addEventListener() {}
  setAttribute() {}
  removeAttribute() {}
}

const source = fs.readFileSync(path.join(__dirname, '../assets/tools/guest.js'), 'utf8');
const tick = async () => { for (let i = 0; i < 4; i++) await new Promise(resolve => setImmediate(resolve)); };
const file = (name, size) => ({ name, size, slice: () => new Blob(['x']) });

function browser(minutes = 60, legacy = false, options = {}) {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const prepared = [], canceled = [];
  let finished = 0;
  let now = Date.now();
  const clock = class extends Date { static now() { return now; } };
  const intervals = [];
  const until = now + minutes * 60000;
  const response = data => ({ ok: true, json: async () => data });
  let failCancel=options.failCancel;
  const fetch = async (url, init) => {
    if (url === '/api/guest') return response({ files: [], upload: true, until: new Date(until).toISOString(), ...(legacy?{}:{limit:100*1024**3,countLimit:1000,requestIds:true,folders:true}) });
    if (url === '/api/prepare') {
      prepared.push(JSON.parse(init.body));
      if(options.delayPrepare)return new Promise((resolve,reject)=>{init.signal.addEventListener('abort',()=>reject(Object.assign(new Error('aborted'),{name:'AbortError'})),{once:true});});
      return response({ id: String(prepared.length), offset: 0 });
    }
    if (url.startsWith('/api/upload-status')) return response({ offset: 0, complete: false });
    if (url.startsWith('/api/cancel')) { canceled.push(url);if(failCancel){failCancel=false;throw new Error('断网');}return response({ ok: true }); }
    if (url.startsWith('/api/finish')) { finished++; return response({ complete: true }); }
    if (url.startsWith('/api/chunk')) {
      // 保持一次上传进行中，真实取消按钮必须通过 AbortController 结束它。
      return new Promise((resolve, reject) => {
        const abort = () => reject(Object.assign(new Error('canceled'), { name: 'AbortError' }));
        if (init.signal.aborted) abort();
        else init.signal.addEventListener('abort', abort, { once: true });
      });
    }
    throw new Error('未预期的 API：' + url);
  };
  vm.runInNewContext(source, {
    document: { getElementById: element, createElement: () => new Element(), querySelector: () => element('notice'), querySelectorAll: () => [] },
    location: { hash: '#' + 'a'.repeat(64), origin: 'http://127.0.0.1' },
    navigator: {}, window: {}, crypto:require('node:crypto').webcrypto, Blob, AbortController, performance, fetch, setTimeout, clearTimeout, Date: clock, setInterval: callback => { intervals.push(callback); return intervals.length; },
  });
  const rows = () => element('uploads').children;
  const cancelRow = row => row.children[2].children[1].onclick();
  return {
    prepared, canceled, rows, element, get finished() { return finished; },
    add(files) { element('picker').onchange({ target: { files, value: '' } }); },
    cancelRow,
    expire() { now = until; intervals.forEach(callback => callback()); },
    async dispose() {
      await Promise.all(rows().filter(row => !row.children[2].children[1].hidden).map(cancelRow));
      await tick();
    },
  };
}

async function run(name, test) {
  const page = browser();
  try { await tick(); await test(page); console.log('PASS ' + name); }
  finally { await page.dispose(); }
}

(async () => {
  await run('浏览器取消大文件后可重选，正在上传的字节仍计入100 GiB', async page => {
    page.add([file('first.bin', 60 * 1024 ** 3)]); await tick();
    page.add([file('blocked.bin', 60 * 1024 ** 3)]); await tick();
    assert.equal(page.rows().length, 1);
    await page.cancelRow(page.rows()[0]); await tick();
    page.add([file('replacement.bin', 60 * 1024 ** 3)]); await tick();
    assert.equal(page.rows().length, 2);
    assert.equal(page.prepared.length, 2);
    assert.equal(page.canceled.length, 1);
  });
  await run('浏览器取消排队文件后释放1000个名额', async page => {
    page.add(Array.from({ length: 1000 }, (_, i) => file('queued-' + i, 1))); await tick();
    page.add([file('blocked', 1)]); assert.equal(page.rows().length, 1000);
    await Promise.all(page.rows().map(page.cancelRow)); await tick();
    page.add([file('replacement', 1)]); await tick();
    assert.equal(page.rows().length, 1001);
    assert.equal(page.prepared.length, 2);
  });
  await run('浏览器已保存文件继续计入1000个上限', async page => {
    page.add(Array.from({ length: 1000 }, (_, i) => file('saved-' + i, 0))); await tick();
    assert.equal(page.finished, 1000);
    page.add([file('blocked', 0)]); await tick();
    assert.equal(page.rows().length, 1000);
    assert.equal(page.prepared.length, 1000);
  });
  const legacy=browser(60,true);
  try { await tick();legacy.add(Array.from({length:101},(_,i)=>file('old-'+i,0)));await tick();assert.equal(legacy.rows().length,0);assert.ok(legacy.element('upload-quota').textContent.includes('100 个'));console.log('PASS 旧服务端缺少数量字段时保留100个和10GiB兼容上限'); }
  finally { await legacy.dispose(); }
  for (const minutes of [1, 60, 1440]) {
    const page = browser(minutes);
    try {
      await tick();
      assert.ok(page.element('session').textContent.includes(minutes + '分00秒后结束'));
      page.add([file('正在上传.bin', 20)]); await tick();
      page.expire(); await tick();
      assert.ok(page.element('session').textContent.includes('已到期'));
      assert.equal(page.element('picker').disabled, true);
      assert.equal(page.element('copy').disabled, true);
      const row = page.rows()[0];
      assert.equal(row.children[2].children[0].hidden, true);
      assert.ok(row.children[1].children[1].textContent.includes('已到期'));
      page.add([file('不能新增.bin', 20)]); await tick();assert.equal(page.prepared.length, 1);
      console.log('PASS 浏览器 ' + minutes + ' 分钟倒计时、到期关闭及上传终态');
    } finally { await page.dispose(); }
  }
  const racing=browser(60,false,{delayPrepare:true,failCancel:true});
  try{
    await tick();racing.add([file('准备中.bin',60*1024**3)]);await tick();await racing.cancelRow(racing.rows()[0]);await tick();
    const row=racing.rows()[0];assert.ok(row.children[1].children[1].textContent.includes('取消未确认'));assert.equal(row.children[2].children[0].textContent,'重试取消');
    assert.match(racing.canceled[0],new RegExp('requestId='+racing.prepared[0].requestId+'$'));
    racing.add([file('仍占额.bin',60*1024**3)]);assert.equal(racing.rows().length,1);
    await row.children[2].children[0].onclick();assert.equal(row.children[1].children[1].textContent,'已取消');
    racing.add([file('已返额.bin',60*1024**3)]);await tick();assert.equal(racing.rows().length,2);
    console.log('PASS 准备响应未到仍可取消，取消失败保留配额且可重试');
  }finally{await racing.dispose();}
  await run('文件夹路径随准备请求发送且列表可区分同名文件',async page=>{
    page.add([{...file('a.txt',0),webkitRelativePath:'资料/子目录/a.txt'}]);await tick();assert.equal(page.prepared[0].relativePath,'资料/子目录/a.txt');assert.equal(page.rows()[0].children[1].children[0].textContent,'资料/子目录/a.txt');
  });
  console.log('TOTAL 9 passed, 0 failed');
})().catch(error => { console.error(error); process.exitCode = 1; });
