using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

if (args.Length == 0)
{
    Console.WriteLine("usage:");
    Console.WriteLine("  pre stats <bundle-or-folder> [more paths...]   asset type counts");
    Console.WriteLine("  pre items <bundle-or-folder> [more paths...]   ItemDefinition catalog scan");
    return 1;
}

var command = args[0] is "stats" or "items" ? args[0] : "stats";
var paths = args[0] is "stats" or "items" ? args[1..] : args;

Logger.Add(new ConsoleLogger(false));

var handler = new ExportHandler(new LibraryConfiguration());
var sw = System.Diagnostics.Stopwatch.StartNew();
GameData gameData = handler.LoadAndProcess(paths);
sw.Stop();
Console.WriteLine($"\nloaded in {sw.Elapsed.TotalSeconds:F1}s");

if (command == "stats")
{
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

// items: scan for ItemDefinition MonoBehaviours and read player-facing fields
var found = 0;
foreach (var asset in gameData.GameBundle.FetchAssets())
{
    if (asset is not IMonoBehaviour monoBehaviour)
    {
        continue;
    }
    if (monoBehaviour.ScriptP?.ClassName_R.String != "ItemDefinition")
    {
        continue;
    }

    SerializableStructure? structure = monoBehaviour.LoadStructure();
    if (structure is null)
    {
        continue;
    }

    var shortname = structure.TryGetField("shortname")?.AsString ?? "?";
    var itemid = structure.TryGetField("itemid") is { } id ? id.PValue : 0;
    var english = "?";
    if (structure.TryGetField("displayName") is { } dn && dn.CValue is SerializableStructure phrase)
    {
        // Rust's Translate.Phrase: current builds use "legacyEnglish", older ones "english"
        english = phrase.TryGetField("legacyEnglish")?.AsString
            ?? phrase.TryGetField("english")?.AsString
            ?? "?";
    }

    if (found < 20)
    {
        Console.WriteLine($"{itemid,12}  {shortname,-28}  {english}");
    }
    found++;
}
Console.WriteLine($"=== {found} ItemDefinitions found ===");
return 0;
