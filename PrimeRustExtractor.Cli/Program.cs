using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using PrimeRustExtractor.Core;

return args.FirstOrDefault() switch
{
    "detect" => Detect(),
    "catalog" => Catalog(args[1..]),
    "find" => Find(args[1..]),
    "stats" => Stats(args[1..]),
    "probe" => Probe(args[1..]),
    "export" => Export(args[1..]),
    _ => Usage(),
};

static int Export(string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine("usage: pre export <query> [--out <dir>]");
        return 1;
    }

    var queryParts = new List<string>();
    var outDir = "export";
    var extraBundles = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out": outDir = args[++i]; break;
            case "--bundles": extraBundles.Add(args[++i]); break;
            default: queryParts.Add(args[i]); break;
        }
    }
    var query = string.Join(' ', queryParts);

    var catalog = RustCatalog.LoadNewest();
    if (catalog == null)
    {
        Console.WriteLine("no catalog found - run: pre catalog");
        return 1;
    }

    // Resolve the query to a prefab path, preferring exact shortname/name hits.
    var matches = catalog.Find(query).Where(e => e.PrefabPath.Length > 0).ToList();
    var entry = matches.FirstOrDefault(e => e.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? matches.FirstOrDefault(e => e.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? matches.FirstOrDefault();
    if (entry == null)
    {
        Console.WriteLine($"nothing in the catalog matches '{query}' with a prefab path");
        return 1;
    }
    var targetName = Path.GetFileNameWithoutExtension(entry.PrefabPath);
    Console.WriteLine($"target: {entry.Name} -> {entry.PrefabPath}");

    var install = RustLocator.GetInstalls().FirstOrDefault();
    if (install == null)
    {
        Console.WriteLine("no Rust install detected");
        return 1;
    }

    Logger.Add(new ConsoleLogger(false));
    var handler = new ExportHandler(new FullConfiguration());
    var sw = System.Diagnostics.Stopwatch.StartNew();
    List<string> loadPaths =
    [
        Path.Combine(install.BundlesPath, "shared", "assetscenes.bundle"),
        Path.Combine(install.BundlesPath, "shared", "content.bundle"),
        .. extraBundles,
    ];
    GameData gameData = handler.LoadAndProcess(loadPaths, LocalFileSystem.Instance);
    Console.WriteLine($"loaded in {sw.Elapsed.TotalSeconds:F1}s");

    // Prefer a processed prefab hierarchy; fall back to a GameObject subtree
    // (Rust ships most prefab content inside AssetScene-* scene files).
    IEnumerable<AssetRipper.Assets.IUnityObjectBase>? exportSet = null;
    var hierarchy = gameData.GameBundle.FetchAssets()
        .OfType<AssetRipper.Processing.Prefabs.PrefabHierarchyObject>()
        .FirstOrDefault(h => h.Name.String.Equals(targetName, StringComparison.OrdinalIgnoreCase));
    if (hierarchy != null)
    {
        Console.WriteLine("matched a prefab hierarchy");
        exportSet = hierarchy.Assets;
    }
    else
    {
        var gameObjects = gameData.GameBundle.FetchAssets()
            .OfType<AssetRipper.SourceGenerated.Classes.ClassID_1.IGameObject>()
            .ToList();
        // Roots in the AssetScene-prefabs scene are named by their full prefab path.
        var root = gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(entry.PrefabPath, StringComparison.OrdinalIgnoreCase))
            ?? gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(targetName, StringComparison.OrdinalIgnoreCase))
            ?? gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Contains(targetName, StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            Console.WriteLine($"matched GameObject '{root.Name.String}' in collection '{root.Collection.Name}'");
            exportSet = AssetRipper.SourceGenerated.Extensions.GameObjectExtensions.FetchHierarchy(root)
                .Cast<AssetRipper.Assets.IUnityObjectBase>();
        }
        else
        {
            var probe = targetName.Length >= 6 ? targetName[..6] : targetName;
            var near = gameObjects
                .Select(go => go.Name.String ?? "")
                .Where(n => n.Contains(probe, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Take(20)
                .ToList();
            Console.WriteLine($"near-miss GameObject names containing '{probe}': {(near.Count > 0 ? string.Join(", ", near) : "(none)")}");
            Console.WriteLine($"total GameObjects loaded: {gameObjects.Count}");
        }
    }
    if (exportSet == null)
    {
        Console.WriteLine($"nothing named '{targetName}' found in loaded bundles");
        return 1;
    }

    Directory.CreateDirectory(outDir);
    var outPath = Path.GetFullPath(Path.Combine(outDir, $"{targetName}.glb"));
    sw.Restart();
    var ok = AssetRipper.Export.PrimaryContent.Models.GlbModelExporter.ExportModel(
        exportSet, outPath, false, LocalFileSystem.Instance);
    sw.Stop();

    if (!ok || !File.Exists(outPath))
    {
        Console.WriteLine("export FAILED");
        return 1;
    }
    Console.WriteLine($"exported in {sw.Elapsed.TotalSeconds:F1}s: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
    return 0;
}

static int Probe(string[] paths)
{
    Logger.Add(new ConsoleLogger(false));
    var handler = new ExportHandler(new FullConfiguration());
    GameData gameData = handler.LoadAndProcess(paths, LocalFileSystem.Instance);

    var scriptNames = new Dictionary<string, int>();
    var total = 0;
    foreach (var asset in gameData.GameBundle.FetchAssets())
    {
        total++;
        if (asset is IMonoBehaviour mb)
        {
            var cls = mb.ScriptP?.ClassName_R.String ?? "(unresolved script)";
            scriptNames[cls] = scriptNames.GetValueOrDefault(cls) + 1;
            if ((mb.Name.String ?? "").Contains("manifest", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"named-manifest asset: name='{mb.Name.String}' scriptClass='{cls}' pathid={mb.PathID}");
            }
        }
    }
    Console.WriteLine($"total assets: {total}");
    foreach (var collection in gameData.GameBundle.FetchAssetCollections().OrderByDescending(c => c.Count).Take(15))
    {
        Console.WriteLine($"{collection.Count,8}  collection: {collection.Name}");
    }
    void DumpBundle(AssetRipper.Assets.Bundles.Bundle bundle, int depth)
    {
        var pad = new string(' ', depth * 4);
        Console.WriteLine($"{pad}bundle: {bundle.Name} ({bundle.GetType().Name})");
        foreach (var collection in bundle.Collections)
        {
            Console.WriteLine($"{pad}    collection: {collection.Name} ({collection.Count})");
        }
        foreach (var resource in bundle.Resources)
        {
            Console.WriteLine($"{pad}    resource: {resource.Name}");
        }
        foreach (var failed in bundle.FailedFiles)
        {
            Console.WriteLine($"{pad}    FAILED: {failed.Name}");
            Console.WriteLine($"{pad}    {failed.StackTrace[..Math.Min(900, failed.StackTrace.Length)]}");
        }
        foreach (var child in bundle.Bundles)
        {
            DumpBundle(child, depth + 1);
        }
    }
    DumpBundle(gameData.GameBundle, 0);
    foreach (var (cls, count) in scriptNames.OrderByDescending(x => x.Value).Take(12))
    {
        Console.WriteLine($"{count,8}  {cls}");
    }
    var manifestish = scriptNames.Keys.Where(k => k.Contains("anifest", StringComparison.Ordinal)).ToList();
    Console.WriteLine("manifest-like script classes: " + (manifestish.Count > 0 ? string.Join(", ", manifestish) : "(none)"));
    return 0;
}

static int Usage()
{
    Console.WriteLine("Prime Rust Extractor");
    Console.WriteLine("  pre detect                                     list Rust installs");
    Console.WriteLine("  pre catalog [bundle paths...]                  build the object catalog (auto-detects install if no paths)");
    Console.WriteLine("  pre find <query> [--kind item|prefab]          search the catalog");
    Console.WriteLine("           [--category <name>] [--path <substr>] [--limit <n>]");
    Console.WriteLine("  pre stats <bundle-or-folder> [...]             asset type counts");
    return 1;
}

static int Detect()
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

static int Catalog(string[] paths)
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
    var handler = new ExportHandler(new FullConfiguration());
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

static int Find(string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine("usage: pre find <query> [--kind item|prefab] [--category <name>] [--path <substr>] [--limit <n>]");
        return 1;
    }

    var queryParts = new List<string>();
    string? kind = null, category = null, pathContains = null;
    var limit = 25;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--kind": kind = args[++i]; break;
            case "--category": category = args[++i]; break;
            case "--path": pathContains = args[++i]; break;
            case "--limit": limit = int.Parse(args[++i]); break;
            default: queryParts.Add(args[i]); break;
        }
    }

    var catalog = RustCatalog.LoadNewest();
    if (catalog == null)
    {
        Console.WriteLine("no catalog found - run: pre catalog");
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

static int Stats(string[] paths)
{
    if (paths.Length == 0)
    {
        return Usage();
    }
    Logger.Add(new ConsoleLogger(false));
    var handler = new ExportHandler(new FullConfiguration());
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
