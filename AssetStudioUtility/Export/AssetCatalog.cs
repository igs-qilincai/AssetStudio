using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    /// <summary>
    /// UI-independent counterpart of AssetStudioGUI's AssetItem, used by the CLI and the web build.
    /// </summary>
    public class AssetEntry
    {
        public int Id;
        public Object Asset;
        public SerializedFile SourceFile;
        public ClassIDType Type;
        public string TypeString;
        public string Name;
        public string Container = string.Empty;
        public long PathID;
        public long FullSize;
        public bool Exportable;
    }

    public static class AssetCatalog
    {
        /// <summary>
        /// Builds the asset list the same way AssetStudioGUI does (names, container paths, sizes).
        /// </summary>
        /// <param name="includeAll">Also list objects that have no dedicated exporter (GameObject, Transform, ...).</param>
        public static List<AssetEntry> Build(AssetsManager assetsManager, bool includeAll = false)
        {
            var entries = new List<AssetEntry>();
            var entryByObject = new Dictionary<Object, AssetEntry>();
            var containers = new List<(PPtr<Object>, string)>();
            var id = 0;
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    var entry = new AssetEntry
                    {
                        Id = id++,
                        Asset = asset,
                        SourceFile = assetsFile,
                        Type = asset.type,
                        TypeString = asset.type.ToString(),
                        PathID = asset.m_PathID,
                        FullSize = asset.byteSize,
                        Name = string.Empty
                    };
                    entryByObject[asset] = entry;
                    switch (asset)
                    {
                        case GameObject m_GameObject:
                            entry.Name = m_GameObject.m_Name;
                            break;
                        case Texture2D m_Texture2D:
                            if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                                entry.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                            entry.Name = m_Texture2D.m_Name;
                            entry.Exportable = true;
                            break;
                        case AudioClip m_AudioClip:
                            if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                                entry.FullSize = asset.byteSize + m_AudioClip.m_Size;
                            entry.Name = m_AudioClip.m_Name;
                            entry.Exportable = true;
                            break;
                        case VideoClip m_VideoClip:
                            if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                                entry.FullSize = asset.byteSize + (long)m_VideoClip.m_ExternalResources.m_Size;
                            entry.Name = m_VideoClip.m_Name;
                            entry.Exportable = true;
                            break;
                        case Shader m_Shader:
                            entry.Name = m_Shader.m_ParsedForm?.m_Name ?? m_Shader.m_Name;
                            entry.Exportable = true;
                            break;
                        case Mesh _:
                        case TextAsset _:
                        case AnimationClip _:
                        case Font _:
                        case MovieTexture _:
                        case Sprite _:
                            entry.Name = ((NamedObject)asset).m_Name;
                            entry.Exportable = true;
                            break;
                        case Animator m_Animator:
                            if (m_Animator.m_GameObject.TryGet(out var gameObject))
                            {
                                entry.Name = gameObject.m_Name;
                            }
                            break;
                        case MonoBehaviour m_MonoBehaviour:
                            if (m_MonoBehaviour.m_Name == "" && m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                            {
                                entry.Name = m_Script.m_ClassName;
                            }
                            else
                            {
                                entry.Name = m_MonoBehaviour.m_Name;
                            }
                            entry.Exportable = true;
                            break;
                        case AssetBundle m_AssetBundle:
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadEnd = preloadIndex + m_Container.Value.preloadSize;
                                for (int k = preloadIndex; k < preloadEnd; k++)
                                {
                                    containers.Add((m_AssetBundle.m_PreloadTable[k], m_Container.Key));
                                }
                            }
                            entry.Name = m_AssetBundle.m_Name;
                            break;
                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                containers.Add((m_Container.Value, m_Container.Key));
                            }
                            break;
                        case NamedObject m_NamedObject:
                            entry.Name = m_NamedObject.m_Name;
                            break;
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        entry.Name = entry.TypeString + " #" + entry.Id;
                    }
                    if (includeAll || entry.Exportable)
                    {
                        entries.Add(entry);
                    }
                }
            }
            foreach (var (pptr, container) in containers)
            {
                if (pptr.TryGet(out var obj) && entryByObject.TryGetValue(obj, out var entry))
                {
                    entry.Container = container;
                }
            }
            return entries;
        }

        public static IEnumerable<AssetEntry> OfTypes(this IEnumerable<AssetEntry> entries, ICollection<ClassIDType> types)
        {
            return types == null || types.Count == 0 ? entries : entries.Where(x => types.Contains(x.Type));
        }
    }
}
