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

        // Additive shaders (particle glows like animal night-eyes) are not lit
        // surfaces: closest glTF equivalent is black base + emissive + blend.
        if (shaderName.Contains("Additive", StringComparison.OrdinalIgnoreCase))
        {
            builder.WithBaseColor(new System.Numerics.Vector4(0, 0, 0, 0.35f));
            builder.WithMetallicRoughness(0f, 1f);
            builder.WithAlpha(AlphaMode.BLEND);
            if (TryGetImage(material, "raw", out var glowImage, out var glowName, "_MainTex"))
            {
                builder.WithEmissive(glowImage.Value, new System.Numerics.Vector3(1, 1, 1));
                NameChannelImage(builder, KnownChannel.Emissive, glowName);
            }
            return builder;
        }

        // --- base color: _MainTex * _Color ---
        // Fur shells (AnimalFur) keep their density mask in _FuzzMask: composite
        // it into the diffuse alpha and blend, or the shell renders as an
        // opaque second skin over the body.
        var baseColor = colors.TryGetValue("_Color", out var c) ? c : new System.Numerics.Vector4(1, 1, 1, 1);
        var fuzzTexture = GetTexture(material, "_FuzzMask");
        var diffuseTexture = GetTexture(material, "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Diffuse");
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
                NameChannelImage(builder, KnownChannel.BaseColor, diffuseTexture.Name.String);
                builder.WithAlpha(AlphaMode.BLEND);
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
        if (!isSpecularWorkflow && TryGetImage(material, $"metalgloss:{glossMapScale:F3}", out var mrImage, out var mrName, "_MetallicGlossMap", "_PackedMap"))
        {
            builder.WithMetallicRoughness(mrImage.Value, 1f, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, mrName);
        }
        else if (TryGetImage(material, $"specgloss:{glossMapScale:F3}", out var sgImage, out var sgName, "_SpecGlossMap", "_SpecularMap", "_Specular"))
        {
            // approximation: gloss alpha becomes roughness, non-metal
            builder.WithMetallicRoughness(sgImage.Value, 0f, 1f);
            NameChannelImage(builder, KnownChannel.MetallicRoughness, sgName);
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
        if (floats.TryGetValue("_Mode", out var mode))
        {
            switch ((int)mode)
            {
                case 1:
                    var cutoff = floats.TryGetValue("_Cutoff", out var co) ? co : 0.5f;
                    builder.WithAlpha(AlphaMode.MASK, cutoff);
                    break;
                case 2 or 3:
                    builder.WithAlpha(AlphaMode.BLEND);
                    break;
            }
        }

        return builder;
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
        var normalSource = baseMode == "normal" ? DetectNormalXSource(img) : NormalXSource.Red;
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

    private enum NormalXSource { Red, Alpha }

    /// <summary>
    /// Unity stores tangent normals swizzled by format: DXT5nm keeps X in alpha
    /// (AssetRipper unpacks flagged ones already), BC5 keeps X/Y in R/G with an
    /// opaque alpha. Decide once per image, not per pixel: a varying alpha
    /// channel means the X data lives there.
    /// </summary>
    private static NormalXSource DetectNormalXSource(Image<Rgba32> img)
    {
        var alphaVaries = false;
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !alphaVaries; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A is > 8 and < 247)
                    {
                        alphaVaries = true;
                        break;
                    }
                }
            }
        });
        return alphaVaries ? NormalXSource.Alpha : NormalXSource.Red;
    }

    private static Rgba32 ReconstructNormal(Rgba32 p, NormalXSource source)
    {
        byte xByte = source == NormalXSource.Alpha ? p.A : p.R;
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
