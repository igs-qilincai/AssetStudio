namespace Texture2DDecoder
{
    public enum TextureCodec
    {
        DXT1,
        DXT5,
        PVRTC2,
        PVRTC4,
        ETC1,
        ETC2,
        ETC2A1,
        ETC2A8,
        EACR,
        EACRSigned,
        EACRG,
        EACRGSigned,
        BC4,
        BC5,
        BC6,
        BC7,
        ATCRGB4,
        ATCRGBA8,
        ASTC
    }

    public interface ITextureDecoderBackend
    {
        /// <summary>
        /// Decodes <paramref name="data"/> into BGRA32 pixels written to <paramref name="image"/>.
        /// <paramref name="blockWidth"/>/<paramref name="blockHeight"/> are only used by ASTC.
        /// </summary>
        bool Decode(TextureCodec codec, byte[] data, int width, int height, byte[] image, int blockWidth = 0, int blockHeight = 0);

        byte[] UnpackCrunch(byte[] data, bool unityCrunch);
    }
}
