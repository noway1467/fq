'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const mode = location.hash === '#remote' ? 'remote' : 'share';
  let token = '', generation = 0, frameTimer = 0, frameUrl = '', drag = false;
  let frameAbort=null;
  let display = 0, displayVersion = 0, zoomLevel = 0;
  let phone = false, scale = 1;
  let trail = [{id: '', name: '共享文件夹'}], items = [], listing = 0;
  let pending = [], sending = false, closing = Promise.resolve();
  const status = (text, error = false) => { $('status').textContent = text; $('status').hidden = !text; $('status').classList.toggle('error', error); };
  const clamp = (n, limit) => Math.max(-limit, Math.min(limit, Math.round(n)));
  if (mode === 'remote') {
    document.title = '飞Q · 远程控制'; $('page-title').textContent = '远程控制';
    $('login-title').textContent = '连接远程设备';
  }
  async function request(path, data, timeout = 10000, currentToken = token) {
    const abort = new AbortController(), timer = setTimeout(() => abort.abort(), timeout);
    if(path.startsWith('/api/screen?'))frameAbort=abort;
    try {
      const options = {signal: abort.signal, cache: 'no-store', credentials: 'same-origin', headers: {}};
      if (data !== undefined) { options.method = 'POST'; options.headers = {'Content-Type': 'application/json', 'X-FQ-Token': currentToken, 'X-FQ-Reconnect': '1'}; options.body = JSON.stringify(data); }
      const response = await fetch(path, options);
      if (!response.ok) {
        let error; try { error = (await response.json()).error; } catch (_) { error = ''; }
        const failure = new Error(error || `请求失败 (${response.status})`); failure.status = response.status; throw failure;
      }
      // 超时覆盖完整响应体；仅收到响应头不能算画面或输入确认已完成。
      return await (path.startsWith('/api/screen?') ? response.blob() : response.json());
    } catch (error) { if (error.name === 'AbortError') throw new Error('连接超时，请检查对方是否在线；远控操作不会自动重发。'); throw error; }
    finally { clearTimeout(timer); if(frameAbort===abort)frameAbort=null; }
  }
  function unavailable() { end('设备未连接或尚未开启远控', true); if (document.documentElement.dataset.native === 'true') location.href='/native/unavailable'; }
  window.feiqCheck = async () => {
    if(mode!=='remote')return;
    try { const data=await request('/api/status',undefined,4000);if(!data.remote)unavailable(); }
    catch (error) { if(error.status!==404)unavailable(); }
  };
  function closePlayer() { $('player').pause(); $('player').removeAttribute('src'); $('player').load(); $('player-panel').hidden = true; }
  function end(message, error = false) {
    const previousToken = token; token = ''; generation++; listing++; clearTimeout(frameTimer); pending = []; drag = false;
    if(frameAbort){frameAbort.abort();frameAbort=null;}
    $('drag').setAttribute('aria-pressed', 'false'); $('disconnect').hidden = true; $('remote').hidden = true; $('files').hidden = true; $('login').hidden = false;
    points.clear(); previous = null; start = null; $('drag').textContent = '拖拽';
    resetWheel();
    $('type-text').disabled = false;
    document.body.classList.remove('remote-active','controls-hidden','typing-mode'); $('restore-panel').hidden = true; $('remote-text').blur(); $('keyboard').textContent = '键盘'; $('keyboard').setAttribute('aria-pressed','false');
    zoomLevel=0; $('screen-wrap').classList.remove('zoomed','zoom-large'); $('zoom').textContent='放大';
    scale=1; screenPoints.clear(); screenDown=null; pinch=null;
    $('remote-error').hidden=true;
    closePlayer(); if (frameUrl) URL.revokeObjectURL(frameUrl); frameUrl = ''; $('screen').removeAttribute('src'); status(message, error);
    if (previousToken) {
      const abort = new AbortController(), timeout = setTimeout(() => abort.abort(), 5000);
      closing = fetch('/api/logout', {method: 'POST', headers: {'Content-Type': 'application/json', 'X-FQ-Token': previousToken}, body: JSON.stringify({mode}), credentials: 'same-origin', keepalive: true, signal: abort.signal}).catch(() => {}).finally(() => clearTimeout(timeout));
    }
  }
  $('disconnect').onclick = $('remote-disconnect').onclick = () => end('');
  window.feiqStop = () => end('');
  window.addEventListener('hashchange', () => { if ((location.hash === '#remote' ? 'remote' : 'share') !== mode) { end('正在切换页面'); location.reload(); } });
  async function connect(reconnect) {
    $('connect').disabled = $('reconnect').disabled = true; status(mode === 'remote' ? '等待电脑确认…' : '连接中…');
    const version = ++generation;
    try {
      await closing; if (version !== generation) return;
      const data = await request(reconnect ? '/api/reconnect' : '/api/login', reconnect ? {mode} : {mode, code: $('code').value.trim()}, 40000);
      if (version !== generation) return;
      token = data.token; $('code').value = ''; $('login').hidden = true; $('disconnect').hidden = false;
      $(mode === 'remote' ? 'remote' : 'files').hidden = false;
      status(''); document.body.classList.toggle('remote-active', mode === 'remote');
      if (mode === 'remote') {
        const screens = await request('/api/displays'); if (version !== generation) return;
        phone=screens.platform==='android'; document.body.classList.toggle('phone-target',phone);$('phone-controls').hidden=!phone;$('phone-enter').hidden=!phone||!screens.imeEnter;
        $('display').replaceChildren(); screens.items.forEach((item,index) => { const option = document.createElement('option'); option.value = item.id; option.textContent = '屏幕 ' + (index+1); $('display').append(option); });
        $('display').hidden = screens.items.length < 2;
        display = Number($('display').value); viewport(); frames(version);
        if(document.documentElement.dataset.nativePairing==='true')location.href='/native/paired';
      } else { trail = [{id: '', name: '共享文件夹'}]; await loadDirectory(); }
    } catch (error) { if (version === generation) { if(mode==='remote'&&!error.status)unavailable();else end(error.message, true); } }
    finally { $('connect').disabled = $('reconnect').disabled = false; }
  }
  $('connect-form').onsubmit = event => { event.preventDefault(); connect(false); };
  $('reconnect').onclick = () => connect(true);
  const size = value => value < 0 ? '大小未知' : value >= 1073741824 ? (value / 1073741824).toFixed(1) + ' GB' : value >= 1048576 ? (value / 1048576).toFixed(1) + ' MB' : value >= 1024 ? Math.ceil(value / 1024) + ' KB' : value + ' B';
  async function loadDirectory() {
    const version = ++listing; $('refresh').disabled = true; $('file-list').textContent = '正在加载目录…';
    try {
      const data = await request('/api/list?id=' + encodeURIComponent(trail[trail.length - 1].id));
      if (version !== listing || !token) return;
      items = data.items; renderFiles(); breadcrumbs(); status('');
    } catch (error) { if (version === listing) { $('file-list').textContent = '目录读取失败，可以返回上级或刷新重试。'; status(error.message, true); breadcrumbs(); } }
    finally { if (version === listing) $('refresh').disabled = false; }
  }
  function breadcrumbs() {
    $('breadcrumbs').replaceChildren();
    trail.forEach((entry, index) => { const button = document.createElement('button'); button.textContent = entry.name; button.title = entry.name; button.onclick = () => { trail = trail.slice(0, index + 1); $('search').value = ''; loadDirectory(); }; $('breadcrumbs').append(button); });
  }
  function renderFiles() {
    const filter = $('search').value.toLocaleLowerCase(), list = items.filter(item => item.name.toLocaleLowerCase().includes(filter)).sort((a,b) => Number(b.dir) - Number(a.dir) || a.name.localeCompare(b.name));
    $('count').textContent = `${list.length} 项`; $('file-list').replaceChildren();
    if (!list.length) { const empty = document.createElement('p'); empty.className = 'empty'; empty.textContent = filter ? '没有匹配的文件' : '这个文件夹是空的'; $('file-list').append(empty); }
    list.forEach(item => {
      const media = /\.(mp4|m4v|webm|mkv|mov|mp3|ogg)$/i.test(item.name);
      const row = document.createElement('div'); row.className = 'file-row';
      const kind = document.createElement('span'); kind.className = 'file-kind'; kind.textContent = item.dir ? '目录' : media ? '影音' : '文件'; row.append(kind);
      const detail = document.createElement('div'); detail.className = 'file-detail';
      const name = document.createElement('div'); name.className = 'file-name'; name.textContent = item.name; name.title = item.name;
      const info = document.createElement('div'); info.className = 'file-size'; info.textContent = item.dir ? '文件夹' : size(item.size); detail.append(name, info); row.append(detail);
      const actions = document.createElement('div'); actions.className = 'file-actions';
      if (item.dir || media) {
        const open = document.createElement('button'); open.textContent = item.dir ? '进入' : '播放'; open.setAttribute('aria-label', open.textContent + ' ' + item.name);
        open.onclick = () => { if (item.dir) { trail.push({id: item.id, name: item.name}); $('search').value = ''; loadDirectory(); } else play(item); }; actions.append(open);
      }
      if (!item.dir) { const download = document.createElement('a'); download.textContent = '下载'; download.href = '/file?id=' + encodeURIComponent(item.id) + '&download=1'; download.download = item.name; download.setAttribute('aria-label', '下载 ' + item.name); actions.append(download); }
      row.append(actions); $('file-list').append(row);
    });
  }
  function play(item) {
    closePlayer(); $('player-panel').hidden = false; $('player-title').textContent = item.name; $('player-error').textContent = '';
    $('player').src = '/file?id=' + encodeURIComponent(item.id); $('player').play().catch(() => { $('player-error').textContent = '请点播放按钮；若仍无法播放，可下载后用本机播放器打开。'; });
    $('close-player').focus();
  }
  $('player').onerror = () => { $('player-error').textContent = '播放失败：格式不支持、共享已关闭，或来源不支持跳转读取。请检查连接或下载后播放。'; };
  $('close-player').onclick = closePlayer; $('refresh').onclick = loadDirectory; $('search').oninput = renderFiles;
  async function frames(version) {
    if (!token || version !== generation) return;
    const screenVersion = displayVersion;
    try {
      const blob = await request('/api/screen?display=' + display, undefined, 8000);
      if (!token || version !== generation) return;
      if (screenVersion !== displayVersion) { frameTimer = setTimeout(() => frames(version), 0); return; }
      const previous = frameUrl; frameUrl = URL.createObjectURL(blob); $('screen').src = frameUrl;
      if (previous) URL.revokeObjectURL(previous);
      frameTimer = setTimeout(() => frames(version), 250);
    } catch (error) { if (version === generation) end(error.message, true); }
  }
  function input(data, done) {
    if (!token || mode !== 'remote') return;
    data.display = display;
    if(phone){data.frameWidth=$('screen').naturalWidth;data.frameHeight=$('screen').naturalHeight;}
    const last = pending[pending.length - 1];
    if (!done && last && last.data.display === data.display && (data.type === 'move' || data.type === 'wheel') && last.data.type === data.type) {
      if (data.type === 'move') last.data.x = clamp(last.data.x + data.x, 2000);
      last.data.y = clamp(last.data.y + data.y, data.type === 'move' ? 2000 : 1200); return;
    }
    if (pending.length >= 32) { end('网络响应过慢，已停止控制以避免积压操作。请重新连接。', true); return; }
    pending.push({data, done, time: Date.now(), version: generation}); pump();
  }
  async function pump() {
    if (sending) return; sending = true; const version = generation;
    try {
      while (pending.length && token && version === generation) {
        const item = pending.shift(); if (item.version !== generation) continue;
        if (Date.now() - item.time > 5000) throw new Error('操作等待过久，已停止控制，不会补发旧操作。');
        try { await request('/api/input', item.data, 5000); }
        catch(error){if(phone&&(error.status===403||error.status===409)){if(item.version===generation){$('remote-error').textContent=error.message;$('remote-error').hidden=false;}continue;}throw error;}
        $('remote-error').hidden=true;
        if (item.version === generation && item.done) item.done();
      }
    } catch (error) { if (version === generation) end(error.message, true); }
    finally { sending = false; if (version === generation) $('type-text').disabled = false; if (pending.length && token) pump(); }
  }
  document.querySelectorAll('[data-key]').forEach(button => { button.onclick = () => input({type: 'key', key: button.dataset.key}); });
  document.querySelectorAll('[data-input]').forEach(button => { button.onclick = () => input({type: button.dataset.input}); });
  $('double-click').onclick = () => { input({type: 'click'}); input({type: 'click'}); };
  $('drag').onclick = () => { drag = !drag; $('drag').setAttribute('aria-pressed', String(drag)); $('drag').textContent = drag ? '松开拖拽' : '拖拽'; input({type: drag ? 'drag' : 'release'}); };
  $('type-text').onclick = () => {
    const text = $('remote-text').value; if (!text) return; $('type-text').disabled = true;
    input({type: 'text', text}, () => { if ($('remote-text').value === text) $('remote-text').value = ''; });
  };
  document.querySelectorAll('[data-phone-key]').forEach(button=>button.onclick=()=>input({type:'phone-key',key:button.dataset.phoneKey}));
  $('phone-enter').onclick=()=>input({type:'phone-enter'});
  const points = new Map(); let previous = null, start = null, moved = false, multi = false;
  const center = () => { const values = [...points.values()]; return {x: values.reduce((s,p) => s+p.x,0)/values.length, y: values.reduce((s,p) => s+p.y,0)/values.length}; };
  $('touchpad').onpointerdown = event => {
    event.preventDefault(); $('touchpad').setPointerCapture(event.pointerId); points.set(event.pointerId, {x:event.clientX,y:event.clientY});
    previous = center(); if (points.size === 1) { start = {x:event.clientX,y:event.clientY,time:Date.now()}; moved = false; multi = false; } else multi = true;
  };
  $('touchpad').onpointermove = event => {
    if (!points.has(event.pointerId)) return; event.preventDefault(); points.set(event.pointerId, {x:event.clientX,y:event.clientY}); const next = center();
    if (!previous) { previous = next; return; }
    const x = next.x - previous.x, y = next.y - previous.y;
    if (start && Math.hypot(next.x - start.x, next.y - start.y) > 5) moved = true;
    if (points.size >= 2) input({type:'wheel',y:clamp(-y * 9,1200)}); else if (moved) input({type:'move',x:clamp(x*1.8,2000),y:clamp(y*1.8,2000)});
    previous = next;
  };
  const lift = event => {
    if (!points.has(event.pointerId)) return; points.delete(event.pointerId);
    if (!points.size && !moved && !multi && start && Date.now()-start.time<400 && event.type === 'pointerup') input({type:'click'});
    previous = points.size ? center() : null;
  };
  $('touchpad').onpointerup = lift; $('touchpad').onpointercancel = lift;
  $('touchpad').oncontextmenu = event => event.preventDefault();
  let wheelPointer = null;
  function resetWheel() { wheelPointer = null; $('wheel-pad').classList.remove('scrolling'); }
  function scrollRemote(y, position) {
    if (!token || phone || mode !== 'remote') return;
    y = clamp(y,1200); if (!y) return;
    if (position) {
      const last=pending[pending.length-1], point=pending[pending.length-2];
      // 画面滚轮的定位与滚动成对合并，避免高频触控板事件挤满输入队列。
      if (point && point.data.type==='point' && last.data.type==='wheel' && point.data.display===display && last.data.display===display) {
        Object.assign(point.data,position); last.data.y=clamp(last.data.y+y,1200); return;
      }
      input({type:'point',...position});
    }
    input({type:'wheel',y});
  }
  $('wheel-pad').onpointerdown = event => {
    if (!token || phone || wheelPointer || event.button !== 0) return;
    event.preventDefault(); $('wheel-pad').setPointerCapture(event.pointerId);
    wheelPointer = {id:event.pointerId,y:event.clientY}; $('wheel-pad').classList.add('scrolling');
  };
  $('wheel-pad').onpointermove = event => {
    if (!wheelPointer || wheelPointer.id !== event.pointerId) return;
    event.preventDefault(); scrollRemote((wheelPointer.y-event.clientY)*9); wheelPointer.y=event.clientY;
  };
  const liftWheel = event => { if (wheelPointer && wheelPointer.id === event.pointerId) resetWheel(); };
  $('wheel-pad').onpointerup=liftWheel; $('wheel-pad').onpointercancel=liftWheel; $('wheel-pad').onlostpointercapture=liftWheel;
  $('wheel-pad').oncontextmenu = event => event.preventDefault();
  $('wheel-pad').onkeydown = event => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault(); scrollRemote(event.key === 'ArrowUp' ? 120 : -120);
  };
  const wheel = event => {
    if (!token || phone || mode !== 'remote' || event.ctrlKey || !event.deltaY) return;
    event.preventDefault();
    // 浏览器可能按像素、行或页报告滚动量，统一后再交给 Windows 滚轮接口。
    const unit = event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? event.currentTarget.clientHeight : 1;
    scrollRemote(-event.deltaY*unit,event.currentTarget === $('screen') ? screenPosition(event.clientX,event.clientY) : null);
  };
  ['touchpad','wheel-pad','screen'].forEach(id => $(id).addEventListener('wheel',wheel,{passive:false}));
  const screenPoints=new Map();let screenDown=null,pinch=null,screenMulti=false;
  function sizeScreen(){
    const wrap=$('screen-wrap'),image=$('screen');if(!image.naturalWidth||!wrap.clientWidth||!wrap.clientHeight)return;
    const fit=Math.min(wrap.clientWidth/image.naturalWidth,wrap.clientHeight/image.naturalHeight);
    const width=image.naturalWidth*fit*scale,height=image.naturalHeight*fit*scale;
    image.style.width=width+'px';image.style.height=height+'px';$('screen-stage').style.width=Math.max(wrap.clientWidth,width)+'px';$('screen-stage').style.height=Math.max(wrap.clientHeight,height)+'px';
    wrap.classList.toggle('zoomed',scale>1.01);$('zoom').textContent=scale>1.01?'适应':'放大';
  }
  const screenPosition=(x,y)=>{const r=$('screen').getBoundingClientRect();return {x:Math.max(0,clamp((x-r.left)/r.width*10000,10000)),y:Math.max(0,clamp((y-r.top)/r.height*10000,10000))};};
  const pair=()=>{const p=[...screenPoints.values()];return {x:(p[0].x+p[1].x)/2,y:(p[0].y+p[1].y)/2,d:Math.max(1,Math.hypot(p[0].x-p[1].x,p[0].y-p[1].y))};};
  $('screen').onload=sizeScreen;
  $('screen-wrap').onpointerdown=event=>{
    if(!token||!$('screen').naturalWidth)return;event.preventDefault();$('screen-wrap').setPointerCapture(event.pointerId);
    screenPoints.set(event.pointerId,{x:event.clientX,y:event.clientY});
    if(screenPoints.size===1){const r=$('screen').getBoundingClientRect();screenMulti=false;screenDown={inside:event.clientX>=r.left&&event.clientX<=r.right&&event.clientY>=r.top&&event.clientY<=r.bottom,x:event.clientX,y:event.clientY,time:Date.now(),left:$('screen-wrap').scrollLeft,top:$('screen-wrap').scrollTop,position:screenPosition(event.clientX,event.clientY),version:displayVersion,width:$('screen').naturalWidth,height:$('screen').naturalHeight};}
    if(screenPoints.size===2){screenMulti=true;const p=pair(),r=$('screen').getBoundingClientRect();pinch={...p,scale,xRatio:(p.x-r.left)/r.width,yRatio:(p.y-r.top)/r.height};}
  };
  $('screen-wrap').onpointermove=event=>{
    if(!screenPoints.has(event.pointerId))return;event.preventDefault();screenPoints.set(event.pointerId,{x:event.clientX,y:event.clientY});
    if(screenPoints.size>=2&&pinch){const p=pair();scale=Math.max(1,Math.min(4,pinch.scale*p.d/pinch.d));sizeScreen();const r=$('screen').getBoundingClientRect();$('screen-wrap').scrollLeft+=r.left+pinch.xRatio*r.width-p.x;$('screen-wrap').scrollTop+=r.top+pinch.yRatio*r.height-p.y;}
    else if(!phone&&!screenMulti&&screenDown){$('screen-wrap').scrollLeft=screenDown.left+screenDown.x-event.clientX;$('screen-wrap').scrollTop=screenDown.top+screenDown.y-event.clientY;}
  };
  const screenUp=event=>{
    if(!screenPoints.has(event.pointerId))return;screenPoints.delete(event.pointerId);
    if(!screenPoints.size&&screenDown&&screenDown.inside&&!screenMulti&&event.type==='pointerup'&&screenDown.version===displayVersion&&screenDown.width===$('screen').naturalWidth&&screenDown.height===$('screen').naturalHeight){
      const distance=Math.hypot(event.clientX-screenDown.x,event.clientY-screenDown.y),duration=Date.now()-screenDown.time,position=screenPosition(event.clientX,event.clientY);
      if(phone)input({type:'gesture',...screenDown.position,endX:position.x,endY:position.y,duration:Math.max(50,Math.min(1500,distance<5&&duration<400?50:duration))});
      else if(distance<5&&duration<400){input({type:'point',...position});input({type:'click'});}
    }
    if(screenPoints.size<2)pinch=null;if(!screenPoints.size)screenDown=null;
  };
  $('screen-wrap').onpointerup=screenUp;$('screen-wrap').onpointercancel=screenUp;$('screen-wrap').oncontextmenu=event=>event.preventDefault();
  $('zoom').onclick=()=>{scale=scale>1.01?1:2;sizeScreen();};
  $('keyboard').onclick = () => {
    const typing = document.body.classList.toggle('typing-mode'); $('keyboard').setAttribute('aria-pressed',String(typing)); $('keyboard').textContent = typing ? '鼠标' : '键盘';
    if (typing) $('remote-text').focus(); else $('remote-text').blur();
  };
  $('panels').onclick = $('restore-panel').onclick = () => { const hidden = document.body.classList.toggle('controls-hidden'); $('restore-panel').hidden = !hidden; if (hidden) $('remote-text').blur(); };
  const setLayout = stacked => { document.body.classList.toggle('layout-stacked', stacked); $('layout').textContent = stacked ? '左右布局' : '上下布局'; $('layout').setAttribute('aria-pressed', String(stacked)); };
  try { setLayout(localStorage.getItem('fq-remote-layout') === 'stacked'); } catch (_) { setLayout(false); }
  $('layout').onclick = () => { const stacked = !document.body.classList.contains('layout-stacked'); setLayout(stacked); try { localStorage.setItem('fq-remote-layout', stacked ? 'stacked' : 'side'); } catch (_) { /* 无痕或禁用存储时只保留本次布局。 */ } };
  $('rotate').onclick = () => { location.href = '/native/rotate'; };
  $('display').onchange = () => {
    if (drag) { input({type:'release'}); drag = false; $('drag').setAttribute('aria-pressed','false'); $('drag').textContent = '拖拽'; }
    display = Number($('display').value); displayVersion++; zoomLevel=0;scale=1;sizeScreen();screenDown=null;resetWheel();
    $('screen-wrap').scrollTop = 0; $('screen-wrap').scrollLeft = 0;
  };
  const viewport = () => {
    const height = window.visualViewport ? window.visualViewport.height : window.innerHeight;
    document.documentElement.style.setProperty('--view-height', height + 'px');
    document.body.classList.toggle('keyboard-open', document.activeElement === $('remote-text') && height < window.innerHeight * .8);
  };
  if (window.visualViewport) window.visualViewport.addEventListener('resize', viewport);
  new ResizeObserver(sizeScreen).observe($('screen-wrap'));
  window.addEventListener('resize', viewport); $('remote-text').addEventListener('focus', viewport); $('remote-text').addEventListener('blur', viewport); viewport();
  $('remote-text').addEventListener('focus',()=>{if(phone)document.body.classList.add('typing-mode');});$('remote-text').addEventListener('blur',()=>{if(phone)document.body.classList.remove('typing-mode');});
  document.querySelectorAll('[data-key],#type-text,#phone-enter').forEach(button => button.addEventListener('pointerdown', event => { if (document.activeElement === $('remote-text')) event.preventDefault(); }));
  $('fullscreen').onclick = async () => { try { if (document.fullscreenElement) await document.exitFullscreen(); else await $('remote').requestFullscreen(); } catch (_) { $('fullscreen').hidden = true; } };
  document.addEventListener('visibilitychange', () => { if (document.hidden && mode === 'remote') end('页面已切到后台，远控已断开。'); });
  document.addEventListener('keydown', event => {
    if (!token || phone || mode !== 'remote' || event.isComposing || !['screen-wrap','touchpad'].includes(document.activeElement.id)) return;
    const keys = {Escape:'escape',Tab:'tab',Backspace:'backspace',ArrowLeft:'left',ArrowRight:'right',ArrowUp:'up',ArrowDown:'down',PageUp:'pageup',PageDown:'pagedown',Home:'home',End:'end'};
    const chords = {a:'selectall',c:'copy',v:'paste',z:'undo',f:'find'};
    let key = event.key === 'Enter' ? (event.ctrlKey ? 'ctrlenter' : event.shiftKey ? 'newline' : 'enter') : keys[event.key];
    if (event.ctrlKey && chords[event.key.toLowerCase()]) key = chords[event.key.toLowerCase()];
    if (key) {event.preventDefault();input({type:'key',key});}
    else if (!event.ctrlKey && !event.altKey && !event.metaKey && [...event.key].length === 1) {event.preventDefault();input({type:'text',text:event.key});}
  });
  window.addEventListener('pagehide', () => { if (token) end('连接已关闭'); });
})();
