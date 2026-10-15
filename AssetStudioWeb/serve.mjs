#!/usr/bin/env node
// Local static server for the published site (no dependencies, Node 18+).
//   node serve.mjs [siteDir] [port]      default siteDir: ./publish/wwwroot, port: 8080
// Also exposes /proxy?url=<http(s) url> so bundles on CDNs without CORS headers can be loaded by URL.
// The proxy only listens on localhost; don't expose it publicly.
import { createServer } from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { extname, join, normalize, resolve } from 'node:path';
import { Readable } from 'node:stream';

const root = resolve(process.argv[2] || join(import.meta.dirname, 'publish', 'wwwroot'));
const port = Number(process.argv[3] || process.env.PORT || 8080);

const types = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json',
    '.wasm': 'application/wasm',
    '.dat': 'application/octet-stream',
    '.blat': 'application/octet-stream',
    '.ico': 'image/x-icon',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
};

async function proxy(req, res, target) {
    let url;
    try {
        url = new URL(target);
        if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('unsupported protocol');
    } catch {
        res.writeHead(400).end('bad url');
        return;
    }
    try {
        const upstream = await fetch(url, { redirect: 'follow' });
        const headers = { 'content-type': upstream.headers.get('content-type') || 'application/octet-stream' };
        const length = upstream.headers.get('content-length');
        if (length) headers['content-length'] = length;
        res.writeHead(upstream.status, headers);
        if (upstream.body) Readable.fromWeb(upstream.body).pipe(res);
        else res.end();
    } catch (e) {
        res.writeHead(502).end(String(e.message || e));
    }
}

createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname === '/proxy') return proxy(req, res, url.searchParams.get('url'));
    let path = normalize(join(root, decodeURIComponent(url.pathname)));
    if (!path.startsWith(root)) return res.writeHead(403).end();
    try {
        if (statSync(path).isDirectory()) path = join(path, 'index.html');
        const stat = statSync(path);
        res.writeHead(200, {
            'content-type': types[extname(path)] || 'application/octet-stream',
            'content-length': stat.size,
            'cache-control': 'no-cache',
        });
        createReadStream(path).pipe(res);
    } catch {
        res.writeHead(404).end('not found');
    }
}).listen(port, '127.0.0.1', () => {
    console.log(`AssetStudio Web: http://localhost:${port}/  (serving ${root})`);
});
