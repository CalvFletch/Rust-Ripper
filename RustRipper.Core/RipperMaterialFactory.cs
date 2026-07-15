using System.Diagnostics.CodeAnalysis;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Extensions;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustRipper.Core;

/// <summary>
/// Full PBR material construction from Rust materials. Everything is derived
/// from the material's own data: shader name decides the workflow, texture
/// slots and float/color properties fill the glTF channels. No name guessing.
/// </summary>
public class RipperMaterialFactory
{
    private readonly Dictionary<IMaterial, MaterialBuilder> materialCache = new();
    private readonly Dictionary<(ITexture2D, string), MemoryImage?> imageCache = new();
    public MaterialBuilder DefaultMaterial { get; } = new MaterialBuilder("DefaultMaterial");

    /// <summary>Materials whose detail-layer paint was baked: PathID -> _DetailColor (as authored, sRGB).</summary>
    public Dictionary<long, System.Numerics.Vector4> DetailPaint { get; } = new();

    /// <summary>Paint-node mode: mask textures per painted material, for sidecar export.</summary>
    public Dictionary<long, (string MaterialName, ITexture2D Mask)> DetailMasks { get; } = new();

    private readonly bool paintNodes;

    public RipperMaterialFactory(bool paintNodes = false)
    {
        this.paintNodes = paintNodes;
    }

    public MaterialBuilder GetOrMake(IMaterial? material)
    {
        if (material is null)
        {
            return DefaultMaterial;
        }
        if (!materialCache.TryGetValue(material, out var builder))
        {
            builder = Make(material);
            materialCache.Add(material, builder);
        }
        return builder;
    }

    private MaterialBuilder Make(IMaterial material)
    {
        var builder = new MaterialBuilder(material.Name).WithMetallicRoughnessShader();
        var shaderName = material.Shader_C21P?.Name.String ?? "";
        var isSpecularWorkflow = shaderName.Contains("Specular", StringComparison.OrdinalIgnoreCase);
        var floats = GetFloats(material);
        var colors = GetColors(material);

        // Everything the game material holds rides along as extras (Blender
        // custom properties): what glTF can't express is still never lost.
        var floatsJson = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in floats)
        {
            floatsJson[key] = value;
        }
        var colorsJson = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in colors)
        {
            colorsJson[key] = new System.Text.Json.Nodes.JsonArray(value.X, value.Y, value.Z, value.W);
        }
        var texturesJson = new System.Text.Json.Nodes.JsonObject();
        foreach (var (slot, texEnv) in material.GetTextureProperties())
        {
            if (texEnv.Texture.TryGetAsset(material.Collection) is ITexture2D slotTexture)
            {
                texturesJson[slot.String] = slotTexture.Name.String;
            }
        }
        builder.Extras = new System.Text.Json.Nodes.JsonObject
        {
            ["unity_material"] = material.Name.String,
            ["unity_path_id"] = material.PathID,
            ["unity_collection"] = material.Collection.Name,
            ["unity_shader"] = shaderName,
            ["unity_floats"] = floatsJson,
            ["unity_colors"] = colorsJson,
            ["unity_textures"] = texturesJson,
        };

        // Glow overlays (flares, particle glows, halos) — detected from render
        // STATE, never names: ZWrite off plus an additive blend (DstBlend=One,
        // e.g. Particles/Additive SrcAlpha+One) or premultiplied blend
        // (One+OneMinusSrcAlpha) on a shader that declares soft-particle
        // fading (_InvFade) — plain transparent/glass lacks _InvFade.
        // glTF has no additive blend: black base whose alpha is the texture's
        // brightness + emissive = bright parts glow, black parts vanish.
        var blend = GetBlendState(material, floats);
        var isGlowOverlay = blend is { } blendState && blendState.ZWrite == 0f
            && (blendState.Dst == 1f || (blendState.Src == 1f && blendState.Dst == 10f && floats.ContainsKey("_InvFade")));
        if (isGlowOverlay)
        {
            builder.WithMetallicRoughness(0f, 1f);
            builder.WithAlpha(AlphaMode.BLEND);
            var glowStrength = floats.TryGetValue("_ColorMultiplier", out var cm) && cm > 0f ? cm : 1f;
            if (TryGetImage(material, "lumalpha", out var glowImage, out var glowName, "_MainTex"))
            {
                builder.WithBaseColor(glowImage.Value, new System.Numerics.Vector4(0, 0, 0, 1));
                NameChannelImage(builder, KnownChannel.BaseColor, glowName);
                builder.WithEmissive(glowImage.Value, new System.Numerics.Vector3(1, 1, 1));
                NameChannelImage(builder, KnownChannel.Emissive, glowName);
            }
            else
            {
                builder.WithBaseColor(new System.Numerics.Vector4(0, 0, 0, 0.35f));
            }
            if (glowStrength > 1f)
            {
                builder.WithChannelParam(KnownChannel.Emissive, KnownProperty.EmissiveStrength, glowStrength);
            }
            return builder;
        }

        // --- base color: _MainTex * _Color ---
        // Unity serializes material colors as sRGB; glTF factors are linear.
        // Fur shells (AnimalFur) keep their density mask in _FuzzMask: composite
        // it into the diffuse alpha and blend, or the shell renders as an
        // opaque second skin over the body.
        // The detail layer is Rust's paint system (barrels: _DetailMask marks
        // the painted band, _DetailColor is the paint) - baked into the albedo.
        var baseColor = SrgbToLinearFactor(colors.TryGetValue("_Color", out var c) ? c : new System.Numerics.Vector4(1, 1, 1, 1));
        var fuzzTexture = GetTexture(material, "_FuzzMask");
        var diffuseTexture = GetTexture(material, "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Diffuse");
        var detailMask = GetTexture(material, "_DetailMask");
        var detailTintActive = floats.TryGetValue("_DetailLayer", out var detailLayer) && detailLayer != 0f
            && detailMask is not null
            && GetTexture(material, "_DetailAlbedoMap") is null
            && colors.TryGetValue("_DetailColor", out var detailColor);
        var baseColorSet = false;
        if (fuzzTexture is not null && diffuseTexture is not null)
        {
            var key = (diffuseTexture, "difffuzz");
            if (!imageCache.TryGetValue(key, out var furImage))
            {
                furImage = DecodeDiffuseWithFuzzAlpha(diffuseTexture, fuzzTexture);
                imageCache.Add(key, furImage);
            }
            if (furImage is not null)
            {
                builder.WithBaseColor(furImage.Value, baseColor);
                // distinct name: this is diffuse RGB + fuzz-mask alpha, not the body albedo
                NameChannelImage(builder, KnownChannel.BaseColor, diffuseTexture.Name.String + "_fuzzalpha");
                builder.WithAlpha(AlphaMode.BLEND);
                baseColorSet = true;
            }
        }
        if (!baseColorSet && detailTintActive && diffuseTexture is not null)
        {
            colors.TryGetValue("_DetailColor", out var dc);
            if (paintNodes)
            {
                // leave the albedo raw; the mask ships as a sidecar and the
                // colour rides in _RUST_PAINT for a mask-driven Mix node setup
                DetailPaint[material.PathID] = dc;
                DetailMasks[material.PathID] = (material.Name.String, detailMask!);
            }
            else
            {
                var key = (diffuseTexture, $"detailtint:{detailMask!.PathID}:{dc.X:F3}:{dc.Y:F3}:{dc.Z:F3}");
                if (!imageCache.TryGetValue(key, out var tintedImage))
                {
                    tintedImage = DecodeDetailTint(diffuseTexture, detailMask, dc);
                    imageCache.Add(key, tintedImage);
                }
                if (tintedImage is not null)
                {
                    builder.WithBaseColor(tintedImage.Value, baseColor);
                    NameChannelImage(builder, KnownChannel.BaseColor, diffuseTexture.Name.String + "_painted");
                    DetailPaint[material.PathID] = dc;
                    baseColorSet = true;
                }
            }
        }
        if (!baseColorSet && diffuseTexture is not null && ColorizeEnabled(floats)
            && GetTexture(material, "_ColorizeMask") is { } colorizeMask)
        {
            var cR = colors.TryGetValue("_ColorizeColorR", out var vr) ? vr : new System.Numerics.Vector4(1, 1, 1, 0);
            var cG = colors.TryGetValue("_ColorizeColorG", out var vg) ? vg : new System.Numerics.Vector4(1, 1, 1, 0);
            var cB = colors.TryGetValue("_ColorizeColorB", out var vb) ? vb : new System.Numerics.Vector4(1, 1, 1, 0);
            var cA = colors.TryGetValue("_ColorizeColorA", out var va) ? va : new System.Numerics.Vector4(1, 1, 1, 0);
            var key = (diffuseTexture, $"colorize:{colorizeMask.PathID}:{cR}:{cG}:{cB}:{cA}");
            if (!imageCache.TryGetValue(key, out var colorizedImage))
            {
                colorizedImage = DecodeColorize(diffuseTexture, colorizeMask, cR, cG, cB, cA);
                imageCache.Add(key, colorizedImage);
            }
            if (colorizedImage is not null)
            {
                builder.WithBaseColor(colorizedImage.Value, baseColor);
                NameChannelImage(builder, KnownChannel.BaseColor, diffuseTexture.Name.String);
                baseColorSet = true;
            }
        }
        if (!baseColorSet && TryGetImage(material, "raw", out var mainImage, out var mainName, "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Diffuse"))
        {
            builder.WithBaseColor(mainImage.Value, baseColor);
            NameChannelImage(builder, KnownChannel.BaseColor, mainName);
            baseColorSet = true;
        }
        if (!baseColorSet)
        {
            builder.WithBaseColor(baseColor);
        }

        // --- normal map (reconstructed from Unity's swizzled storage) ---
        if (TryGetImage(material, "normal", out var normalImage, out var normalName, "_BumpMap", "_NormalMap", "_Normal"))
        {
            var scale = floats.TryGetValue("_BumpScale", out var bs) ? bs : 1f;
            builder.WithNormal(normalImage.Value, scale);
            NameChannelImage(builder, KnownChannel.Normal, normalName);
        }

        // --- metallic / roughness ---
        // Unity: smoothness lives in the gloss map's alpha, scaled by
        // _GlossMapScale; with _SmoothnessTextureChannel=1 it lives in the
        // albedo's alpha instead. The scale is baked into the converted texture
        // (exact), factors only carry constants.
        var glossiness = floats.TryGetValue("_Glossiness", out var g) ? g
            : floats.TryGetValue("_Smoothness", out var s) ? s : 0.5f;
        var glossMapScale = floats.TryGetValue("_GlossMapScale", out var gms) ? gms : 1f;
        var metallic = isSpecularWorkflow ? 0f : (floats.TryGetValue("_Metallic", out var met) ? met : 0f);
        var smoothnessFromAlbedo = floats.TryGetValue("_SmoothnessTextureChannel", out var stc) && (int)stc == 1;
        if (TryGetImage(material, $"packedmap:{glossMapScale:F3}", out var pmImage, out var pmName, "_PackedMap"))
        {
            // Rust _PackedMap: G=glossiness, B=metallic, A=AO -> glTF ORM in one image
            builder.WithMetallicRoughness(pmImage.Value, 1f, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, pmName);
            builder.WithOcclusion(pmImage.Value, floats.TryGetValue("_OcclusionStrength", out var pmOcc) ? pmOcc : 1f);
            NameChannelImage(builder, KnownChannel.Occlusion, pmName);
        }
        else if (!isSpecularWorkflow && TryGetImage(material, $"metalgloss:{glossMapScale:F3}", out var mrImage, out var mrName, "_MetallicGlossMap"))
        {
            builder.WithMetallicRoughness(mrImage.Value, 1f, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, mrName);
        }
        else if (TryGetImage(material, $"specgloss:{glossMapScale:F3}", out var sgImage, out var sgName, "_SpecGlossMap", "_SpecularMap", "_Specular"))
        {
            // gloss alpha becomes roughness (non-metal), and the spec map's RGB
            // is the dielectric F0 exactly - KHR_materials_specular carries it
            // (Blender wires specularColorTexture into Specular Tint)
            builder.WithMetallicRoughness(sgImage.Value, 0f, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, sgName);
            if (TryGetImage(material, "raw", out var sgRaw, out var sgRawName, "_SpecGlossMap", "_SpecularMap", "_Specular"))
            {
                builder.WithSpecularColor(ImageBuilder.From(sgRaw.Value, sgRawName ?? "specular"), null);
            }
        }
        else if (smoothnessFromAlbedo && diffuseTexture is not null
            && TryGetImage(material, $"albedogloss:{glossMapScale:F3}", out var agImage, out var agName, "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Diffuse"))
        {
            builder.WithMetallicRoughness(agImage.Value, metallic, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, agName);
        }
        else
        {
            builder.WithMetallicRoughness(metallic, 1f - glossiness);
        }

        // --- culling ---
        if ((floats.TryGetValue("_Cull", out var cull) && (int)cull == 0)
            || (floats.TryGetValue("_DoubleSided", out var doubleSided) && doubleSided != 0f))
        {
            builder.WithDoubleSide(true);
        }

        // --- index of refraction (KHR_materials_ior) ---
        if (floats.TryGetValue("_Ior", out var ior) && ior > 0f)
        {
            builder.IndexOfRefraction = ior;
        }

        // --- occlusion ---
        if (TryGetImage(material, "raw", out var occlusionImage, out var occlusionName, "_OcclusionMap", "_AO"))
        {
            var strength = floats.TryGetValue("_OcclusionStrength", out var os) ? os : 1f;
            builder.WithOcclusion(occlusionImage.Value, strength);
            NameChannelImage(builder, KnownChannel.Occlusion, occlusionName);
        }

        // --- emission (HDR intensity goes to KHR_materials_emissive_strength) ---
        if (colors.TryGetValue("_EmissionColor", out var emission) && (emission.X > 0 || emission.Y > 0 || emission.Z > 0))
        {
            var strength = MathF.Max(1f, MathF.Max(emission.X, MathF.Max(emission.Y, emission.Z)));
            var emissiveRgb = new System.Numerics.Vector3(emission.X, emission.Y, emission.Z) / strength;
            if (TryGetImage(material, "raw", out var emissionImage, out var emissionName, "_EmissionMap"))
            {
                builder.WithEmissive(emissionImage.Value, emissiveRgb);
                NameChannelImage(builder, KnownChannel.Emissive, emissionName);
            }
            else
            {
                builder.WithEmissive(emissiveRgb);
            }
            if (strength > 1f)
            {
                builder.WithChannelParam(KnownChannel.Emissive, KnownProperty.EmissiveStrength, strength);
            }
        }

        // --- alpha (Unity standard _Mode: 0 opaque, 1 cutout, 2 fade, 3 transparent) ---
        var alphaSet = false;
        if (floats.TryGetValue("_Mode", out var mode))
        {
            switch ((int)mode)
            {
                case 1:
                    var cutoff = floats.TryGetValue("_Cutoff", out var co) ? co : 0.5f;
                    builder.WithAlpha(AlphaMode.MASK, cutoff);
                    alphaSet = true;
                    break;
                case 2 or 3:
                    builder.WithAlpha(AlphaMode.BLEND);
                    alphaSet = true;
                    break;
            }
        }
        // shaders without _Mode (particles, custom): the render state itself
        // says transparent - depth write off + over blending (rotor blur planes)
        if (!alphaSet && blend is { } transparentState
            && transparentState.ZWrite == 0f && transparentState.Dst == 10f)
        {
            builder.WithAlpha(AlphaMode.BLEND);
        }

        // --- secondary UV routing (e.g. AttackHeli cockpit emissive on UV2) ---
        if (floats.TryGetValue("_EmissionUVSec", out var emissionUv) && (int)emissionUv > 0)
        {
            SetChannelUv(builder, KnownChannel.Emissive, (int)emissionUv);
        }
        if (floats.TryGetValue("_OcclusionUVSet", out var occlusionUv) && (int)occlusionUv > 0)
        {
            SetChannelUv(builder, KnownChannel.Occlusion, (int)occlusionUv);
        }

        // --- glass (KHR_materials_transmission) ---
        // the shader IS the method: refraction shaders are transmissive glass
        if (shaderName.Contains("Refraction", StringComparison.OrdinalIgnoreCase)
            || shaderName.Contains("Glass", StringComparison.OrdinalIgnoreCase))
        {
            builder.WithChannelParam(KnownChannel.Transmission, KnownProperty.TransmissionFactor, 0.9f);
        }

        return builder;
    }

    private static void SetChannelUv(MaterialBuilder builder, KnownChannel channel, int uvSet)
    {
        var texture = builder.GetChannel(channel)?.Texture;
        if (texture is not null)
        {
            texture.CoordinateSet = uvSet;
        }
    }

    private static void NameChannelImage(MaterialBuilder builder, KnownChannel channel, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        var image = builder.GetChannel(channel)?.Texture?.PrimaryImage;
        if (image is not null)
        {
            image.Name = name;
        }
    }

    /// <summary>
    /// Decode a texture and post-process per channel semantics:
    /// raw       - as decoded
    /// normal    - reconstruct XYZ from Unity's AG/RG swizzled storage
    /// metalgloss- Unity R=metal A=smoothness -> glTF G=roughness B=metal
    /// specgloss - Unity RGB=spec A=gloss     -> glTF G=roughness (approximation)
    /// </summary>
    private bool TryGetImage(IMaterial material, string mode, [NotNullWhen(true)] out MemoryImage? image, out string? textureName, params string[] slotNames)
    {
        image = null;
        textureName = null;
        foreach (var slot in slotNames)
        {
            if (!material.TryGetTextureProperty(slot, out var texEnv))
            {
                continue;
            }
            if (texEnv.Texture.TryGetAsset(material.Collection) is not ITexture2D texture)
            {
                continue;
            }
            var key = (texture, mode);
            if (!imageCache.TryGetValue(key, out image))
            {
                image = Decode(texture, mode);
                imageCache.Add(key, image);
            }
            if (image is not null)
            {
                textureName = texture.Name;
                return true;
            }
        }
        return false;
    }

    /// <summary>Decode any texture straight to PNG bytes (sidecar export).</summary>
    public static bool TryDecodePng(ITexture2D texture, [NotNullWhen(true)] out byte[]? png)
    {
        png = null;
        if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
        {
            return false;
        }
        using var stream = new MemoryStream();
        bitmap.SaveAsPng(stream);
        png = stream.ToArray();
        return true;
    }

    private static ITexture2D? GetTexture(IMaterial material, params string[] slotNames)
    {
        foreach (var slot in slotNames)
        {
            if (material.TryGetTextureProperty(slot, out var texEnv)
                && texEnv.Texture.TryGetAsset(material.Collection) is ITexture2D texture)
            {
                return texture;
            }
        }
        return null;
    }

    /// <summary>
    /// Render blend state, purely from data: the material's own _SrcBlend/
    /// _DstBlend/_ZWrite floats when the shader exposes them, else the shader
    /// asset's parsed first-pass state (covers Unity builtin shaders).
    /// Unity BlendMode: 0=Zero 1=One 5=SrcAlpha 10=OneMinusSrcAlpha.
    /// </summary>
    private static (float Src, float Dst, float ZWrite)? GetBlendState(IMaterial material, Dictionary<string, float> floats)
    {
        if (floats.TryGetValue("_SrcBlend", out var src) && floats.TryGetValue("_DstBlend", out var dst))
        {
            return (src, dst, floats.TryGetValue("_ZWrite", out var zw) ? zw : 1f);
        }
        try
        {
            if (material.Shader_C21P?.ParsedForm is { } parsed
                && parsed.SubShaders.Count > 0
                && parsed.SubShaders[0].Passes.Count > 0
                && parsed.SubShaders[0].Passes[0].State is { } state)
            {
                return (state.RtBlend0.SourceBlend.Val, state.RtBlend0.DestinationBlend.Val, state.ZWrite.Val);
            }
        }
        catch
        {
            // shader variants without a parsed form
        }
        return null;
    }

    /// <summary>
    /// Colorize is authored on many materials but only active when an enable
    /// flag says so (stock clothing has _ColorizeLayer=1 with Enabled=0 - the
    /// layer exists for runtime skin tinting). When no enable flag is present
    /// at all, _ColorizeLayer alone decides.
    /// </summary>
    public static bool ColorizeEnabled(Dictionary<string, float> floats)
    {
        if (!floats.TryGetValue("_ColorizeLayer", out var layer) || layer == 0f)
        {
            return false;
        }
        var hasFlag = floats.ContainsKey("_ColorizeLayerEnabled") || floats.ContainsKey("colorizeLayerEnabled");
        if (!hasFlag)
        {
            return true;
        }
        return (floats.TryGetValue("_ColorizeLayerEnabled", out var e1) && e1 != 0f)
            || (floats.TryGetValue("colorizeLayerEnabled", out var e2) && e2 != 0f);
    }

    /// <summary>
    /// Bake the colorize layer: the _ColorizeMask's R/G/B/A channels select
    /// where _ColorizeColorR/G/B/A multiply into the albedo (linear space);
    /// each colour's own alpha scales its strength.
    /// </summary>
    private static MemoryImage? DecodeColorize(ITexture2D diffuseTexture, ITexture2D maskTexture,
        System.Numerics.Vector4 cR, System.Numerics.Vector4 cG, System.Numerics.Vector4 cB, System.Numerics.Vector4 cA)
    {
        if (!TextureConverter.TryConvertToBitmap(diffuseTexture, out DirectBitmap diffuseBitmap)
            || !TextureConverter.TryConvertToBitmap(maskTexture, out DirectBitmap maskBitmap))
        {
            return null;
        }
        using var diffuseStream = new MemoryStream();
        diffuseBitmap.SaveAsPng(diffuseStream);
        diffuseStream.Position = 0;
        using var maskStream = new MemoryStream();
        maskBitmap.SaveAsPng(maskStream);
        maskStream.Position = 0;

        using var diffuse = Image.Load<Rgba32>(diffuseStream);
        using var mask = Image.Load<Rgba32>(maskStream);
        if (mask.Width != diffuse.Width || mask.Height != diffuse.Height)
        {
            mask.Mutate(x => x.Resize(diffuse.Width, diffuse.Height));
        }

        Span<System.Numerics.Vector3> tints =
        [
            new(MathF.Pow(cR.X, 2.2f), MathF.Pow(cR.Y, 2.2f), MathF.Pow(cR.Z, 2.2f)),
            new(MathF.Pow(cG.X, 2.2f), MathF.Pow(cG.Y, 2.2f), MathF.Pow(cG.Z, 2.2f)),
            new(MathF.Pow(cB.X, 2.2f), MathF.Pow(cB.Y, 2.2f), MathF.Pow(cB.Z, 2.2f)),
            new(MathF.Pow(cA.X, 2.2f), MathF.Pow(cA.Y, 2.2f), MathF.Pow(cA.Z, 2.2f)),
        ];
        Span<float> strengths = [cR.W, cG.W, cB.W, cA.W];
        for (var y = 0; y < diffuse.Height; y++)
        {
            for (var x = 0; x < diffuse.Width; x++)
            {
                var p = diffuse[x, y];
                var m = mask[x, y];
                Span<byte> maskChannels = [m.R, m.G, m.B, m.A];
                var r = MathF.Pow(p.R / 255f, 2.2f);
                var g = MathF.Pow(p.G / 255f, 2.2f);
                var b = MathF.Pow(p.B / 255f, 2.2f);
                for (var c = 0; c < 4; c++)
                {
                    var w = maskChannels[c] / 255f * strengths[c];
                    if (w <= 0f)
                    {
                        continue;
                    }
                    r += (r * tints[c].X - r) * w;
                    g += (g * tints[c].Y - g) * w;
                    b += (b * tints[c].Z - b) * w;
                }
                diffuse[x, y] = new Rgba32(
                    (byte)(MathF.Pow(Math.Clamp(r, 0f, 1f), 1f / 2.2f) * 255f),
                    (byte)(MathF.Pow(Math.Clamp(g, 0f, 1f), 1f / 2.2f) * 255f),
                    (byte)(MathF.Pow(Math.Clamp(b, 0f, 1f), 1f / 2.2f) * 255f),
                    p.A);
            }
        }
        using var outStream = new MemoryStream();
        diffuse.SaveAsPng(outStream);
        return new MemoryImage(outStream.ToArray());
    }

    /// <summary>Unity color properties are sRGB; glTF color factors are linear.</summary>
    private static System.Numerics.Vector4 SrgbToLinearFactor(System.Numerics.Vector4 srgb)
        => new(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f), srgb.W);

    /// <summary>
    /// Bake Rust's detail-layer paint into the albedo: where the mask says
    /// painted, multiply the albedo by _DetailColor (in linear space, like the
    /// shader). This is how barrels get their red/blue/brown bands.
    /// </summary>
    private static MemoryImage? DecodeDetailTint(ITexture2D diffuseTexture, ITexture2D maskTexture, System.Numerics.Vector4 detailColor)
    {
        if (!TextureConverter.TryConvertToBitmap(diffuseTexture, out DirectBitmap diffuseBitmap)
            || !TextureConverter.TryConvertToBitmap(maskTexture, out DirectBitmap maskBitmap))
        {
            return null;
        }
        using var diffuseStream = new MemoryStream();
        diffuseBitmap.SaveAsPng(diffuseStream);
        diffuseStream.Position = 0;
        using var maskStream = new MemoryStream();
        maskBitmap.SaveAsPng(maskStream);
        maskStream.Position = 0;

        using var diffuse = Image.Load<Rgba32>(diffuseStream);
        using var mask = Image.Load<Rgba32>(maskStream);
        if (mask.Width != diffuse.Width || mask.Height != diffuse.Height)
        {
            mask.Mutate(x => x.Resize(diffuse.Width, diffuse.Height));
        }

        // mask channel: alpha when it carries data (Unity's convention is
        // mask.a), red otherwise (single-channel formats decode into red)
        var alphaVaries = false;
        for (var y = 0; y < mask.Height && !alphaVaries; y += 8)
        {
            for (var x = 0; x < mask.Width; x += 8)
            {
                if (mask[x, y].A is > 8 and < 247)
                {
                    alphaVaries = true;
                    break;
                }
            }
        }

        var tintR = MathF.Pow(detailColor.X, 2.2f);
        var tintG = MathF.Pow(detailColor.Y, 2.2f);
        var tintB = MathF.Pow(detailColor.Z, 2.2f);
        for (var y = 0; y < diffuse.Height; y++)
        {
            for (var x = 0; x < diffuse.Width; x++)
            {
                var p = diffuse[x, y];
                var m = (alphaVaries ? mask[x, y].A : mask[x, y].R) / 255f;
                if (m <= 0f)
                {
                    continue;
                }
                var r = MathF.Pow(p.R / 255f, 2.2f);
                var g = MathF.Pow(p.G / 255f, 2.2f);
                var b = MathF.Pow(p.B / 255f, 2.2f);
                r += (r * tintR - r) * m;
                g += (g * tintG - g) * m;
                b += (b * tintB - b) * m;
                diffuse[x, y] = new Rgba32(
                    (byte)(MathF.Pow(r, 1f / 2.2f) * 255f),
                    (byte)(MathF.Pow(g, 1f / 2.2f) * 255f),
                    (byte)(MathF.Pow(b, 1f / 2.2f) * 255f),
                    p.A);
            }
        }
        using var outStream = new MemoryStream();
        diffuse.SaveAsPng(outStream);
        return new MemoryImage(outStream.ToArray());
    }

    /// <summary>Diffuse RGB with the fuzz mask's red channel as alpha (fur shell density).</summary>
    private static MemoryImage? DecodeDiffuseWithFuzzAlpha(ITexture2D diffuseTexture, ITexture2D fuzzTexture)
    {
        if (!TextureConverter.TryConvertToBitmap(diffuseTexture, out DirectBitmap diffuseBitmap)
            || !TextureConverter.TryConvertToBitmap(fuzzTexture, out DirectBitmap fuzzBitmap))
        {
            return null;
        }
        using var diffuseStream = new MemoryStream();
        diffuseBitmap.SaveAsPng(diffuseStream);
        diffuseStream.Position = 0;
        using var fuzzStream = new MemoryStream();
        fuzzBitmap.SaveAsPng(fuzzStream);
        fuzzStream.Position = 0;

        using var diffuse = Image.Load<Rgba32>(diffuseStream);
        using var fuzz = Image.Load<Rgba32>(fuzzStream);
        if (fuzz.Width != diffuse.Width || fuzz.Height != diffuse.Height)
        {
            fuzz.Mutate(x => x.Resize(diffuse.Width, diffuse.Height));
        }
        for (var y = 0; y < diffuse.Height; y++)
        {
            for (var x = 0; x < diffuse.Width; x++)
            {
                var p = diffuse[x, y];
                p.A = fuzz[x, y].R;
                diffuse[x, y] = p;
            }
        }
        using var outStream = new MemoryStream();
        diffuse.SaveAsPng(outStream);
        return new MemoryImage(outStream.ToArray());
    }

    private static MemoryImage? Decode(ITexture2D texture, string mode)
    {
        if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
        {
            return null;
        }
        using var pngStream = new MemoryStream();
        bitmap.SaveAsPng(pngStream);
        if (mode == "raw")
        {
            return new MemoryImage(pngStream.ToArray());
        }

        // mode may carry a smoothness scale suffix, e.g. "metalgloss:0.850"
        var scale = 1f;
        var baseMode = mode;
        var colonIndex = mode.IndexOf(':');
        if (colonIndex >= 0)
        {
            baseMode = mode[..colonIndex];
            scale = float.Parse(mode[(colonIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }

        pngStream.Position = 0;
        using var img = Image.Load<Rgba32>(pngStream);
        var normalSource = baseMode == "normal" ? DetectNormalLayout(img) : NormalLayout.XRed_ZBlue;
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    row[x] = baseMode switch
                    {
                        "normal" => ReconstructNormal(p, normalSource),
                        "metalgloss" => new Rgba32(0, Roughness(p.A, scale), p.R, 255),
                        "specgloss" => new Rgba32(0, Roughness(p.A, scale), 0, 255),
                        "albedogloss" => new Rgba32(0, Roughness(p.A, scale), 255, 255),
                        "packedmap" => new Rgba32(p.A, Roughness(p.G, scale), p.B, 255),
                        "lumalpha" => new Rgba32(p.R, p.G, p.B, (byte)(Math.Max(p.R, Math.Max(p.G, p.B)) * p.A / 255)),
                        _ => p,
                    };
                }
            }
        });
        using var outStream = new MemoryStream();
        img.SaveAsPng(outStream);
        return new MemoryImage(outStream.ToArray());
    }

    /// <summary>Unity smoothness (texture alpha x scale) to glTF roughness.</summary>
    private static byte Roughness(byte smoothness, float scale)
        => (byte)Math.Clamp(255f - smoothness * scale, 0f, 255f);

    public enum NormalLayout
    {
        XRed_ZBlue,   // plain RGB tangent normal
        XAlpha_ZBlue, // classic DXT5nm swizzle, never unpacked
        XBlue_ZRed,   // engine's UnpackNormal output for RGBA-layout formats (BC7):
                      // it writes with BGRA indices, landing Z in red and X in blue
    }

    /// <summary>
    /// Pick the channel layout that is self-consistent: a real tangent normal
    /// satisfies z = sqrt(1 - x^2 - y^2), so the candidate whose XY best
    /// reproduces the stored Z channel is the true layout. Decided once per
    /// image from sampled pixels — no format flags, no trust in upstream
    /// unpacking order.
    /// </summary>
    public static NormalLayout DetectNormalLayout(Image<Rgba32> img)
    {
        double errXRed = 0, errXAlpha = 0, errXBlue = 0;
        long count = 0;
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y += 4)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 4)
                {
                    var p = row[x];
                    var yN = p.G / 255f * 2f - 1f;
                    errXRed += Math.Abs(p.B - PredictZByte(p.R / 255f * 2f - 1f, yN));
                    errXAlpha += Math.Abs(p.B - PredictZByte(p.A / 255f * 2f - 1f, yN));
                    errXBlue += Math.Abs(p.R - PredictZByte(p.B / 255f * 2f - 1f, yN));
                    count++;
                }
            }
        });
        if (count == 0)
        {
            return NormalLayout.XRed_ZBlue;
        }
        var best = Math.Min(errXRed, Math.Min(errXAlpha, errXBlue));
        return best == errXBlue ? NormalLayout.XBlue_ZRed
            : best == errXAlpha ? NormalLayout.XAlpha_ZBlue
            : NormalLayout.XRed_ZBlue;
    }

    private static float PredictZByte(float x, float y)
    {
        var zSq = 1f - Math.Clamp(x * x + y * y, 0f, 1f);
        return (MathF.Sqrt(zSq) + 1f) / 2f * 255f;
    }

    private static Rgba32 ReconstructNormal(Rgba32 p, NormalLayout layout)
    {
        byte xByte = layout switch
        {
            NormalLayout.XAlpha_ZBlue => p.A,
            NormalLayout.XBlue_ZRed => p.B,
            _ => p.R,
        };
        var x = xByte / 255f * 2f - 1f;
        var y = p.G / 255f * 2f - 1f;
        var zSq = 1f - Math.Clamp(x * x + y * y, 0f, 1f);
        var z = MathF.Sqrt(zSq);
        return new Rgba32(
            (byte)((x * 0.5f + 0.5f) * 255),
            (byte)((y * 0.5f + 0.5f) * 255),
            (byte)((z * 0.5f + 0.5f) * 255),
            255);
    }

    /// <summary>Read a single float property off a material (e.g. _ApplyVertexColor).</summary>
    public static bool TryGetFloat(IMaterial material, string name, out float value)
    {
        value = 0f;
        var sheet = material.SavedProperties_C21;
        if (!sheet.Has_Floats_AssetDictionary_Utf8String_Single())
        {
            return false;
        }
        foreach (var pair in sheet.Floats_AssetDictionary_Utf8String_Single)
        {
            if (pair.Key.String == name)
            {
                value = pair.Value;
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, float> GetFloats(IMaterial material)
    {
        var result = new Dictionary<string, float>();
        var sheet = material.SavedProperties_C21;
        if (sheet.Has_Floats_AssetDictionary_Utf8String_Single())
        {
            foreach (var pair in sheet.Floats_AssetDictionary_Utf8String_Single)
            {
                result[pair.Key.String] = pair.Value;
            }
        }
        return result;
    }

    private static Dictionary<string, System.Numerics.Vector4> GetColors(IMaterial material)
    {
        var result = new Dictionary<string, System.Numerics.Vector4>();
        var sheet = material.SavedProperties_C21;
        if (sheet.Has_Colors_AssetDictionary_Utf8String_ColorRGBAf())
        {
            foreach (var pair in sheet.Colors_AssetDictionary_Utf8String_ColorRGBAf)
            {
                result[pair.Key.String] = new System.Numerics.Vector4(pair.Value.R, pair.Value.G, pair.Value.B, pair.Value.A);
            }
        }
        return result;
    }
}
