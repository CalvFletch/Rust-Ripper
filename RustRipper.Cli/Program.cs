using System.Net;
using System.Text;
using System.Text.Json;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Extensions;
using RustRipper.Core;

return args.FirstOrDefault() switch
{
    "detect" => Cli.Detect(),
    "catalog" => Cli.Catalog(args[1..]),
    "find" => Cli.Find(args[1..]),
    "export" => Cli.Export(args[1..]),
    "mat" => Cli.Mat(args[1..]),
    "texstat" => Cli.TexStat(args[1..]),
    "serve" => Cli.Serve(args[1..]),
    "stats" => Cli.Stats(args[1..]),
    _ => Cli.Usage(),
};

internal static class Cli
{
    public static int Usage()
    {
        Console.WriteLine("Rust Ripper");
        Console.WriteLine("  ripper detect                                    list Rust installs");
        Console.WriteLine("  ripper catalog [bundle paths...]                 build the object catalog");
        Console.WriteLine("  ripper find <query> [--kind item|prefab] [--category <n>] [--path <s>] [--limit <n>]");
        Console.WriteLine("  ripper export <query> [--out <dir>] [--bundles <extra.bundle>]...");
        Console.WriteLine("  ripper mat <query> [--bundles <extra.bundle>]   per-material shader + properties");
        Console.WriteLine("  ripper serve [--port <n>] [--bundles <extra>]   resident daemon: load once, export instantly");
        Console.WriteLine("  ripper stats <bundle-or-folder> [...]           asset type counts");
        return 1;
    }

    public static int Detect()
    {
        var installs = RustLocator.GetInstalls();
        foreach (var install in installs)
        {
            Console.WriteLine($"Rust [B: {install.BuildId ?? "?"}] {install.GameRoot}");
        }
        if (installs.Count == 0)
        {
            Console.WriteLine("no Rust installs found");
            return 1;
        }
        return 0;
    }

    public static int Catalog(string[] paths)
    {
        string buildId = "manual";
        if (paths.Length == 0)
        {
            var install = RustLocator.GetInstalls().FirstOrDefault();
            if (install == null)
            {
                Console.WriteLine("no Rust install detected; pass bundle paths explicitly");
                return 1;
            }
            buildId = install.BuildId ?? "unknown";
            paths =
            [
                Path.Combine(install.BundlesPath, "shared", "items.preload.bundle"),
                Path.Combine(install.BundlesPath, "shared", "content.bundle"),
            ];
            Console.WriteLine($"install: {install.GameRoot} (build {buildId})");
        }

        Logger.Add(new ConsoleLogger(false));
        var handler = new RipperExportHandler(new FullConfiguration());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GameData gameData = handler.LoadAndProcess(paths, LocalFileSystem.Instance);
        Console.WriteLine($"loaded in {sw.Elapsed.TotalSeconds:F1}s");

        sw.Restart();
        var catalog = RustCatalog.Build(gameData, buildId);
        catalog.Save();
        sw.Stop();

        var items = catalog.Entries.Count(e => e.Kind == "item");
        var prefabs = catalog.Entries.Count(e => e.Kind == "prefab");
        var joined = catalog.Entries.Count(e => e.Kind == "item" && e.PrefabPath.Length > 0);
        Console.WriteLine($"catalog built in {sw.Elapsed.TotalSeconds:F1}s: {items} items ({joined} with prefab paths), {prefabs} prefabs");
        Console.WriteLine($"saved: {RustCatalog.CachePath(buildId)}");
        return 0;
    }

    /// <summary>Value of a flag like "--out dir"; prints a message instead of throwing when the value is missing.</summary>
    private static string? FlagValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            Console.WriteLine($"missing value after {args[i]}");
            return null;
        }
        return args[++i];
    }

    public static int Find(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: ripper find <query> [--kind item|prefab] [--category <name>] [--path <substr>] [--limit <n>]");
            return 1;
        }

        var queryParts = new List<string>();
        string? kind = null, category = null, pathContains = null;
        var limit = 25;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--kind": kind = FlagValue(args, ref i); break;
                case "--category": category = FlagValue(args, ref i); break;
                case "--path": pathContains = FlagValue(args, ref i); break;
                case "--limit": limit = int.TryParse(FlagValue(args, ref i), out var n) ? n : limit; break;
                default: queryParts.Add(args[i]); break;
            }
        }

        var catalog = RustCatalog.LoadNewest();
        if (catalog == null)
        {
            Console.WriteLine("no catalog found - run: ripper catalog");
            return 1;
        }

        var shown = 0;
        foreach (var entry in catalog.Find(string.Join(' ', queryParts), kind, category, pathContains))
        {
            Console.WriteLine($"[{entry.Kind,-6}] {entry.Name,-38} {entry.ShortName,-30} {entry.Category,-12} {entry.PrefabPath}");
            if (++shown >= limit)
            {
                Console.WriteLine("... (more; raise --limit or refine the query)");
                break;
            }
        }
        Console.WriteLine($"=== {shown} shown (catalog build {catalog.BuildId}, {catalog.Entries.Count} entries) ===");
        return 0;
    }

    public static int Export(string[] args)
    {
        var queryParts = new List<string>();
        var outDir = "export";
        var extraBundles = new List<string>();
        var options = new RipperGlbOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = FlagValue(args, ref i) ?? outDir; break;
                case "--bundles": if (FlagValue(args, ref i) is { } b) { extraBundles.Add(b); } break;
                case "--lod": options = options with { LodLevel = int.TryParse(FlagValue(args, ref i), out var lod) ? lod : 0 }; break;
                case "--all-lods": options = options with { AllLods = true }; break;
                case "--shadows": options = options with { IncludeShadowProxies = true }; break;
                case "--no-prune": options = options with { PruneEmpties = false }; break;
                case "--vertex-colors": options = options with { IncludeVertexColors = true }; break;
                case "--paint-nodes": options = options with { PaintNodes = true }; break;
                case "--no-lights": options = options with { IncludeLights = false }; break;
                case "--keep-chains": options = options with { CollapseEmptyChains = false }; break;
                case "--show-utility": options = options with { HideUtility = false }; break;
                default: queryParts.Add(args[i]); break;
            }
        }
        if (queryParts.Count == 0)
        {
            Console.WriteLine("usage: ripper export <query> [--out <dir>] [--lod <n>] [--all-lods] [--shadows] [--no-prune] [--vertex-colors] [--bundles <extra.bundle>]...");
            return 1;
        }

        var session = Session.Start(extraBundles);
        if (session == null)
        {
            return 1;
        }
        session = EnsureTextures(session, string.Join(' ', queryParts), extraBundles);
        var result = session.ExportGlb(string.Join(' ', queryParts), outDir, options);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    /// <summary>
    /// If the target references assets in unloaded bundles (meshes, materials,
    /// textures), load exactly those bundles INTO the live session - no session
    /// restart, no reprocessing. Iterates because newly loaded assets can
    /// reveal further dependencies (a skinned mesh bundle brings materials
    /// whose textures live in a third bundle).
    /// </summary>
    internal static Session EnsureTextures(Session session, string query, List<string> extraBundles)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var resolved = session.ResolveExportSet(query);
            if (resolved == null)
            {
                return session;
            }
            var missing = session.FindMissingDependencyBundles(resolved.Value.Assets);
            if (missing.Count == 0)
            {
                return session;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            session.AddBundles(missing);
            Console.WriteLine($"dependency closure (pass {pass + 1}): {missing.Count} bundle(s) added in {sw.Elapsed.TotalSeconds:F1}s: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }
        return session;
    }

    public static int Mat(string[] args)
    {
        var queryParts = new List<string>();
        var extraBundles = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bundles")
            {
                extraBundles.Add(args[++i]);
            }
            else
            {
                queryParts.Add(args[i]);
            }
        }
        if (queryParts.Count == 0)
        {
            Console.WriteLine("usage: ripper mat <query> [--bundles <extra.bundle>]");
            return 1;
        }

        var session = Session.Start(extraBundles);
        if (session == null)
        {
            return 1;
        }
        var report = session.MaterialReport(string.Join(' ', queryParts));
        Console.WriteLine(report ?? "no match");
        return report != null ? 0 : 1;
    }

    public static int TexStat(string[] args)
    {
        var queryParts = new List<string>();
        var extraBundles = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bundles")
            {
                if (FlagValue(args, ref i) is { } b) { extraBundles.Add(b); }
            }
            else
            {
                queryParts.Add(args[i]);
            }
        }
        if (queryParts.Count == 0)
        {
            Console.WriteLine("usage: ripper texstat <query> [--bundles <extra.bundle>]");
            return 1;
        }

        var session = Session.Start(extraBundles);
        if (session == null)
        {
            return 1;
        }
        session = EnsureTextures(session, string.Join(' ', queryParts), extraBundles);
        var report = session.TextureReport(string.Join(' ', queryParts));
        Console.WriteLine(report ?? "no match");
        return report != null ? 0 : 1;
    }

    public static int Serve(string[] args)
    {
        var port = 17071;
        var extraBundles = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--bundles": extraBundles.Add(args[++i]); break;
            }
        }

        var session = Session.Start(extraBundles);
        if (session == null)
        {
            return 1;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"Rust Ripper daemon listening on http://127.0.0.1:{port}/  (find /find?q=..., export /export?q=..., materials /mat?q=..., /status)");

        while (true)
        {
            var context = listener.GetContext();
            try
            {
                var request = context.Request;
                var q = request.QueryString["q"] ?? "";
                switch (request.Url?.AbsolutePath)
                {
                    case "/status":
                        WriteJson(context, 200, new { build = session.Catalog.BuildId, entries = session.Catalog.Entries.Count, loadedBundles = session.LoadedBundles.Select(Path.GetFileName), uptimeSeconds = (int)session.Uptime.TotalSeconds });
                        break;
                    case "/find":
                        var limit = int.TryParse(request.QueryString["limit"], out var l) ? l : 25;
                        var results = session.Catalog.Find(q, request.QueryString["kind"], request.QueryString["category"], request.QueryString["path"]).Take(limit).ToList();
                        WriteJson(context, 200, results);
                        break;
                    case "/export":
                        var outDir = request.QueryString["out"] ?? "export";
                        if (request.QueryString["notex"] == null)
                        {
                            // dependency closure may replace the session with a bigger one;
                            // pass the current bundle set so closures accumulate across exports
                            session = Cli.EnsureTextures(session, q, session.LoadedBundles.ToList());
                        }
                        var options = new RipperGlbOptions
                        {
                            LodLevel = int.TryParse(request.QueryString["lod"], out var lodLevel) ? lodLevel : 0,
                            AllLods = request.QueryString["all-lods"] != null,
                            IncludeShadowProxies = request.QueryString["shadows"] != null,
                            PruneEmpties = request.QueryString["no-prune"] == null,
                            IncludeVertexColors = request.QueryString["vertex-colors"] != null,
                            PaintNodes = request.QueryString["paint-nodes"] != null,
                            IncludeLights = request.QueryString["no-lights"] == null,
                            CollapseEmptyChains = request.QueryString["keep-chains"] == null,
                            HideUtility = request.QueryString["show-utility"] == null,
                        };
                        var result = session.ExportGlb(q, outDir, options);
                        WriteJson(context, result.Success ? 200 : 404, new { success = result.Success, message = result.Message, path = result.Path, seconds = result.Seconds });
                        break;
                    case "/mat":
                        var report = session.MaterialReport(q);
                        WriteJson(context, report != null ? 200 : 404, new { report });
                        break;
                    case "/texstat":
                        session = Cli.EnsureTextures(session, q, session.LoadedBundles.ToList());
                        var texReport = session.TextureReport(q);
                        WriteJson(context, texReport != null ? 200 : 404, new { report = texReport });
                        break;
                    case "/matscan":
                        WriteJson(context, 200, new { report = session.MatScan() });
                        break;
                    case "/coverage":
                        WriteJson(context, 200, new { report = session.Coverage() });
                        break;
                    case "/fields":
                        var fieldsReport = session.FieldsReport(q, request.QueryString["cls"]);
                        WriteJson(context, fieldsReport != null ? 200 : 404, new { report = fieldsReport });
                        break;
                    case "/texdump":
                        var texOut = request.QueryString["out"] ?? "export";
                        var dumped = session.DumpTexture(q, texOut);
                        WriteJson(context, dumped != null ? 200 : 404, new { path = dumped });
                        break;
                    case "/hier":
                        var hierReport = session.HierarchyReport(q);
                        WriteJson(context, hierReport != null ? 200 : 404, new { report = hierReport });
                        break;
                    case "/matusers":
                        WriteJson(context, 200, session.MatUsers(q));
                        break;
                    case "/shaderdump":
                        var shaderOut = request.QueryString["out"] ?? "export";
                        var shaderReport = session.ShaderDump(q, shaderOut,
                            request.QueryString["stage"] ?? "fragment",
                            request.QueryString["kw"] ?? "",
                            request.QueryString["plat"] ?? "",
                            int.TryParse(request.QueryString["max"], out var maxDisasm) ? maxDisasm : 4,
                            int.TryParse(request.QueryString["rawentry"], out var rawEntryIdx) ? rawEntryIdx : -1);
                        WriteJson(context, shaderReport != null ? 200 : 404, shaderReport ?? new { error = $"no shader or material matches '{q}'" });
                        break;
                    default:
                        WriteJson(context, 404, new { error = "unknown endpoint" });
                        break;
                }
            }
            catch (Exception ex)
            {
                try { WriteJson(context, 500, new { error = ex.Message }); } catch { }
            }
        }
    }

    private static void WriteJson(HttpListenerContext context, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    public static int Stats(string[] paths)
    {
        if (paths.Length == 0)
        {
            return Usage();
        }
        Logger.Add(new ConsoleLogger(false));
        var handler = new RipperExportHandler(new FullConfiguration());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GameData gameData = handler.LoadAndProcess(paths, LocalFileSystem.Instance);
        sw.Stop();
        Console.WriteLine($"loaded in {sw.Elapsed.TotalSeconds:F1}s");

        var counts = new Dictionary<string, int>();
        var total = 0;
        foreach (var asset in gameData.GameBundle.FetchAssets())
        {
            counts[asset.ClassName] = counts.GetValueOrDefault(asset.ClassName) + 1;
            total++;
        }
        Console.WriteLine($"=== {total} assets ===");
        foreach (var (className, count) in counts.OrderByDescending(x => x.Value).Take(15))
        {
            Console.WriteLine($"{count,8}  {className}");
        }
        return 0;
    }
}

/// <summary>
/// ExportHandler with only the processors GLB hierarchy export needs.
/// Skips assembly/sprite/audio/lighting processing - faster, and avoids a
/// SpriteProcessor assertion crash when texture bundles are co-loaded.
/// </summary>
internal sealed class RipperExportHandler : ExportHandler
{
    public RipperExportHandler(FullConfiguration settings) : base(settings) { }

    protected override IEnumerable<AssetRipper.Processing.IAssetProcessor> GetProcessors()
    {
        yield return new AssetRipper.Processing.Scenes.SceneDefinitionProcessor();
        yield return new AssetRipper.Processing.MainAssetProcessor();
        yield return new AssetRipper.Processing.Prefabs.PrefabProcessor();
    }
}

/// <summary>
/// A loaded game session: bundles parsed once, then queried/exported repeatedly.
/// This is the heart of daemon mode and the future UI backend.
/// </summary>
internal sealed class Session
{
    public required GameData GameData { get; init; }
    public required RustCatalog Catalog { get; init; }
    public required BundleIndex Index { get; init; }
    public required List<string> LoadedBundles { get; init; }
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Uptime => clock.Elapsed;
    private static bool loggerInitialized;

    public static Session? Start(List<string> extraBundles)
    {
        var catalog = RustCatalog.LoadNewest();
        if (catalog == null)
        {
            Console.WriteLine("no catalog found - run: ripper catalog");
            return null;
        }
        var install = RustLocator.GetInstalls().FirstOrDefault();
        if (install == null)
        {
            Console.WriteLine("no Rust install detected");
            return null;
        }

        if (!loggerInitialized)
        {
            Logger.Add(new ConsoleLogger(false));
            loggerInitialized = true;
        }
        var index = BundleIndex.LoadOrBuild(install.BundlesPath, install.BuildId ?? "unknown");
        var handler = new RipperExportHandler(new FullConfiguration());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<string> loadPaths =
        [
            Path.Combine(install.BundlesPath, "shared", "assetscenes.bundle"),
            Path.Combine(install.BundlesPath, "shared", "content.bundle"),
            .. extraBundles,
        ];
        // Unity's built-in primitives (Cube, Sphere, Quad...) and default
        // materials - Rust prefabs reference these constantly
        foreach (var dataDir in new[] { "RustClient_Data", "RustClient_x64_Data", "Rust_Data" })
        {
            foreach (var builtin in new[] { "unity default resources", "unity_builtin_extra" })
            {
                var builtinPath = Path.Combine(install.GameRoot, dataDir, "Resources", builtin);
                if (File.Exists(builtinPath))
                {
                    loadPaths.Add(builtinPath);
                }
            }
        }
        loadPaths = loadPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GameData gameData = handler.LoadAndProcess(loadPaths, LocalFileSystem.Instance);
        Console.WriteLine($"session loaded in {sw.Elapsed.TotalSeconds:F1}s ({loadPaths.Count} bundles)");
        return new Session { GameData = gameData, Catalog = catalog, Index = index, LoadedBundles = loadPaths };
    }

    /// <summary>
    /// Load bundle files into the LIVE session. New collections get their
    /// dependency lists initialized normally; pre-existing collections have
    /// null slots where a dependency wasn't loaded at the time - those are
    /// patched from our BundleIndex dependency tables (same ordered list the
    /// game serialized). No session restart, no reprocessing: prefab roots and
    /// processing passes from the initial load stay valid.
    /// </summary>
    public void AddBundles(List<string> paths)
    {
        var factory = new GameAssetFactory(GameData.AssemblyManager);
        var newCollections = new List<SerializedAssetCollection>();
        foreach (var path in paths)
        {
            AssetRipper.IO.Files.FileBase file;
            try
            {
                file = AssetRipper.IO.Files.SchemeReader.LoadFile(path, LocalFileSystem.Instance);
                file.ReadContentsRecursively();
            }
            catch (Exception ex)
            {
                Logger.Error($"failed to load {path}: {ex.Message}");
                continue;
            }
            while (file is CompressedFile compressed)
            {
                file = compressed.UncompressedFile;
            }
            if (file is AssetRipper.IO.Files.FileContainer container)
            {
                var serializedBundle = SerializedBundle.FromFileContainer(container, factory);
                GameData.GameBundle.AddBundle(serializedBundle);
                newCollections.AddRange(serializedBundle.FetchAssetCollections().OfType<SerializedAssetCollection>());
                LoadedBundles.Add(path);
            }
            else
            {
                Logger.Error($"not a bundle container: {path}");
            }
        }

        foreach (var collection in newCollections)
        {
            collection.InitializeDependencyList(null);
        }

        // patch older collections' unresolved slots now that new CABs exist
        var byName = GameData.GameBundle.FetchAssetCollections()
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var collection in GameData.GameBundle.FetchAssetCollections().OfType<SerializedAssetCollection>().ToList())
        {
            string[]? deps = null;
            for (var i = 1; i < collection.Dependencies.Count; i++)
            {
                if (collection.Dependencies[i] is not null)
                {
                    continue;
                }
                deps ??= Index.GetDependencies(collection.Name);
                if (i - 1 < deps.Length && byName.TryGetValue(deps[i - 1], out var resolvedCollection))
                {
                    collection.SetDependency(i, resolvedCollection);
                }
            }
        }
    }

    /// <summary>
    /// Bundles (not yet loaded) that hold assets the export set references:
    /// meshes on skinned renderers, materials, and the materials' textures.
    /// Generic BFS over FetchDependencies: resolvable references are followed
    /// (renderer -> material -> texture), unresolvable external ones are mapped
    /// to their bundle (PPtr FileID N indexes the collection's dependency table
    /// at N-1). MonoBehaviour references (loot tables, sounds, other prefabs)
    /// are not chased - they aren't part of the visual model.
    /// </summary>
    public List<string> FindMissingDependencyBundles(IEnumerable<IUnityObjectBase> exportSet)
    {
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<IUnityObjectBase>();
        var queue = new Queue<IUnityObjectBase>(exportSet);
        while (queue.Count > 0)
        {
            var asset = queue.Dequeue();
            if (!visited.Add(asset) || asset is IMonoBehaviour)
            {
                continue;
            }
            string[]? deps = null;
            foreach (var (_, pptr) in asset.FetchDependencies())
            {
                if (pptr.PathID == 0)
                {
                    continue;
                }
                if (asset.Collection.TryGetAsset(pptr.FileID, pptr.PathID) is { } resolved)
                {
                    queue.Enqueue(resolved);
                    continue;
                }
                if (pptr.FileID <= 0)
                {
                    continue;
                }
                deps ??= Index.GetDependencies(asset.Collection.Name);
                if (pptr.FileID - 1 < deps.Length && Index.CabToBundle.TryGetValue(deps[pptr.FileID - 1], out var bundle))
                {
                    needed.Add(bundle);
                }
            }
        }
        return needed.Where(b => !LoadedBundles.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public CatalogEntry? Resolve(string query)
    {
        var matches = Catalog.Find(query).Where(e => e.PrefabPath.Length > 0).ToList();
        return matches.FirstOrDefault(e => e.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault(e => e.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    /// <summary>
    /// Drop GameObjects whose subtree contains no renderer (colliders, sockets,
    /// logic nodes...) so the export doesn't import as a forest of empties.
    /// Ancestor chains of mesh carriers are kept so transforms stay intact.
    /// </summary>
    public static IEnumerable<IUnityObjectBase> PruneToMeshCarriers(IEnumerable<IUnityObjectBase> assets)
    {
        var list = assets.ToList();
        var keep = new HashSet<IGameObject>();
        foreach (var gameObject in list.OfType<IGameObject>())
        {
            if (gameObject.FetchHierarchy().OfType<IRenderer>().Any())
            {
                keep.Add(gameObject);
            }
        }
        foreach (var asset in list)
        {
            switch (asset)
            {
                case IGameObject go when keep.Contains(go):
                    yield return go;
                    break;
                case AssetRipper.SourceGenerated.Classes.ClassID_2.IComponent component
                    when component.GameObject_C2P is IGameObject owner && keep.Contains(owner):
                    yield return component;
                    break;
                case IGameObject:
                case AssetRipper.SourceGenerated.Classes.ClassID_2.IComponent:
                    break;
                default:
                    yield return asset;
                    break;
            }
        }
    }

    public (IEnumerable<IUnityObjectBase> Assets, string Name, IGameObject Root)? ResolveExportSet(string query)
    {
        var entry = Resolve(query);
        if (entry == null)
        {
            return null;
        }
        var targetName = Path.GetFileNameWithoutExtension(entry.PrefabPath);

        var hierarchy = GameData.GameBundle.FetchAssets()
            .OfType<AssetRipper.Processing.Prefabs.PrefabHierarchyObject>()
            .FirstOrDefault(h => h.Name.String.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (hierarchy != null)
        {
            return (hierarchy.Assets, targetName, hierarchy.Root);
        }

        // Roots in the AssetScene-prefabs scene are named by their full prefab path.
        var gameObjects = GameData.GameBundle.FetchAssets().OfType<IGameObject>();
        var root = gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(entry.PrefabPath, StringComparison.OrdinalIgnoreCase))
            ?? gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            return (root.FetchHierarchy().Cast<IUnityObjectBase>(), targetName, root);
        }
        return null;
    }

    public (bool Success, string Message, string? Path, double Seconds) ExportGlb(string query, string outDir, RipperGlbOptions? options = null)
    {
        options ??= new RipperGlbOptions();
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return (false, $"nothing matching '{query}' found", null, 0);
        }
        Directory.CreateDirectory(outDir);
        var outPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(outDir, $"{resolved.Value.Name}.glb"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sceneBuilder = RipperGlbBuilder.Build(resolved.Value.Root, options, GameData, out var builder);
        bool ok;
        using (var fileStream = File.Create(outPath))
        {
            try
            {
                var model = sceneBuilder.ToGltf2();
                if (!options.IncludeVertexColors)
                {
                    RipperGlbBuilder.DemoteMaskVertexColors(model, builder.VertexColorTintMaterials);
                }
                RipperGlbBuilder.AddPaintAttributes(model, builder.DetailPaint);
                RipperGlbBuilder.AddPaletteAttributes(model, builder.RuntimeTintMaterials, ReadPalette(resolved.Value.Root));
                model.WriteGLB(fileStream);
                ok = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"GLB write failed: {ex.Message}");
                ok = false;
            }
        }
        // detail-mask sidecars in layer-nodes mode
        if (ok && options.PaintNodes)
        {
            foreach (var (_, (materialName, mask)) in builder.DetailMasks)
            {
                if (RipperMaterialFactory.TryDecodePng(mask, out var png))
                {
                    var maskPath = System.IO.Path.Combine(outDir, $"{resolved.Value.Name}.detailmask.{materialName}.png");
                    File.WriteAllBytes(maskPath, png);
                    Console.WriteLine($"detail mask: {maskPath}");
                }
            }
        }
        // runtime-tinted materials: every set texture slot ships as a sidecar -
        // the layer cannot be composited without its masks and tint maps
        if (ok && builder.RuntimeTintMaterialAssets.Count > 0)
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var material in builder.RuntimeTintMaterialAssets)
            {
                foreach (var (_, texEnv) in material.GetTextureProperties())
                {
                    if (texEnv.Texture.TryGetAsset(material.Collection) is not AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                        || !written.Add(texture.Name.String)
                        || !RipperMaterialFactory.TryDecodePng(texture, out var png))
                    {
                        continue;
                    }
                    var texPath = System.IO.Path.Combine(outDir, $"{resolved.Value.Name}.{texture.Name.String}.png");
                    File.WriteAllBytes(texPath, png);
                }
            }
            Console.WriteLine($"layer textures: {written.Count} sidecars");
        }
        sw.Stop();
        return ok && File.Exists(outPath)
            ? (true, $"exported in {sw.Elapsed.TotalSeconds:F1}s: {outPath} ({new FileInfo(outPath).Length / 1024} KB)", outPath, sw.Elapsed.TotalSeconds)
            : (false, "export failed", null, sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Raw per-channel statistics of every texture the target's materials use,
    /// straight from the decoded game data. Ground truth for channel-packing
    /// questions (where does X live in a normal map, what's in the alpha).
    /// </summary>
    public string? TextureReport(string query)
    {
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"=== texture channels for {resolved.Value.Name} ===");
        var seen = new HashSet<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>();
        foreach (var renderer in resolved.Value.Assets.OfType<IRenderer>())
        {
            foreach (var materialPtr in renderer.Materials_C25)
            {
                if (!materialPtr.TryGetAsset(renderer.Collection, out IMaterial? material))
                {
                    continue;
                }
                foreach (var (slot, texEnv) in material.GetTextureProperties())
                {
                    if (texEnv.Texture.TryGetAsset(material.Collection) is not AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                        || !seen.Add(texture))
                    {
                        continue;
                    }
                    if (!AssetRipper.Export.Modules.Textures.TextureConverter.TryConvertToBitmap(texture, out var bitmap))
                    {
                        sb.AppendLine($"  {slot.String,-26} {texture.Name.String,-30} DECODE FAILED ({texture.Format_C28E})");
                        continue;
                    }
                    // channel-true view: same PNG path the material factory consumes
                    // (DirectBitmap in-memory order varies by source format)
                    using var pngStream = new MemoryStream();
                    bitmap.SaveAsPng(pngStream);
                    pngStream.Position = 0;
                    using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngStream);
                    var mins = new byte[] { 255, 255, 255, 255 };
                    var maxs = new byte[4];
                    var sums = new long[4];
                    long count = 0;
                    for (var py = 0; py < img.Height; py += 3)
                    {
                        for (var px = 0; px < img.Width; px += 3)
                        {
                            var p = img[px, py];
                            Span<byte> v = [p.R, p.G, p.B, p.A];
                            for (var ch = 0; ch < 4; ch++)
                            {
                                if (v[ch] < mins[ch]) { mins[ch] = v[ch]; }
                                if (v[ch] > maxs[ch]) { maxs[ch] = v[ch]; }
                                sums[ch] += v[ch];
                            }
                            count++;
                        }
                    }
                    string Ch(int i) => $"{mins[i]}..{maxs[i]} ~{(count > 0 ? sums[i] / count : 0)}";
                    sb.AppendLine($"  {slot.String,-26} {texture.Name.String,-30} {texture.Width_C28}x{texture.Height_C28} {texture.Format_C28E}");
                    sb.AppendLine($"      R {Ch(0),-16} G {Ch(1),-16} B {Ch(2),-16} A {Ch(3)}");
                    if (slot.String.Contains("Normal", StringComparison.OrdinalIgnoreCase) || slot.String.Contains("Bump", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"      normal layout detected: {RipperMaterialFactory.DetectNormalLayout(img)}");
                    }
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Inventory of every loaded material using a colour method: detail-layer
    /// paint, colorize layers, vertex-color tint. Also lists every
    /// ConstructionSkin_ColourLookup palette (building skin colours).
    /// </summary>
    public string MatScan()
    {
        var sb = new StringBuilder();
        var count = 0;
        sb.AppendLine("=== materials using colour methods (loaded bundles) ===");
        foreach (var material in GameData.GameBundle.FetchAssets().OfType<IMaterial>())
        {
            float apply = 0, applyAlpha = 0, colorize = 0, detail = 0;
            RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexColor", out apply);
            RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexAlpha", out applyAlpha);
            RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "_ColorizeLayer", out colorize);
            RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "_DetailLayer", out detail);
            var maskName = material.TryGetTextureProperty("_DetailMask", out var maskEnv)
                && maskEnv.Texture.TryGetAsset(material.Collection) is AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D maskTex
                ? maskTex.Name.String : "";
            var hasDetailPaint = detail != 0 && maskName.Length > 0;
            if (apply == 0 && applyAlpha == 0 && colorize == 0 && !hasDetailPaint)
            {
                continue;
            }
            var shader = material.Shader_C21P?.Name.String ?? "?";
            var flags = new List<string>();
            if (apply != 0) { flags.Add("vertexColor"); }
            if (applyAlpha != 0) { flags.Add("vertexAlpha"); }
            if (colorize != 0)
            {
                RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "_ColorizeLayerEnabled", out var ce1);
                RustRipper.Core.RipperMaterialFactory.TryGetFloat(material, "colorizeLayerEnabled", out var ce2);
                flags.Add(ce1 != 0f || ce2 != 0f ? "colorize(ON)" : "colorize(off)");
            }
            if (hasDetailPaint) { flags.Add($"detailPaint mask={maskName}"); }
            sb.AppendLine($"  {material.Name.String,-40} {shader,-44} {string.Join(", ", flags)}");
            count++;
        }
        sb.AppendLine($"=== {count} materials ===");

        foreach (var mono in GameData.GameBundle.FetchAssets().OfType<IMonoBehaviour>())
        {
            if (mono.ScriptP?.ClassName_R.String != "ConstructionSkin_ColourLookup")
            {
                continue;
            }
            sb.AppendLine($"palette: {mono.Name.String} ({mono.Collection.Name})");
            if (mono.LoadStructure() is { } structure && structure.TryGetField("AllColours") is { } coloursField)
            {
                var i = 1;
                foreach (var element in coloursField.AsAssetArray)
                {
                    if (element is AssetRipper.Import.Structure.Assembly.Serializable.SerializableStructure colour
                        && colour.TryGetField("r") is { } r && colour.TryGetField("g") is { } g && colour.TryGetField("b") is { } b)
                    {
                        var rr = (int)(Math.Clamp(r.AsSingle, 0, 1) * 255);
                        var gg = (int)(Math.Clamp(g.AsSingle, 0, 1) * 255);
                        var bb = (int)(Math.Clamp(b.AsSingle, 0, 1) * 255);
                        sb.AppendLine($"  #{i,-3} #{rr:x2}{gg:x2}{bb:x2}  ({r.AsSingle:F3}, {g.AsSingle:F3}, {b.AsSingle:F3})");
                        i++;
                    }
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full prefab hierarchy with every component (MonoBehaviours by script
    /// class name). The go-to probe for "which component claims this
    /// renderer" questions.
    /// </summary>
    /// <summary>Animator with its controller resolution state - the probe for
    /// "why does this prefab export no animations".</summary>
    private static string AnimatorLabel(AssetRipper.SourceGenerated.Classes.ClassID_95.IAnimator animator)
    {
        try
        {
            if (animator.Has_Controller_PPtr_RuntimeAnimatorController_5())
            {
                var pptr = animator.Controller_PPtr_RuntimeAnimatorController_5;
                var resolved = animator.Controller_PPtr_RuntimeAnimatorController_5P;
                return resolved is null
                    ? $"Animator(controller UNRESOLVED FileID {pptr.FileID} PathID {pptr.PathID})"
                    : $"Animator(controller={resolved.Name.String} [{resolved.ClassName}])";
            }
        }
        catch (Exception ex)
        {
            return $"Animator(controller error: {ex.Message})";
        }
        return "Animator(no controller field)";
    }

    public string? HierarchyReport(string query)
    {
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"=== hierarchy for {resolved.Value.Name} ===");
        var localControllers = resolved.Value.Root.Collection
            .OfType<AssetRipper.SourceGenerated.Classes.ClassID_93.IRuntimeAnimatorController>()
            .ToList();
        if (localControllers.Count > 0)
        {
            sb.AppendLine($"controllers in collection: {string.Join(", ", localControllers.Select(c => $"{c.Name.String} [{c.ClassName}]"))}");
        }
        void Walk(AssetRipper.SourceGenerated.Classes.ClassID_4.ITransform transform, int depth)
        {
            if (transform.GameObject_C4P is not { } gameObject)
            {
                return;
            }
            var components = new List<string>();
            foreach (var componentPtr in gameObject.FetchComponents())
            {
                var component = componentPtr.TryGetAsset(gameObject.Collection);
                var label = component switch
                {
                    null => "?missing",
                    IMonoBehaviour monoBehaviour => $"MB:{monoBehaviour.ScriptP?.ClassName_R.String ?? "?"}",
                    AssetRipper.SourceGenerated.Classes.ClassID_205.ILODGroup lodGroup =>
                        $"LODGroup(lods={lodGroup.LODs.Count}, renderers=[{string.Join("/", lodGroup.LODs.Select(l => $"{l.Renderers.Count(rp => rp.Renderer.TryGetAsset(lodGroup.Collection, out AssetRipper.SourceGenerated.Classes.ClassID_25.IRenderer? _))}r"))}])",
                    AssetRipper.SourceGenerated.Classes.ClassID_95.IAnimator animator => AnimatorLabel(animator),
                    _ => component.ClassName,
                };
                if (label != "Transform")
                {
                    components.Add(label);
                }
            }
            sb.AppendLine($"{new string(' ', depth * 2)}{gameObject.Name.String}  [{string.Join(", ", components)}]");
            foreach (var child in transform.Children_C4P)
            {
                if (child is not null)
                {
                    Walk(child, depth + 1);
                }
            }
        }
        Walk(resolved.Value.Root.GetTransform(), 0);
        return sb.ToString();
    }

    /// <summary>
    /// Palette colours for a prefab whose hierarchy declares a palette source
    /// (ComponentSemantics.PaletteSources): follow the component's lookup
    /// reference to the ColourLookup asset and read its colour array. Colours
    /// exactly as the game serializes them.
    /// </summary>
    private List<System.Numerics.Vector4> ReadPalette(IGameObject root)
    {
        // palette source in the exported prefab itself
        foreach (var monoBehaviour in root.FetchHierarchy().OfType<IMonoBehaviour>())
        {
            if (TryResolveLookup(monoBehaviour, out var lookup, out var coloursField)
                && coloursField is not null
                && ReadLookupColours(lookup!, coloursField.Value) is { Count: > 0 } palette)
            {
                Console.WriteLine($"palette: {palette.Count} colours ({lookup!.Name.String})");
                return palette;
            }
        }
        // model prefabs referenced by a skin shell carry the tinted materials
        // but not the palette component - if all loaded palette sources agree
        // on one lookup asset, use it; ambiguity means no attributes
        var lookups = new Dictionary<long, IMonoBehaviour>();
        foreach (var monoBehaviour in GameData.GameBundle.FetchAssets().OfType<IMonoBehaviour>())
        {
            if (TryResolveLookup(monoBehaviour, out var lookup, out _))
            {
                lookups[lookup!.PathID] = lookup;
            }
        }
        if (lookups.Count == 1)
        {
            var lookup = lookups.Values.First();
            TryResolveColoursField(lookup, out var coloursField);
            if (coloursField is not null && ReadLookupColours(lookup, coloursField.Value) is { Count: > 0 } palette)
            {
                Console.WriteLine($"palette: {palette.Count} colours ({lookup.Name.String}, resolved globally)");
                return palette;
            }
        }
        else if (lookups.Count > 1)
        {
            Console.WriteLine($"palette: ambiguous ({lookups.Count} lookups loaded), no palette attributes");
        }
        return [];
    }

    private static bool TryResolveLookup(IMonoBehaviour monoBehaviour, out IMonoBehaviour? lookup, out SerializableValue? coloursField)
    {
        lookup = null;
        coloursField = null;
        var className = monoBehaviour.ScriptP?.ClassName_R.String;
        var schema = RustRipper.Core.ComponentSemantics.PaletteSources.FirstOrDefault(s => s.ClassName == className);
        if (schema is null || monoBehaviour.LoadStructure() is not { } structure
            || structure.TryGetField(schema.LookupField) is not { CValue: AssetRipper.Assets.Metadata.IPPtr pptr }
            || monoBehaviour.Collection.TryGetAsset(pptr.FileID, pptr.PathID) is not IMonoBehaviour resolved)
        {
            return false;
        }
        lookup = resolved;
        TryResolveColoursField(resolved, out coloursField);
        return true;
    }

    private static void TryResolveColoursField(IMonoBehaviour lookup, out SerializableValue? coloursField)
    {
        coloursField = null;
        var lookupClass = lookup.ScriptP?.ClassName_R.String;
        foreach (var schema in RustRipper.Core.ComponentSemantics.PaletteSources)
        {
            if (lookup.LoadStructure() is { } structure && structure.TryGetField(schema.ColoursField) is { } field)
            {
                coloursField = field;
                return;
            }
        }
    }

    private static List<System.Numerics.Vector4> ReadLookupColours(IMonoBehaviour lookup, SerializableValue coloursField)
    {
        var palette = new List<System.Numerics.Vector4>();
        foreach (var element in coloursField.AsAssetArray)
        {
            if (element is SerializableStructure colour
                && colour.TryGetField("r") is { } r && colour.TryGetField("g") is { } g
                && colour.TryGetField("b") is { } b)
            {
                var a = colour.TryGetField("a")?.AsSingle ?? 1f;
                palette.Add(new System.Numerics.Vector4(r.AsSingle, g.AsSingle, b.AsSingle, a));
            }
        }
        return palette;
    }

    /// <summary>Decode any loaded texture by asset name to a PNG on disk
    /// (extras-referenced textures like detail albedos and light cookies).</summary>
    public string? DumpTexture(string textureName, string outDir)
    {
        var texture = GameData.GameBundle.FetchAssets()
            .OfType<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>()
            .FirstOrDefault(t => t.Name.String.Equals(textureName, StringComparison.OrdinalIgnoreCase));
        if (texture is null || !RipperMaterialFactory.TryDecodePng(texture, out var png))
        {
            return null;
        }
        Directory.CreateDirectory(outDir);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(outDir, $"{texture.Name.String}.png"));
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>
    /// Material survey by shader: every loaded material whose parsed shader
    /// name matches, its detail-layer slots, whether base and detail albedo
    /// are the same texture asset (PPtr identity - works even when texture
    /// bundles are not loaded), and which loaded prefab roots render it.
    /// Plus an aggregate over ALL loaded materials that set a detail albedo.
    /// Scope: currently loaded bundles.
    /// </summary>
    public object MatUsers(string shaderQuery)
    {
        (int FileID, long PathID)? Slot(IMaterial material, string slot)
        {
            if (material.TryGetTextureProperty(slot, out var texEnv) && texEnv.Texture.PathID != 0)
            {
                return (texEnv.Texture.FileID, texEnv.Texture.PathID);
            }
            return null;
        }
        string TexName(IMaterial material, string slot)
        {
            if (!material.TryGetTextureProperty(slot, out var texEnv) || texEnv.Texture.PathID == 0)
            {
                return "";
            }
            return texEnv.Texture.TryGetAsset(material.Collection) is AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                ? texture.Name.String
                : $"(unloaded FileID {texEnv.Texture.FileID} PathID {texEnv.Texture.PathID})";
        }
        float? F(IMaterial material, string name)
            => RipperMaterialFactory.TryGetFloat(material, name, out var v) ? v : null;

        var allMaterials = GameData.GameBundle.FetchAssets().OfType<IMaterial>().ToList();
        var matched = allMaterials
            .Where(m => (m.Shader_C21P?.ParsedForm.Name_R.String ?? "").Contains(shaderQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var users = matched.ToDictionary(m => m, _ => new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var renderer in GameData.GameBundle.FetchAssets().OfType<IRenderer>())
        {
            foreach (var pptr in renderer.Materials_C25)
            {
                if (pptr.TryGetAsset(renderer.Collection, out IMaterial? used) && used != null
                    && users.TryGetValue(used, out var roots))
                {
                    roots.Add(renderer.GameObject_C25P?.GetRoot().Name.String ?? "?");
                }
            }
        }

        var materials = matched.Select(m => new
        {
            material = m.Name.String,
            collection = m.Collection.Name,
            shader = m.Shader_C21P?.ParsedForm.Name_R.String ?? "",
            mainTex = TexName(m, "_MainTex"),
            detailAlbedo = TexName(m, "_DetailAlbedoMap"),
            sameAlbedoTexture = Slot(m, "_DetailAlbedoMap") is { } d && d.Equals(Slot(m, "_MainTex")),
            blendMask = TexName(m, "_DetailBlendMaskMap"),
            tintMap = TexName(m, "_DetailTintMap"),
            detailBlendLayer = F(m, "_DetailBlendLayer"),
            detailBlendFactor = F(m, "_DetailBlendFactor"),
            detailBlendFalloff = F(m, "_DetailBlendFalloff"),
            usedBy = users[m].Take(12).ToList(),
            userCount = users[m].Count,
        }).OrderBy(x => x.material).ToList();

        int sameCount = 0, differentCount = 0;
        var differentExamples = new List<object>();
        foreach (var m in allMaterials)
        {
            if (Slot(m, "_DetailAlbedoMap") is not { } detail)
            {
                continue;
            }
            if (detail.Equals(Slot(m, "_MainTex")))
            {
                sameCount++;
            }
            else
            {
                differentCount++;
                if (differentExamples.Count < 15)
                {
                    differentExamples.Add(new
                    {
                        material = m.Name.String,
                        shader = m.Shader_C21P?.ParsedForm.Name_R.String ?? "",
                        mainTex = TexName(m, "_MainTex"),
                        detailAlbedo = TexName(m, "_DetailAlbedoMap"),
                    });
                }
            }
        }
        return new
        {
            query = shaderQuery,
            matchedMaterials = matched.Count,
            materials,
            detailAlbedoAggregate = new
            {
                withDetailAlbedo = sameCount + differentCount,
                sameAsMainTex = sameCount,
                differentFromMainTex = differentCount,
                differentExamples,
            },
        };
    }

    /// <summary>
    /// Compiled shader program dump: find the Shader asset by its parsed
    /// name (or through a material that uses it) and hand it to the
    /// extraction layer. The exact blend math lives only in these programs.
    /// </summary>
    public object? ShaderDump(string query, string outDir, string stage, string keywords, string platform, int max, int rawEntry = -1)
    {
        var shaders = GameData.GameBundle.FetchAssets()
            .OfType<AssetRipper.SourceGenerated.Classes.ClassID_48.IShader>()
            .ToList();
        var shader = shaders.FirstOrDefault(s => s.ParsedForm.Name_R.String.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? shaders.FirstOrDefault(s => s.ParsedForm.Name_R.String.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (shader == null)
        {
            shader = GameData.GameBundle.FetchAssets().OfType<IMaterial>()
                .FirstOrDefault(m => m.Name.String.Equals(query, StringComparison.OrdinalIgnoreCase))
                ?.Shader_C21P;
        }
        if (shader == null)
        {
            return null;
        }
        var kwFilter = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ShaderBlobDump.Dump(shader, outDir, stage, kwFilter, platform, max, rawEntry);
    }

    private Dictionary<string, string>? guidToPath;

    /// <summary>
    /// Serialized field dump for every MonoBehaviour in a prefab (optionally
    /// one class). GameObjectRef guids resolve through the GameManifest
    /// catalog to prefab paths - the key for cross-prefab composition
    /// (ConditionalModel.prefab, wearables, module items).
    /// </summary>
    public string? FieldsReport(string query, string? classFilter)
    {
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return null;
        }
        guidToPath ??= Catalog.Entries
            .Where(e => e.PrefabGuid.Length > 0 && e.PrefabPath.Length > 0)
            .GroupBy(e => e.PrefabGuid)
            .ToDictionary(g => g.Key, g => g.First().PrefabPath);

        var sb = new StringBuilder();
        sb.AppendLine($"=== fields for {resolved.Value.Name}{(classFilter != null ? $" (class {classFilter})" : "")} ===");
        foreach (var monoBehaviour in resolved.Value.Root.FetchHierarchy().OfType<IMonoBehaviour>())
        {
            var className = monoBehaviour.ScriptP?.ClassName_R.String ?? "?";
            if (classFilter != null && !className.Equals(classFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sb.AppendLine($"--- {className}  (PathID {monoBehaviour.PathID})");
            if (monoBehaviour.LoadStructure() is { } structure)
            {
                DumpStructure(sb, structure, monoBehaviour.Collection, 1);
            }
        }
        return sb.ToString();
    }

    private void DumpStructure(StringBuilder sb, SerializableStructure structure, AssetRipper.Assets.Collections.AssetCollection collection, int depth)
    {
        if (depth > 4)
        {
            return;
        }
        for (var i = 0; i < structure.Type.Fields.Count && i < structure.Fields.Length; i++)
        {
            DumpValue(sb, structure.Type.Fields[i].Name, structure.Fields[i], collection, depth);
        }
    }

    private void DumpValue(StringBuilder sb, string name, SerializableValue value, AssetRipper.Assets.Collections.AssetCollection collection, int depth)
    {
        var pad = new string(' ', depth * 2);
        switch (value.CValue)
        {
            case null:
                sb.AppendLine($"{pad}{name} = {value.PValue}");
                break;
            case AssetRipper.Primitives.Utf8String u:
                var text = u.String;
                sb.AppendLine($"{pad}{name} = \"{text}\"{GuidHint(name, text)}");
                break;
            case string s:
                sb.AppendLine($"{pad}{name} = \"{s}\"{GuidHint(name, s)}");
                break;
            case AssetRipper.Assets.Metadata.IPPtr pptr:
                var target = collection.TryGetAsset(pptr.FileID, pptr.PathID);
                sb.AppendLine($"{pad}{name} = PPtr -> {(target is null ? $"unresolved({pptr.FileID}/{pptr.PathID})" : $"{target.ClassName} \"{(target as AssetRipper.Assets.IUnityObjectBase)?.GetBestName()}\"")}");
                break;
            case SerializableStructure sub:
                sb.AppendLine($"{pad}{name}:");
                DumpStructure(sb, sub, collection, depth + 1);
                break;
            case IUnityAssetBase[] array:
                sb.AppendLine($"{pad}{name}[{array.Length}]:");
                foreach (var element in array.Take(8))
                {
                    if (element is SerializableStructure elementStructure)
                    {
                        DumpStructure(sb, elementStructure, collection, depth + 1);
                    }
                }
                break;
            default:
                sb.AppendLine($"{pad}{name} = ({value.CValue.GetType().Name})");
                break;
        }
    }

    private string GuidHint(string fieldName, string text)
    {
        if (text.Length == 32 && guidToPath is not null && guidToPath.TryGetValue(text, out var path))
        {
            return $"   -> {path}";
        }
        return "";
    }

    /// <summary>
    /// Coverage report: of everything in the loaded bundles, how much do our
    /// interpretation tables actually cover? Materials grouped by shader
    /// (mapped = an explicit ShaderProfiles row), MonoBehaviours grouped by
    /// class (handled = a ComponentSemantics row or core reader). Priorities
    /// come from these numbers, not from individual assets.
    /// </summary>
    public string Coverage()
    {
        var sb = new StringBuilder();

        var shaderCounts = new Dictionary<string, int>();
        long totalMaterials = 0, mappedMaterials = 0;
        foreach (var material in GameData.GameBundle.FetchAssets().OfType<IMaterial>())
        {
            var shader = material.Shader_C21P?.Name.String ?? "(no shader)";
            shaderCounts[shader] = shaderCounts.GetValueOrDefault(shader) + 1;
            totalMaterials++;
            if (RustRipper.Core.ShaderProfiles.IsMapped(shader))
            {
                mappedMaterials++;
            }
        }
        sb.AppendLine($"=== material coverage: {mappedMaterials}/{totalMaterials} instances ({(totalMaterials > 0 ? 100.0 * mappedMaterials / totalMaterials : 0):F1}%) on mapped shaders ===");
        foreach (var (shader, count) in shaderCounts.OrderByDescending(e => e.Value).Take(40))
        {
            var status = RustRipper.Core.ShaderProfiles.IsMapped(shader)
                ? RustRipper.Core.ShaderProfiles.Resolve(shader).Id
                : "UNMAPPED (fallback: standard)";
            sb.AppendLine($"  {count,6}  {shader,-52} {status}");
        }

        var classCounts = new Dictionary<string, int>();
        foreach (var monoBehaviour in GameData.GameBundle.FetchAssets().OfType<IMonoBehaviour>())
        {
            var className = monoBehaviour.ScriptP?.ClassName_R.String ?? "(unresolved script)";
            classCounts[className] = classCounts.GetValueOrDefault(className) + 1;
        }
        var handled = new HashSet<string>(RustRipper.Core.ComponentSemantics.LodStates.Select(s => s.ClassName));
        handled.UnionWith(RustRipper.Core.ComponentSemantics.HiddenStateVariants.Keys);
        sb.AppendLine();
        sb.AppendLine($"=== component classes (top 40 of {classCounts.Count}; semantics rows are read, rest passthrough-to-extras) ===");
        foreach (var (className, count) in classCounts.OrderByDescending(e => e.Value).Take(40))
        {
            var status = handled.Contains(className) ? "semantics" : "passthrough";
            sb.AppendLine($"  {count,6}  {className,-52} {status}");
        }
        return sb.ToString();
    }

    /// <summary>Stored-Z prediction for tangent normals encoded as (n+1)/2*255.</summary>
    private static float PredictZ(float x, float y)
    {
        var zSq = 1f - Math.Clamp(x * x + y * y, 0f, 1f);
        return (MathF.Sqrt(zSq) + 1f) / 2f * 255f;
    }

    /// <summary>
    /// Per-material report: shader (the "material method"), every texture slot,
    /// floats and colors - derived entirely from the game's own data.
    /// </summary>
    public string? MaterialReport(string query)
    {
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== materials for {resolved.Value.Name} ===");
        var seen = new HashSet<IMaterial>();
        foreach (var renderer in resolved.Value.Assets.OfType<IRenderer>())
        {
            foreach (var materialPtr in renderer.Materials_C25)
            {
                if (materialPtr.TryGetAsset(renderer.Collection, out IMaterial? material) && seen.Add(material))
                {
                    AppendMaterial(sb, material);
                }
            }
        }
        return seen.Count > 0 ? sb.ToString() : sb.AppendLine("(no materials resolved)").ToString();
    }

    private static void AppendMaterial(StringBuilder sb, IMaterial material)
    {
        var shaderName = material.Shader_C21P?.Name ?? "(shader not loaded)";
        sb.AppendLine($"\nmaterial: {material.Name}");
        sb.AppendLine($"  method (shader): {shaderName}");
        var keywords = RustRipper.Core.RipperMaterialFactory.GetShaderKeywords(material);
        if (keywords.Count > 0)
        {
            sb.AppendLine($"  keywords: {string.Join(' ', keywords)}");
        }
        foreach (var (name, texEnv) in material.GetTextureProperties())
        {
            string texDescription;
            if (texEnv.Texture.TryGetAsset(material.Collection, out var texture))
            {
                texDescription = texture.Name;
            }
            else if (texEnv.Texture.IsNull())
            {
                texDescription = "(none)";
            }
            else
            {
                texDescription = $"MISSING -> FileID {texEnv.Texture.FileID}, PathID {texEnv.Texture.PathID}";
            }
            sb.AppendLine($"  tex   {name,-24} = {texDescription}");
        }
        var sheet = material.SavedProperties_C21;
        if (sheet.Has_Floats_AssetDictionary_Utf8String_Single())
        {
            foreach (var pair in sheet.Floats_AssetDictionary_Utf8String_Single)
            {
                sb.AppendLine($"  float {pair.Key,-24} = {pair.Value}");
            }
        }
        if (sheet.Has_Colors_AssetDictionary_Utf8String_ColorRGBAf())
        {
            foreach (var pair in sheet.Colors_AssetDictionary_Utf8String_ColorRGBAf)
            {
                sb.AppendLine($"  color {pair.Key,-24} = ({pair.Value.R:F3}, {pair.Value.G:F3}, {pair.Value.B:F3}, {pair.Value.A:F3})");
            }
        }
    }
}
