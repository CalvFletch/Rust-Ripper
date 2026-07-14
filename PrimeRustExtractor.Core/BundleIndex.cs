using System.Text.Json;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;

namespace PrimeRustExtractor.Core;

/// <summary>
/// Header-level index of Rust's bundles: which CAB lives in which bundle, and
/// each CAB's ordered dependency list (FileID N resolves to Dependencies[N-1]).
/// Both are cached on disk keyed by Steam build id.
/// </summary>
public class BundleIndex
{
    public string BuildId { get; set; } = "";
    public Dictionary<string, string> CabToBundle { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> CabDependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static BundleIndex Build(string bundlesPath, string buildId)
    {
        var index = new BundleIndex { BuildId = buildId };
        foreach (var bundlePath in Directory.EnumerateFiles(bundlesPath, "*.bundle", SearchOption.AllDirectories))
        {
            if (bundlePath.Contains($"_unpacked{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                using var stream = SmartStream.OpenRead(bundlePath, LocalFileSystem.Instance);
                if (SchemeReader.ReadFile(stream, bundlePath, Path.GetFileName(bundlePath)) is FileStreamBundleFile bundle)
                {
                    foreach (var node in bundle.DirectoryInfo.Nodes)
                    {
                        index.CabToBundle[node.PathFixed] = bundlePath;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[index] skipping {Path.GetFileName(bundlePath)}: {ex.Message}");
            }
        }
        return index;
    }

    /// <summary>
    /// Ordered dependency names of a CAB (PPtr FileID N maps to result[N-1]).
    /// Requires decompressing the owning bundle once; the result is cached.
    /// </summary>
    public string[] GetDependencies(string cabName)
    {
        if (CabDependencies.TryGetValue(cabName, out var cached))
        {
            return cached;
        }
        if (!CabToBundle.TryGetValue(cabName, out var bundlePath))
        {
            return [];
        }

        using var stream = SmartStream.OpenRead(bundlePath, LocalFileSystem.Instance);
        var file = SchemeReader.ReadFile(stream, bundlePath, Path.GetFileName(bundlePath));
        if (file is FileContainer container)
        {
            container.ReadContentsRecursively();
            foreach (var serializedFile in container.FetchSerializedFiles())
            {
                var deps = serializedFile.Dependencies.ToArray().Select(d => d.GetFilePath()).ToArray();
                CabDependencies[serializedFile.NameFixed] = deps;
            }
        }
        Save();
        return CabDependencies.TryGetValue(cabName, out var result) ? result : [];
    }

    // ---- persistence ----

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeRustExtractor", "bundleindex");

    public static string CachePath(string buildId) => Path.Combine(CacheDir, $"{buildId}.json");

    public void Save()
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(CachePath(BuildId), JsonSerializer.Serialize(this));
    }

    public static BundleIndex? Load(string buildId)
    {
        var path = CachePath(buildId);
        return File.Exists(path) ? JsonSerializer.Deserialize<BundleIndex>(File.ReadAllText(path)) : null;
    }

    public static BundleIndex LoadOrBuild(string bundlesPath, string buildId)
    {
        var index = Load(buildId);
        if (index == null)
        {
            index = Build(bundlesPath, buildId);
            index.Save();
        }
        return index;
    }
}
