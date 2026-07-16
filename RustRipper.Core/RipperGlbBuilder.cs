using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Numerics;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimationClips;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_111;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_205;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
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

    /// <summary>Dissolve every empty except the root: a node with no mesh and
    /// no light contributes nothing but a transform, which is folded into its
    /// children — they re-parent to the nearest content node (mesh) or the
    /// root, matching how an artist would clean the hierarchy.</summary>
    public bool CollapseEmptyChains { get; init; } = true;

    /// <summary>Flag placeholder-material geometry (IO wiring origins etc.) as
    /// unity_hidden so it imports hidden. Off keeps it visible; disabled
    /// renderers and inactive objects are always flagged.</summary>
    public bool HideUtility { get; init; } = true;

    /// <summary>GameObject PathIDs kept even without content (e.g. vehicle
    /// module socket transforms read from the chassis data).</summary>
    public IReadOnlySet<long> ForceKeepPathIds { get; init; } = new HashSet<long>();
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
    public IReadOnlySet<long> RuntimeTintMaterials => materials.RuntimeTintMaterials;
    public IReadOnlyList<AssetRipper.SourceGenerated.Classes.ClassID_21.IMaterial> RuntimeTintMaterialAssets => materials.RuntimeTintMaterialAssets;
    public IReadOnlyDictionary<long, (string MaterialName, AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D Mask)> DetailMasks => materials.DetailMasks;

    public static SceneBuilder Build(IGameObject root, RipperGlbOptions options)
        => Build(root, options, null, out _);

    public static SceneBuilder Build(IGameObject root, RipperGlbOptions options, out RipperGlbBuilder builder)
        => Build(root, options, null, out builder);

    public static SceneBuilder Build(IGameObject root, RipperGlbOptions options, GameData? gameData, out RipperGlbBuilder builder)
    {
        builder = new RipperGlbBuilder(options);
        builder.BuildLodMembership(root);
        builder.BuildNameConventionLodMembership(root);
        builder.BuildHiddenStateVariants(root);
        builder.BuildSocketForceKeep(root);
        builder.BuildBoneKeep(root);
        builder.PrepareAnimations(root, gameData);
        builder.BuildKeepSet(root);
        var sceneBuilder = new SceneBuilder();
        builder.AddGameObject(sceneBuilder, null, root.GetTransform());
        builder.FinishSkins(sceneBuilder);
        builder.EmitAnimations();
        return sceneBuilder;
    }

    /// <summary>
    /// Palette attributes for runtime-tinted primitives: one flat vertex-colour
    /// attribute per palette entry (_RUST_PAINT_01..), colours straight from
    /// the game's ColourLookup asset (sRGB authored -> linear attributes).
    /// Consumers select the paint by selecting the attribute; the game's own
    /// index (customColour, 1-based) matches the attribute number.
    /// </summary>
    public static void AddPaletteAttributes(SharpGLTF.Schema2.ModelRoot model,
        IReadOnlySet<long> runtimeTintMaterials, IReadOnlyList<System.Numerics.Vector4> palette)
    {
        if (palette.Count == 0 || runtimeTintMaterials.Count == 0)
        {
            return;
        }
        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var pathId = (primitive.Material?.Extras as JsonObject)?["unity_path_id"]?.GetValue<long>();
                if (pathId is not { } id || !runtimeTintMaterials.Contains(id))
                {
                    continue;
                }
                var vertexCount = primitive.GetVertexAccessor("POSITION")?.Count ?? 0;
                if (vertexCount <= 0)
                {
                    continue;
                }
                for (var i = 0; i < palette.Count; i++)
                {
                    var c = palette[i];
                    var linear = new System.Numerics.Vector4(
                        MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);
                    var flat = new System.Numerics.Vector4[vertexCount];
                    Array.Fill(flat, linear);
                    // named after the game's own field: customColour is the 1-based palette index
                    primitive.WithVertexAccessor($"_RUST_CUSTOMCOLOUR_{i + 1:00}", flat);
                }
            }
        }
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
                primitive.WithVertexAccessor("_RUST_DETAILCOLOR", flat);
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
                    ComponentSemantics.LodStates.FirstOrDefault(s => s.ClassName == monoBehaviour.ScriptP?.ClassName_R.String) is { } schema:
                {
                    if (monoBehaviour.LoadStructure() is not { } structure
                        || structure.TryGetField(schema.StatesField) is not { } states)
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
                        var distance = state.TryGetField(schema.DistanceField)?.AsSingle ?? 0f;
                        var shadowOnly = state.TryGetField(schema.ShadowModeField) is { } sm
                            && (ShadowCastingMode)sm.AsInt32 == ShadowCastingMode.ShadowsOnly;
                        if (state.TryGetField(schema.RendererField) is { CValue: AssetRipper.Assets.Metadata.IPPtr pptr }
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

    /// <summary>
    /// Narrow, user-approved exception for vehicle modules: their body LOD
    /// meshes are claimed by NO component (verified exhaustively - the client
    /// switches them by child-name convention in code stripped from the data).
    /// The gate: sibling GameObjects under one parent whose names match
    /// exactly "LOD&lt;n&gt;&lt;same suffix&gt;", at least two levels including 0,
    /// and only renderers no LOD component has already claimed.
    /// </summary>
    private void BuildNameConventionLodMembership(IGameObject root)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"^LOD(\d+)(.*)$");
        var groups = new Dictionary<string, List<(int Level, IRenderer Renderer)>>();
        foreach (var gameObject in root.FetchHierarchy().OfType<IGameObject>())
        {
            var match = pattern.Match(gameObject.Name.String);
            if (!match.Success || !gameObject.TryGetComponent(out IRenderer? renderer))
            {
                continue;
            }
            var suffix = match.Groups[2].Value;
            if (!groups.TryGetValue(suffix, out var list))
            {
                groups[suffix] = list = new List<(int, IRenderer)>();
            }
            list.Add((int.Parse(match.Groups[1].Value), renderer));
        }
        foreach (var (_, set) in groups)
        {
            if (set.Count < 2 || !set.Any(e => e.Level == 0)
                || set.Any(e => lodMembership.ContainsKey(e.Renderer)))
            {
                continue;
            }
            foreach (var (level, renderer) in set)
            {
                lodMembership.TryAdd(renderer, (level, false));
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

    private readonly HashSet<long> socketKeep = new();
    private readonly HashSet<long> boneKeep = new();
    private readonly HashSet<long> animatedKeep = new();
    private readonly Dictionary<long, NodeBuilder> nodeByGameObject = new();
    private readonly List<(IMeshBuilder<MaterialBuilder> Mesh, NodeBuilder Node, ISkinnedMeshRenderer Renderer, MeshData Data)> pendingSkins = new();

    private bool Contributes(IGameObject gameObject)
        => EmitsGeometry(gameObject)
        || (options.IncludeLights && gameObject.TryGetComponent(out ILight? _))
        || options.ForceKeepPathIds.Contains(gameObject.PathID)
        || socketKeep.Contains(gameObject.PathID)
        || boneKeep.Contains(gameObject.PathID)
        || animatedKeep.Contains(gameObject.PathID);

    /// <summary>Bones referenced by any skinned mesh keep their transforms:
    /// they are the glTF joints, so they are exempt from pruning and from
    /// empty-chain collapse (a folded bone would corrupt the skin).</summary>
    private void BuildBoneKeep(IGameObject root)
    {
        foreach (var gameObject in root.FetchHierarchy().OfType<IGameObject>())
        {
            if (!gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinned)
                || skinned.MeshP is not IMesh mesh
                || !mesh.IsSet())
            {
                continue;
            }
            foreach (var bone in skinned.BonesP.WhereNotNull())
            {
                if (bone.GameObject_C4P is { } boneGo)
                {
                    boneKeep.Add(boneGo.PathID);
                    // animation plays in each bone's original local space:
                    // no ancestor may be dissolved into it
                    KeepAncestors(boneGo, boneKeep);
                }
            }
            if (skinned.RootBone.TryGetAsset(skinned.Collection) is ITransform rootBone
                && rootBone.GameObject_C4P is { } rootBoneGo)
            {
                boneKeep.Add(rootBoneGo.PathID);
            }
        }
    }

    // ---- animations ----

    private readonly List<(IAnimationClip Clip, Dictionary<string, IGameObject> PathMap)> pendingAnimations = new();

    /// <summary>Clips already decoded from their binary muscle data - the
    /// converter appends to the clip's curve lists, so a daemon session must
    /// never process the same clip twice.</summary>
    private static readonly HashSet<(string Collection, long PathID)> processedClips = new();

    /// <summary>
    /// Find every AnimationClip reachable from Animator/Animation components,
    /// decode binary clips into editor curves (engine converter + CRC path
    /// recovery), and force-keep the animated transform paths so pruning and
    /// empty-collapse cannot eat the animation targets.
    /// </summary>
    private void PrepareAnimations(IGameObject root, GameData? gameData)
    {
        PathChecksumCache? cache = null;
        foreach (var gameObject in root.FetchHierarchy().OfType<IGameObject>())
        {
            var clips = new List<IAnimationClip>();
            if (gameObject.TryGetComponent(out IAnimator? animator) && animator is not null)
            {
                clips.AddRange(AnimatorClips(animator));
            }
            if (gameObject.TryGetComponent(out IAnimation? animation) && animation is not null)
            {
                foreach (var clipPtr in animation.Animations)
                {
                    if (clipPtr.TryGetAsset(animation.Collection) is IAnimationClip legacyClip)
                    {
                        clips.Add(legacyClip);
                    }
                }
            }
            if (clips.Count == 0)
            {
                continue;
            }
            var pathMap = new Dictionary<string, IGameObject>();
            BuildPathMap(gameObject, "", pathMap);
            foreach (var clip in clips.Distinct())
            {
                if (clip.Has_ClipBindingConstant_C74()
                    && processedClips.Add((clip.Collection.Name, clip.PathID))
                    && gameData is not null)
                {
                    cache ??= new PathChecksumCache(gameData);
                    try
                    {
                        AnimationClipConverter.Process(clip, cache.Value);
                    }
                    catch
                    {
                        // undecodable clip: skip, everything else still exports
                        continue;
                    }
                }
                pendingAnimations.Add((clip, pathMap));
                foreach (var path in ClipPaths(clip))
                {
                    if (pathMap.TryGetValue(path, out var target))
                    {
                        animatedKeep.Add(target.PathID);
                        // clip values assume the ORIGINAL local space: if an
                        // ancestor were dissolved, its matrix would fold into
                        // this node's static transform and every keyframe
                        // would play offset - keep the whole chain
                        KeepAncestors(target, animatedKeep);
                    }
                }
            }
        }
    }

    private static IEnumerable<IAnimationClip> AnimatorClips(IAnimator animator)
    {
        AssetRipper.SourceGenerated.Classes.ClassID_93.IRuntimeAnimatorController? controller = null;
        try
        {
            if (animator.Has_Controller_PPtr_RuntimeAnimatorController_5())
            {
                controller = animator.Controller_PPtr_RuntimeAnimatorController_5P;
            }
            else if (animator.Has_Controller_PPtr_RuntimeAnimatorController_4_3())
            {
                controller = animator.Controller_PPtr_RuntimeAnimatorController_4_3P;
            }
            else if (animator.Has_Controller_PPtr_AnimatorController_4())
            {
                controller = (AssetRipper.SourceGenerated.Classes.ClassID_93.IRuntimeAnimatorController?)animator.Controller_PPtr_AnimatorController_4P;
            }
        }
        catch
        {
        }
        return ControllerClips(controller);
    }

    private static IEnumerable<IAnimationClip> ControllerClips(
        AssetRipper.SourceGenerated.Classes.ClassID_93.IRuntimeAnimatorController? controller)
    {
        if (controller is IAnimatorController direct)
        {
            foreach (var clipPtr in direct.AnimationClips)
            {
                if (clipPtr.TryGetAsset(direct.Collection) is IAnimationClip clip)
                {
                    yield return clip;
                }
            }
        }
        else if (controller is AssetRipper.SourceGenerated.Classes.ClassID_221.IAnimatorOverrideController overrides)
        {
            // per-species clip overrides over a shared base controller
            // (the new-generation animals): the override clips are the real
            // animations; base clips fill states the species left alone
            var replacedOriginals = new HashSet<IAnimationClip>();
            foreach (var pair in overrides.Clips)
            {
                if (pair.OriginalClip.TryGetAsset(overrides.Collection) is IAnimationClip original)
                {
                    replacedOriginals.Add(original);
                }
                if (pair.OverrideClip.TryGetAsset(overrides.Collection) is IAnimationClip overrideClip)
                {
                    yield return overrideClip;
                }
            }
            if (overrides.ControllerP is { } baseController)
            {
                foreach (var clip in ControllerClips(baseController))
                {
                    if (!replacedOriginals.Contains(clip))
                    {
                        yield return clip;
                    }
                }
            }
        }
    }

    private static void KeepAncestors(IGameObject gameObject, HashSet<long> keepSet)
    {
        var transform = gameObject.GetTransform().Father_C4P;
        while (transform is not null)
        {
            if (transform.GameObject_C4P is { } ancestor && !keepSet.Add(ancestor.PathID))
            {
                break;
            }
            transform = transform.Father_C4P;
        }
    }

    private static void BuildPathMap(IGameObject parent, string parentPath, Dictionary<string, IGameObject> map)
    {
        map[parentPath] = parent;
        foreach (var childTransform in parent.GetTransform().Children_C4P.WhereNotNull())
        {
            if (childTransform.GameObject_C4P is not { } child)
            {
                continue;
            }
            var path = parentPath.Length == 0 ? child.Name.String : $"{parentPath}/{child.Name.String}";
            BuildPathMap(child, path, map);
        }
    }

    private static IEnumerable<string> ClipPaths(IAnimationClip clip)
    {
        foreach (var curve in clip.RotationCurves_C74)
        {
            yield return curve.Path.String;
        }
        foreach (var curve in clip.PositionCurves_C74)
        {
            yield return curve.Path.String;
        }
        foreach (var curve in clip.ScaleCurves_C74)
        {
            yield return curve.Path.String;
        }
        if (clip.Has_EulerCurves_C74())
        {
            foreach (var curve in clip.EulerCurves_C74)
            {
                yield return curve.Path.String;
            }
        }
    }

    /// <summary>
    /// Emit decoded curves as glTF animation channels, one glTF animation per
    /// clip name. Keyframes convert with the same handedness rules as static
    /// transforms; Hermite slopes flatten to linear keys (exact at the keys).
    /// Humanoid muscle channels have no transform paths and fall out
    /// naturally; unresolvable paths are skipped.
    /// </summary>
    private void EmitAnimations()
    {
        foreach (var (clip, pathMap) in pendingAnimations)
        {
            var track = clip.Name.String;
            foreach (var curve in clip.RotationCurves_C74)
            {
                if (!TryGetAnimatedNode(pathMap, curve.Path.String, out var node))
                {
                    continue;
                }
                var rotation = node.UseRotation(track);
                foreach (var key in curve.Curve.Curve)
                {
                    var q = new System.Numerics.Quaternion(key.Value.X, key.Value.Y, key.Value.Z, key.Value.W);
                    rotation.WithPoint(key.Time, System.Numerics.Quaternion.Normalize(GlbCoordinateConversion.ToGltfQuaternionConvert(q)));
                }
            }
            if (clip.Has_EulerCurves_C74())
            {
                foreach (var curve in clip.EulerCurves_C74)
                {
                    if (!TryGetAnimatedNode(pathMap, curve.Path.String, out var node))
                    {
                        continue;
                    }
                    var rotation = node.UseRotation(track);
                    foreach (var key in curve.Curve.Curve)
                    {
                        // Unity euler order: Z, then X, then Y
                        var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(
                            key.Value.Y * (MathF.PI / 180f), key.Value.X * (MathF.PI / 180f), key.Value.Z * (MathF.PI / 180f));
                        rotation.WithPoint(key.Time, System.Numerics.Quaternion.Normalize(GlbCoordinateConversion.ToGltfQuaternionConvert(q)));
                    }
                }
            }
            foreach (var curve in clip.PositionCurves_C74)
            {
                if (!TryGetAnimatedNode(pathMap, curve.Path.String, out var node))
                {
                    continue;
                }
                var translation = node.UseTranslation(track);
                foreach (var key in curve.Curve.Curve)
                {
                    translation.WithPoint(key.Time, GlbCoordinateConversion.ToGltfVector3Convert(
                        new System.Numerics.Vector3(key.Value.X, key.Value.Y, key.Value.Z)));
                }
            }
            foreach (var curve in clip.ScaleCurves_C74)
            {
                if (!TryGetAnimatedNode(pathMap, curve.Path.String, out var node))
                {
                    continue;
                }
                var scale = node.UseScale(track);
                foreach (var key in curve.Curve.Curve)
                {
                    scale.WithPoint(key.Time, new System.Numerics.Vector3(key.Value.X, key.Value.Y, key.Value.Z));
                }
            }
        }
    }

    private bool TryGetAnimatedNode(Dictionary<string, IGameObject> pathMap, string path, [NotNullWhen(true)] out NodeBuilder? node)
    {
        node = null;
        return pathMap.TryGetValue(path, out var gameObject)
            && nodeByGameObject.TryGetValue(gameObject.PathID, out node);
    }

    /// <summary>Bind stashed skinned meshes once the whole tree (and thus
    /// every joint node) exists. The prefab is in bind pose, so inverse bind
    /// matrices derive from the joints' current world transforms - no matrix
    /// convention gymnastics, and exact for shipped prefabs. Falls back to a
    /// rigid attach when a joint is unresolvable.</summary>
    private void FinishSkins(SceneBuilder sceneBuilder)
    {
        foreach (var (meshBuilder, node, skinned, meshData) in pendingSkins)
        {
            var bonePtrs = skinned.BonesP.ToArray();
            var joints = new NodeBuilder[bonePtrs.Length];
            var resolved = true;
            for (var i = 0; i < bonePtrs.Length; i++)
            {
                if (bonePtrs[i]?.GameObject_C4P is { } boneGo
                    && nodeByGameObject.TryGetValue(boneGo.PathID, out var jointNode))
                {
                    joints[i] = jointNode;
                }
                else
                {
                    resolved = false;
                    break;
                }
            }
            var maxIndex = -1;
            if (meshData.Skin is { } skin)
            {
                foreach (var w in skin)
                {
                    maxIndex = Math.Max(maxIndex, Math.Max(Math.Max(w.Index0, w.Index1), Math.Max(w.Index2, w.Index3)));
                }
            }
            if (!resolved || joints.Length == 0 || maxIndex >= joints.Length)
            {
                sceneBuilder.AddRigidMesh(meshBuilder, node);
                continue;
            }
            try
            {
                sceneBuilder.AddSkinnedMesh(meshBuilder, node.WorldMatrix, joints);
            }
            catch (ArgumentException)
            {
                // SharpGLTF rejects some joint sets (duplicate bone entries,
                // joints outside the scene graph) - attach rigid, as before
                sceneBuilder.AddRigidMesh(meshBuilder, node);
            }
        }
    }

    /// <summary>Force-keep attachment transforms declared by serialized socket
    /// lists (ComponentSemantics.SocketLists) as named empties.</summary>
    private void BuildSocketForceKeep(IGameObject root)
    {
        foreach (var monoBehaviour in root.FetchHierarchy().OfType<IMonoBehaviour>())
        {
            if (monoBehaviour.LoadStructure() is not { } structure)
            {
                continue;
            }
            foreach (var schema in ComponentSemantics.SocketLists)
            {
                if (structure.TryGetField(schema.ListField) is not { } list)
                {
                    continue;
                }
                foreach (var element in list.AsAssetArray)
                {
                    if (element is SerializableStructure socket
                        && socket.TryGetField(schema.TransformField) is { CValue: AssetRipper.Assets.Metadata.IPPtr pptr }
                        && monoBehaviour.Collection.TryGetAsset(pptr.FileID, pptr.PathID) is ITransform socketTransform
                        && socketTransform.GameObject_C4P is { } socketGo)
                    {
                        socketKeep.Add(socketGo.PathID);
                    }
                }
            }
        }
    }

    /// <summary>
    /// State-variant components declare which child subtrees are alternate
    /// runtime states (locked/unlocked/blocked keypads...). The non-default
    /// states export hidden - present, togglable, not cluttering the view.
    /// Read from the components' own serialized fields, per class
    /// (ComponentSemantics.HiddenStateVariants).
    /// </summary>
    private readonly HashSet<long> hiddenStateRoots = new();

    private void BuildHiddenStateVariants(IGameObject root)
    {
        foreach (var monoBehaviour in root.FetchHierarchy().OfType<IMonoBehaviour>())
        {
            if (monoBehaviour.ScriptP?.ClassName_R.String is not { } className
                || !ComponentSemantics.HiddenStateVariants.TryGetValue(className, out var fields)
                || monoBehaviour.LoadStructure() is not { } structure)
            {
                continue;
            }
            foreach (var fieldName in fields)
            {
                if (structure.TryGetField(fieldName) is { CValue: AssetRipper.Assets.Metadata.IPPtr pptr }
                    && monoBehaviour.Collection.TryGetAsset(pptr.FileID, pptr.PathID) is IGameObject target)
                {
                    hiddenStateRoots.Add(target.PathID);
                }
            }
        }
    }

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
        => AddGameObject(sceneBuilder, parentNode, transform, System.Numerics.Matrix4x4.Identity, false);

    private void AddGameObject(SceneBuilder sceneBuilder, NodeBuilder? parentNode, ITransform transform, System.Numerics.Matrix4x4 pending, bool hiddenState)
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
        hiddenState = hiddenState || hiddenStateRoots.Contains(gameObject.PathID);

        // combined local transform including any collapsed ancestors (glTF space)
        var combined = LocalMatrixGltf(transform) * pending;

        if (options.CollapseEmptyChains && parentNode is not null && !Contributes(gameObject))
        {
            foreach (var childTransform in transform.Children_C4P.WhereNotNull())
            {
                AddGameObject(sceneBuilder, parentNode, childTransform, combined, hiddenState);
            }
            return;
        }

        // roots carry the full prefab path in their name; the node gets the
        // short prefab name, the path rides in extras
        var nodeName = parentNode is null
            ? System.IO.Path.GetFileNameWithoutExtension(gameObject.Name.String)
            : gameObject.Name.String;
        var node = parentNode is null ? new NodeBuilder(nodeName) : parentNode.CreateNode(nodeName);
        node.Extras = new JsonObject
        {
            ["unity_game_object"] = gameObject.Name.String,
            ["unity_path_id"] = gameObject.PathID,
            ["unity_collection"] = gameObject.Collection.Name,
            ["unity_layer"] = gameObject.Layer,
        };
        if (parentNode is null)
        {
            node.Extras["unity_prefab_path"] = gameObject.Name.String;
        }
        // kept because an AnimationClip animates this transform - consumers
        // can display these small; deleting them breaks the animations
        if (animatedKeep.Contains(gameObject.PathID))
        {
            node.Extras["unity_animated"] = true;
        }
        // hidden-in-game signals: inactive GameObject, or a non-default state
        // variant subtree (unlocked/blocked keypads). Roots are exempt: Rust
        // stores prefab-scene roots deactivated and activates at spawn.
        if (parentNode is not null
            && (hiddenState || (!gameObject.IsActive_Boolean && gameObject.IsActive_Byte == 0)))
        {
            node.Extras["unity_hidden"] = true;
        }
        if (parentNode is not null)
        {
            node.LocalMatrix = combined;
        }
        sceneBuilder.AddNode(node);
        nodeByGameObject[gameObject.PathID] = node;

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
            // point lights are directionless: no flip needed, attach in place.
            // Spots/directionals need the -Z flip — inline only on leaf nodes,
            // otherwise the flip would rotate the children too.
            var noGeometry = !EmitsGeometry(gameObject);
            var isLeaf = noGeometry
                && !transform.Children_C4P.WhereNotNull().Any(ct =>
                    ct.GameObject_C4P is { } childGo && (!options.PruneEmpties || keep.Contains(childGo)));
            var inline = light.Type == 2 ? noGeometry : isLeaf;
            AddLight(sceneBuilder, node, light, inline);
        }

        foreach (var childTransform in transform.Children_C4P.WhereNotNull())
        {
            AddGameObject(sceneBuilder, node, childTransform, System.Numerics.Matrix4x4.Identity, hiddenState);
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

        // every Unity light property rides along for the addon
        JsonObject LightExtras() => new()
        {
            ["unity_light_type"] = light.Type,
            ["unity_intensity"] = light.Intensity,
            ["unity_range"] = light.Range,
            ["unity_spot_angle"] = light.SpotAngle,
            ["unity_inner_spot_angle"] = light.InnerSpotAngle,
            ["unity_color"] = new JsonArray(light.Color.R, light.Color.G, light.Color.B, light.Color.A),
            ["unity_shadows"] = (int)light.ShadowsE,
            ["unity_bounce_intensity"] = light.BounceIntensity,
            ["unity_render_mode"] = light.RenderMode,
            ["unity_color_temperature"] = light.ColorTemperature,
            ["unity_use_color_temperature"] = light.UseColorTemperature,
            ["unity_cookie"] = light.CookieP?.Name.String,
            ["unity_cookie_size"] = light.CookieSize,
        };
        lightBuilder.Extras = LightExtras();

        var flip = System.Numerics.Matrix4x4.CreateFromQuaternion(
            System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI));
        if (inlineFlip)
        {
            lightBuilder.Name = node.Name;
            if (light.Type != 2)
            {
                node.LocalMatrix = flip * node.LocalMatrix;
            }
            if (node.Extras is JsonObject nodeExtras)
            {
                nodeExtras["unity_light"] = LightExtras();
            }
            sceneBuilder.AddLight(lightBuilder, node);
            return;
        }
        lightBuilder.Name = node.Name + "_light";
        var lightNode = node.CreateNode(lightBuilder.Name);
        lightNode.LocalMatrix = flip;
        lightNode.Extras = new JsonObject { ["unity_light"] = LightExtras() };
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
        var hasRealMaterial = false;
        for (var i = 0; i < subsetIndices.Length; i++)
        {
            var material = i < renderer.Materials_C25.Count
                && renderer.Materials_C25[i].TryGetAsset(renderer.Collection, out AssetRipper.SourceGenerated.Classes.ClassID_21.IMaterial? m)
                ? m : null;
            if (material is not null && material.Name.String != "Default-Material")
            {
                hasRealMaterial = true;
            }
            if (material is not null
                && ((RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexColor", out var avc) && avc != 0f)
                    || (RipperMaterialFactory.TryGetFloat(material, "_ApplyVertexAlpha", out var ava) && ava != 0f)))
            {
                vertexColorTintMaterials.Add(material.PathID);
            }
            pairs[i] = (subMeshes[subsetIndices[i]], materials.GetOrMake(material));
        }
        // hidden-in-game signals: disabled renderer always; placeholder-only
        // material (utility geometry like IO wiring origins) unless opted out
        if ((!renderer.Enabled_C25 || (options.HideUtility && !hasRealMaterial))
            && node.Extras is JsonObject nodeExtras)
        {
            nodeExtras["unity_hidden"] = true;
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
        // skinned meshes bind after the walk, once every joint node exists
        if (renderer is ISkinnedMeshRenderer skinnedRenderer && meshData.HasSkin)
        {
            pendingSkins.Add((meshBuilder, node, skinnedRenderer, meshData));
        }
        else
        {
            sceneBuilder.AddRigidMesh(meshBuilder, node);
        }
    }
}
