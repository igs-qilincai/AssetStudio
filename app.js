// AssetStudio Web — UI thread. All parsing/decoding happens in worker.js (.NET + wasm texture decoder).
const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

// ---------------------------------------------------------------- worker RPC
const worker = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
let nextId = 1;
const pending = new Map();

worker.onmessage = (event) => {
    const msg = event.data;
    if (msg.type === 'log') return addLog(msg.level, msg.message);
    if (msg.type === 'progress') return setProgress(msg.value, msg.text);
    if (msg.type === 'status') return setStatus(msg.text);
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.error) p.reject(new Error(msg.error));
    else p.resolve(msg.result);
};
worker.onerror = (e) => {
    setStatus('Worker 發生錯誤：' + (e.message || e));
    addLog(4, 'Worker error: ' + (e.message || e));
};

function call(cmd, args, transfer) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        worker.postMessage({ id, cmd, args }, transfer || []);
    });
}

// ---------------------------------------------------------------- state
const state = {
    assets: [],          // { id, type, name, container, size, pathId, file, exportable }
    filtered: [],
    selected: new Set(),
    anchor: null,        // index in filtered for shift-select
    current: null,       // asset shown in preview
    types: new Map(),    // type -> enabled
    showAll: false,
    search: '',
    sortKey: null,
    sortDesc: false,
    busy: false,
    ready: false,
};

// ---------------------------------------------------------------- status / log
let logCount = 0;
function setStatus(text) { $('#status').textContent = text; }
function setProgress(value, text) {
    $('#progress-bar').style.width = `${Math.max(0, Math.min(100, value || 0))}%`;
    if (text) setStatus(text);
}
function addLog(level, message) {
    // level follows AssetStudio.LoggerEvent: 2 info, 3 warning, 4 error
    const div = document.createElement('div');
    div.className = `lvl-${level}`;
    div.textContent = `[${(performance.now() / 1000).toFixed(1)}s] ${message}`;
    $('#log-list').appendChild(div);
    if (level >= 3) {
        logCount++;
        $('#log-count').textContent = logCount;
    }
    if (level === 2 && message.length < 200) setStatus(message);
}
$('#btn-log').onclick = () => { $('#log-panel').hidden = !$('#log-panel').hidden; };
$('#btn-log-close').onclick = () => { $('#log-panel').hidden = true; };

function formatSize(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}

async function busy(label, fn) {
    if (state.busy) return;
    state.busy = true;
    updateButtons();
    setProgress(0, label);
    try {
        return await fn();
    } catch (e) {
        console.error(e);
        addLog(4, String(e.message || e));
        setStatus('失敗：' + String(e.message || e).split('\n')[0]);
    } finally {
        state.busy = false;
        setProgress(0);
        updateButtons();
    }
}

function updateButtons() {
    const loaded = state.assets.length > 0;
    const disabled = !state.ready || state.busy;
    for (const id of ['#btn-open', '#btn-open-folder', '#url-input', '#btn-url']) $(id).disabled = disabled;
    $('#btn-clear').disabled = disabled || !loaded;
    $('#btn-unpack').disabled = disabled || !loaded;
    $('#btn-export-selected').disabled = disabled || state.selected.size === 0;
    $('#btn-export-filtered').disabled = disabled || state.filtered.length === 0;
    $('#btn-download-current').disabled = disabled || !state.current;
    $('#selected-count').textContent = state.selected.size;
    $('#filtered-count').textContent = state.filtered.length;
}

// ---------------------------------------------------------------- loading files
async function readFiles(fileList) {
    const files = [];
    for (const file of fileList) {
        const path = file.webkitRelativePath || file._relativePath || file.name;
        files.push({ path, file });
    }
    return files;
}

async function loadFiles(entries) {
    if (!entries.length) return;
    await busy(`讀取 ${entries.length} 個檔案…`, async () => {
        // send in batches so huge folders don't need everything in memory twice
        let batch = [], batchBytes = 0;
        const flush = async () => {
            if (!batch.length) return;
            await call('addFiles', batch, batch.map((f) => f.buffer));
            batch = []; batchBytes = 0;
        };
        for (const { path, file, buffer } of entries) {
            const data = buffer || await file.arrayBuffer();
            batch.push({ path, buffer: data });
            batchBytes += data.byteLength;
            if (batchBytes > 64 * 1024 * 1024) await flush();
        }
        await flush();
        setStatus('解析中…');
        const started = performance.now();
        const summary = await call('load', $('#unity-version').value);
        const assets = await call('assets');
        state.assets = assets.map(([id, type, name, container, size, pathId, file, exportable]) =>
            ({ id, type, name, container, size, pathId, file, exportable: exportable === 1 }));
        // everything is reloaded, so ids from a previous load are stale
        state.selected.clear();
        state.anchor = null;
        state.current = null;
        showPreviewPlaceholder();
        if ($('.center-tabs .tab.active').dataset.view === 'bundle') renderBundleFiles();
        renderSummary(summary);
        buildTypeFilters();
        applyFilter();
        $('#empty-state').hidden = state.assets.length > 0;
        const exportable = state.assets.filter((a) => a.exportable).length;
        setStatus(`載入完成：${summary.files.length} 個序列化檔、${state.assets.length} 個物件（可匯出 ${exportable}），耗時 ${((performance.now() - started) / 1000).toFixed(1)} 秒`);
        if (summary.files.length === 0) {
            setStatus('沒有讀到任何 Unity 序列化檔，請確認檔案格式（詳見記錄）');
            $('#log-panel').hidden = false;
        }
    });
}

function renderSummary(summary) {
    const box = $('#file-summary');
    box.innerHTML = '';
    if (!summary.files.length) {
        box.innerHTML = '<p class="muted">尚未載入檔案</p>';
        return;
    }
    for (const f of summary.files) {
        const div = document.createElement('div');
        div.className = 'file';
        div.innerHTML = '<b></b><span></span>';
        div.querySelector('b').textContent = f.name;
        div.querySelector('span').textContent = `${f.source} · Unity ${f.unityVersion} · ${f.platform} · ${f.objects}/${f.totalObjects} 物件`;
        div.title = div.querySelector('span').textContent;
        box.appendChild(div);
    }
}

$('#btn-open').onclick = () => $('#file-input').click();
$('#btn-open-folder').onclick = () => $('#folder-input').click();
$('#file-input').onchange = async (e) => { await loadFiles(await readFiles(e.target.files)); e.target.value = ''; };
$('#folder-input').onchange = async (e) => { await loadFiles(await readFiles(e.target.files)); e.target.value = ''; };

$('#url-form').onsubmit = async (e) => {
    e.preventDefault();
    const url = $('#url-input').value.trim();
    if (url) await loadUrl(url);
};

async function loadUrl(url) {
    let buffer;
    await busy('下載中…', async () => {
        const response = await fetchWithFallback(url);
        const total = Number(response.headers.get('content-length')) || 0;
        const reader = response.body.getReader();
        const chunks = [];
        let received = 0;
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            chunks.push(value);
            received += value.length;
            setProgress(total ? (received / total) * 100 : 0, `下載中… ${formatSize(received)}${total ? ' / ' + formatSize(total) : ''}`);
        }
        buffer = await new Blob(chunks).arrayBuffer();
    });
    if (buffer) {
        const name = decodeURIComponent(new URL(url, location.href).pathname.split('/').pop() || 'download.bundle');
        await loadFiles([{ path: name, buffer }]);
    }
}

// Cross-origin downloads need CORS. When served by serve.mjs, /proxy?url= relays the request.
async function fetchWithFallback(url) {
    try {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response;
    } catch (e) {
        const proxied = await fetch('proxy?url=' + encodeURIComponent(url)).catch(() => null);
        if (proxied && proxied.ok) return proxied;
        throw new Error(`無法下載（${e.message}）。跨網域的檔案需要伺服器允許 CORS，或使用 serve.mjs 的 proxy；也可以先下載後拖曳進來。`);
    }
}

$('#btn-clear').onclick = () => busy('清除中…', async () => {
    await call('clear');
    state.assets = [];
    state.selected.clear();
    state.current = null;
    state.types.clear();
    renderSummary({ files: [] });
    buildTypeFilters();
    applyFilter();
    showPreviewPlaceholder();
    $('#bundle-files').innerHTML = '';
    $('#empty-state').hidden = false;
    setStatus('已清除');
});

// drag & drop (files and folders)
let dragDepth = 0;
window.addEventListener('dragenter', (e) => { e.preventDefault(); dragDepth++; $('#drop-overlay').hidden = false; });
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('#drop-overlay').hidden = true; } });
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', async (e) => {
    e.preventDefault();
    dragDepth = 0;
    $('#drop-overlay').hidden = true;
    if (!state.ready || state.busy) return;
    const items = Array.from(e.dataTransfer.items || []);
    const entries = items.map((i) => i.webkitGetAsEntry && i.webkitGetAsEntry()).filter(Boolean);
    let files;
    if (entries.length) {
        files = [];
        for (const entry of entries) await walkEntry(entry, '', files);
    } else {
        files = await readFiles(e.dataTransfer.files);
    }
    await loadFiles(files);
});

async function walkEntry(entry, prefix, out) {
    if (entry.isFile) {
        const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
        out.push({ path: prefix + file.name, file });
    } else if (entry.isDirectory) {
        const reader = entry.createReader();
        for (;;) {
            const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
            if (!batch.length) break;
            for (const child of batch) await walkEntry(child, prefix + entry.name + '/', out);
        }
    }
}

// ---------------------------------------------------------------- filtering / table
function buildTypeFilters() {
    const counts = new Map();
    for (const a of state.assets) {
        if (!state.showAll && !a.exportable) continue;
        counts.set(a.type, (counts.get(a.type) || 0) + 1);
    }
    const box = $('#type-filters');
    box.innerHTML = '';
    for (const [type, count] of [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
        if (!state.types.has(type)) state.types.set(type, true);
        const label = document.createElement('label');
        label.innerHTML = '<input type="checkbox"><span></span><span class="count"></span>';
        const input = label.querySelector('input');
        input.checked = state.types.get(type);
        input.onchange = () => { state.types.set(type, input.checked); applyFilter(); };
        label.children[1].textContent = type;
        label.children[2].textContent = count;
        box.appendChild(label);
    }
}

$('#btn-types-all').onclick = () => { for (const k of state.types.keys()) state.types.set(k, true); buildTypeFilters(); applyFilter(); };
$('#btn-types-none').onclick = () => { for (const k of state.types.keys()) state.types.set(k, false); buildTypeFilters(); applyFilter(); };
$('#show-all').onchange = (e) => { state.showAll = e.target.checked; buildTypeFilters(); applyFilter(); };

let searchTimer;
$('#search').oninput = (e) => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { state.search = e.target.value.trim().toLowerCase(); applyFilter(); }, 120);
};

$$('.th').forEach((th) => {
    th.onclick = () => {
        const key = th.dataset.sort;
        if (state.sortKey === key) state.sortDesc = !state.sortDesc;
        else { state.sortKey = key; state.sortDesc = false; }
        $$('.th').forEach((t) => t.classList.remove('sorted', 'desc'));
        th.classList.add('sorted');
        if (state.sortDesc) th.classList.add('desc');
        applyFilter();
    };
});

function applyFilter() {
    const q = state.search;
    let list = state.assets.filter((a) =>
        (state.showAll || a.exportable) &&
        state.types.get(a.type) !== false &&
        (!q || a.name.toLowerCase().includes(q) || a.container.toLowerCase().includes(q) || a.pathId.includes(q)));
    if (state.sortKey) {
        const k = state.sortKey;
        const dir = state.sortDesc ? -1 : 1;
        list = list.slice().sort((a, b) => {
            const x = a[k], y = b[k];
            if (k === 'size') return (x - y) * dir;
            if (k === 'pathId') return (BigInt(x) < BigInt(y) ? -1 : BigInt(x) > BigInt(y) ? 1 : 0) * dir;
            return String(x).localeCompare(String(y)) * dir;
        });
    }
    state.filtered = list;
    const visible = new Set(list.map((a) => a.id));
    for (const id of [...state.selected]) if (!visible.has(id)) state.selected.delete(id);
    $('#result-count').textContent = state.assets.length ? `${list.length} / ${state.assets.length}` : '';
    renderRows(true);
    updateButtons();
}

const rowsEl = $('#asset-rows');
const spacer = rowsEl.querySelector('.spacer');
const ROW_HEIGHT = 28;
const rowPool = [];
rowsEl.addEventListener('scroll', () => renderRows());
window.addEventListener('resize', () => renderRows());

function renderRows(reset) {
    if (reset) rowsEl.scrollTop = Math.min(rowsEl.scrollTop, Math.max(0, state.filtered.length * ROW_HEIGHT - rowsEl.clientHeight));
    spacer.style.height = `${state.filtered.length * ROW_HEIGHT}px`;
    const first = Math.max(0, Math.floor(rowsEl.scrollTop / ROW_HEIGHT) - 5);
    const count = Math.ceil(rowsEl.clientHeight / ROW_HEIGHT) + 10;
    const last = Math.min(state.filtered.length, first + count);
    while (rowPool.length < last - first) {
        const row = document.createElement('div');
        row.className = 'row';
        row.innerHTML = '<div class="col-name"></div><div class="col-type"><span class="type-badge"></span></div><div class="col-container"></div><div class="col-size"></div><div class="col-pathid"></div>';
        row.addEventListener('click', onRowClick);
        rowsEl.appendChild(row);
        rowPool.push(row);
    }
    for (let i = 0; i < rowPool.length; i++) {
        const row = rowPool[i];
        const index = first + i;
        if (index >= last) { row.style.display = 'none'; continue; }
        const a = state.filtered[index];
        row.style.display = '';
        row.style.top = `${index * ROW_HEIGHT}px`;
        row.dataset.index = index;
        row.children[0].textContent = a.name;
        row.children[0].title = a.name;
        row.children[1].firstChild.textContent = a.type;
        row.children[2].textContent = a.container;
        row.children[2].title = a.container;
        row.children[3].textContent = formatSize(a.size);
        row.children[4].textContent = a.pathId;
        row.classList.toggle('selected', state.selected.has(a.id));
    }
}

function onRowClick(e) {
    const index = Number(e.currentTarget.dataset.index);
    selectIndex(index, e.shiftKey, e.metaKey || e.ctrlKey);
}

function selectIndex(index, range, toggle) {
    const asset = state.filtered[index];
    if (!asset) return;
    if (range && state.anchor != null) {
        const [from, to] = state.anchor < index ? [state.anchor, index] : [index, state.anchor];
        if (!toggle) state.selected.clear();
        for (let i = from; i <= to; i++) state.selected.add(state.filtered[i].id);
    } else if (toggle) {
        if (state.selected.has(asset.id)) state.selected.delete(asset.id); else state.selected.add(asset.id);
        state.anchor = index;
    } else {
        state.selected.clear();
        state.selected.add(asset.id);
        state.anchor = index;
    }
    renderRows();
    updateButtons();
    showPreview(asset);
}

rowsEl.addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    e.preventDefault();
    const current = state.anchor ?? -1;
    const next = Math.max(0, Math.min(state.filtered.length - 1, current + (e.key === 'ArrowDown' ? 1 : -1)));
    selectIndex(next, e.shiftKey, false);
    const top = next * ROW_HEIGHT;
    if (top < rowsEl.scrollTop) rowsEl.scrollTop = top;
    else if (top + ROW_HEIGHT > rowsEl.scrollTop + rowsEl.clientHeight) rowsEl.scrollTop = top + ROW_HEIGHT - rowsEl.clientHeight;
});
rowsEl.addEventListener('keydown', (e) => {
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'a') {
        e.preventDefault();
        for (const a of state.filtered) state.selected.add(a.id);
        renderRows();
        updateButtons();
    }
});

// ---------------------------------------------------------------- preview
$$('.preview .tab').forEach((tab) => {
    tab.onclick = () => {
        $$('.preview .tab').forEach((t) => t.classList.toggle('active', t === tab));
        $$('.preview .pane').forEach((p) => p.classList.toggle('active', p.id === 'pane-' + tab.dataset.pane));
        if (tab.dataset.pane === 'dump' && state.current) loadDump(state.current);
    };
});

function showPreviewPlaceholder() {
    $('#pane-preview').innerHTML = '<p class="muted pad">選擇一個資產以預覽</p>';
    $('#pane-dump pre').textContent = '';
    $('#pane-info dl').innerHTML = '';
}

let previewToken = 0;
let dumpLoadedFor = null;
let previewUrls = [];

async function showPreview(asset) {
    state.current = asset;
    updateButtons();
    const token = ++previewToken;
    dumpLoadedFor = null;
    previewUrls.forEach((u) => URL.revokeObjectURL(u));
    previewUrls = [];
    const pane = $('#pane-preview');
    pane.innerHTML = '<p class="muted pad">載入中…</p>';
    $('#pane-dump pre').textContent = '';
    let info;
    try {
        info = await call('info', asset.id);
    } catch (e) {
        pane.innerHTML = '';
        pane.appendChild(notice('無法讀取：' + e.message.split('\n')[0]));
        return;
    }
    if (token !== previewToken) return;
    renderInfo(info);
    if ($('.preview .tab.active').dataset.pane === 'dump') loadDump(asset);
    try {
        switch (info.preview) {
            case 'image': await renderImage(asset, info, token); break;
            case 'text': await renderText(asset, token); break;
            case 'shader': await renderText(asset, token, true); break;
            case 'font': await renderFont(asset, token); break;
            case 'audio': renderAudio(info); break;
            case 'mesh':
                pane.innerHTML = '';
                pane.appendChild(notice(`Mesh：${info.vertexCount} 個頂點，${info.subMeshes} 個 SubMesh。可匯出成 OBJ。`));
                break;
            default:
                await renderDumpInline(asset, token);
        }
    } catch (e) {
        if (token !== previewToken) return;
        pane.innerHTML = '';
        pane.appendChild(notice('預覽失敗：' + e.message.split('\n')[0]));
        addLog(4, e.message);
    }
}

function notice(text) {
    const div = document.createElement('div');
    div.className = 'notice';
    div.textContent = text;
    return div;
}

function renderInfo(info) {
    const labels = {
        type: '類型', name: '名稱', container: 'Container', pathId: 'PathID', size: '大小', file: '來源檔', unityVersion: 'Unity 版本',
        width: '寬', height: '高', format: '格式', mipCount: 'Mip 數', streamed: '外部資源', pixelsPerUnit: 'Pixels/Unit',
        atlas: 'SpriteAtlas', texture: 'Texture', length: '長度', channels: '聲道', frequency: '取樣率', compression: '壓縮',
        loadType: 'LoadType', vertexCount: '頂點數', subMeshes: 'SubMesh',
    };
    const dl = $('#pane-info dl');
    dl.innerHTML = '';
    for (const [key, value] of Object.entries(info)) {
        if (key === 'id' || key === 'preview' || value === '' || value == null) continue;
        const dt = document.createElement('dt');
        dt.textContent = labels[key] || key;
        const dd = document.createElement('dd');
        dd.textContent = key === 'size' ? `${formatSize(value)} (${value})` : String(value);
        dl.append(dt, dd);
    }
}

async function renderImage(asset, info, token) {
    const img = await call('image', asset);
    if (token !== previewToken) return;
    const pane = $('#pane-preview');
    pane.innerHTML = '';
    if (!img) {
        pane.appendChild(notice(info.streamed ? '貼圖資料在外部 .resS 檔，請一併載入該檔案。' : '無法解碼此貼圖（格式不支援或資料缺失）。'));
        return;
    }
    const wrap = document.createElement('div');
    wrap.className = 'image-preview';
    const checker = document.createElement('div');
    checker.className = 'checker';
    const canvas = document.createElement('canvas');
    canvas.width = img.width;
    canvas.height = img.height;
    canvas.getContext('2d').putImageData(new ImageData(img.rgba, img.width, img.height), 0, 0);
    canvas.title = '點擊切換原始大小';
    canvas.onclick = () => wrap.classList.toggle('zoomed');
    checker.appendChild(canvas);
    const meta = document.createElement('div');
    meta.className = 'image-meta';
    meta.innerHTML = '<span></span><span></span>';
    meta.children[0].textContent = `${img.width} × ${img.height}`;
    meta.children[1].textContent = info.format || (info.atlas ? `Atlas: ${info.atlas}` : '');
    wrap.append(checker, meta);
    pane.appendChild(wrap);
}

function decodeText(bytes) {
    try {
        return { text: new TextDecoder('utf-8', { fatal: true }).decode(bytes), binary: false };
    } catch {
        return { text: hexDump(bytes.subarray(0, 4096)), binary: true };
    }
}

function hexDump(bytes) {
    const lines = [];
    for (let i = 0; i < bytes.length; i += 16) {
        const slice = bytes.subarray(i, i + 16);
        const hex = Array.from(slice, (b) => b.toString(16).padStart(2, '0')).join(' ');
        const ascii = Array.from(slice, (b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : '.')).join('');
        lines.push(`${i.toString(16).padStart(8, '0')}  ${hex.padEnd(48)}  ${ascii}`);
    }
    return lines.join('\n');
}

const TEXT_LIMIT = 512 * 1024;

async function renderText(asset, token, isShader) {
    const bytes = await call('content', asset.id);
    if (token !== previewToken) return;
    const pane = $('#pane-preview');
    pane.innerHTML = '';
    const { text, binary } = decodeText(bytes);
    if (binary) pane.appendChild(notice(`二進位內容（${formatSize(bytes.length)}），以下為前 4KB 的 hex。`));
    else if (text.length > TEXT_LIMIT) pane.appendChild(notice(`內容較大，只顯示前 ${formatSize(TEXT_LIMIT)}，完整內容請下載。`));
    const pre = document.createElement('pre');
    pre.className = 'code';
    pre.textContent = binary ? text : text.slice(0, TEXT_LIMIT);
    pane.appendChild(pre);
    if (isShader) pane.prepend(notice('Shader 由編譯後的程式反組譯而來，不是可直接使用的原始 .shader。'));
}

async function renderFont(asset, token) {
    const bytes = await call('content', asset.id);
    if (token !== previewToken) return;
    const pane = $('#pane-preview');
    pane.innerHTML = '';
    const family = `preview-font-${asset.id}`;
    try {
        const face = new FontFace(family, bytes);
        await face.load();
        document.fonts.add(face);
    } catch {
        pane.appendChild(notice('瀏覽器無法載入此字型，可直接下載。'));
        return;
    }
    const box = document.createElement('div');
    box.className = 'font-preview';
    box.style.fontFamily = `"${family}"`;
    for (const size of [14, 20, 28, 40]) {
        const p = document.createElement('p');
        p.style.fontSize = `${size}px`;
        p.textContent = 'AssetStudio 0123456789 恭喜發財 你贏得';
        box.appendChild(p);
    }
    pane.appendChild(box);
}

function renderAudio(info) {
    const pane = $('#pane-preview');
    pane.innerHTML = '';
    pane.appendChild(notice(`AudioClip：${info.compression}，${info.channels} 聲道，${info.frequency} Hz，${Number(info.length).toFixed(2)} 秒。` +
        '音訊以 FMOD Sound Bank（.fsb）格式存放，瀏覽器無法直接播放；匯出會得到原始 .fsb，可用 vgmstream / foobar2000 等工具轉檔。'));
}

async function renderDumpInline(asset, token) {
    const text = await call('dump', asset.id);
    if (token !== previewToken) return;
    const pane = $('#pane-preview');
    pane.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'code';
    pre.textContent = text ? text.slice(0, TEXT_LIMIT) : '(此物件沒有 TypeTree，無法 dump)';
    pane.appendChild(pre);
}

async function loadDump(asset) {
    if (dumpLoadedFor === asset.id) return;
    dumpLoadedFor = asset.id;
    const pre = $('#pane-dump pre');
    pre.textContent = '載入中…';
    try {
        const text = await call('dump', asset.id);
        if (state.current !== asset) return;
        pre.textContent = text ? text.slice(0, TEXT_LIMIT) : '(此物件沒有 TypeTree，無法 dump)';
    } catch (e) {
        pre.textContent = '失敗：' + e.message;
    }
}

// ---------------------------------------------------------------- export
function exportOptions() {
    return {
        mode: $('#export-mode').value,
        group: $('#export-group').value,
        imageFormat: $('#export-image-format').value,
    };
}

function saveBlob(blob, name) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function zipName(suffix) {
    const first = state.assets[0]?.file || 'assets';
    return `${first.replace(/[\\/:*?"<>|]/g, '_')}_${suffix}.zip`;
}

async function pickDirectory() {
    if (!$('#export-to-folder').checked) return null;
    if (!window.showDirectoryPicker) {
        alertOnce('此瀏覽器不支援直接寫入資料夾，改為下載 ZIP。');
        return null;
    }
    return await window.showDirectoryPicker({ mode: 'readwrite' });
}

let alerted = false;
function alertOnce(text) {
    if (!alerted) { alerted = true; addLog(3, text); setStatus(text); }
}

async function exportItems(items, label) {
    if (!items.length) return;
    let directory = null;
    try {
        directory = await pickDirectory();
    } catch {
        return; // picker cancelled
    }
    await busy(`匯出 ${items.length} 個資產…`, async () => {
        const opts = exportOptions();
        const result = await call('exportMany', { items, ...opts, directory });
        const summary = `匯出 ${result.exported} 個（略過 ${result.skipped}，失敗 ${result.failed}），耗時 ${result.seconds} 秒`;
        if (result.zip) saveBlob(result.zip, zipName(label));
        setStatus(summary);
        if (result.failed) $('#log-panel').hidden = false;
    });
}

$('#btn-export-selected').onclick = () => exportItems(state.filtered.filter((a) => state.selected.has(a.id)), 'selected');
$('#btn-export-filtered').onclick = () => exportItems(state.filtered, 'export');

$('#btn-download-current').onclick = () => busy('匯出中…', async () => {
    const asset = state.current;
    if (!asset) return;
    const file = await call('exportOne', { item: asset, ...exportOptions() });
    if (!file || !file.data || !file.data.length) {
        setStatus('此資產沒有可匯出的內容');
        return;
    }
    saveBlob(new Blob([file.data]), file.path.split('/').pop());
    setStatus(`已下載 ${file.path.split('/').pop()}`);
});

// ---------------------------------------------------------------- bundle contents (raw unpack)
$$('.center-tabs .tab').forEach((tab) => {
    tab.onclick = () => {
        $$('.center-tabs .tab').forEach((t) => t.classList.toggle('active', t === tab));
        $$('.center .view').forEach((v) => v.classList.toggle('active', v.id === 'view-' + tab.dataset.view));
        if (tab.dataset.view === 'bundle') renderBundleFiles();
        else renderRows();
    };
});

async function renderBundleFiles() {
    const box = $('#bundle-files');
    if (!state.assets.length) { box.innerHTML = ''; return; }
    box.innerHTML = '<p class="muted">讀取中…</p>';
    const rows = await call('bundleFiles');
    const table = document.createElement('table');
    table.innerHTML = '<thead><tr><th>Bundle</th><th>檔案</th><th>大小</th><th></th></tr></thead><tbody></tbody>';
    for (const [index, bundle, path, size] of rows) {
        const tr = document.createElement('tr');
        tr.innerHTML = '<td></td><td></td><td class="num"></td><td><button>下載</button></td>';
        tr.children[0].textContent = bundle;
        tr.children[1].textContent = path;
        tr.children[2].textContent = formatSize(size);
        tr.querySelector('button').onclick = () => busy('下載中…', async () => {
            const data = await call('bundleFile', index);
            saveBlob(new Blob([data]), path.split('/').pop());
            setStatus(`已下載 ${path}`);
        });
        table.tBodies[0].appendChild(tr);
    }
    box.innerHTML = '';
    if (!rows.length) box.appendChild(notice('載入的檔案不是 bundle（沒有可解包的內容）'));
    else box.appendChild(table);
}

$('#btn-unpack').onclick = async () => {
    let directory = null;
    try {
        directory = await pickDirectory();
    } catch {
        return;
    }
    await busy('解包中…', async () => {
        const result = await call('unpackAll', { directory });
        if (result.zip) saveBlob(result.zip, zipName('unpacked'));
        setStatus(`已解出 ${result.count} 個檔案`);
    });
};

// ---------------------------------------------------------------- startup
(async () => {
    updateButtons();
    try {
        await call('init');
        state.ready = true;
        setStatus('就緒：拖曳或開啟 AssetBundle 檔案');
    } catch (e) {
        setStatus('初始化失敗：' + e.message.split('\n')[0]);
        addLog(4, e.message);
    }
    updateButtons();
    const url = new URLSearchParams(location.search).get('url');
    if (state.ready && url) {
        $('#url-input').value = url;
        await loadUrl(url);
    }
})();
