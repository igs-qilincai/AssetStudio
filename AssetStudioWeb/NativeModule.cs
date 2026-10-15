using System;
using System.Runtime.InteropServices.JavaScript;
using AssetStudio;
using Texture2DDecoder;

namespace AssetStudioWeb
{
    /// <summary>
    /// Bindings to the Emscripten module built from Texture2DDecoderNative + LZ4/LZMA
    /// (wwwroot/native, see Texture2DDecoderNative/build-wasm.sh), registered as JS module "native" in worker.js.
    /// Spans are passed as MemoryView: the JS side copies them in/out of the module's heap synchronously.
    /// </summary>
    internal static partial class NativeModule
    {
        [JSImport("decode", "native")]
        internal static partial bool Decode(int codec, [JSMarshalAs<JSType.MemoryView>] Span<byte> data, int width, int height, int blockWidth, int blockHeight, [JSMarshalAs<JSType.MemoryView>] Span<byte> image);

        [JSImport("unpackCrunch", "native")]
        internal static partial int UnpackCrunch([JSMarshalAs<JSType.MemoryView>] Span<byte> data, bool unityCrunch);

        [JSImport("takeUnpacked", "native")]
        internal static partial void TakeUnpacked([JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

        [JSImport("lz4", "native")]
        internal static partial int Lz4([JSMarshalAs<JSType.MemoryView>] Span<byte> source, [JSMarshalAs<JSType.MemoryView>] Span<byte> target);

        [JSImport("lzma", "native")]
        internal static partial int Lzma([JSMarshalAs<JSType.MemoryView>] Span<byte> source, [JSMarshalAs<JSType.MemoryView>] Span<byte> target);
    }

    internal class WasmTextureDecoder : ITextureDecoderBackend
    {
        public bool Decode(TextureCodec codec, byte[] data, int width, int height, byte[] image, int blockWidth = 0, int blockHeight = 0)
        {
            return NativeModule.Decode((int)codec, data, width, height, blockWidth, blockHeight, image);
        }

        public byte[] UnpackCrunch(byte[] data, bool unityCrunch)
        {
            var size = NativeModule.UnpackCrunch(data, unityCrunch);
            if (size <= 0)
                return null;
            var result = new byte[size];
            NativeModule.TakeUnpacked(result);
            return result;
        }
    }

    internal class WasmBundleDecompressor : IBundleDecompressor
    {
        public int DecompressLz4(byte[] source, int sourceOffset, int sourceLength, byte[] target, int targetOffset, int targetLength)
        {
            return NativeModule.Lz4(source.AsSpan(sourceOffset, sourceLength), target.AsSpan(targetOffset, targetLength));
        }

        public int DecompressLzma(byte[] source, int sourceOffset, int sourceLength, byte[] target, int targetOffset, int targetLength)
        {
            return NativeModule.Lzma(source.AsSpan(sourceOffset, sourceLength), target.AsSpan(targetOffset, targetLength));
        }
    }
}
