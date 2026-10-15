using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public enum AssetGroupOption
    {
        None,
        TypeName,
        ContainerPath,
        SourceFile
    }

    public class ExportOptions
    {
        public ImageFormat ImageFormat = ImageFormat.Png;
        /// <summary>Decode textures/sprites to images instead of writing the raw texture data.</summary>
        public bool ConvertTexture = true;
        /// <summary>Convert audio to WAV through FMOD when the native FMOD library is available.</summary>
        public bool ConvertAudio = true;
        /// <summary>Use the extension of the container path for TextAssets (e.g. .json, .bytes).</summary>
        public bool RestoreExtensionName = true;
        public AssetGroupOption GroupOption = AssetGroupOption.ContainerPath;
    }

    public class ExportedFile
    {
        public string Extension;
        public byte[] Data;

        public ExportedFile(string extension, byte[] data)
        {
            Extension = extension;
            Data = data;
        }
    }

    /// <summary>
    /// UI-independent export logic (mirrors AssetStudioGUI's Exporter). Returns bytes instead of writing files
    /// so the same code serves the command line tool and the browser build.
    /// </summary>
    public static class AssetExporter
    {
        private static bool fmodUnavailable;

        /// <summary>Converts an asset to its natural file format. Returns null when it can't be converted.</summary>
        public static ExportedFile Convert(AssetEntry entry, ExportOptions options)
        {
            switch (entry.Asset)
            {
                case Texture2D m_Texture2D:
                    if (!options.ConvertTexture)
                        return new ExportedFile(".tex", m_Texture2D.image_data.GetData());
                    return EncodeImage(m_Texture2D.ConvertToImage(true), options.ImageFormat);
                case Sprite m_Sprite:
                    return EncodeImage(m_Sprite.GetImage(), options.ImageFormat);
                case AudioClip m_AudioClip:
                    return ExportAudioClip(m_AudioClip, options);
                case Shader m_Shader:
                    return new ExportedFile(".shader", Encoding.UTF8.GetBytes(m_Shader.Convert()));
                case TextAsset m_TextAsset:
                    var extension = ".txt";
                    if (options.RestoreExtensionName && !string.IsNullOrEmpty(entry.Container))
                    {
                        var containerExtension = Path.GetExtension(entry.Container);
                        if (!string.IsNullOrEmpty(containerExtension))
                            extension = containerExtension;
                    }
                    return new ExportedFile(extension, m_TextAsset.m_Script);
                case MonoBehaviour m_MonoBehaviour:
                    var type = m_MonoBehaviour.ToType();
                    if (type == null)
                        return null;
                    return new ExportedFile(".json", Encoding.UTF8.GetBytes(JsonWriter.Serialize(type)));
                case Font m_Font:
                    if (m_Font.m_FontData == null || m_Font.m_FontData.Length < 4)
                        return null;
                    var fontData = m_Font.m_FontData;
                    var isOtf = fontData[0] == 'O' && fontData[1] == 'T' && fontData[2] == 'T' && fontData[3] == 'O';
                    return new ExportedFile(isOtf ? ".otf" : ".ttf", fontData);
                case Mesh m_Mesh:
                    var obj = MeshToObj(m_Mesh);
                    return obj == null ? null : new ExportedFile(".obj", Encoding.UTF8.GetBytes(obj));
                case VideoClip m_VideoClip:
                    if (m_VideoClip.m_ExternalResources.m_Size <= 0)
                        return null;
                    return new ExportedFile(Path.GetExtension(m_VideoClip.m_OriginalPath), m_VideoClip.m_VideoData.GetData());
                case MovieTexture m_MovieTexture:
                    return new ExportedFile(".ogv", m_MovieTexture.m_MovieData);
                default:
                    return Dump(entry);
            }
        }

        /// <summary>Serialized bytes of the object as stored in the file.</summary>
        public static ExportedFile Raw(AssetEntry entry)
        {
            return new ExportedFile(".dat", entry.Asset.GetRawData());
        }

        /// <summary>The object's fields read through its TypeTree, as JSON (or AssetStudio's text dump as fallback).</summary>
        public static ExportedFile Dump(AssetEntry entry)
        {
            var json = DumpJson(entry.Asset);
            if (json != null)
                return new ExportedFile(".json", Encoding.UTF8.GetBytes(json));
            var text = entry.Asset.Dump();
            return text == null ? null : new ExportedFile(".txt", Encoding.UTF8.GetBytes(text));
        }

        public static string DumpJson(Object asset)
        {
            var type = asset.ToType();
            if (type == null && asset is MonoBehaviour)
                return null;
            return type == null ? null : JsonWriter.Serialize(type);
        }

        /// <summary>Decoded image for Texture2D and Sprite assets (top-down, BGRA32), or null.</summary>
        public static Image<Bgra32> GetImage(AssetEntry entry)
        {
            switch (entry.Asset)
            {
                case Texture2D m_Texture2D:
                    return m_Texture2D.ConvertToImage(true);
                case Sprite m_Sprite:
                    return m_Sprite.GetImage();
                default:
                    return null;
            }
        }

        /// <summary>Relative directory for an exported asset, following AssetStudioGUI's grouping options.</summary>
        public static string GetGroupDirectory(AssetEntry entry, AssetGroupOption option)
        {
            switch (option)
            {
                case AssetGroupOption.TypeName:
                    return entry.TypeString;
                case AssetGroupOption.ContainerPath:
                    if (!string.IsNullOrEmpty(entry.Container))
                        return Path.GetDirectoryName(entry.Container) ?? string.Empty;
                    return string.Empty;
                case AssetGroupOption.SourceFile:
                    if (string.IsNullOrEmpty(entry.SourceFile.originalPath))
                        return entry.SourceFile.fileName + "_export";
                    return Path.Combine(Path.GetFileName(entry.SourceFile.originalPath) + "_export", entry.SourceFile.fileName);
                default:
                    return string.Empty;
            }
        }

        public static string FixFileName(string str)
        {
            if (str.Length >= 260)
                return Path.GetRandomFileName();
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            return new string(str.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static ExportedFile EncodeImage(Image<Bgra32> image, ImageFormat format)
        {
            if (image == null)
                return null;
            using (image)
            using (var stream = image.ConvertToStream(format))
            {
                return new ExportedFile("." + format.ToString().ToLower(), stream.ToArray());
            }
        }

        private static ExportedFile ExportAudioClip(AudioClip m_AudioClip, ExportOptions options)
        {
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return null;
            var converter = new AudioClipConverter(m_AudioClip);
            if (options.ConvertAudio && converter.IsSupport && !fmodUnavailable)
            {
                try
                {
                    var wav = converter.ConvertToWav();
                    if (wav != null)
                        return new ExportedFile(".wav", wav);
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
                {
                    // FMOD is only shipped for Windows; fall back to the raw FSB stream elsewhere.
                    fmodUnavailable = true;
                }
            }
            return new ExportedFile(converter.GetExtensionName(), m_AudioData);
        }

        private static string MeshToObj(Mesh m_Mesh)
        {
            if (m_Mesh.m_VertexCount <= 0 || m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                return null;
            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            int c = m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4 ? 4 : 3;
            for (int v = 0; v < m_Mesh.m_VertexCount; v++)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\r\n", -m_Mesh.m_Vertices[v * c], m_Mesh.m_Vertices[v * c + 1], m_Mesh.m_Vertices[v * c + 2]);
            }
            if (m_Mesh.m_UV0?.Length > 0)
            {
                c = 4;
                if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                    c = 2;
                else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                    c = 3;
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "vt {0} {1}\r\n", m_Mesh.m_UV0[v * c], m_Mesh.m_UV0[v * c + 1]);
                }
            }
            if (m_Mesh.m_Normals?.Length > 0)
            {
                if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                    c = 3;
                else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                    c = 4;
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "vn {0} {1} {2}\r\n", -m_Mesh.m_Normals[v * c], m_Mesh.m_Normals[v * c + 1], m_Mesh.m_Normals[v * c + 2]);
                }
            }
            int sum = 0;
            for (var i = 0; i < m_Mesh.m_SubMeshes.Length; i++)
            {
                sb.AppendLine($"g {m_Mesh.m_Name}_{i}");
                int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (int f = sum; f < end; f++)
                {
                    sb.AppendFormat("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n", m_Mesh.m_Indices[f * 3 + 2] + 1, m_Mesh.m_Indices[f * 3 + 1] + 1, m_Mesh.m_Indices[f * 3] + 1);
                }
                sum = end;
            }
            sb.Replace("NaN", "0");
            return sb.ToString();
        }
    }
}
