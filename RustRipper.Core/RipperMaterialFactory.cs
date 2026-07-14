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

        // --- base color: _MainTex * _Color ---
        var baseColor = colors.TryGetValue("_Color", out var c) ? c : new System.Numerics.Vector4(1, 1, 1, 1);
        if (TryGetImage(material, "raw", out var mainImage, "_MainTex", "_BaseColorMap", "_AlbedoMap"))
        {
            builder.WithBaseColor(mainImage.Value, baseColor);
        }
        else
        {
            builder.WithBaseColor(baseColor);
        }

        // --- normal map (reconstructed from Unity's swizzled storage) ---
        if (TryGetImage(material, "normal", out var normalImage, "_BumpMap", "_NormalMap", "_Normal"))
        {
            var scale = floats.TryGetValue("_BumpScale", out var bs) ? bs : 1f;
            builder.WithNormal(normalImage.Value, scale);
        }

        // --- metallic / roughness ---
        var glossiness = floats.TryGetValue("_Glossiness", out var g) ? g
            : floats.TryGetValue("_Smoothness", out var s) ? s : 0.5f;
        if (!isSpecularWorkflow && TryGetImage(material, "metalgloss", out var mrImage, "_MetallicGlossMap", "_PackedMap"))
        {
            builder.WithMetallicRoughness(mrImage.Value, 1f, 1f);
        }
        else if (isSpecularWorkflow && TryGetImage(material, "specgloss", out var sgImage, "_SpecGlossMap", "_SpecularMap"))
        {
            // approximation: gloss alpha becomes roughness, non-metal
            builder.WithMetallicRoughness(sgImage.Value, 0f, 1f);
        }
        else
        {
            var metallic = isSpecularWorkflow ? 0f : (floats.TryGetValue("_Metallic", out var m) ? m : 0f);
            builder.WithMetallicRoughness(metallic, 1f - glossiness);
        }

        // --- occlusion ---
        if (TryGetImage(material, "raw", out var occlusionImage, "_OcclusionMap"))
        {
            var strength = floats.TryGetValue("_OcclusionStrength", out var os) ? os : 1f;
            builder.WithOcclusion(occlusionImage.Value, strength);
        }

        // --- emission ---
        if (colors.TryGetValue("_EmissionColor", out var emission) && (emission.X > 0 || emission.Y > 0 || emission.Z > 0))
        {
            if (TryGetImage(material, "raw", out var emissionImage, "_EmissionMap"))
            {
                builder.WithEmissive(emissionImage.Value, new System.Numerics.Vector3(emission.X, emission.Y, emission.Z));
            }
            else
            {
                builder.WithEmissive(new System.Numerics.Vector3(emission.X, emission.Y, emission.Z));
            }
        }

        // --- alpha cutout (Unity standard: _Mode 1 = cutout) ---
        if (floats.TryGetValue("_Mode", out var mode) && (int)mode == 1)
        {
            var cutoff = floats.TryGetValue("_Cutoff", out var co) ? co : 0.5f;
            builder.WithAlpha(AlphaMode.MASK, cutoff);
        }

        return builder;
    }

    /// <summary>
    /// Decode a texture and post-process per channel semantics:
    /// raw       - as decoded
    /// normal    - reconstruct XYZ from Unity's AG/RG swizzled storage
    /// metalgloss- Unity R=metal A=smoothness -> glTF G=roughness B=metal
    /// specgloss - Unity RGB=spec A=gloss     -> glTF G=roughness (approximation)
    /// </summary>
    private bool TryGetImage(IMaterial material, string mode, [NotNullWhen(true)] out MemoryImage? image, params string[] slotNames)
    {
        image = null;
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
                return true;
            }
        }
        return false;
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

        pngStream.Position = 0;
        using var img = Image.Load<Rgba32>(pngStream);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    row[x] = mode switch
                    {
                        "normal" => ReconstructNormal(p),
                        "metalgloss" => new Rgba32(0, (byte)(255 - p.A), p.R, 255),
                        "specgloss" => new Rgba32(0, (byte)(255 - p.A), 0, 255),
                        _ => p,
                    };
                }
            }
        });
        using var outStream = new MemoryStream();
        img.SaveAsPng(outStream);
        return new MemoryImage(outStream.ToArray());
    }

    private static Rgba32 ReconstructNormal(Rgba32 p)
    {
        // Unity stores tangent normals swizzled: DXT5nm keeps X in alpha, BC5 in R/G.
        // Detect per pixel: a plain RGB normal already has a meaningful blue channel.
        byte xByte = p.A is 0 or 255 ? p.R : p.A;
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
