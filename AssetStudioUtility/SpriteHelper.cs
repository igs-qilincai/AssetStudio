using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AssetStudio
{
    public static class SpriteHelper
    {
        public static Image<Bgra32> GetImage(this Sprite m_Sprite)
        {
            if (m_Sprite.m_SpriteAtlas != null && m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlas))
            {
                if (m_SpriteAtlas.m_RenderDataMap.TryGetValue(m_Sprite.m_RenderDataKey, out var spriteAtlasData) && spriteAtlasData.texture.TryGet(out var m_Texture2D))
                {
                    return CutImage(m_Sprite, m_Texture2D, spriteAtlasData.textureRect, spriteAtlasData.textureRectOffset, spriteAtlasData.downscaleMultiplier, spriteAtlasData.settingsRaw);
                }
            }
            else
            {
                if (m_Sprite.m_RD.texture.TryGet(out var m_Texture2D))
                {
                    return CutImage(m_Sprite, m_Texture2D, m_Sprite.m_RD.textureRect, m_Sprite.m_RD.textureRectOffset, m_Sprite.m_RD.downscaleMultiplier, m_Sprite.m_RD.settingsRaw);
                }
            }
            return null;
        }

        private static Image<Bgra32> CutImage(Sprite m_Sprite, Texture2D m_Texture2D, Rectf textureRect, Vector2 textureRectOffset, float downscaleMultiplier, SpriteSettings settingsRaw)
        {
            var originalImage = GetTextureImage(m_Texture2D);
            if (originalImage != null)
            {
                Image<Bgra32> resizedImage = null;
                try
                {
                    if (downscaleMultiplier > 0f && downscaleMultiplier != 1f)
                    {
                        var width = (int)(m_Texture2D.m_Width / downscaleMultiplier);
                        var height = (int)(m_Texture2D.m_Height / downscaleMultiplier);
                        resizedImage = originalImage.Clone(x => x.Resize(width, height));
                        originalImage = resizedImage;
                    }
                    var rectX = (int)Math.Floor(textureRect.x);
                    var rectY = (int)Math.Floor(textureRect.y);
                    var rectRight = (int)Math.Ceiling(textureRect.x + textureRect.width);
                    var rectBottom = (int)Math.Ceiling(textureRect.y + textureRect.height);
                    rectRight = Math.Min(rectRight, originalImage.Width);
                    rectBottom = Math.Min(rectBottom, originalImage.Height);
                    var rect = new Rectangle(rectX, rectY, rectRight - rectX, rectBottom - rectY);
                    var spriteImage = originalImage.Clone(x => x.Crop(rect));
                    if (settingsRaw.packed == 1)
                    {
                        //RotateAndFlip
                        switch (settingsRaw.packingRotation)
                        {
                            case SpritePackingRotation.FlipHorizontal:
                                spriteImage.Mutate(x => x.Flip(FlipMode.Horizontal));
                                break;
                            case SpritePackingRotation.FlipVertical:
                                spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                                break;
                            case SpritePackingRotation.Rotate180:
                                spriteImage.Mutate(x => x.Rotate(180));
                                break;
                            case SpritePackingRotation.Rotate90:
                                spriteImage.Mutate(x => x.Rotate(270));
                                break;
                        }
                    }

                    //Tight
                    if (settingsRaw.packingMode == SpritePackingMode.Tight)
                    {
                        try
                        {
                            var triangles = GetMaskTriangles(m_Sprite, textureRectOffset);
                            var mask = RasterizeTriangles(triangles, rect.Width, rect.Height);
                            ApplyMask(spriteImage, mask, rect.Width, rect.Height);
                            spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                            return spriteImage;
                        }
                        catch
                        {
                            // ignored
                        }
                    }

                    //Rectangle
                    spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                    return spriteImage;
                }
                finally
                {
                    resizedImage?.Dispose();
                }
            }

            return null;
        }

        /// <summary>Number of decoded atlas textures kept in memory while cutting sprites.</summary>
        public static int TextureCacheSize = 4;

        private static readonly LinkedList<KeyValuePair<Texture2D, Image<Bgra32>>> textureCache = new LinkedList<KeyValuePair<Texture2D, Image<Bgra32>>>();

        // Sprites usually share a few atlas textures; decoding the atlas once per sprite is the dominant export cost.
        // The cache is not meant for exporting sprites from several threads at once.
        private static Image<Bgra32> GetTextureImage(Texture2D m_Texture2D)
        {
            lock (textureCache)
            {
                for (var node = textureCache.First; node != null; node = node.Next)
                {
                    if (node.Value.Key == m_Texture2D)
                    {
                        textureCache.Remove(node);
                        textureCache.AddFirst(node);
                        return node.Value.Value;
                    }
                }
            }
            var image = m_Texture2D.ConvertToImage(false);
            if (image == null)
            {
                return null;
            }
            lock (textureCache)
            {
                textureCache.AddFirst(new KeyValuePair<Texture2D, Image<Bgra32>>(m_Texture2D, image));
                while (textureCache.Count > Math.Max(1, TextureCacheSize))
                {
                    textureCache.Last.Value.Value.Dispose();
                    textureCache.RemoveLast();
                }
            }
            return image;
        }

        public static void ClearTextureCache()
        {
            lock (textureCache)
            {
                foreach (var item in textureCache)
                {
                    item.Value.Dispose();
                }
                textureCache.Clear();
            }
        }

        /// <summary>
        /// Mesh triangles of a tight sprite in pixel coordinates of the cropped texture rect (bottom-up),
        /// flattened as x0,y0,x1,y1,x2,y2,... AssetStudioWeb's worker.js consumes the same data.
        /// </summary>
        public static float[] GetMaskTriangles(Sprite m_Sprite, AssetStudio.Vector2 textureRectOffset)
        {
            var triangles = GetTriangles(m_Sprite.m_RD);
            var scale = m_Sprite.m_PixelsToUnits;
            var dx = m_Sprite.m_Rect.width * m_Sprite.m_Pivot.X - textureRectOffset.X;
            var dy = m_Sprite.m_Rect.height * m_Sprite.m_Pivot.Y - textureRectOffset.Y;
            var result = new float[triangles.Length * 6];
            for (int i = 0; i < triangles.Length; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    result[i * 6 + j * 2] = triangles[i][j].X * scale + dx;
                    result[i * 6 + j * 2 + 1] = triangles[i][j].Y * scale + dy;
                }
            }
            return result;
        }

        /// <summary>
        /// Marks pixels whose centre lies inside any triangle (edges inclusive). Unlike ImageSharp's non-antialiased
        /// polygon fill this leaves no gaps along edges shared by neighbouring triangles.
        /// Doubles are used so the browser build (worker.js) produces bit-identical masks.
        /// </summary>
        public static bool[] RasterizeTriangles(float[] triangles, int width, int height)
        {
            var mask = new bool[width * height];
            for (int i = 0; i + 5 < triangles.Length; i += 6)
            {
                double ax = triangles[i], ay = triangles[i + 1], bx = triangles[i + 2], by = triangles[i + 3], cx = triangles[i + 4], cy = triangles[i + 5];
                var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (area == 0)
                    continue;
                var sign = area > 0 ? 1 : -1;
                var minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
                var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                var minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
                var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                for (int y = minY; y <= maxY; y++)
                {
                    var py = y + 0.5;
                    for (int x = minX; x <= maxX; x++)
                    {
                        var px = x + 0.5;
                        var w0 = ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) * sign;
                        var w1 = ((cx - bx) * (py - by) - (cy - by) * (px - bx)) * sign;
                        var w2 = ((ax - cx) * (py - cy) - (ay - cy) * (px - cx)) * sign;
                        if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                            mask[y * width + x] = true;
                    }
                }
            }
            return mask;
        }

        // Pixels outside the mask become transparent. The mask is tiled like ImageSharp's ImageBrush did (relevant
        // for 90° packed sprites), and fully transparent pixels are written as 0,0,0,0 as the old compositing did.
        private static void ApplyMask(Image<Bgra32> image, bool[] mask, int maskWidth, int maskHeight)
        {
            for (int y = 0; y < image.Height; y++)
            {
                var row = image.GetPixelRowSpan(y);
                var maskRow = (y % maskHeight) * maskWidth;
                for (int x = 0; x < row.Length; x++)
                {
                    if (!mask[maskRow + x % maskWidth] || row[x].A == 0)
                    {
                        row[x] = default;
                    }
                }
            }
        }

        public static Vector2[][] GetTriangles(SpriteRenderData m_RD)
        {
            if (m_RD.vertices != null) //5.6 down
            {
                var vertices = m_RD.vertices.Select(x => (Vector2)x.pos).ToArray();
                var triangleCount = m_RD.indices.Length / 3;
                var triangles = new Vector2[triangleCount][];
                for (int i = 0; i < triangleCount; i++)
                {
                    var first = m_RD.indices[i * 3];
                    var second = m_RD.indices[i * 3 + 1];
                    var third = m_RD.indices[i * 3 + 2];
                    var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                    triangles[i] = triangle;
                }
                return triangles;
            }
            else //5.6 and up
            {
                var triangles = new List<Vector2[]>();
                var m_VertexData = m_RD.m_VertexData;
                var m_Channel = m_VertexData.m_Channels[0]; //kShaderChannelVertex
                var m_Stream = m_VertexData.m_Streams[m_Channel.stream];
                using (var vertexReader = new BinaryReader(new MemoryStream(m_VertexData.m_DataSize)))
                {
                    using (var indexReader = new BinaryReader(new MemoryStream(m_RD.m_IndexBuffer)))
                    {
                        foreach (var subMesh in m_RD.m_SubMeshes)
                        {
                            vertexReader.BaseStream.Position = m_Stream.offset + subMesh.firstVertex * m_Stream.stride + m_Channel.offset;

                            var vertices = new Vector2[subMesh.vertexCount];
                            for (int v = 0; v < subMesh.vertexCount; v++)
                            {
                                vertices[v] = vertexReader.ReadVector3();
                                vertexReader.BaseStream.Position += m_Stream.stride - 12;
                            }

                            indexReader.BaseStream.Position = subMesh.firstByte;

                            var triangleCount = subMesh.indexCount / 3u;
                            for (int i = 0; i < triangleCount; i++)
                            {
                                var first = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var second = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var third = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                                triangles.Add(triangle);
                            }
                        }
                    }
                }
                return triangles.ToArray();
            }
        }
    }
}
