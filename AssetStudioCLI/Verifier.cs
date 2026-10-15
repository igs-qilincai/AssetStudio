using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using AssetStudio;
using Object = AssetStudio.Object;

namespace AssetStudioCLI
{
    /// <summary>
    /// Cross-checks the hand-written class readers against the TypeTree embedded in the file.
    /// Every public field whose name matches a TypeTree field is compared recursively, so any
    /// layout drift in a new Unity version shows up as a mismatch instead of silently bad data.
    /// </summary>
    internal static class Verifier
    {
        private class Stat
        {
            public int Total;
            public int Failed;
            public int Mismatched;
            public readonly List<string> Samples = new List<string>();
        }

        public static int Run(AssetsManager manager)
        {
            var stats = new SortedDictionary<string, Stat>();
            foreach (var file in manager.assetsFileList)
            {
                foreach (var info in file.m_Objects)
                {
                    var typeName = Enum.IsDefined(typeof(ClassIDType), info.classID) ? ((ClassIDType)info.classID).ToString() : info.classID.ToString();
                    if (!stats.TryGetValue(typeName, out var stat))
                        stats[typeName] = stat = new Stat();
                    stat.Total++;
                    if (!file.ObjectsDic.TryGetValue(info.m_PathID, out var obj))
                    {
                        stat.Failed++;
                        continue;
                    }
                    if (obj.GetType() == typeof(Object) || obj.serializedType?.m_Type == null)
                        continue;
                    OrderedDictionary dict;
                    try
                    {
                        dict = obj.ToType();
                    }
                    catch (Exception e)
                    {
                        stat.Mismatched++;
                        if (stat.Samples.Count < 3)
                            stat.Samples.Add($"#{obj.m_PathID}: typetree read failed: {e.Message}");
                        continue;
                    }
                    var errors = new List<string>();
                    CompareObject(obj, dict, obj.GetType().Name, errors, 0);
                    if (errors.Count > 0)
                    {
                        stat.Mismatched++;
                        if (stat.Samples.Count < 3)
                            stat.Samples.Add($"#{obj.m_PathID}: " + string.Join("; ", errors.Take(4)));
                    }
                }
            }

            var bad = 0;
            foreach (var kv in stats)
            {
                var s = kv.Value;
                var flag = s.Failed > 0 || s.Mismatched > 0 ? "FAIL" : "ok  ";
                if (s.Failed > 0 || s.Mismatched > 0)
                    bad++;
                Console.WriteLine($"{flag} {kv.Key,-30} total={s.Total,-6} failed={s.Failed,-5} mismatched={s.Mismatched}");
                foreach (var sample in s.Samples)
                    Console.WriteLine($"       {sample}");
            }
            return bad;
        }

        private static void CompareObject(object obj, OrderedDictionary dict, string path, List<string> errors, int depth)
        {
            if (depth > 8)
                return;
            foreach (var field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dict.Contains(field.Name))
                    continue;
                CompareValue(field.GetValue(obj), dict[field.Name], path + "." + field.Name, errors, depth);
            }
        }

        private static void CompareValue(object a, object b, string path, List<string> errors, int depth)
        {
            if (a == null || b == null)
                return;
            var type = a.GetType();
            if (type.IsEnum || type.IsPrimitive)
            {
                if (!IsNumber(b))
                    return;
                var x = Convert.ToDouble(a);
                var y = Convert.ToDouble(b);
                if (!(x == y || double.IsNaN(x) && double.IsNaN(y) || Math.Abs(x - y) < 1e-4))
                    errors.Add($"{path}: {a} != {b}");
                return;
            }
            if (a is string sa)
            {
                if (b is string sb && sa != sb)
                    errors.Add($"{path}: \"{Trim(sa)}\" != \"{Trim(sb)}\"");
                return;
            }
            if (a is byte[] bytes)
            {
                var count = b switch
                {
                    byte[] bb => bb.Length,
                    IList list => list.Count,
                    string s => System.Text.Encoding.UTF8.GetByteCount(s),
                    _ => -1
                };
                if (count >= 0 && count != bytes.Length && !(b is string))
                    errors.Add($"{path}: length {bytes.Length} != {count}");
                return;
            }
            if (b is OrderedDictionary od)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PPtr<>))
                {
                    var fileId = (int)type.GetField("m_FileID").GetValue(a);
                    var pathId = (long)type.GetField("m_PathID").GetValue(a);
                    if (od.Contains("m_FileID") && Convert.ToInt64(od["m_FileID"]) != fileId ||
                        od.Contains("m_PathID") && Convert.ToInt64(od["m_PathID"]) != pathId)
                        errors.Add($"{path}: PPtr({fileId},{pathId}) != ({od["m_FileID"]},{od["m_PathID"]})");
                    return;
                }
                CompareObject(a, od, path, errors, depth + 1);
                return;
            }
            if (b is IList lb && a is IEnumerable ea && !(a is string))
            {
                var items = ea.Cast<object>().ToList();
                if (items.Count != lb.Count)
                {
                    // Unity sometimes stores flattened vectors (e.g. float[] for Vector3[]); only flag obvious drifts
                    if (items.Count == 0 || lb.Count == 0 || items.Count % lb.Count != 0)
                        errors.Add($"{path}: count {items.Count} != {lb.Count}");
                    return;
                }
                for (int i = 0; i < Math.Min(items.Count, 2); i++)
                {
                    var bi = lb[i];
                    if (bi is KeyValuePair<object, object> kv)
                        bi = kv.Value;
                    var ai = items[i];
                    var at = ai?.GetType();
                    if (at != null && at.IsGenericType && at.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        ai = at.GetProperty("Value").GetValue(ai);
                    CompareValue(ai, bi, $"{path}[{i}]", errors, depth + 1);
                }
            }
        }

        private static bool IsNumber(object o) => o is sbyte || o is byte || o is short || o is ushort || o is int || o is uint || o is long || o is ulong || o is float || o is double || o is bool;

        private static string Trim(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    }
}
