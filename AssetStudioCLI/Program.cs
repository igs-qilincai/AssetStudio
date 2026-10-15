using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AssetStudioCLI
{
    internal class ConsoleLogger : ILogger
    {
        public bool Verbose;

        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (loggerEvent >= LoggerEvent.Warning || Verbose && loggerEvent >= LoggerEvent.Info)
                Console.Error.WriteLine($"[{loggerEvent}] {message}");
        }
    }

    internal class Options
    {
        public string Command;
        public List<string> Inputs = new List<string>();
        public string Output;
        public HashSet<ClassIDType> Types = new HashSet<ClassIDType>();
        public string Mode = "convert";
        public AssetGroupOption Group = AssetGroupOption.ContainerPath;
        public ImageFormat ImageFormat = ImageFormat.Png;
        public string Filter;
        public string UnityVersion;
        public bool All;
        public bool Verbose;
    }

    internal static class Program
    {
        private const string Usage = @"AssetStudioCLI - unpack Unity asset bundles (Unity 3.x ~ 6000.x)

Usage: AssetStudioCLI <command> [options] <file-or-folder>...

Commands:
  info              Unity version, platform and object counts of each file
  list              List exportable assets (id, type, name, container path)
  export            Export assets (textures/sprites as images, text, audio, fonts, ...)
  unpack            Extract the raw files stored inside bundles (.assets/.resS/...)
  verify            Cross-check AssetStudio's class readers against the files' TypeTrees
  dump-typetrees    Print the TypeTree of every class in a bundle

Options:
  -o, --output <dir>        Output folder (default: <first input>_export)
  -t, --types <list>        Comma separated types, e.g. Texture2D,Sprite,TextAsset,AudioClip
  -m, --mode <mode>         export mode: convert (default) | raw | dump
  -g, --group <mode>        folder layout: container (default) | type | source | none
  -f, --filter <text>       only assets whose name or container contains <text>
      --image-format <fmt>  png (default) | jpeg | bmp | tga
      --all                 include assets without a dedicated exporter (dumped as JSON)
      --unity-version <v>   Unity version to use for files with a stripped version
  -v, --verbose             Print info messages
";

        private static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Options options;
            try
            {
                options = ParseArgs(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Console.Error.Write(Usage);
                return 2;
            }
            if (options == null)
            {
                Console.Write(Usage);
                return 0;
            }

            var logger = new ConsoleLogger { Verbose = options.Verbose };
            Logger.Default = logger;

            if (options.Command == "dump-typetrees")
            {
                foreach (var input in options.Inputs)
                    TypeTreeDumper.Dump(input);
                return 0;
            }

            if (options.Command == "unpack")
            {
                return Unpack(options);
            }

            var manager = new AssetsManager { SpecifyUnityVersion = options.UnityVersion };
            var stopwatch = Stopwatch.StartNew();
            Load(manager, options.Inputs);
            Console.Error.WriteLine($"Loaded {manager.assetsFileList.Count} serialized file(s), {manager.assetsFileList.Sum(x => x.Objects.Count)} objects in {stopwatch.Elapsed.TotalSeconds:0.00}s");

            switch (options.Command)
            {
                case "info":
                    return Info(manager);
                case "list":
                    return List(manager, options);
                case "export":
                    return Export(manager, options);
                case "verify":
                    return Verifier.Run(manager) == 0 ? 0 : 1;
                default:
                    Console.Error.WriteLine($"Unknown command '{options.Command}'");
                    return 2;
            }
        }

        private static Options ParseArgs(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
                return null;
            var options = new Options { Command = args[0].ToLowerInvariant() };
            for (int i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}");
                switch (arg)
                {
                    case "-o":
                    case "--output":
                        options.Output = Next();
                        break;
                    case "-t":
                    case "--types":
                        foreach (var name in Next().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!Enum.TryParse<ClassIDType>(name.Trim(), true, out var type))
                                throw new ArgumentException($"Unknown type '{name}'");
                            options.Types.Add(type);
                        }
                        break;
                    case "-m":
                    case "--mode":
                        options.Mode = Next().ToLowerInvariant();
                        if (options.Mode != "convert" && options.Mode != "raw" && options.Mode != "dump")
                            throw new ArgumentException($"Unknown mode '{options.Mode}'");
                        break;
                    case "-g":
                    case "--group":
                        var group = Next().ToLowerInvariant();
                        switch (group)
                        {
                            case "container": options.Group = AssetGroupOption.ContainerPath; break;
                            case "type": options.Group = AssetGroupOption.TypeName; break;
                            case "source": options.Group = AssetGroupOption.SourceFile; break;
                            case "none": options.Group = AssetGroupOption.None; break;
                            default: throw new ArgumentException($"Unknown group mode '{group}'");
                        }
                        break;
                    case "-f":
                    case "--filter":
                        options.Filter = Next();
                        break;
                    case "--image-format":
                        if (!Enum.TryParse<ImageFormat>(Next(), true, out options.ImageFormat))
                            throw new ArgumentException("Unknown image format");
                        break;
                    case "--all":
                        options.All = true;
                        break;
                    case "--unity-version":
                        options.UnityVersion = Next();
                        break;
                    case "-v":
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    default:
                        if (arg.StartsWith("-"))
                            throw new ArgumentException($"Unknown option '{arg}'");
                        options.Inputs.Add(arg);
                        break;
                }
            }
            if (options.Inputs.Count == 0)
                throw new ArgumentException("No input file or folder given");
            foreach (var input in options.Inputs)
            {
                if (!File.Exists(input) && !Directory.Exists(input))
                    throw new ArgumentException($"Input not found: {input}");
            }
            return options;
        }

        private static void Load(AssetsManager manager, List<string> inputs)
        {
            var files = new List<string>();
            foreach (var input in inputs)
            {
                if (Directory.Exists(input))
                    files.AddRange(Directory.GetFiles(input, "*.*", SearchOption.AllDirectories));
                else
                    files.Add(input);
            }
            manager.LoadFiles(files.ToArray());
        }

        private static int Info(AssetsManager manager)
        {
            foreach (var file in manager.assetsFileList)
            {
                Console.WriteLine($"{file.fileName}");
                Console.WriteLine($"  source:   {file.originalPath ?? file.fullName}");
                Console.WriteLine($"  unity:    {file.unityVersion}   format: {(int)file.header.m_Version}   platform: {file.m_TargetPlatform}");
                Console.WriteLine($"  objects:  {file.Objects.Count} loaded / {file.m_Objects.Count} total");
                foreach (var group in file.Objects.GroupBy(x => x.type).OrderByDescending(x => x.Count()))
                    Console.WriteLine($"    {group.Key,-30} {group.Count(),6}");
            }
            return 0;
        }

        private static List<AssetEntry> SelectEntries(AssetsManager manager, Options options)
        {
            IEnumerable<AssetEntry> entries = AssetCatalog.Build(manager, options.All || options.Types.Count > 0).OfTypes(options.Types);
            if (!string.IsNullOrEmpty(options.Filter))
            {
                entries = entries.Where(x => x.Name.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             x.Container.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return entries.ToList();
        }

        private static int List(AssetsManager manager, Options options)
        {
            var entries = SelectEntries(manager, options);
            Console.WriteLine($"{"Id",6}  {"Type",-14} {"Size",10}  {"Name",-40} Container");
            foreach (var e in entries)
            {
                Console.WriteLine($"{e.Id,6}  {e.TypeString,-14} {e.FullSize,10}  {e.Name,-40} {e.Container}");
            }
            Console.Error.WriteLine($"{entries.Count} assets");
            return 0;
        }

        private static int Export(AssetsManager manager, Options options)
        {
            var output = options.Output ?? options.Inputs[0].TrimEnd('/', '\\') + "_export";
            var entries = SelectEntries(manager, options);
            var exportOptions = new ExportOptions { ImageFormat = options.ImageFormat, GroupOption = options.Group };
            var stopwatch = Stopwatch.StartNew();
            int exported = 0, skipped = 0, failed = 0;
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                try
                {
                    ExportedFile file;
                    switch (options.Mode)
                    {
                        case "raw":
                            file = AssetExporter.Raw(entry);
                            break;
                        case "dump":
                            file = AssetExporter.Dump(entry);
                            break;
                        default:
                            file = AssetExporter.Convert(entry, exportOptions);
                            break;
                    }
                    if (file?.Data == null)
                    {
                        skipped++;
                        Logger.Info($"Skipped {entry.TypeString} {entry.Name}");
                        continue;
                    }
                    var dir = Path.Combine(output, AssetExporter.GetGroupDirectory(entry, options.Group));
                    var baseName = AssetExporter.FixFileName(entry.Name);
                    var path = Path.Combine(dir, baseName + file.Extension);
                    if (!usedPaths.Add(path))
                    {
                        path = Path.Combine(dir, $"{baseName} #{entry.PathID}{file.Extension}");
                        usedPaths.Add(path);
                    }
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, file.Data);
                    exported++;
                }
                catch (Exception e)
                {
                    failed++;
                    Logger.Error($"Export {entry.TypeString} '{entry.Name}' failed", e);
                }
                if ((i + 1) % 50 == 0 || i + 1 == entries.Count)
                    Console.Error.Write($"\r[{i + 1}/{entries.Count}] exporting...");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Exported {exported} assets to {Path.GetFullPath(output)} in {stopwatch.Elapsed.TotalSeconds:0.00}s ({skipped} skipped, {failed} failed)");
            return failed > 0 ? 1 : 0;
        }

        private static int Unpack(Options options)
        {
            var output = options.Output ?? options.Inputs[0].TrimEnd('/', '\\') + "_unpacked";
            var count = 0;
            foreach (var input in options.Inputs)
            {
                var files = Directory.Exists(input) ? Directory.GetFiles(input, "*.*", SearchOption.AllDirectories) : new[] { input };
                foreach (var path in files)
                {
                    using (var reader = new FileReader(path))
                    {
                        StreamFile[] fileList;
                        switch (reader.FileType)
                        {
                            case FileType.BundleFile:
                                fileList = new BundleFile(reader).fileList;
                                break;
                            case FileType.WebFile:
                                fileList = new WebFile(reader).fileList;
                                break;
                            default:
                                continue;
                        }
                        var dir = Path.Combine(output, Path.GetFileName(path) + "_unpacked");
                        foreach (var file in fileList)
                        {
                            var target = Path.Combine(dir, file.path);
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            using (var fs = File.Create(target))
                            {
                                file.stream.Position = 0;
                                file.stream.CopyTo(fs);
                            }
                            file.stream.Dispose();
                            Console.WriteLine(target);
                            count++;
                        }
                    }
                }
            }
            Console.Error.WriteLine($"Extracted {count} file(s) to {Path.GetFullPath(output)}");
            return 0;
        }
    }

    internal static class TypeTreeDumper
    {
        public static void Dump(string path)
        {
            var manager = new AssetsManager();
            var reader = new FileReader(path);
            IEnumerable<FileReader> readers;
            if (reader.FileType == FileType.BundleFile)
                readers = new BundleFile(reader).fileList.Select(f => new FileReader(f.path, f.stream));
            else
                readers = new[] { reader };
            foreach (var sub in readers)
            {
                if (sub.FileType != FileType.AssetsFile)
                    continue;
                var sf = new SerializedFile(sub, manager);
                Console.WriteLine($"##### {sf.fileName} ({sf.unityVersion})");
                var seen = new HashSet<int>();
                foreach (var t in sf.m_Types)
                {
                    if (t.m_Type == null || !seen.Add(t.classID) && t.classID != (int)ClassIDType.MonoBehaviour)
                        continue;
                    Console.WriteLine($"=== {(ClassIDType)t.classID} ({t.classID})");
                    foreach (var n in t.m_Type.m_Nodes)
                    {
                        var align = (n.m_MetaFlag & 0x4000) != 0 ? " [A]" : "";
                        Console.WriteLine($"{new string(' ', n.m_Level * 2)}{n.m_Type} {n.m_Name}{align}");
                    }
                }
            }
        }
    }
}
