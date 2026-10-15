// Thin C API used by the browser build (AssetStudioWeb). Compiled together with the decoder sources by build-wasm.sh.
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include "../bcn.h"
#include "../pvrtc.h"
#include "../etc.h"
#include "../atc.h"
#include "../astc.h"
#include "../crunch.h"
#include "../unitycrunch.h"

#include <emscripten/emscripten.h>

// Must match Texture2DDecoder.TextureCodec in Texture2DDecoderWrapper/ITextureDecoderBackend.cs
enum TextureCodec
{
    DXT1, DXT5, PVRTC2, PVRTC4, ETC1, ETC2, ETC2A1, ETC2A8, EACR, EACRSigned, EACRG, EACRGSigned,
    BC4, BC5, BC6, BC7, ATCRGB4, ATCRGBA8, ASTC
};

extern "C" {

EMSCRIPTEN_KEEPALIVE void* t2d_malloc(uint32_t size) { return malloc(size); }

EMSCRIPTEN_KEEPALIVE void t2d_free(void* p) { free(p); }

// Decodes `data` into BGRA32 pixels in `image` (width * height * 4 bytes). Returns 1 on success.
EMSCRIPTEN_KEEPALIVE int t2d_decode(int codec, const uint8_t* data, int width, int height, int blockWidth, int blockHeight, uint32_t* image)
{
    switch (codec)
    {
        case DXT1: return decode_bc1(data, width, height, image);
        case DXT5: return decode_bc3(data, width, height, image);
        case PVRTC2: return decode_pvrtc(data, width, height, image, 1);
        case PVRTC4: return decode_pvrtc(data, width, height, image, 0);
        case ETC1: return decode_etc1(data, width, height, image);
        case ETC2: return decode_etc2(data, width, height, image);
        case ETC2A1: return decode_etc2a1(data, width, height, image);
        case ETC2A8: return decode_etc2a8(data, width, height, image);
        case EACR: return decode_eacr(data, width, height, image);
        case EACRSigned: return decode_eacr_signed(data, width, height, image);
        case EACRG: return decode_eacrg(data, width, height, image);
        case EACRGSigned: return decode_eacrg_signed(data, width, height, image);
        case BC4: return decode_bc4(data, width, height, image);
        case BC5: return decode_bc5(data, width, height, image);
        case BC6: return decode_bc6(data, width, height, image);
        case BC7: return decode_bc7(data, width, height, image);
        case ATCRGB4: return decode_atc_rgb4(data, width, height, image);
        case ATCRGBA8: return decode_atc_rgba8(data, width, height, image);
        case ASTC: return decode_astc(data, width, height, blockWidth, blockHeight, image);
        default: return 0;
    }
}

// Unpacks crunched texture data to the underlying DXT/ETC blocks. The result must be released with t2d_free_crunch.
EMSCRIPTEN_KEEPALIVE void* t2d_unpack_crunch(const uint8_t* data, uint32_t size, int unityCrunch, uint32_t* resultSize)
{
    void* result = nullptr;
    *resultSize = 0;
    bool ok = unityCrunch
        ? unity_crunch_unpack_level(data, size, 0, &result, resultSize)
        : crunch_unpack_level(data, size, 0, &result, resultSize);
    return ok ? result : nullptr;
}

EMSCRIPTEN_KEEPALIVE void t2d_free_crunch(void* p) { delete[] static_cast<uint8_t*>(p); }

}
