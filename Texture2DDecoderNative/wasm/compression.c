// LZ4 / LZMA block decompression for the browser build. The managed decoders used by AssetStudio
// are very slow under the .NET WebAssembly interpreter, so BundleFile delegates to these instead.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <emscripten/emscripten.h>

#include "lzma/LzmaDec.h"

// Decodes one LZ4 block. Returns the number of bytes written, or -1 on malformed input.
EMSCRIPTEN_KEEPALIVE int as_lz4_decode(const uint8_t* src, int srcLen, uint8_t* dst, int dstLen)
{
    const uint8_t* ip = src;
    const uint8_t* const iend = src + srcLen;
    uint8_t* op = dst;
    uint8_t* const oend = dst + dstLen;

    while (ip < iend)
    {
        const unsigned token = *ip++;
        size_t length = token >> 4;
        if (length == 15)
        {
            unsigned b;
            do
            {
                if (ip >= iend) return -1;
                b = *ip++;
                length += b;
            } while (b == 255);
        }
        if (length > (size_t)(iend - ip) || length > (size_t)(oend - op)) return -1;
        memcpy(op, ip, length);
        op += length;
        ip += length;
        if (ip >= iend) break; // the last sequence only has literals

        if (iend - ip < 2) return -1;
        const size_t offset = (size_t)ip[0] | ((size_t)ip[1] << 8);
        ip += 2;
        if (offset == 0 || offset > (size_t)(op - dst)) return -1;

        length = token & 15;
        if (length == 15)
        {
            unsigned b;
            do
            {
                if (ip >= iend) return -1;
                b = *ip++;
                length += b;
            } while (b == 255);
        }
        length += 4;
        if (length > (size_t)(oend - op)) return -1;

        const uint8_t* match = op - offset;
        if (offset >= length)
        {
            memcpy(op, match, length);
            op += length;
        }
        else
        {
            while (length--) *op++ = *match++; // overlapping copy
        }
    }
    return (int)(op - dst);
}

static void* SzAlloc(ISzAllocPtr p, size_t size) { (void)p; return malloc(size); }
static void SzFree(ISzAllocPtr p, void* address) { (void)p; free(address); }
static const ISzAlloc g_Alloc = { SzAlloc, SzFree };

// Decodes an LZMA stream prefixed with its 5-byte properties header (Unity bundle layout).
// Returns the number of bytes written, or -1 on error.
EMSCRIPTEN_KEEPALIVE int as_lzma_decode(const uint8_t* src, int srcLen, uint8_t* dst, int dstLen)
{
    if (srcLen < LZMA_PROPS_SIZE) return -1;
    SizeT outLen = (SizeT)dstLen;
    SizeT inLen = (SizeT)(srcLen - LZMA_PROPS_SIZE);
    ELzmaStatus status;
    SRes res = LzmaDecode(dst, &outLen, src + LZMA_PROPS_SIZE, &inLen, src, LZMA_PROPS_SIZE, LZMA_FINISH_ANY, &status, &g_Alloc);
    return res == SZ_OK ? (int)outLen : -1;
}
