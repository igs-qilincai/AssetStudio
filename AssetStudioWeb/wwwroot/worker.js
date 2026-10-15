// Runs the .NET runtime (AssetStudio core) and the texture decoder off the UI thread.
// Protocol: main -> { id, cmd, args }, worker -> { id, result } | { id, error } | { type: 'log' | 'progress' | 'status', ... }
import { dotnet } from './_framework/dotnet.js';
import createNativeModule from './native/assetstudio-native.js';
import { ZipWriter } from './zip.js';

let api;
let native;
let unpacked = null;

const post = (msg, transfer) => self.postMessage(msg, transfer || []);
const status = (text) => post({ type: 'status', text });

// Copies a MemoryView (C# Span) into the native module's heap, runs fn(pointer) and frees it.
function withInput(view, fn) {
    const length = view.byteLength;
    const pointer = native._t2d_malloc(Math.max(1, length));
    try {
        view.copyTo(native.HEAPU8.subarray(pointer, pointer + length));
        return fn(pointer, length);
    } finally {
        native._t2d_free(pointer);
    }
}

// ---- bridge to the Emscripten module, imported from C# as JS module "native" (see NativeModule.cs) ----
const nativeImports = {
    decode(codec, dataView, width, height, blockWidth, blockHeight, imageView) {
        const dataLen = dataView.byteLength;
        const outLen = width * height * 4;
        const pIn = native._t2d_malloc(dataLen);
        const pOut = native._t2d_malloc(outLen);
        try {
            dataView.copyTo(native.HEAPU8.subarray(pIn, pIn + dataLen));
            const ok = native._t2d_decode(codec, pIn, width, height, blockWidth, blockHeight, pOut);
            if (ok) imageView.set(native.HEAPU8.subarray(pOut, pOut + outLen));
            return !!ok;
        } finally {
            native._t2d_free(pIn);
            native._t2d_free(pOut);
        }
    },
    unpackCrunch(dataView, unityCrunch) {
        const dataLen = dataView.byteLength;
        const pIn = native._t2d_malloc(dataLen);
        const pSize = native._t2d_malloc(4);
        try {
            dataView.copyTo(native.HEAPU8.subarray(pIn, pIn + dataLen));
            const pResult = native._t2d_unpack_crunch(pIn, dataLen, unityCrunch ? 1 : 0, pSize);
            const size = native.HEAPU32[pSize >> 2];
            if (!pResult) return 0;
            unpacked = native.HEAPU8.slice(pResult, pResult + size);
            native._t2d_free_crunch(pResult);
            return size;
        } finally {
            native._t2d_free(pIn);
            native._t2d_free(pSize);
        }
    },
    takeUnpacked(destView) {
        destView.set(unpacked);
        unpacked = null;
    },
    lz4: (sourceView, targetView) => decompress(native._as_lz4_decode, sourceView, targetView),
    lzma: (sourceView, targetView) => decompress(native._as_lzma_decode, sourceView, targetView),
};

function decompress(fn, sourceView, targetView) {
    const targetLength = targetView.byteLength;
    const pTarget = native._t2d_malloc(Math.max(1, targetLength));
    try {
        const written = withInput(sourceView, (pSource, sourceLength) => fn(pSource, sourceLength, pTarget, targetLength));
        if (written > 0) targetView.set(native.HEAPU8.subarray(pTarget, pTarget + written));
        return written;
    } finally {
        native._t2d_free(pTarget);
    }
}

const host = {
    log: (level, message) => post({ type: 'log', level, message }),
    progress: (value) => post({ type: 'progress', value }),
};

async function init() {
    status('Loading native module…');
    native = await createNativeModule({ locateFile: (f) => new URL('./native/' + f, import.meta.url).href });
    status('Starting .NET runtime…');
    const runtime = await dotnet.create();
    runtime.setModuleImports('native', nativeImports);
    runtime.setModuleImports('host', host);
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    api = exports.AssetStudioWeb.WebApi;
    api.Init();
    return { ok: true };
}

// ---- images ----
// C# returns [width][height][flipY] + BGRA pixels. Textures come back bottom-up (as stored by Unity),
// sprites cut by ImageSharp come back top-down.
function parseImagePayload(bytes) {
    if (!bytes || bytes.length < 12) return null;
    const view = new DataView(bytes.buffer, bytes.byteOffset, 12);
    const width = view.getInt32(0, true);
    const height = view.getInt32(4, true);
    const flipY = view.getInt32(8, true) === 1;
    const start = bytes.byteOffset + 12;
    // Uint32 view needs 4-byte alignment; copy if the marshalled array isn't aligned
    const pixels = start % 4 === 0
        ? new Uint32Array(bytes.buffer, start, width * height)
        : new Uint32Array(bytes.slice(12, 12 + width * height * 4).buffer);
    return { width, height, pixels, bottomUp: flipY };
}

// BGRA words (optionally bottom-up) -> top-down RGBA bytes
function toRgba({ width, height, pixels, bottomUp }) {
    const src = new Uint8Array(pixels.buffer, pixels.byteOffset, width * height * 4);
    const rgba = new Uint8ClampedArray(width * height * 4);
    const stride = width * 4;
    for (let y = 0; y < height; y++) {
        const s = (bottomUp ? height - 1 - y : y) * stride;
        const d = y * stride;
        for (let x = 0; x < stride; x += 4) {
            rgba[d + x] = src[s + x + 2];
            rgba[d + x + 1] = src[s + x + 1];
            rgba[d + x + 2] = src[s + x];
            rgba[d + x + 3] = src[s + x + 3];
        }
    }
    return { width, height, rgba };
}

// Decoded textures kept for sprite cutting (sprites usually share a few atlases).
const textureCache = new Map();
function getTexture(id) {
    let tex = textureCache.get(id);
    if (tex) {
        textureCache.delete(id);
        textureCache.set(id, tex);
        return tex;
    }
    tex = parseImagePayload(api.GetImage(id));
    if (!tex) return null;
    textureCache.set(id, tex);
    while (textureCache.size > 4) textureCache.delete(textureCache.keys().next().value);
    return tex;
}

// Port of AssetStudioUtility/SpriteHelper.CutImage working on 32-bit BGRA words. Rows stay in texture order
// (bottom-up) until the end, exactly like the C# version, so rotations and the tight mesh mask line up.
function cutSprite(info) {
    const tex = getTexture(info.texture);
    if (!tex) return null;
    const [rx, ry, rw, rh] = info.rect;
    // C# adds these as float32; doing it in float64 can round the crop edge to a different pixel
    const x0 = Math.floor(rx), y0 = Math.floor(ry);
    const f32 = Math.fround;
    const x1 = Math.min(Math.ceil(f32(f32(rx) + f32(rw))), tex.width), y1 = Math.min(Math.ceil(f32(f32(ry) + f32(rh))), tex.height);
    const cropW = x1 - x0, cropH = y1 - y0;
    if (x0 < 0 || y0 < 0 || cropW <= 0 || cropH <= 0) return null;
    let w = cropW, h = cropH;
    let px = new Uint32Array(w * h);
    for (let y = 0; y < h; y++) {
        const s = (y0 + y) * tex.width + x0;
        px.set(tex.pixels.subarray(s, s + w), y * w);
    }
    if (info.packed === 1) {
        const out = new Uint32Array(w * h);
        switch (info.rotation) {
            case 1: // FlipHorizontal
                for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) out[y * w + x] = px[y * w + (w - 1 - x)];
                px = out;
                break;
            case 2: // FlipVertical
                for (let y = 0; y < h; y++) out.set(px.subarray((h - 1 - y) * w, (h - y) * w), y * w);
                px = out;
                break;
            case 3: // Rotate180
                px.reverse();
                break;
            case 4: { // Rotate90 -> ImageSharp Rotate(270)
                const nw = h, nh = w;
                for (let y = 0; y < nh; y++) for (let x = 0; x < nw; x++) out[y * nw + x] = px[x * w + (w - 1 - y)];
                px = out; w = nw; h = nh;
                break;
            }
        }
    }
    if (info.tight && info.triangles) {
        // same rasterizer as SpriteHelper.RasterizeTriangles; fround restores the exact float32 inputs
        const mask = new Uint8Array(cropW * cropH);
        const t = info.triangles.map(Math.fround);
        for (let i = 0; i + 5 < t.length; i += 6) rasterizeTriangle(mask, cropW, cropH, t[i], t[i + 1], t[i + 2], t[i + 3], t[i + 4], t[i + 5]);
        // ImageSharp composites the mask in premultiplied space, so every fully transparent pixel ends up 0,0,0,0
        for (let y = 0; y < h; y++) {
            const my = (y % cropH) * cropW;
            for (let x = 0; x < w; x++) {
                const i = y * w + x;
                if (!mask[my + (x % cropW)] || px[i] >>> 24 === 0) px[i] = 0;
            }
        }
    }
    return { width: w, height: h, pixels: px, bottomUp: true };
}

function rasterizeTriangle(mask, mw, mh, ax, ay, bx, by, cx, cy) {
    const area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    if (area === 0) return;
    const minX = Math.max(0, Math.floor(Math.min(ax, bx, cx))), maxX = Math.min(mw - 1, Math.ceil(Math.max(ax, bx, cx)));
    const minY = Math.max(0, Math.floor(Math.min(ay, by, cy))), maxY = Math.min(mh - 1, Math.ceil(Math.max(ay, by, cy)));
    const sign = area > 0 ? 1 : -1;
    for (let y = minY; y <= maxY; y++) {
        const py = y + 0.5;
        for (let x = minX; x <= maxX; x++) {
            const pxc = x + 0.5;
            const w0 = ((bx - ax) * (py - ay) - (by - ay) * (pxc - ax)) * sign;
            const w1 = ((cx - bx) * (py - by) - (cy - by) * (pxc - bx)) * sign;
            const w2 = ((ax - cx) * (py - cy) - (ay - cy) * (pxc - cx)) * sign;
            if (w0 >= 0 && w1 >= 0 && w2 >= 0) mask[y * mw + x] = 1;
        }
    }
}

// Top-down RGBA image for a Texture2D or Sprite, or null.
function getImage(item) {
    if (item.type === 'Sprite') {
        const info = api.GetSpriteInfo(item.id);
        if (info) {
            const sprite = cutSprite(JSON.parse(info));
            return sprite ? toRgba(sprite) : null;
        }
    }
    const img = parseImagePayload(api.GetImage(item.id));
    return img ? toRgba(img) : null;
}

// ---- PNG encoder: lossless (canvas would round colours through premultiplied alpha) ----
const PNG_CRC = (() => {
    const table = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        table[n] = c >>> 0;
    }
    return table;
})();

function pngChunk(type, data) {
    const chunk = new Uint8Array(12 + data.length);
    const view = new DataView(chunk.buffer);
    view.setUint32(0, data.length);
    for (let i = 0; i < 4; i++) chunk[4 + i] = type.charCodeAt(i);
    chunk.set(data, 8);
    let crc = 0xffffffff;
    for (let i = 4; i < 8 + data.length; i++) crc = PNG_CRC[(crc ^ chunk[i]) & 0xff] ^ (crc >>> 8);
    view.setUint32(8 + data.length, (crc ^ 0xffffffff) >>> 0);
    return chunk;
}

async function encodePng({ width, height, rgba }) {
    const stride = width * 4;
    const raw = new Uint8Array((stride + 1) * height);
    const candidate = new Uint8Array(stride);
    for (let y = 0; y < height; y++) {
        const row = y * stride;
        const out = y * (stride + 1);
        // pick None / Sub / Up per row by smallest sum of absolute values (standard PNG heuristic)
        let best = 0, bestScore = Infinity;
        for (let filter = 0; filter <= 2; filter++) {
            if (filter === 2 && y === 0) continue;
            let score = 0;
            for (let x = 0; x < stride; x++) {
                const v = rgba[row + x];
                const f = filter === 0 ? v : filter === 1 ? (x >= 4 ? v - rgba[row + x - 4] : v) : v - rgba[row - stride + x];
                const b = f & 0xff;
                score += b < 128 ? b : 256 - b;
            }
            if (score < bestScore) { bestScore = score; best = filter; }
        }
        raw[out] = best;
        for (let x = 0; x < stride; x++) {
            const v = rgba[row + x];
            candidate[x] = best === 0 ? v : best === 1 ? (x >= 4 ? v - rgba[row + x - 4] : v) : v - rgba[row - stride + x];
        }
        raw.set(candidate, out + 1);
    }
    const idat = new Uint8Array(await new Response(new Blob([raw]).stream().pipeThrough(new CompressionStream('deflate'))).arrayBuffer());
    const ihdr = new Uint8Array(13);
    const v = new DataView(ihdr.buffer);
    v.setUint32(0, width);
    v.setUint32(4, height);
    ihdr[8] = 8; // bit depth
    ihdr[9] = 6; // RGBA
    const parts = [new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), pngChunk('IHDR', ihdr), pngChunk('IDAT', idat), pngChunk('IEND', new Uint8Array(0))];
    return new Uint8Array(await new Blob(parts).arrayBuffer());
}

async function encodeImage(img, format) {
    if (format !== 'jpeg' && format !== 'webp') return { ext: '.png', data: await encodePng(img) };
    const canvas = new OffscreenCanvas(img.width, img.height);
    canvas.getContext('2d').putImageData(new ImageData(img.rgba, img.width, img.height), 0, 0);
    const blob = await canvas.convertToBlob({ type: 'image/' + format, quality: 0.95 });
    return { ext: format === 'jpeg' ? '.jpg' : '.webp', data: new Uint8Array(await blob.arrayBuffer()) };
}

function splitExport(bytes) {
    if (!bytes || bytes.length < 4) return null;
    const pathLength = new DataView(bytes.buffer, bytes.byteOffset, 4).getInt32(0, true);
    const path = new TextDecoder().decode(bytes.subarray(4, 4 + pathLength));
    return { path, data: bytes.subarray(4 + pathLength) };
}

const IMAGE_TYPES = new Set(['Texture2D', 'Sprite']);

async function exportOne(item, opts) {
    if (opts.mode === 'convert' && IMAGE_TYPES.has(item.type)) {
        const img = getImage(item);
        if (!img) return null;
        const file = await encodeImage(img, opts.imageFormat);
        return { path: api.GetExportPath(item.id, file.ext, opts.group), data: file.data };
    }
    return splitExport(api.ExportAsset(item.id, opts.mode, opts.group));
}

// Paths are compared case-insensitively: "UP_EN.png" and "Up_EN.png" would overwrite each other on macOS/Windows.
function uniquePath(path, used, item) {
    if (!used.has(path.toLowerCase())) { used.add(path.toLowerCase()); return path; }
    const dot = path.lastIndexOf('.');
    const slash = path.lastIndexOf('/');
    const base = dot > slash ? path.slice(0, dot) : path;
    const ext = dot > slash ? path.slice(dot) : '';
    let candidate = `${base} #${item.pathId}${ext}`;
    for (let n = 2; used.has(candidate.toLowerCase()); n++) candidate = `${base} #${item.pathId}_${n}${ext}`;
    used.add(candidate.toLowerCase());
    return candidate;
}

async function writeToDirectory(root, path, data) {
    const parts = path.split('/').filter(Boolean);
    let dir = root;
    for (const part of parts.slice(0, -1)) dir = await dir.getDirectoryHandle(part, { create: true });
    const handle = await dir.getFileHandle(parts[parts.length - 1], { create: true });
    const writable = await handle.createWritable();
    await writable.write(data);
    await writable.close();
}

// Exports a list of assets either into a zip (returned as Blob) or into a directory handle (File System Access API).
function checkDirectorySupport(directory) {
    if (directory && !('createWritable' in FileSystemFileHandle.prototype)) {
        throw new Error('此瀏覽器不支援直接寫入資料夾，請取消勾選「直接寫入資料夾」改用 ZIP。');
    }
}

async function exportMany({ items, mode, group, imageFormat, directory }) {
    checkDirectorySupport(directory);
    const zip = directory ? null : new ZipWriter();
    const used = new Set();
    let exported = 0, skipped = 0, failed = 0;
    const started = performance.now();
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        try {
            const file = await exportOne(item, { mode, group, imageFormat });
            if (!file || !file.data || file.data.length === 0) { skipped++; }
            else {
                const path = uniquePath(file.path, used, item);
                if (zip) zip.add(path, file.data); else await writeToDirectory(directory, path, file.data);
                exported++;
            }
        } catch (e) {
            failed++;
            post({ type: 'log', level: 4, message: `Export ${item.type} "${item.name}" failed: ${e.message || e}` });
        }
        if (i % 5 === 0 || i === items.length - 1) {
            post({ type: 'progress', value: Math.round(((i + 1) / items.length) * 100), text: `Exporting ${i + 1}/${items.length}: ${item.name}` });
        }
    }
    const seconds = ((performance.now() - started) / 1000).toFixed(1);
    return { exported, skipped, failed, seconds, zip: zip ? zip.finish() : null };
}

async function unpackAll({ directory }) {
    checkDirectorySupport(directory);
    const rows = JSON.parse(api.ListBundleFiles());
    const zip = directory ? null : new ZipWriter();
    for (let i = 0; i < rows.length; i++) {
        const [index, bundle, path] = rows[i];
        const data = api.GetBundleFile(index);
        const target = `${bundle}_unpacked/${path}`;
        if (zip) zip.add(target, data); else await writeToDirectory(directory, target, data);
        post({ type: 'progress', value: Math.round(((i + 1) / rows.length) * 100), text: `Unpacking ${path}` });
    }
    return { count: rows.length, zip: zip ? zip.finish() : null };
}

const commands = {
    init,
    addFiles(files) {
        for (const f of files) api.AddFile(f.path, new Uint8Array(f.buffer));
        return files.length;
    },
    load: (unityVersion) => {
        textureCache.clear(); // asset ids change on every load
        return JSON.parse(api.Load(unityVersion || ''));
    },
    clear: () => { textureCache.clear(); api.Clear(); return true; },
    assets: () => JSON.parse(api.GetAssets()),
    info: (id) => JSON.parse(api.GetAssetInfo(id)),
    image: (item) => {
        const img = getImage(item);
        return img ? { value: img, transfer: [img.rgba.buffer] } : null;
    },
    content: (id) => {
        const bytes = api.GetContent(id);
        return { value: bytes, transfer: [bytes.buffer] };
    },
    dump: (id) => api.GetDump(id),
    exportOne: async ({ item, mode, group, imageFormat }) => exportOne(item, { mode, group, imageFormat }),
    exportMany,
    bundleFiles: () => JSON.parse(api.ListBundleFiles()),
    bundleFile: (index) => {
        const bytes = api.GetBundleFile(index);
        return { value: bytes, transfer: [bytes.buffer] };
    },
    unpackAll,
};

// Commands run strictly one after another: the .NET side is single threaded.
let queue = Promise.resolve();
self.onmessage = (event) => {
    const { id, cmd, args } = event.data;
    queue = queue.then(async () => {
        try {
            const result = await commands[cmd](args);
            if (result && result.transfer) post({ id, result: result.value }, result.transfer);
            else post({ id, result });
        } catch (e) {
            post({ id, error: (e && (e.stack || e.message)) || String(e) });
        }
    });
};
