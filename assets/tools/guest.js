'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const token = location.hash.slice(1).split('&')[0];
  const link = location.origin + '/#' + token;
  let expires = 0, uploading = false, ended = false;
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
      if (!response.ok) throw new Error(data.error || '传输失败，请重试');
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
    const active = queue.filter(t => t.state !== 'canceled');
    if (active.length + files.length > 100 || active.reduce((sum, t) => sum + t.file.size, 0) + [...files].reduce((sum, f) => sum + f.size, 0) > 10737418240) { message('本次最多接收100个文件、合计10 GiB，请分批开启'); return; }
    for (const file of files) {
      const view = row(file.name), bar = document.createElement('progress'); bar.max = file.size || 1; bar.value = 0; view.main.append(bar);
      const retry = document.createElement('button'); retry.textContent = '继续'; retry.className = 'soft'; retry.hidden = true;
      const cancel = document.createElement('button'); cancel.textContent = '取消'; cancel.className = 'danger';
      view.actions.append(retry, cancel); $('uploads').append(view.item);
      const task = { file, view, bar, retry, cancel, id: null, offset: 0, state: 'queued', controller: null }; queue.push(task); view.meta.textContent = size(file.size) + ' · 排队中';
      retry.onclick = () => { if (ended) return; task.state = 'queued'; retry.hidden = true; cancel.hidden = false; view.meta.className = 'meta'; view.meta.textContent = size(file.size) + ' · 准备续传…'; pump(); };
      cancel.onclick = async () => { task.state = 'canceled'; if (task.controller) task.controller.abort(); cancel.hidden = true; retry.hidden = true; view.meta.className = 'meta'; view.meta.textContent = '已取消'; if (task.id) try { await api('/api/cancel?id=' + task.id, 'POST'); } catch (e) { view.meta.textContent = '已停止上传；' + e.message; } };
    }
    pump();
  }
  async function pump() {
    if (uploading || ended) return; const task = queue.find(t => t.state === 'queued'); if (!task) return;
    uploading = true; task.state = 'sending'; task.controller = new AbortController(); const started = performance.now(); let initial = 0;
    try {
      if (!task.id) { const ready = await api('/api/prepare', 'POST', { name: task.file.name, size: task.file.size }, task.controller.signal); task.id = ready.id; }
      const status = await api('/api/upload-status?id=' + task.id); task.offset = status.offset; initial = task.offset;
      while (task.offset < task.file.size) {
        if (task.state === 'canceled') return;
        const result = await api('/api/chunk?id=' + task.id + '&offset=' + task.offset, 'POST', task.file.slice(task.offset, task.offset + 1048576), task.controller.signal);
        task.offset = result.offset; task.bar.value = task.offset;
        const rate = (task.offset - initial) / Math.max(.001, (performance.now() - started) / 1000);
        const pct = task.file.size ? Math.min(99, Math.round(task.offset * 100 / task.file.size)) : 100;
        task.view.meta.textContent = pct + '% · ' + size(task.offset) + ' / ' + size(task.file.size) + ' · ' + size(rate) + '/s';
      }
      if (task.state === 'canceled') return;
      task.view.meta.textContent = '正在校验保存…'; await api('/api/finish?id=' + task.id, 'POST', undefined, task.controller.signal);
      task.state = 'done'; task.bar.value = task.file.size || 1; task.bar.classList.add('done'); task.view.meta.className = 'meta done'; task.view.meta.textContent = size(task.file.size) + ' · 对方已保存'; task.cancel.hidden = true;
    } catch (e) { if (task.state !== 'canceled') { task.state = 'failed'; task.view.meta.className = 'meta failed'; task.view.meta.textContent = '上传未完成 · ' + (e.name === 'AbortError' ? '连接超时，点击继续' : e.message); task.retry.hidden = false; } }
    finally { task.controller = null; uploading = false; pump(); }
  }
  $('picker').onchange = e => { add(e.target.files); e.target.value = ''; };
  $('drop').onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); $('picker').click(); } };
  for (const event of ['dragenter', 'dragover']) $('drop').addEventListener(event, e => { e.preventDefault(); $('drop').classList.add('over'); });
  for (const event of ['dragleave', 'drop']) $('drop').addEventListener(event, e => { e.preventDefault(); $('drop').classList.remove('over'); if (event === 'drop') add(e.dataTransfer.files); });
  async function init() {
    try {
      if (!/^[a-f0-9]{64}$/.test(token)) throw new Error('链接不完整，请使用对方复制的完整链接或二维码');
      const data = await api('/api/guest'); expires = Date.parse(data.until);
      const totalSize = data.files.reduce((sum, f) => sum + (f.size || 0), 0);
      $('count').textContent = data.files.length ? data.files.length + ' 个文件 · ' + size(totalSize) : '0 个文件';
      $('downloads').replaceChildren();
      for (const file of data.files) {
        const view = row(file.name);
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
        $('session').textContent = seconds ? '连接已就绪 · ' + Math.floor(seconds / 60) + '分' + String(seconds % 60).padStart(2, '0') + '秒后结束' : '本次临时传输已结束';
        if (!seconds && !ended) {
          ended = true; $('picker').disabled = true; $('copy').disabled = true; $('qr-toggle').disabled = true; $('qr-panel').hidden = true;
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
