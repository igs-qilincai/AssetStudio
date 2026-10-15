using System;
using System.Runtime.InteropServices;
using AssetStudio.PInvoke;

namespace Texture2DDecoder
{
    public static unsafe partial class TextureDecoder
    {

        /// <summary>
        /// Alternative decoder implementation (e.g. a WebAssembly module in the browser).
        /// When null, the native Texture2DDecoderNative library is used through P/Invoke.
        /// </summary>
        public static ITextureDecoderBackend Backend { get; set; }

        static TextureDecoder()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
            {
                DllLoader.PreloadDll(T2DDll.DllName);
            }
        }

        public static bool DecodeDXT1(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.DXT1, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeDXT5(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.DXT5, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodePVRTC(byte[] data, int width, int height, byte[] image, bool is2bpp)
        {
            if (Backend != null)
            {
                return Backend.Decode(is2bpp ? TextureCodec.PVRTC2 : TextureCodec.PVRTC4, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodePVRTC(pData, width, height, pImage, is2bpp);
                }
            }
        }

        public static bool DecodeETC1(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ETC1, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ETC2, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A1(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ETC2A1, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A8(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ETC2A8, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACR(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.EACR, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACR(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRSigned(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.EACRSigned, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRG(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.EACRG, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRG(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRGSigned(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.EACRGSigned, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRGSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC4(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.BC4, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC5(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.BC5, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC6(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.BC6, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC6(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC7(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.BC7, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC7(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGB4(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ATCRGB4, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGB4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGBA8(byte[] data, int width, int height, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ATCRGBA8, data, width, height, image);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGBA8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeASTC(byte[] data, int width, int height, int blockWidth, int blockHeight, byte[] image)
        {
            if (Backend != null)
            {
                return Backend.Decode(TextureCodec.ASTC, data, width, height, image, blockWidth, blockHeight);
            }
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeASTC(pData, width, height, blockWidth, blockHeight, pImage);
                }
            }
        }

        public static byte[] UnpackCrunch(byte[] data)
        {
            if (Backend != null)
            {
                return Backend.UnpackCrunch(data, false);
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = data)
            {
                UnpackCrunch(pData, (uint)data.Length, out pBuffer, out bufferSize);
            }

            if (pBuffer == null)
            {
                return null;
            }

            var result = new byte[bufferSize];

            Marshal.Copy(new IntPtr(pBuffer), result, 0, (int)bufferSize);

            DisposeBuffer(ref pBuffer);

            return result;
        }

        public static byte[] UnpackUnityCrunch(byte[] data)
        {
            if (Backend != null)
            {
                return Backend.UnpackCrunch(data, true);
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = data)
            {
                UnpackUnityCrunch(pData, (uint)data.Length, out pBuffer, out bufferSize);
            }

            if (pBuffer == null)
            {
                return null;
            }

            var result = new byte[bufferSize];

            Marshal.Copy(new IntPtr(pBuffer), result, 0, (int)bufferSize);

            DisposeBuffer(ref pBuffer);

            return result;
        }

    }
}
