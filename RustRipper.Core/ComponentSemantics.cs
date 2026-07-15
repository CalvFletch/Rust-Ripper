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

    /// <summary>Components that select a runtime paint colour from a palette
    /// ScriptableObject (colour index chosen per-instance by the game).
    /// The palette's colours are exported as one vertex-colour attribute per
    /// entry (_RUST_PAINT_01..) on runtime-tinted primitives.</summary>
    public sealed record PaletteSourceSchema(string ClassName, string LookupField, string ColoursField);

    public static readonly PaletteSourceSchema[] PaletteSources =
    [
        // ConstructionSkin_CustomDetail : ConstructionSkin { ConstructionSkin_ColourLookup ColourLookup }
        new("ConstructionSkin_CustomDetail", "ColourLookup", "AllColours"),
    ];
}
