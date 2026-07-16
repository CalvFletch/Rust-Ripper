namespace RustRipper.Core;

/// <summary>
/// Layer 2 interpretation table for script components (docs/ARCHITECTURE.md):
/// which serialized fields of which script classes carry exporter-relevant
/// semantics. Class names are type identity. Adding support for a script is
/// adding a row here, never a branch in the scene walk.
/// </summary>
public static class ComponentSemantics
{
    /// <summary>Facepunch LOD components: a serialized state list ordered by
    /// distance, each state naming a renderer and a shadow mode.</summary>
    public sealed record LodStatesSchema(string ClassName, string StatesField, string DistanceField, string RendererField, string ShadowModeField);

    public static readonly LodStatesSchema[] LodStates =
    [
        new("RendererLOD", "States", "distance", "renderer", "shadowMode"),
        new("MeshLOD", "States", "distance", "renderer", "shadowMode"),
        new("SkinnedMeshLOD", "States", "distance", "renderer", "shadowMode"),
    ];

    /// <summary>Components whose fields reference alternate runtime-state
    /// subtrees; the listed fields are the NON-default states (export hidden).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> HiddenStateVariants = new Dictionary<string, string[]>
    {
        ["ModularCarCodeLockVisuals"] = ["unlockedVisuals", "blockedVisuals"],
    };

    /// <summary>Serialized socket lists: attachment transforms other prefabs
    /// mount to (vehicle modules). The transforms are force-kept as named
    /// empties in the export.</summary>
    public sealed record SocketListSchema(string ListField, string TransformField);

    public static readonly SocketListSchema[] SocketLists =
    [
        new("moduleSockets", "socketTransform"),
    ];

    // Runtime paint palettes (ConstructionSkin_ColourLookup) are not resolved
    // through hierarchy components: the material's own _DetailTintMap texture
    // IS the lookup's Sample texture, so the exporter joins on texture
    // identity (see Session.ResolvePalettes).
}
