using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Texture2DDecoder;
using Object = AssetStudio.Object;

namespace AssetStudioWeb
{
    /// <summary>
    /// API exported to JavaScript. Everything runs inside a Web Worker (wwwroot/worker.js);
    /// files dropped on the page are copied into the in-memory file system under /data.
    /// </summary>
    public static partial class WebApi
    {
        private const string DataRoot = "/data";

        private static AssetsManager assetsManager = new AssetsManager();
        private static List<AssetEntry> entries = new List<AssetEntry>();
        private static Dictionary<Object, AssetEntry> entryByObject = new Dictionary<Object, AssetEntry>();
        private static readonly List<string> stagedFiles = new List<string>();
        private static List<(string bundle, StreamFile file)> bundleContents;

        [JSImport("log", "host")]
        private static partial void HostLog(int level, string message);

        [JSImport("progress", "host")]
        private static partial void HostProgress(int value);

        private class HostLogger : ILogger
        {
            public void Log(LoggerEvent loggerEvent, string message)
            {
                if (loggerEvent >= LoggerEvent.Info)
                    HostLog((int)loggerEvent, message);
            }
        }

        private class HostProgressReporter : IProgress<int>
        {
            public void Report(int value) => HostProgress(value);
        }

        [JSExport]
        public static void Init()
        {
            Logger.Default = new HostLogger();
            Progress.Default = new HostProgressReporter();
            TextureDecoder.Backend = new WasmTextureDecoder();
            BundleFile.Decompressor = new WasmBundleDecompressor();
            SpriteHelper.TextureCacheSize = 3;
        }

        /// <summary>Copies a file into the virtual file system. <paramref name="path"/> keeps folder structure for folder uploads.</summary>
        [JSExport]
        public static void AddFile(string path, byte[] data)
        {
            var fullPath = Path.Combine(DataRoot, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllBytes(fullPath, data);
            if (!stagedFiles.Contains(fullPath))
                stagedFiles.Add(fullPath);
        }

        /// <summary>Loads all added files. Returns a JSON summary of the loaded serialized files.</summary>
        [JSExport]
        public static string Load(string unityVersion)
        {
            // AssetsManager can't add files to an existing load (objects would be read twice), so reload everything;
            // this also lets newly added files resolve references from earlier ones (shared assets, .resS).
            assetsManager.Clear();
            SpriteHelper.ClearTextureCache();
            assetsManager = new AssetsManager { SpecifyUnityVersion = string.IsNullOrWhiteSpace(unityVersion) ? null : unityVersion.Trim() };
            if (stagedFiles.Count > 0)
            {
                assetsManager.LoadFiles(stagedFiles.ToArray());
            }
            bundleContents = null;
            entries = AssetCatalog.Build(assetsManager, includeAll: true);
            entryByObject = entries.ToDictionary(x => x.Asset);
            return GetSummary();
        }

        [JSExport]
        public static void Clear()
        {
            assetsManager.Clear();
            assetsManager = new AssetsManager();
            entries = new List<AssetEntry>();
            entryByObject = new Dictionary<Object, AssetEntry>();
            stagedFiles.Clear();
            bundleContents = null;
            SpriteHelper.ClearTextureCache();
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, true);
            }
            GC.Collect();
        }

        [JSExport]
        public static string GetSummary()
        {
            var files = new List<object>();
            foreach (var file in assetsManager.assetsFileList)
            {
                files.Add(Dict(
                    ("name", file.fileName),
                    ("source", Path.GetFileName(file.originalPath ?? file.fullName)),
                    ("unityVersion", file.unityVersion),
                    ("platform", file.m_TargetPlatform.ToString()),
                    ("format", (int)file.header.m_Version),
                    ("objects", file.Objects.Count),
                    ("totalObjects", file.m_Objects.Count)));
            }
            return JsonWriter.Serialize(Dict(("files", files), ("assetCount", entries.Count)));
        }

        /// <summary>
        /// Compact asset table: [id, type, name, container, size, pathId, sourceFile, exportable].
        /// </summary>
        [JSExport]
        public static string GetAssets()
        {
            var rows = new List<object>(entries.Count);
            foreach (var e in entries)
            {
                rows.Add(new List<object> { e.Id, e.TypeString, e.Name, e.Container, e.FullSize, e.PathID.ToString(), e.SourceFile.fileName, e.Exportable ? 1 : 0 });
            }
            return JsonWriter.Serialize(rows);
        }

        [JSExport]
        public static string GetAssetInfo(int id)
        {
            var e = Find(id);
            var info = Dict(
                ("id", e.Id), ("type", e.TypeString), ("name", e.Name), ("container", e.Container),
                ("pathId", e.PathID.ToString()), ("size", e.FullSize), ("file", e.SourceFile.fileName),
                ("unityVersion", e.SourceFile.unityVersion));
            string preview;
            switch (e.Asset)
            {
                case Texture2D tex:
                    preview = "image";
                    info["width"] = tex.m_Width;
                    info["height"] = tex.m_Height;
                    info["format"] = tex.m_TextureFormat.ToString();
                    info["mipCount"] = tex.m_MipCount;
                    info["streamed"] = !string.IsNullOrEmpty(tex.m_StreamData?.path);
                    break;
                case Sprite sprite:
                    preview = "image";
                    info["width"] = (int)Math.Round(sprite.m_Rect.width);
                    info["height"] = (int)Math.Round(sprite.m_Rect.height);
                    info["pixelsPerUnit"] = sprite.m_PixelsToUnits;
                    if (sprite.m_SpriteAtlas != null && sprite.m_SpriteAtlas.TryGet(out var atlas))
                        info["atlas"] = atlas.m_Name;
                    if (sprite.m_RD.texture.TryGet(out var spriteTexture))
                        info["texture"] = spriteTexture.m_Name;
                    break;
                case TextAsset text:
                    preview = "text";
                    info["length"] = text.m_Script?.Length ?? 0;
                    break;
                case Shader _:
                    preview = "shader";
                    break;
                case Font font:
                    preview = font.m_FontData?.Length > 0 ? "font" : "dump";
                    break;
                case AudioClip audio:
                    preview = "audio";
                    info["channels"] = audio.m_Channels;
                    info["frequency"] = audio.m_Frequency;
                    info["length"] = audio.m_Length;
                    info["compression"] = audio.m_CompressionFormat.ToString();
                    info["loadType"] = audio.m_LoadType;
                    break;
                case Mesh mesh:
                    preview = "mesh";
                    info["vertexCount"] = mesh.m_VertexCount;
                    info["subMeshes"] = mesh.m_SubMeshes?.Length ?? 0;
                    break;
                default:
                    preview = "dump";
                    break;
            }
            info["preview"] = preview;
            return JsonWriter.Serialize(info);
        }

        /// <summary>
        /// Decoded image as [width:int32][height:int32][flipY:int32] followed by BGRA32 pixels; empty if unavailable.
        /// Textures are returned bottom-up (flipY = 1) to skip a managed flip; JS flips while converting to RGBA.
        /// </summary>
        [JSExport]
        public static byte[] GetImage(int id)
        {
            var e = Find(id);
            switch (e.Asset)
            {
                case Texture2D tex:
                    {
                        var size = tex.m_Width * tex.m_Height * 4;
                        if (size <= 0)
                            return Array.Empty<byte>();
                        var result = new byte[12 + size];
                        var pixels = new byte[size];
                        if (!new Texture2DConverter(tex).DecodeTexture2D(pixels))
                            return Array.Empty<byte>();
                        WriteHeader(result, tex.m_Width, tex.m_Height, 1);
                        Buffer.BlockCopy(pixels, 0, result, 12, size);
                        return result;
                    }
                case Sprite sprite:
                    using (var image = sprite.GetImage())
                    {
                        if (image == null)
                            return Array.Empty<byte>();
                        var size = image.Width * image.Height * 4;
                        var result = new byte[12 + size];
                        WriteHeader(result, image.Width, image.Height, 0);
                        if (image.TryGetSinglePixelSpan(out var span))
                        {
                            MemoryMarshal.AsBytes(span).CopyTo(result.AsSpan(12));
                        }
                        else
                        {
                            for (int y = 0; y < image.Height; y++)
                                MemoryMarshal.AsBytes(image.GetPixelRowSpan(y)).CopyTo(result.AsSpan(12 + y * image.Width * 4));
                        }
                        return result;
                    }
                default:
                    return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Everything needed to cut a sprite out of its texture on the JS side (same logic as SpriteHelper.GetImage,
        /// which is very slow under the WebAssembly interpreter). Empty when the JS path can't handle it.
        /// </summary>
        [JSExport]
        public static string GetSpriteInfo(int id)
        {
            if (!(Find(id).Asset is Sprite m_Sprite))
                return string.Empty;
            Texture2D m_Texture2D;
            Rectf textureRect;
            AssetStudio.Vector2 textureRectOffset;
            float downscaleMultiplier;
            SpriteSettings settingsRaw;
            if (m_Sprite.m_SpriteAtlas != null && m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlas))
            {
                if (!m_SpriteAtlas.m_RenderDataMap.TryGetValue(m_Sprite.m_RenderDataKey, out var spriteAtlasData) || !spriteAtlasData.texture.TryGet(out m_Texture2D))
                    return string.Empty;
                textureRect = spriteAtlasData.textureRect;
                textureRectOffset = spriteAtlasData.textureRectOffset;
                downscaleMultiplier = spriteAtlasData.downscaleMultiplier;
                settingsRaw = spriteAtlasData.settingsRaw;
            }
            else if (m_Sprite.m_RD.texture.TryGet(out m_Texture2D))
            {
                textureRect = m_Sprite.m_RD.textureRect;
                textureRectOffset = m_Sprite.m_RD.textureRectOffset;
                downscaleMultiplier = m_Sprite.m_RD.downscaleMultiplier;
                settingsRaw = m_Sprite.m_RD.settingsRaw;
            }
            else
            {
                return string.Empty;
            }
            if (downscaleMultiplier > 0f && downscaleMultiplier != 1f || !entryByObject.TryGetValue(m_Texture2D, out var textureEntry))
                return string.Empty; // resized atlases go through ImageSharp

            var info = Dict(
                ("texture", textureEntry.Id),
                ("rect", new List<object> { textureRect.x, textureRect.y, textureRect.width, textureRect.height }),
                ("packed", (int)settingsRaw.packed),
                ("rotation", (int)settingsRaw.packingRotation),
                ("tight", settingsRaw.packingMode == SpritePackingMode.Tight));
            if (settingsRaw.packingMode == SpritePackingMode.Tight)
            {
                try
                {
                    info["triangles"] = SpriteHelper.GetMaskTriangles(m_Sprite, textureRectOffset).Cast<object>().ToList();
                }
                catch
                {
                    info["tight"] = false; // same as SpriteHelper: fall back to the rectangle
                }
            }
            return JsonWriter.Serialize(info);
        }

        /// <summary>Text for TextAsset (raw bytes) and Shader (converted source) previews.</summary>
        [JSExport]
        public static byte[] GetContent(int id)
        {
            var e = Find(id);
            switch (e.Asset)
            {
                case TextAsset text:
                    return text.m_Script ?? Array.Empty<byte>();
                case Shader shader:
                    return Encoding.UTF8.GetBytes(shader.Convert());
                case Font font:
                    return font.m_FontData ?? Array.Empty<byte>();
                case AudioClip audio:
                    return audio.m_AudioData.GetData() ?? Array.Empty<byte>();
                default:
                    return Array.Empty<byte>();
            }
        }

        [JSExport]
        public static string GetDump(int id)
        {
            var e = Find(id);
            return AssetExporter.DumpJson(e.Asset) ?? e.Asset.Dump() ?? string.Empty;
        }

        /// <summary>
        /// Exports one asset. mode: convert | raw | dump. Images are converted by the caller (canvas PNG),
        /// so Texture2D/Sprite in convert mode are not handled here.
        /// Result: [pathLength:int32][UTF-8 relative path][file bytes], or empty when nothing to export.
        /// </summary>
        [JSExport]
        public static byte[] ExportAsset(int id, string mode, string group)
        {
            var e = Find(id);
            ExportedFile file;
            switch (mode)
            {
                case "raw":
                    file = AssetExporter.Raw(e);
                    break;
                case "dump":
                    file = AssetExporter.Dump(e);
                    break;
                default:
                    file = AssetExporter.Convert(e, new ExportOptions { ConvertAudio = false });
                    break;
            }
            if (file?.Data == null)
                return Array.Empty<byte>();
            var path = Encoding.UTF8.GetBytes(GetExportPath(id, file.Extension, group));
            var result = new byte[4 + path.Length + file.Data.Length];
            BitConverter.TryWriteBytes(result.AsSpan(0, 4), path.Length);
            Buffer.BlockCopy(path, 0, result, 4, path.Length);
            Buffer.BlockCopy(file.Data, 0, result, 4 + path.Length, file.Data.Length);
            return result;
        }

        [JSExport]
        public static string GetExportPath(int id, string extension, string group)
        {
            var e = Find(id);
            var dir = AssetExporter.GetGroupDirectory(e, ParseGroup(group)).Replace('\\', '/');
            var name = AssetExporter.FixFileName(e.Name) + extension;
            return string.IsNullOrEmpty(dir) ? name : dir.TrimEnd('/') + "/" + name;
        }

        /// <summary>Raw files stored inside the loaded bundles (like AssetStudio's "Extract file").</summary>
        [JSExport]
        public static string ListBundleFiles()
        {
            LoadBundleContents();
            var rows = bundleContents.Select((x, i) => (object)new List<object> { i, x.bundle, x.file.path, x.file.stream.Length }).ToList();
            return JsonWriter.Serialize(rows);
        }

        [JSExport]
        public static byte[] GetBundleFile(int index)
        {
            LoadBundleContents();
            var stream = bundleContents[index].file.stream;
            var data = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(data);
            return data;
        }

        private static void LoadBundleContents()
        {
            if (bundleContents != null)
                return;
            bundleContents = new List<(string, StreamFile)>();
            foreach (var path in stagedFiles)
            {
                try
                {
                    using (var reader = new FileReader(path))
                    {
                        StreamFile[] files;
                        if (reader.FileType == FileType.BundleFile)
                            files = new BundleFile(reader).fileList;
                        else if (reader.FileType == FileType.WebFile)
                            files = new WebFile(reader).fileList;
                        else
                            continue;
                        foreach (var file in files)
                            bundleContents.Add((Path.GetFileName(path), file));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unable to unpack {Path.GetFileName(path)}", ex);
                }
            }
        }

        private static AssetEntry Find(int id)
        {
            // entries are built in id order, so the index usually equals the id
            if (id >= 0 && id < entries.Count && entries[id].Id == id)
                return entries[id];
            return entries.First(x => x.Id == id);
        }

        private static AssetGroupOption ParseGroup(string group)
        {
            switch (group)
            {
                case "type": return AssetGroupOption.TypeName;
                case "source": return AssetGroupOption.SourceFile;
                case "none": return AssetGroupOption.None;
                default: return AssetGroupOption.ContainerPath;
            }
        }

        private static void WriteHeader(byte[] buffer, int width, int height, int flags)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), width);
            BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), height);
            BitConverter.TryWriteBytes(buffer.AsSpan(8, 4), flags);
        }

        private static OrderedDictionary Dict(params (string key, object value)[] items)
        {
            var dict = new OrderedDictionary();
            foreach (var (key, value) in items)
                dict[key] = value;
            return dict;
        }
    }
}
