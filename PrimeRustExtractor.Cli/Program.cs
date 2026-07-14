using System.Net;
using System.Text;
using System.Text.Json;
using AssetRipper.Assets;
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
using PrimeRustExtractor.Core;

return args.FirstOrDefault() switch
{
    "detect" => Cli.Detect(),
    "catalog" => Cli.Catalog(args[1..]),
    "find" => Cli.Find(args[1..]),
    "export" => Cli.Export(args[1..]),
    "mat" => Cli.Mat(args[1..]),
    "serve" => Cli.Serve(args[1..]),
    "stats" => Cli.Stats(args[1..]),
    _ => Cli.Usage(),
};

internal static class Cli
{
    public static int Usage()
    {
        Console.WriteLine("Prime Rust Extractor");
        Console.WriteLine("  pre detect                                    list Rust installs");
        Console.WriteLine("  pre catalog [bundle paths...]                 build the object catalog");
        Console.WriteLine("  pre find <query> [--kind item|prefab] [--category <n>] [--path <s>] [--limit <n>]");
        Console.WriteLine("  pre export <query> [--out <dir>] [--bundles <extra.bundle>]...");
        Console.WriteLine("  pre mat <query> [--bundles <extra.bundle>]   per-material shader + properties");
        Console.WriteLine("  pre serve [--port <n>] [--bundles <extra>]   resident daemon: load once, export instantly");
        Console.WriteLine("  pre stats <bundle-or-folder> [...]           asset type counts");
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

    public static int Find(string[] args)
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

    public static int Export(string[] args)
    {
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
        if (queryParts.Count == 0)
        {
            Console.WriteLine("usage: pre export <query> [--out <dir>] [--bundles <extra.bundle>]...");
            return 1;
        }

        var session = Session.Start(extraBundles);
        if (session == null)
        {
            return 1;
        }
        var result = session.ExportGlb(string.Join(' ', queryParts), outDir);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
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
            Console.WriteLine("usage: pre mat <query> [--bundles <extra.bundle>]");
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
        Console.WriteLine($"PRIME daemon listening on http://127.0.0.1:{port}/  (find /find?q=..., export /export?q=..., materials /mat?q=..., /status)");

        while (true)
        {
            var context = listener.GetContext();
            try
            {
                HandleRequest(session, context);
            }
            catch (Exception ex)
            {
                WriteJson(context, 500, new { error = ex.Message });
            }
        }
    }

    private static void HandleRequest(Session session, HttpListenerContext context)
    {
        var request = context.Request;
        var q = request.QueryString["q"] ?? "";
        switch (request.Url?.AbsolutePath)
        {
            case "/status":
                WriteJson(context, 200, new { build = session.Catalog.BuildId, entries = session.Catalog.Entries.Count, uptimeSeconds = (int)session.Uptime.TotalSeconds });
                break;
            case "/find":
                var limit = int.TryParse(request.QueryString["limit"], out var l) ? l : 25;
                var results = session.Catalog.Find(q, request.QueryString["kind"], request.QueryString["category"], request.QueryString["path"]).Take(limit).ToList();
                WriteJson(context, 200, results);
                break;
            case "/export":
                var outDir = request.QueryString["out"] ?? "export";
                var result = session.ExportGlb(q, outDir);
                WriteJson(context, result.Success ? 200 : 404, new { success = result.Success, message = result.Message, path = result.Path, seconds = result.Seconds });
                break;
            case "/mat":
                var report = session.MaterialReport(q);
                WriteJson(context, report != null ? 200 : 404, new { report });
                break;
            default:
                WriteJson(context, 404, new { error = "unknown endpoint" });
                break;
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
}

/// <summary>
/// A loaded game session: bundles parsed once, then queried/exported repeatedly.
/// This is the heart of daemon mode and the future UI backend.
/// </summary>
internal sealed class Session
{
    public required GameData GameData { get; init; }
    public required RustCatalog Catalog { get; init; }
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Uptime => clock.Elapsed;

    public static Session? Start(List<string> extraBundles)
    {
        var catalog = RustCatalog.LoadNewest();
        if (catalog == null)
        {
            Console.WriteLine("no catalog found - run: pre catalog");
            return null;
        }
        var install = RustLocator.GetInstalls().FirstOrDefault();
        if (install == null)
        {
            Console.WriteLine("no Rust install detected");
            return null;
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
        Console.WriteLine($"session loaded in {sw.Elapsed.TotalSeconds:F1}s ({loadPaths.Count} bundles)");
        return new Session { GameData = gameData, Catalog = catalog };
    }

    public CatalogEntry? Resolve(string query)
    {
        var matches = Catalog.Find(query).Where(e => e.PrefabPath.Length > 0).ToList();
        return matches.FirstOrDefault(e => e.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault(e => e.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    public (IEnumerable<IUnityObjectBase> Assets, string Name)? ResolveExportSet(string query)
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
            return (hierarchy.Assets, targetName);
        }

        // Roots in the AssetScene-prefabs scene are named by their full prefab path.
        var gameObjects = GameData.GameBundle.FetchAssets().OfType<IGameObject>();
        var root = gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(entry.PrefabPath, StringComparison.OrdinalIgnoreCase))
            ?? gameObjects.FirstOrDefault(go => (go.Name.String ?? "").Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            return (root.FetchHierarchy().Cast<IUnityObjectBase>(), targetName);
        }
        return null;
    }

    public (bool Success, string Message, string? Path, double Seconds) ExportGlb(string query, string outDir)
    {
        var resolved = ResolveExportSet(query);
        if (resolved == null)
        {
            return (false, $"nothing matching '{query}' found", null, 0);
        }
        Directory.CreateDirectory(outDir);
        var outPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(outDir, $"{resolved.Value.Name}.glb"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = AssetRipper.Export.PrimaryContent.Models.GlbModelExporter.ExportModel(
            resolved.Value.Assets, outPath, false, LocalFileSystem.Instance);
        sw.Stop();
        return ok && File.Exists(outPath)
            ? (true, $"exported in {sw.Elapsed.TotalSeconds:F1}s: {outPath} ({new FileInfo(outPath).Length / 1024} KB)", outPath, sw.Elapsed.TotalSeconds)
            : (false, "export failed", null, sw.Elapsed.TotalSeconds);
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
