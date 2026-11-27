'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const token = location.hash.slice(1).split('&')[0];
  const link = location.origin + '/#' + token;
  let expires = 0, uploading = false, ended = false;
  let uploadLimit = 10 * 1024 ** 3, countLimit = 100;
  let requestIds = false, folders = false;
  const queue = [];
  const size = bytes => bytes >= 1073741824 ? (bytes / 1073741824).toFixed(2) + ' GiB' : bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + ' MiB' : bytes >= 1024 ? (bytes / 1024).toFixed(1) + ' KiB' : bytes + ' B';
  const message = (text, info = false) => {
    const el = $('message');
    el.textContent = text;
    el.className = info ? 'info' : '';
  };
  const fileKind = name => {
    const ext = (name.includes('.') ? name.split('.').pop() : '').toLowerCase();
    if (/^(png|jpe?g|gif|webp|bmp|svg|ico|heic|avif)$/.test(ext)) return 'image';
    if (/^(mp4|mkv|webm|mov|avi|flv|wmv|m4v)$/.test(ext)) return 'video';
    if (/^(mp3|wav|flac|aac|ogg|m4a|opus)$/.test(ext)) return 'audio';
    if (/^(zip|rar|7z|tar|gz|bz2|xz)$/.test(ext)) return 'archive';
    if (/^(pdf|docx?|xlsx?|pptx?|txt|md|csv|json|xml)$/.test(ext)) return 'doc';
    if (/^(apk|exe|msi|dmg|deb|rpm)$/.test(ext)) return 'app';
    return 'other';
  };
  async function api(path, method = 'GET', body, signal) {
    const headers = { 'X-FQ-Guest': token };
    if (body && !(body instanceof Blob)) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(body); }
    const controller = new AbortController();
    const abort = () => controller.abort();
    if (signal) { if (signal.aborted) controller.abort(); else signal.addEventListener('abort', abort, { once: true }); }
    const timer = setTimeout(() => controller.abort(), 20000);
    try {
      const response = await fetch(path, { method, headers, body, signal: controller.signal, cache: 'no-store', redirect: 'error' });
      const data = await response.json();
      if (!response.ok) throw Object.assign(new Error(data.error || '传输失败，请重试'), {status: response.status});
      return data;
    } finally { clearTimeout(timer); if (signal) signal.removeEventListener('abort', abort); }
  }
  function row(name) {
    const item = document.createElement('div'); item.className = 'file';
    const icon = document.createElement('span'); icon.className = 'file-icon';
    icon.dataset.kind = fileKind(name);
    icon.textContent = (name.includes('.') ? name.split('.').pop() : 'FILE').slice(0, 4).toUpperCase();
    const main = document.createElement('div'); main.className = 'file-main';
    const title = document.createElement('div'); title.className = 'file-name'; title.textContent = name;
    const meta = document.createElement('div'); meta.className = 'meta'; main.append(title, meta);
    const actions = document.createElement('div'); actions.className = 'file-actions'; item.append(icon, main, actions);
    return { item, main, meta, actions };
  }
  function drawQr() {
    const code = qrcodegen.QrCode.encodeText(link, qrcodegen.QrCode.Ecc.MEDIUM);
    const canvas = $('qr'), scale = 5, border = 4; canvas.width = canvas.height = (code.size + border * 2) * scale;
    const ctx = canvas.getContext('2d'); ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height); ctx.fillStyle = '#16202a';
    for (let y = 0; y < code.size; y++) for (let x = 0; x < code.size; x++) if (code.getModule(x, y)) ctx.fillRect((x + border) * scale, (y + border) * scale, scale, scale);
    const preview = $('qr-link');
    if (preview) preview.textContent = link;
  }
  $('qr-toggle').onclick = () => {
    const show = $('qr-panel').hidden;
    $('qr-panel').hidden = !show;
    $('qr-toggle').setAttribute('aria-expanded', String(show));
    if (show) drawQr();
  };
  $('copy').onclick = async () => {
    try {
      if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(link);
      else { const input = document.createElement('textarea'); input.value = link; $('qr-panel').append(input); input.select(); const copied = document.execCommand('copy'); input.remove(); if (!copied) throw new Error(); }
      message('链接已复制到剪贴板', true);
      const btn = $('copy');
      btn.textContent = '已复制';
      setTimeout(() => { if (!ended) btn.textContent = '复制链接'; }, 1800);
    }
    catch { message('浏览器未允许复制，请复制地址栏中的完整链接'); }
  };
  function add(files) {
    if (ended) { message('本次传输已结束'); return; }
    const entries = [...files].map(value => value.file ? value : {file:value, relativePath:value.webkitRelativePath || undefined});
    if (entries.some(entry => entry.relativePath) && !folders) { message('对方版本不支持文件夹，请更新后重开临时传输'); return; }
    if (!entries.length) { message('所选文件夹没有可发送的文件（不发送空文件夹）'); return; }
    const active = queue.filter(t => t.state !== 'canceled');
    if (active.length + entries.length > countLimit || active.reduce((sum, t) => sum + t.file.size, 0) + entries.reduce((sum, entry) => sum + entry.file.size, 0) > uploadLimit) { message(`本次最多接收 ${countLimit} 个文件、合计 ${size(uploadLimit)}，请分批开启`); return; }
    for (const {file, relativePath} of entries) {
      const view = row(relativePath || file.name), bar = document.createElement('progress'); bar.max = file.size || 1; bar.value = 0; view.main.append(bar);
      const retry = document.createElement('button'); retry.textContent = '继续'; retry.className = 'soft'; retry.hidden = true;
      const cancel = document.createElement('button'); cancel.textContent = '取消'; cancel.className = 'danger';
      view.actions.append(retry, cancel); $('uploads').append(view.item);
      const requestId = requestIds ? Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('') : undefined;
      const task = { file, relativePath, requestId, view, bar, retry, cancel, id: null, offset: 0, state: 'queued', controller: null, preparing: null, started:false }; queue.push(task); view.meta.textContent = size(file.size) + ' · 排队中';
      retry.onclick = () => { if (ended) return; if(task.state==='cancel-failed')return cancelTask(task);task.state = 'queued'; retry.hidden = true; cancel.hidden = false; view.meta.className = 'meta'; view.meta.textContent = size(file.size) + ' · 准备续传…'; pump(); };
      cancel.onclick = () => cancelTask(task);
    }
    pump();
  }
  async function cancelTask(task) {
    if(ended||task.state==='done'||task.state==='canceled'||task.state==='canceling')return;
    task.state='canceling';task.cancel.hidden=task.retry.hidden=true;task.view.meta.className='meta';task.view.meta.textContent='正在取消…';
    // 老服务端不支持请求标识时，先取回准备结果再取消，避免丢失服务端任务 ID。
    if(task.controller&&(task.requestId||task.id))task.controller.abort();
    try {
      if(task.started){
        if(!task.requestId&&!task.id)await task.preparing;
        await api('/api/cancel?'+(task.requestId?'requestId='+task.requestId:'id='+task.id),'POST');
      }
      task.state='canceled';task.view.meta.textContent='已取消';
    } catch(error) {
      if(error.status===410){task.state='canceled';task.view.meta.textContent='已取消';}
      else {
        if(error.status===409&&task.id)try{
          const saved=await api('/api/upload-status?id='+task.id);
          if(saved.complete){task.state='done';task.bar.value=task.file.size||1;task.view.meta.textContent=size(task.file.size)+' · 对方已保存，无法取消';return;}
        }catch(_) { /* 保留取消未确认状态，用户可以重试。 */ }
        task.state='cancel-failed';task.view.meta.textContent='取消未确认 · '+error.message;task.retry.textContent='重试取消';task.retry.hidden=ended;
      }
    }
  }
  async function pump() {
    if (uploading || ended) return; const task = queue.find(t => t.state === 'queued'); if (!task) return;
    uploading = true; task.state = 'sending'; task.controller = new AbortController(); const started = performance.now(); let initial = 0;
    try {
      if (!task.id) {task.started=true;task.preparing=api('/api/prepare', 'POST', { name: task.file.name, size: task.file.size, requestId:task.requestId, relativePath:task.relativePath }, task.controller.signal).then(ready=>{task.id=ready.id;});await task.preparing;}
      if(task.state!=='sending')return;
      const status = await api('/api/upload-status?id=' + task.id, 'GET', undefined, task.controller.signal);if(task.state!=='sending')return;task.offset = status.offset; initial = task.offset;
      while (task.offset < task.file.size) {
        if (task.state !== 'sending') return;
        const result = await api('/api/chunk?id=' + task.id + '&offset=' + task.offset, 'POST', task.file.slice(task.offset, task.offset + 1048576), task.controller.signal);
        if(task.state!=='sending')return;task.offset = result.offset; task.bar.value = task.offset;
        const rate = (task.offset - initial) / Math.max(.001, (performance.now() - started) / 1000);
        const pct = task.file.size ? Math.min(99, Math.round(task.offset * 100 / task.file.size)) : 100;
        task.view.meta.textContent = pct + '% · ' + size(task.offset) + ' / ' + size(task.file.size) + ' · ' + size(rate) + '/s';
      }
      if (task.state !== 'sending') return;
      task.view.meta.textContent = '正在校验保存…'; const saved = await api('/api/finish?id=' + task.id, 'POST', undefined, task.controller.signal);
      if(task.state!=='sending')return;
      task.state = 'done'; task.bar.value = task.file.size || 1; task.bar.classList.add('done'); task.view.meta.className = 'meta done'; task.view.meta.textContent = size(task.file.size) + ' · 对方已保存' + (saved.warning?' · '+saved.warning:''); task.cancel.hidden = true;
    } catch (e) { if (task.state === 'sending') { task.state = 'failed'; task.view.meta.className = 'meta failed'; task.view.meta.textContent = ended ? '临时传输已到期，上传未完成' : '上传未完成 · ' + (e.name === 'AbortError' ? '连接超时，点击继续' : e.message); task.retry.hidden = ended; } }
    finally { task.controller = null; uploading = false; pump(); }
  }
  $('picker').onchange = e => { add(e.target.files); e.target.value = ''; };
  $('folder-picker').onchange = e => { add(e.target.files); e.target.value = ''; };
  $('choose-folder').onclick = () => $('folder-picker').click();
  $('drop').onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); $('picker').click(); } };
  for (const event of ['dragenter', 'dragover']) $('drop').addEventListener(event, e => { e.preventDefault(); $('drop').classList.add('over'); });
  $('drop').addEventListener('dragleave', e => {e.preventDefault();$('drop').classList.remove('over');});
  $('drop').addEventListener('drop', async e => {
    e.preventDefault();$('drop').classList.remove('over');
    const roots=[...(e.dataTransfer.items||[])].map(item=>item.webkitGetAsEntry?item.webkitGetAsEntry():null);
    if(!roots.some(entry=>entry&&entry.isDirectory)){add(e.dataTransfer.files);return;}
    if(!folders){message('对方版本不支持文件夹，请更新后重开临时传输');return;}
    const entries=[];let bytes=0;
    async function walk(entry,parent,depth){
      if(ended)throw new Error('本次传输已结束');
      if(depth>32)throw new Error('文件夹最多支持 32 层路径');
      const relative=parent?parent+'/'+entry.name:entry.name;
      if(entry.isFile){const file=await new Promise((resolve,reject)=>entry.file(resolve,reject));entries.push({file,relativePath:parent?relative:undefined});bytes+=file.size;if(entries.length>countLimit||bytes>uploadLimit)throw new Error('文件夹超过本次接收上限，请分批开启');}
      else if(entry.isDirectory){const reader=entry.createReader();while(true){const batch=await new Promise((resolve,reject)=>reader.readEntries(resolve,reject));if(!batch.length)break;for(const child of batch)await walk(child,relative,depth+1);}}
    }
    try{for(const entry of roots)if(entry)await walk(entry,'',1);add(entries);}catch(error){message(error.message);}
  });
  async function init() {
    try {
      if (!/^[a-f0-9]{64}$/.test(token)) throw new Error('链接不完整，请使用对方复制的完整链接或二维码');
      const data = await api('/api/guest'); expires = Date.parse(data.until);
      requestIds=data.requestIds===true;folders=data.folders===true;
      $('choose-folder').hidden=!folders||!('webkitdirectory' in $('folder-picker'));
      if (Number.isSafeInteger(data.limit) && data.limit > 0) uploadLimit = data.limit;
      if (Number.isInteger(data.countLimit) && data.countLimit > 0 && data.countLimit <= 1000) countLimit = data.countLimit;
      $('upload-quota').textContent = `限 ${countLimit} 个 · ${size(uploadLimit)}`;
      const totalSize = data.files.reduce((sum, f) => sum + (f.size || 0), 0);
      $('count').textContent = data.files.length ? data.files.length + ' 个文件 · ' + size(totalSize) : '0 个文件';
      $('downloads').replaceChildren();
      for (const file of data.files) {
        const view = row(file.relativePath || file.name);
        view.meta.textContent = size(file.size);
        const a = document.createElement('a');
        a.className = 'download';
        a.innerHTML = '<svg class="btn-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v11M8 11l4 4 4-4"/><path d="M4 16v2.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V16"/></svg><span>下载</span>';
        a.href = '/file?id=' + encodeURIComponent(file.id) + '&token=' + encodeURIComponent(token);
        a.download = file.name;
        view.actions.append(a);
        $('downloads').append(view.item);
      }
      if (!data.files.length) {
        const empty = document.createElement('p'); empty.className = 'empty'; empty.textContent = '对方未分享下载文件，正在等待你上传文件'; $('downloads').append(empty);
      }
      $('upload').hidden = !data.upload;
      const tick = () => {
        const seconds = Math.max(0, Math.ceil((expires - Date.now()) / 1000));
        $('session').textContent = seconds ? '连接已就绪 · ' + Math.floor(seconds / 60) + '分' + String(seconds % 60).padStart(2, '0') + '秒后结束' : '本次临时传输已到期结束';
        if (!seconds && !ended) {
          ended = true; $('picker').disabled = $('folder-picker').disabled = $('choose-folder').disabled = true; $('copy').disabled = true; $('qr-toggle').disabled = true; $('qr-panel').hidden = true;
          $('qr-toggle').setAttribute('aria-expanded', 'false');
          document.querySelector('.notice').classList.add('ended');
          $('drop').hidden = true;
          for (const dl of document.querySelectorAll('.download')) { dl.removeAttribute('href'); dl.setAttribute('aria-disabled', 'true'); dl.textContent = '已结束'; }
          for (const task of queue) { task.retry.hidden = true; task.cancel.hidden = true; if (task.controller) task.controller.abort(); }
          message('让对方重新开启临时传输，即可使用新链接继续操作。');
        }
      };
      tick(); setInterval(tick, 1000);
      if (location.hash.endsWith('&qr')) {
        $('qr-panel').hidden = false;
        $('qr-toggle').setAttribute('aria-expanded', 'true');
        drawQr();
      }
    } catch (e) { $('session').textContent = '暂时无法连接'; $('count').textContent = '连接失败'; $('downloads').replaceChildren(); message(e.message); }
  }
  init();
})();
