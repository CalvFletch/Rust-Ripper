using System.Buffers;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_205;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.MarkerInterfaces;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Material;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace RustRipper.Core;

public record RipperGlbOptions
{
    public int LodLevel { get; init; } = 0;
    public bool AllLods { get; init; } = false;
    public bool IncludeShadowProxies { get; init; } = false;
    public bool PruneEmpties { get; init; } = true;

    /// <summary>Force COLOR_0 into every mesh. By default vertex colors are only
    /// exported when a material opts in via _ApplyVertexColor/_ApplyVertexAlpha —
    /// Rust otherwise uses them for shader masks (wind weights, AO), and glTF
    /// viewers multiplying COLOR_0 into base color would blacken meshes.</summary>
    public bool IncludeVertexColors { get; init; } = false;

    /// <summary>Leave detail paint UNBAKED for node-graph workflows: albedo stays
    /// raw, the _DetailMask textures are written as PNGs next to the GLB, and
    /// _RUST_PAINT carries the colour — the addon builds mask-driven Mix nodes.</summary>
    public bool PaintNodes { get; init; } = false;

    /// <summary>Export Unity Light components as real glTF lights
    /// (KHR_lights_punctual) — Blender imports them as lamps.</summary>
    public bool IncludeLights { get; init; } = true;

    /// <summary>Collapse pass-through empties: a node with no mesh/light and
    /// exactly one kept child contributes nothing but a transform, which gets
    /// folded into that child. Branching nodes and the root survive.</summary>
    public bool CollapseEmptyChains { get; init; } = true;
}

/// <summary>
/// Decision-aware GLB scene builder. Unlike the engine's GlbLevelBuilder it
/// makes keep/skip decisions during the transform walk, from actual component
/// data: LOD membership (Unity LODGroup + Facepunch RendererLOD/MeshLOD
/// states), shadow-proxy detection (ShadowCastingMode), and structural
/// empties pruning. Object names are never consulted.
/// </summary>
public class RipperGlbBuilder
{
    private static readonly string[] FacepunchLodClasses = ["RendererLOD", "MeshLOD", "SkinnedMeshLOD"];

    private readonly RipperGlbOptions options;
    private readonly RipperMaterialFactory materials;
    private readonly Dictionary<IMesh, MeshData> meshCache = new();
    private readonly Dictionary<IRenderer, (int Level, bool ShadowOnly)> lodMembership = new();
    private readonly HashSet<IGameObject> keep = new();
    private readonly HashSet<long> vertexColorTintMaterials = new();

    private RipperGlbBuilder(RipperGlbOptions options)
    {
        this.options = options;
        materials = new RipperMaterialFactory(options.PaintNodes);
    }

    public IReadOnlySet<long> VertexColorTintMaterials => vertexColorTintMaterials;
    public IReadOnlyDictionary<long, System.Numerics.Vector4> DetailPaint => materials.DetailPaint;
    public IReadOnlyDictionary<long, (string MaterialName, AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D Mask)> DetailMasks => materials.DetailMasks;

    public static SceneBuilder Build(IGameObject root, RipperGlbOptions options)
        => Build(root, options, out _);

    public static SceneBuilder Build(IGameObject root, RipperGlbOptions options, out RipperGlbBuilder builder)
    {
        builder = new RipperGlbBuilder(options);
        builder.BuildLodMembership(root);
        builder.BuildKeepSet(root);
        var sceneBuilder = new SceneBuilder();
        builder.AddGameObject(sceneBuilder, null, root.GetTransform());
        return sceneBuilder;
    }

    /// <summary>
    /// The "colour attribute method" for detail-layer paint: primitives whose
    /// material had paint baked also get a flat _RUST_PAINT vertex color
    /// (the authored _DetailColor). Viewers ignore the custom attribute; in
    /// Blender it imports as a color attribute the user can grab or rewire.
    /// </summary>
    public static void AddPaintAttributes(SharpGLTF.Schema2.ModelRoot model, IReadOnlyDictionary<long, System.Numerics.Vector4> detailPaint)
    {
        if (detailPaint.Count == 0)
        {
            return;
        }
        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var pathId = (primitive.Material?.Extras as JsonObject)?["unity_path_id"]?.GetValue<long>();
                if (pathId is not { } id || !detailPaint.TryGetValue(id, out var paint))
                {
                    continue;
                }
                var vertexCount = primitive.GetVertexAccessor("POSITION")?.Count ?? 0;
                if (vertexCount <= 0)
                {
                    continue;
                }
                // Blender color attributes are linear; _DetailColor is authored sRGB
                var linear = new System.Numerics.Vector4(
                    MathF.Pow(paint.X, 2.2f), MathF.Pow(paint.Y, 2.2f), MathF.Pow(paint.Z, 2.2f), paint.W);
                var flat = new System.Numerics.Vector4[vertexCount];
                Array.Fill(flat, linear);
                primitive.WithVertexAccessor("_RUST_PAINT", flat);
            }
        }
    }

    /// <summary>
    /// Vertex colors are always exported so the data survives, but COLOR_0
    /// multiplies into base color per the glTF spec — correct only for
    /// materials that opt in via _ApplyVertexColor. For everything else the
    /// attribute is renamed to _RUST_COLOR: Blender still imports it as a mesh
    /// attribute (masks stay available), viewers stop darkening the shading.
    /// </summary>
    public static void DemoteMaskVertexColors(SharpGLTF.Schema2.ModelRoot model, IReadOnlySet<long> tintMaterials)
    {
        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                if (primitive.GetVertexAccessor("COLOR_0") is not { } colors)
                {
                    continue;
                }
                var pathId = (primitive.Material?.Extras as JsonObject)?["unity_path_id"]?.GetValue<long>();
                if (pathId is { } id && tintMaterials.Contains(id))
                {
                    continue;
                }
                primitive.SetVertexAccessor("COLOR_0", null);
                primitive.SetVertexAccessor("_RUST_COLOR", colors);
            }
        }
    }

    // ---- decision data ----

    /// <summary>
    /// Map every renderer claimed by a LOD system to its level (0 = highest
    /// detail) and whether its state is a shadow-only proxy.
    /// Sources: Unity LODGroup m_LODs, Facepunch RendererLOD/MeshLOD States
    /// (read through the embedded typetree, ordered by state distance).
    /// </summary>
    private void BuildLodMembership(IGameObject root)
    {
        foreach (var extension in root.FetchHierarchy())
        {
            switch (extension)
            {
                case ILODGroup lodGroup:
                {
                    var level = 0;
                    foreach (var lod in lodGroup.LODs)
                    {
                        foreach (var lodRenderer in lod.Renderers)
                        {
                            if (lodRenderer.Renderer.TryGetAsset(lodGroup.Collection, out IRenderer? renderer))
                            {
                                lodMembership.TryAdd(renderer, (level, false));
                            }
                        }
                        level++;
                    }
                    break;
                }
                case IMonoBehaviour monoBehaviour when
                    FacepunchLodClasses.Contains(monoBehaviour.ScriptP?.ClassName_R.String):
                {
                    if (monoBehaviour.LoadStructure() is not { } structure
                        || structure.TryGetField("States") is not { } states)
                    {
                        break;
                    }
                    var parsed = new List<(float Distance, IRenderer Renderer, bool ShadowOnly)>();
                    foreach (var element in states.AsAssetArray)
                    {
                        if (element is not SerializableStructure state)
                        {
                            continue;
                        }
                        var distance = state.TryGetField("distance")?.AsSingle ?? 0f;
                        var shadowOnly = state.TryGetField("shadowMode") is { } sm
                            && (ShadowCastingMode)sm.AsInt32 == ShadowCastingMode.ShadowsOnly;
                        if (state.TryGetField("renderer") is { CValue: AssetRipper.Assets.Metadata.IPPtr pptr }
                            && monoBehaviour.Collection.TryGetAsset(pptr.FileID, pptr.PathID) is IRenderer stateRenderer)
                        {
                            parsed.Add((distance, stateRenderer, shadowOnly));
                        }
                    }
                    var ordered = parsed.OrderBy(p => p.Distance).ToList();
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        lodMembership.TryAdd(ordered[i].Renderer, (i, ordered[i].ShadowOnly));
                    }
                    break;
                }
            }
        }
    }

    private bool RendererAllowed(IRenderer renderer)
    {
        if (!options.IncludeShadowProxies && renderer.GetShadowCastingMode() == ShadowCastingMode.ShadowsOnly)
        {
            return false;
        }
        if (lodMembership.TryGetValue(renderer, out var membership))
        {
            if (membership.ShadowOnly && !options.IncludeShadowProxies)
            {
                return false;
            }
            if (!options.AllLods && membership.Level != options.LodLevel)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>GameObjects worth visiting: subtree contains at least one allowed
    /// renderer that emits geometry. Particle systems and other mesh-less
    /// renderers don't count — keeping them would leave empties in the GLB.</summary>
    private void BuildKeepSet(IGameObject root)
    {
        foreach (var gameObject in root.FetchHierarchy().OfType<IGameObject>())
        {
            if (keep.Contains(gameObject))
            {
                continue;
            }
            if (gameObject.FetchHierarchy().OfType<IGameObject>().Any(Contributes))
            {
                // ancestors are implied: their subtrees contain the same renderer
                keep.Add(gameObject);
            }
        }
    }

    private bool Contributes(IGameObject gameObject)
        => EmitsGeometry(gameObject)
        || (options.IncludeLights && gameObject.TryGetComponent(out ILight? _));

    private bool EmitsGeometry(IGameObject gameObject)
    {
        if (gameObject.TryGetComponent(out IMeshFilter? meshFilter)
            && meshFilter.TryGetMesh(out IMesh? mesh)
            && mesh.IsSet()
            && gameObject.TryGetComponent(out IRenderer? renderer)
            && renderer is not ISkinnedMeshRenderer
            && RendererAllowed(renderer))
        {
            return true;
        }
        return gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinned)
            && skinned.MeshP is IMesh skinnedMesh
            && skinnedMesh.IsSet()
            && RendererAllowed(skinned);
    }

    // ---- the walk ----

    private void AddGameObject(SceneBuilder sceneBuilder, NodeBuilder? parentNode, ITransform transform)
        => AddGameObject(sceneBuilder, parentNode, transform, System.Numerics.Matrix4x4.Identity);

    private void AddGameObject(SceneBuilder sceneBuilder, NodeBuilder? parentNode, ITransform transform, System.Numerics.Matrix4x4 pending)
    {
        var gameObject = transform.GameObject_C4P;
        if (gameObject is null)
        {
            return;
        }
        if (options.PruneEmpties && !keep.Contains(gameObject))
        {
            return;
        }

        // combined local transform including any collapsed ancestors (glTF space)
        var combined = LocalMatrixGltf(transform) * pending;

        if (options.CollapseEmptyChains && parentNode is not null && !Contributes(gameObject))
        {
            var keptChildren = transform.Children_C4P.WhereNotNull()
                .Where(ct => ct.GameObject_C4P is { } childGo && (!options.PruneEmpties || keep.Contains(childGo)))
                .ToList();
            if (keptChildren.Count == 1)
            {
                AddGameObject(sceneBuilder, parentNode, keptChildren[0], combined);
                return;
            }
        }

        var node = parentNode is null ? new NodeBuilder(gameObject.Name) : parentNode.CreateNode(gameObject.Name);
        node.Extras = new JsonObject
        {
            ["unity_game_object"] = gameObject.Name.String,
            ["unity_path_id"] = gameObject.PathID,
            ["unity_collection"] = gameObject.Collection.Name,
        };
        if (parentNode is not null)
        {
            node.LocalMatrix = combined;
        }
        sceneBuilder.AddNode(node);

        // static / dynamic meshes via MeshFilter + (Mesh)Renderer
        if (gameObject.TryGetComponent(out IMeshFilter? meshFilter)
            && meshFilter.TryGetMesh(out IMesh? mesh)
            && mesh.IsSet()
            && gameObject.TryGetComponent(out IRenderer? renderer)
            && renderer is not ISkinnedMeshRenderer
            && RendererAllowed(renderer)
            && TryGetMeshData(mesh, out var meshData))
        {
            AddMesh(sceneBuilder, node, mesh, meshData, renderer);
        }

        // skinned meshes: mesh lives on the renderer itself (engine builder skips these entirely)
        if (gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinned)
            && skinned.MeshP is IMesh skinnedMesh
            && skinnedMesh.IsSet()
            && RendererAllowed(skinned)
            && TryGetMeshData(skinnedMesh, out var skinnedData))
        {
            AddMesh(sceneBuilder, node, skinnedMesh, skinnedData, skinned);
        }

        if (options.IncludeLights && gameObject.TryGetComponent(out ILight? light))
        {
            // leaf light nodes take the -Z flip inline instead of a wrapper child
            var isLeaf = !EmitsGeometry(gameObject)
                && !transform.Children_C4P.WhereNotNull().Any(ct =>
                    ct.GameObject_C4P is { } childGo && (!options.PruneEmpties || keep.Contains(childGo)));
            AddLight(sceneBuilder, node, light, isLeaf);
        }

        foreach (var childTransform in transform.Children_C4P.WhereNotNull())
        {
            AddGameObject(sceneBuilder, node, childTransform);
        }
    }

    /// <summary>Local TRS as a glTF-space matrix (row-vector convention: S*R*T).</summary>
    private static System.Numerics.Matrix4x4 LocalMatrixGltf(ITransform transform)
    {
        var scale = System.Numerics.Matrix4x4.CreateScale(transform.LocalScale_C4.CastToStruct());
        var rotation = System.Numerics.Matrix4x4.CreateFromQuaternion(
            GlbCoordinateConversion.ToGltfQuaternionConvert(transform.LocalRotation_C4));
        var translation = System.Numerics.Matrix4x4.CreateTranslation(
            GlbCoordinateConversion.ToGltfVector3Convert(transform.LocalPosition_C4));
        return scale * rotation * translation;
    }

    /// <summary>
    /// Unity Light -> KHR_lights_punctual. glTF lights emit along the node's
    /// -Z while Unity emits along +Z, so the light hangs off a child node
    /// rotated half a turn. Intensity: Unity's unitless value scaled to
    /// candela (rough visual match; the addon can rescale).
    /// </summary>
    private static void AddLight(SceneBuilder sceneBuilder, NodeBuilder node, ILight light, bool inlineFlip)
    {
        var color = new System.Numerics.Vector3(light.Color.R, light.Color.G, light.Color.B);
        var candela = MathF.Max(light.Intensity, 0.01f) * 100f;
        var outerCone = Math.Clamp(light.SpotAngle * MathF.PI / 360f, 0.01f, MathF.PI / 2f - 0.001f);
        var innerCone = Math.Clamp(light.InnerSpotAngle * MathF.PI / 360f, 0f, outerCone - 0.001f);
        LightBuilder? lightBuilder = light.Type switch
        {
            2 => new LightBuilder.Point { Color = color, Intensity = candela, Range = light.Range },
            0 => new LightBuilder.Spot
            {
                Color = color,
                Intensity = candela,
                Range = light.Range,
                OuterConeAngle = outerCone,
                InnerConeAngle = innerCone,
            },
            1 => new LightBuilder.Directional { Color = color, Intensity = MathF.Max(light.Intensity, 0.01f) },
            _ => null,
        };
        if (lightBuilder is null)
        {
            return;
        }
        var flip = System.Numerics.Matrix4x4.CreateFromQuaternion(
            System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI));
        if (inlineFlip)
        {
            lightBuilder.Name = node.Name;
            node.LocalMatrix = flip * node.LocalMatrix;
            sceneBuilder.AddLight(lightBuilder, node);
            return;
        }
        lightBuilder.Name = node.Name + "_light";
        var lightNode = node.CreateNode(lightBuilder.Name);
        lightNode.LocalMatrix = flip;
        sceneBuilder.AddLight(lightBuilder, lightNode);
    }

    private bool TryGetMeshData(IMesh mesh, out MeshData meshData)
    {
        if (meshCache.TryGetValue(mesh, out meshData))
        {
            return true;
        }
        if (MeshData.TryMakeFromMesh(mesh, out meshData))
        {
            meshCache.Add(mesh, meshData);
            return true;
        }
        return false;
    }

    private void AddMesh(SceneBuilder sceneBuilder, NodeBuilder node, IMesh mesh, MeshData meshData, IRenderer renderer)
    {
        var subMeshes = mesh.SubMeshes;
        int[] subsetIndices;
        if (renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count > 0)
        {
            subsetIndices = renderer.SubsetIndices_C25.Select(i => (int)i).ToArray();
        }
        else if (renderer.Has_StaticBatchInfo_C25() && renderer.StaticBatchInfo_C25.SubMeshCount > 0)
        {
            subsetIndices = Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
        }
        else
        {
            subsetIndices = Enumerable.Range(0, subMeshes.Count).ToArray();
        }

        var pairs = new (ISubMesh, MaterialBuilder)[subsetIndices.Length];
        for (var i = 0; i < subsetIndices.Length; i++)
        {
            var material = i < renderer.Materials_C25.Count
                && renderer.Materials_C25[i].TryGetAsset(renderer.Collection, out AssetRipper.SourceGenerated.Classes.ClassID_21.IMaterial? m)
                ? m : null;
            if (material is not null
                && ((RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexColor", out var avc) && avc != 0f)
                    || (RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexAlpha", out var ava) && ava != 0f)))
            {
                vertexColorTintMaterials.Add(material.PathID);
            }
            pairs[i] = (subMeshes[subsetIndices[i]], materials.GetOrMake(material));
        }
        IMeshBuilder<MaterialBuilder> meshBuilder = GlbSubMeshBuilder.BuildSubMeshes(
            new ArraySegment<(ISubMesh, MaterialBuilder)>(pairs), mesh.Is16BitIndices(), meshData,
            Transformation.Identity, Transformation.Identity);
        if (meshBuilder is SharpGLTF.BaseBuilder baseBuilder)
        {
            baseBuilder.Name = mesh.Name.String;
            baseBuilder.Extras = new JsonObject
            {
                ["unity_mesh"] = mesh.Name.String,
                ["unity_path_id"] = mesh.PathID,
                ["unity_collection"] = mesh.Collection.Name,
            };
        }
        sceneBuilder.AddRigidMesh(meshBuilder, node);
    }
}
