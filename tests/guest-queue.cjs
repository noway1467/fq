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

function browser() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const prepared = [], canceled = [];
  let finished = 0;
  const response = data => ({ ok: true, json: async () => data });
  const fetch = async (url, options) => {
    if (url === '/api/guest') return response({ files: [], upload: true, until: new Date(Date.now() + 900000).toISOString() });
    if (url === '/api/prepare') {
      prepared.push(JSON.parse(options.body));
      return response({ id: String(prepared.length), offset: 0 });
    }
    if (url.startsWith('/api/upload-status')) return response({ offset: 0, complete: false });
    if (url.startsWith('/api/cancel')) { canceled.push(url); return response({ ok: true }); }
    if (url.startsWith('/api/finish')) { finished++; return response({ complete: true }); }
    if (url.startsWith('/api/chunk')) {
      // 保持一次上传进行中，真实取消按钮必须通过 AbortController 结束它。
      return new Promise((resolve, reject) => {
        const abort = () => reject(Object.assign(new Error('canceled'), { name: 'AbortError' }));
        if (options.signal.aborted) abort();
        else options.signal.addEventListener('abort', abort, { once: true });
      });
    }
    throw new Error('未预期的 API：' + url);
  };
  vm.runInNewContext(source, {
    document: { getElementById: element, createElement: () => new Element(), querySelector: () => element('notice'), querySelectorAll: () => [] },
    location: { hash: '#' + 'a'.repeat(64), origin: 'http://127.0.0.1' },
    navigator: {}, window: {}, Blob, AbortController, performance, fetch, setTimeout, clearTimeout, setInterval: () => 0,
  });
  const rows = () => element('uploads').children;
  const cancelRow = row => row.children[2].children[1].onclick();
  return {
    prepared, canceled, rows, element, get finished() { return finished; },
    add(files) { element('picker').onchange({ target: { files, value: '' } }); },
    cancelRow,
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
  await run('浏览器取消大文件后可重选，正在上传的字节仍计入10 GiB', async page => {
    page.add([file('first.bin', 6 * 1024 ** 3)]); await tick();
    page.add([file('blocked.bin', 6 * 1024 ** 3)]); await tick();
    assert.equal(page.rows().length, 1);
    await page.cancelRow(page.rows()[0]); await tick();
    page.add([file('replacement.bin', 6 * 1024 ** 3)]); await tick();
    assert.equal(page.rows().length, 2);
    assert.equal(page.prepared.length, 2);
    assert.equal(page.canceled.length, 1);
  });
  await run('浏览器取消排队文件后释放100个名额', async page => {
    page.add(Array.from({ length: 100 }, (_, i) => file('queued-' + i, 1))); await tick();
    page.add([file('blocked', 1)]); assert.equal(page.rows().length, 100);
    await Promise.all(page.rows().map(page.cancelRow)); await tick();
    page.add([file('replacement', 1)]); await tick();
    assert.equal(page.rows().length, 101);
    assert.equal(page.prepared.length, 2);
  });
  await run('浏览器已保存文件继续计入100个上限', async page => {
    page.add(Array.from({ length: 100 }, (_, i) => file('saved-' + i, 0))); await tick();
    assert.equal(page.finished, 100);
    page.add([file('blocked', 0)]); await tick();
    assert.equal(page.rows().length, 100);
    assert.equal(page.prepared.length, 100);
  });
  console.log('TOTAL 3 passed, 0 failed');
})().catch(error => { console.error(error); process.exitCode = 1; });
