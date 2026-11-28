'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Element {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.value = '';
    this.attributes = {};
    this.hidden = false;
    this.className = '';
    this.classList = { add: name => { this.className += ' ' + name; }, remove: name => { this.className = this.className.split(' ').filter(item => item !== name).join(' '); } };
  }
  append(...children) { for (const child of children) child.parentNode = this; this.children.push(...children); }
  prepend(...children) { for (const child of children) child.parentNode = this; this.children.unshift(...children); }
  replaceChildren(...children) { this.children = children; }
  remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(child => child !== this); }
  select() {}
  addEventListener() {}
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
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
  const prepared = [], canceled = [], textPosts = [], finishResolvers = [];
  const events = {};
  let resolveTextPoll, resolveTextPost;
  let finished = 0;
  let now = Date.now(), elapsed = 0;
  const clock = class extends Date { static now() { return now + (options.clockSkew || 0); } };
  const intervals = [];
  const until = now + minutes * 60000;
  const response = data => ({ ok: true, json: async () => data });
  let failCancel=options.failCancel;
  let failChunkOnce=options.failChunkOnce;
  const fetch = async (url, init) => {
    if (url === '/api/guest') return response({ files: options.files || [], directories: options.directories || [], archive: {enabled:true}, text: options.text === true, upload: true, until: new Date(until).toISOString(), ...(legacy?{}:{remainingMilliseconds:minutes*60000,limit:100*1024**3,countLimit:1000,requestIds:true,folders:true}) });
    if (url.startsWith('/api/text?')) {
      if(options.delayTextPoll)return new Promise(resolve=>resolveTextPoll=value=>resolve(response(value)));
      return response({items:(options.hostTexts || []).filter(item=>item.seq>Number(url.split('=')[1])),last:(options.hostTexts || []).length});
    }
    if (url === '/api/text') {
      textPosts.push({url,body:JSON.parse(init.body),headers:init.headers});
      if(options.delayTextPost)return new Promise(resolve=>resolveTextPost=()=>resolve(response({ok:true})));
      return response({ok:true});
    }
    if (url === '/api/prepare') {
      prepared.push(JSON.parse(init.body));
      if(options.delayPrepare)return new Promise((resolve,reject)=>{init.signal.addEventListener('abort',()=>reject(Object.assign(new Error('aborted'),{name:'AbortError'})),{once:true});});
      return response({ id: String(prepared.length), offset: 0 });
    }
    if (url.startsWith('/api/upload-status')) return response({ offset: 0, complete: false });
    if (url.startsWith('/api/cancel')) { canceled.push(url);if(failCancel){failCancel=false;throw new Error('断网');}return response({ ok: true }); }
    if (url.startsWith('/api/finish')) {
      if(options.delayFinish)return new Promise((resolve,reject)=>{
        finishResolvers.push(()=>{finished++;resolve(response({complete:true}));});
        init.signal.addEventListener('abort',()=>reject(Object.assign(new Error('aborted'),{name:'AbortError'})),{once:true});
      });
      finished++; return response({ complete: true });
    }
    if (url.startsWith('/api/chunk')) {
      if(failChunkOnce){failChunkOnce=false;throw new Error('断网');}
      if(options.confirmChunk)return response({offset:options.confirmChunk});
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
    document: { getElementById: element, createElement: () => new Element(), querySelector: () => element('notice'), querySelectorAll: () => [], execCommand:()=>false },
    location: { hash: '#' + 'a'.repeat(64), origin: 'http://127.0.0.1' },
    navigator: {}, window: {addEventListener:(name,callback)=>events[name]=callback}, crypto:require('node:crypto').webcrypto, Blob, AbortController, TextEncoder, performance:{now:()=>elapsed}, fetch, setTimeout, clearTimeout, Date: clock, setInterval: callback => { intervals.push(callback); return intervals.length; }, clearInterval() {},
  });
  const rows = () => element('uploads').children;
  const cancelRow = row => row.children[2].children[1].onclick();
  return {
    prepared, canceled, rows, element, textPosts, finishResolvers, get finished() { return finished; },
    add(files) { element('picker').onchange({ target: { files, value: '' } }); },
    cancelRow,
    resolveTextPoll(value) { resolveTextPoll(value); },
    resolveTextPost() { resolveTextPost(); },
    expire() { elapsed=minutes*60000; now = until; intervals.forEach(callback => callback()); },
    shiftClock(milliseconds) { now+=milliseconds; intervals.forEach(callback=>callback()); },
    async dispose() {
      await Promise.all(rows().filter(row => !row.children[2].children[1].hidden).map(cancelRow));
      events.pagehide();
      await tick();
    },
  };
}
let passed = 0;
const pass = name => { passed++; console.log('PASS ' + name); };

async function run(name, test) {
  const page = browser();
  try { await tick(); await test(page); pass(name); }
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
  try { await tick();legacy.add(Array.from({length:101},(_,i)=>file('old-'+i,0)));await tick();assert.equal(legacy.rows().length,0);assert.ok(legacy.element('upload-quota').textContent.includes('100 个'));pass('旧服务端缺少数量字段时保留100个和10GiB兼容上限'); }
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
      pass('浏览器 ' + minutes + ' 分钟倒计时、到期关闭及上传终态');
    } finally { await page.dispose(); }
  }
  const racing=browser(60,false,{delayPrepare:true,failCancel:true});
  try{
    await tick();racing.add([file('准备中.bin',60*1024**3)]);await tick();await racing.cancelRow(racing.rows()[0]);await tick();
    const row=racing.rows()[0];assert.ok(row.children[1].children[1].textContent.includes('取消未确认'));assert.equal(row.children[2].children[0].textContent,'重试取消');
    racing.element('cancel-queued').onclick();assert.ok(row.children[1].children[1].textContent.includes('取消未确认'));assert.equal(racing.canceled.length,1);
    assert.match(racing.canceled[0],new RegExp('requestId='+racing.prepared[0].requestId+'$'));
    racing.add([file('仍占额.bin',60*1024**3)]);assert.equal(racing.rows().length,1);
    await row.children[2].children[0].onclick();assert.equal(row.children[1].children[1].textContent,'已取消');
    racing.add([file('已返额.bin',60*1024**3)]);await tick();assert.equal(racing.rows().length,2);
    pass('准备响应未到仍可取消，取消失败保留配额且可重试');
  }finally{await racing.dispose();}
  await run('文件夹路径随准备请求发送且列表可区分同名文件',async page=>{
    page.add([{...file('a.txt',0),webkitRelativePath:'资料/子目录/a.txt'}]);await tick();assert.equal(page.prepared[0].relativePath,'资料/子目录/a.txt');assert.equal(page.rows()[0].children[1].children[0].textContent,'资料/子目录/a.txt');
  });
  for(const skew of [-86400000,86400000]) {
    const page=browser(5,false,{clockSkew:skew});
    try {
      await tick();assert.ok(page.element('session').textContent.includes('5分00秒'));assert.notEqual(page.element('picker').disabled,true);
      page.shiftClock(-skew*2);assert.ok(page.element('session').textContent.includes('5分00秒'));
      page.expire();assert.equal(page.element('picker').disabled,true);
      pass('访客时钟偏差及中途调时不影响剩余时间 '+skew);
    } finally {await page.dispose();}
  }
  await run('取消全部未开始仅修改本地快照，保持正在上传任务', async page => {
    page.add([file('上传中',5),file('排队一',7),file('排队二',9)]);await tick();
    assert.equal(page.element('cancel-queued').disabled,false);page.element('cancel-queued').onclick();await tick();
    assert.equal(page.prepared.length,1);assert.equal(page.canceled.length,0);
    assert.equal(page.rows()[0].children[2].children[1].hidden,false);
    assert.equal(page.rows()[1].children[1].children[1].textContent,'已取消（未开始）');
    assert.equal(page.rows()[2].children[1].children[1].textContent,'已取消（未开始）');
    assert.equal(page.element('upload-saved').textContent,'已保存件数：0 / 1');
    assert.equal(page.element('upload-total').max,5);
  });
  const preparing=browser(60,false,{delayPrepare:true});
  try {
    await tick();preparing.add([file('准备中',3),file('未开始',4)]);await tick();preparing.element('cancel-queued').onclick();await tick();
    assert.equal(preparing.prepared.length,1);assert.equal(preparing.canceled.length,0);assert.notEqual(preparing.rows()[0].children[1].children[1].textContent,'已取消（未开始）');
    assert.equal(preparing.rows()[1].children[1].children[1].textContent,'已取消（未开始）');pass('批量取消不动准备中项');
  } finally { await preparing.dispose(); }
  const resumed=browser(60,false,{failChunkOnce:true});
  try {
    await tick();resumed.add([file('重试项',3),file('正在上传',4),file('新排队项',5)]);await tick();
    resumed.rows()[0].children[2].children[0].onclick();resumed.element('cancel-queued').onclick();await tick();
    assert.ok(resumed.rows()[0].children[1].children[1].textContent.includes('准备续传'));
    assert.equal(resumed.rows()[2].children[1].children[1].textContent,'已取消（未开始）');assert.equal(resumed.canceled.length,0);pass('批量取消不动已开始的排队续传项');
  } finally { await resumed.dispose(); }
  const summary=browser(60,false,{confirmChunk:10,delayFinish:true});
  try {
    await tick();summary.add([file('等待保存',10)]);await tick();
    assert.equal(summary.element('upload-bytes').textContent,'已传字节：10 B / 10 B（100%）');
    assert.equal(summary.element('upload-saved').textContent,'已保存件数：0 / 1');
    summary.finishResolvers.shift()();await tick();assert.equal(summary.element('upload-saved').textContent,'已保存件数：1 / 1');pass('字节已确认100%不冒充保存完成');
  } finally { await summary.dispose(); }
  const emptySummary=browser(60,false,{delayFinish:true});
  try {
    await tick();emptySummary.add([file('空一',0),file('空二',0)]);await tick();
    assert.equal(emptySummary.element('upload-bytes').textContent,'已传字节：0 B / 0 B');assert.equal(emptySummary.element('upload-total').value,0);
    assert.equal(emptySummary.element('upload-saved').textContent,'已保存件数：0 / 2');emptySummary.finishResolvers.shift()();await tick();
    assert.equal(emptySummary.element('upload-saved').textContent,'已保存件数：1 / 2');pass('全零字节队列按服务端保存确认统计');
  } finally { await emptySummary.dispose(); }
  const allNodes=node=>[node,...node.children.flatMap(allNodes)];
  const search=browser(60,false,{files:[{id:'1',name:'中文.txt',relativePath:'资料/子目录/中文.txt',size:1},{id:'2',name:'English.txt',relativePath:'资料/English.txt',size:2},{id:'3',name:'root.bin',size:3}],directories:[{id:'dir',name:'资料',relativePath:'资料'}]});
  try {
    await tick();const group=()=>allNodes(search.element('directory-downloads')).find(node=>node.dataset.path==='资料');
    group().children[0].children[0].onclick();assert.equal(group().children[1].hidden,true);
    search.element('download-search').value='子目录/中文';search.element('download-search').oninput();assert.equal(group().children[1].hidden,false);
    assert.equal(group().children[0].children[0].disabled,true);assert.deepEqual(allNodes(search.element('directory-downloads')).filter(node=>node.dataset.file).map(node=>node.dataset.file),['1']);
    search.element('download-search').value='';search.element('download-search').oninput();assert.equal(group().children[1].hidden,true);
    search.element('download-search').value='english';search.element('download-search').oninput();assert.ok(search.element('count').textContent.startsWith('1 / 3'));
    search.element('download-search').value='missing';search.element('download-search').oninput();assert.equal(search.element('downloads').children[0].textContent,'未找到匹配的文件');pass('名称与相对路径搜索临时展开祖先，清词恢复折叠');
  } finally { await search.dispose(); }
  const text=browser(60,false,{text:true,hostTexts:[{seq:1,text:'<img src=x onerror=alert(1)>\n中文 English 😀'}]});
  try {
    await tick();assert.equal(text.element('text-transfer').hidden,false);
    assert.equal(text.element('text-received').children[0].children[0].textContent,'<img src=x onerror=alert(1)>\n中文 English 😀');assert.equal(text.element('text-received').children[0].children[0].children.length,0);
    text.element('text-draft').value='😀'.repeat(1024)+'x';await text.element('text-send').onclick();assert.equal(text.textPosts.length,0);
    text.element('text-draft').value='\0\0';await text.element('text-send').onclick();assert.equal(text.textPosts.length,0);
    text.element('text-draft').value='访客 <svg onload=alert(1)> 中文';await text.element('text-send').onclick();assert.equal(text.textPosts[0].url,'/api/text');assert.equal(text.textPosts[0].body.text,'访客 <svg onload=alert(1)> 中文');assert.equal(text.textPosts[0].headers['X-FQ-Guest'],'a'.repeat(64));
    assert.equal(text.element('text-sent').children[0].children[0].children.length,0);text.expire();await tick();assert.equal(text.element('text-received').children.length,0);assert.equal(text.element('text-sent').children.length,0);assert.equal(text.element('text-draft').value,'');pass('文本纯文本渲染、正文投递、UTF8限制和到期清空');
  } finally { await text.dispose(); }
  for(const direction of ['poll','post']) {
    const late=browser(1,false,{text:true,delayTextPoll:direction==='poll',delayTextPost:direction==='post'});
    try {
      await tick();let posting;
      if(direction==='post'){late.element('text-draft').value='迟到发送';posting=late.element('text-send').onclick();await tick();}
      late.expire();
      if(direction==='poll')late.resolveTextPoll({items:[{seq:1,text:'迟到发布'}],last:1});else late.resolveTextPost();
      if(posting)await posting;await tick();assert.equal(late.element('text-received').children.length,0);assert.equal(late.element('text-sent').children.length,0);assert.equal(late.element('text-status').textContent,'');assert.equal(late.element('text-transfer').hidden,true);pass('文本结束后迟到 '+direction+' 回调不能恢复内容');
    } finally { await late.dispose(); }
  }
  console.log('TOTAL '+passed+' passed, 0 failed');
})().catch(error => { console.error(error); process.exitCode = 1; });
