// Minimal ZIP writer (STORE, no compression) with ZIP64 support. Exported images are already compressed,
// so storing keeps exports fast. Entries are kept as Blob parts to avoid one huge contiguous buffer.
const CRC_TABLE = (() => {
    const table = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        table[n] = c >>> 0;
    }
    return table;
})();

function crc32(data) {
    let crc = 0xffffffff;
    for (let i = 0; i < data.length; i++) crc = CRC_TABLE[(crc ^ data[i]) & 0xff] ^ (crc >>> 8);
    return (crc ^ 0xffffffff) >>> 0;
}

function dosDateTime(date) {
    const time = (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1);
    const day = ((date.getFullYear() - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();
    return { time, day };
}

const U32_MAX = 0xffffffff;

export class ZipWriter {
    constructor() {
        this.parts = [];
        this.entries = [];
        this.offset = 0;
        this.encoder = new TextEncoder();
        this.stamp = dosDateTime(new Date());
    }

    add(path, data) {
        const name = this.encoder.encode(path);
        const crc = crc32(data);
        const size = data.length;
        const zip64 = size >= U32_MAX || this.offset >= U32_MAX;
        const extra = zip64 ? 20 : 0;
        const header = new DataView(new ArrayBuffer(30 + name.length + extra));
        header.setUint32(0, 0x04034b50, true);
        header.setUint16(4, zip64 ? 45 : 20, true);
        header.setUint16(6, 0x0800, true); // UTF-8 names
        header.setUint16(8, 0, true); // stored
        header.setUint16(10, this.stamp.time, true);
        header.setUint16(12, this.stamp.day, true);
        header.setUint32(14, crc, true);
        header.setUint32(18, zip64 ? U32_MAX : size, true);
        header.setUint32(22, zip64 ? U32_MAX : size, true);
        header.setUint16(26, name.length, true);
        header.setUint16(28, extra, true);
        new Uint8Array(header.buffer).set(name, 30);
        if (zip64) {
            header.setUint16(30 + name.length, 0x0001, true);
            header.setUint16(32 + name.length, 16, true);
            header.setBigUint64(34 + name.length, BigInt(size), true);
            header.setBigUint64(42 + name.length, BigInt(size), true);
        }
        this.parts.push(header.buffer, data);
        this.entries.push({ name, crc, size, offset: this.offset });
        this.offset += header.byteLength + size;
    }

    finish() {
        const centralStart = this.offset;
        let centralSize = 0;
        for (const e of this.entries) {
            const zip64 = e.size >= U32_MAX || e.offset >= U32_MAX;
            const extra = zip64 ? 28 : 0;
            const h = new DataView(new ArrayBuffer(46 + e.name.length + extra));
            h.setUint32(0, 0x02014b50, true);
            h.setUint16(4, zip64 ? 45 : 20, true);
            h.setUint16(6, zip64 ? 45 : 20, true);
            h.setUint16(8, 0x0800, true);
            h.setUint16(10, 0, true);
            h.setUint16(12, this.stamp.time, true);
            h.setUint16(14, this.stamp.day, true);
            h.setUint32(16, e.crc, true);
            h.setUint32(20, zip64 ? U32_MAX : e.size, true);
            h.setUint32(24, zip64 ? U32_MAX : e.size, true);
            h.setUint16(28, e.name.length, true);
            h.setUint16(30, extra, true);
            h.setUint32(42, zip64 ? U32_MAX : e.offset, true);
            new Uint8Array(h.buffer).set(e.name, 46);
            if (zip64) {
                const p = 46 + e.name.length;
                h.setUint16(p, 0x0001, true);
                h.setUint16(p + 2, 24, true);
                h.setBigUint64(p + 4, BigInt(e.size), true);
                h.setBigUint64(p + 12, BigInt(e.size), true);
                h.setBigUint64(p + 20, BigInt(e.offset), true);
            }
            this.parts.push(h.buffer);
            centralSize += h.byteLength;
        }
        const count = this.entries.length;
        const needZip64 = count >= 0xffff || centralStart >= U32_MAX || centralSize >= U32_MAX;
        if (needZip64) {
            const z = new DataView(new ArrayBuffer(56 + 20));
            z.setUint32(0, 0x06064b50, true);
            z.setBigUint64(4, 44n, true);
            z.setUint16(12, 45, true);
            z.setUint16(14, 45, true);
            z.setBigUint64(24, BigInt(count), true);
            z.setBigUint64(32, BigInt(count), true);
            z.setBigUint64(40, BigInt(centralSize), true);
            z.setBigUint64(48, BigInt(centralStart), true);
            z.setUint32(56, 0x07064b50, true);
            z.setBigUint64(64, BigInt(centralStart + centralSize), true);
            z.setUint32(72, 1, true);
            this.parts.push(z.buffer);
        }
        const end = new DataView(new ArrayBuffer(22));
        end.setUint32(0, 0x06054b50, true);
        end.setUint16(8, needZip64 ? 0xffff : count, true);
        end.setUint16(10, needZip64 ? 0xffff : count, true);
        end.setUint32(12, needZip64 ? U32_MAX : centralSize, true);
        end.setUint32(16, needZip64 ? U32_MAX : centralStart, true);
        this.parts.push(end.buffer);
        return new Blob(this.parts, { type: 'application/zip' });
    }
}
