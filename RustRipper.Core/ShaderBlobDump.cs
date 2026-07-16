using System.Runtime.InteropServices;
using System.Text;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Subclasses.ConstantBuffer;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;
using AssetRipper.SourceGenerated.Subclasses.TextureParameter;
using K4os.Compression.LZ4;

namespace RustRipper.Core;

/// <summary>
/// Compiled shader program extraction (L1: pure data reading). Decompresses
/// the Shader asset's LZ4 program blob, lists every variant of a stage with
/// its keywords, and disassembles selected programs: DXBC through the OS
/// d3dcompiler, SMOL-V/SPIR-V through the engine's own libraries, GL/Metal
/// programs are already source text. Nothing here interprets or invents -
/// the output is what Facepunch's compiler produced.
/// </summary>
public static class ShaderBlobDump
{
    // Unity ShaderGpuProgramType (UnityCsReference, stable across versions)
    private static readonly string[] GpuProgramTypeNames =
    {
        "Unknown", "GLLegacy", "GLES31AEP", "GLES31", "GLES3", "GLES",
        "GLCore32", "GLCore41", "GLCore43",
        "DX9VertexSM20", "DX9VertexSM30", "DX9PixelSM20", "DX9PixelSM30",
        "DX10Level9Vertex", "DX10Level9Pixel",
        "DX11VertexSM40", "DX11VertexSM50", "DX11PixelSM40", "DX11PixelSM50",
        "DX11GeometrySM40", "DX11GeometrySM50", "DX11HullSM50", "DX11DomainSM50",
        "MetalVS", "MetalFS", "SPIRV",
        "ConsoleVS", "ConsoleFS", "ConsoleHS", "ConsoleDS", "ConsoleGS",
        "RayTracing", "PS5NGGC",
    };

    private sealed record Variant(
        int SubShader, int Pass, string PassName, string Stage,
        uint BlobIndex, int GpuProgramType, string[] Keywords,
        ISerializedSubProgram? SubProgram, ISerializedPass PassAsset,
        ISerializedProgram? Program = null, int Tier = -1, int TierIndex = -1)
    {
        public string LightMode
        {
            get
            {
                try
                {
                    foreach (var pair in PassAsset.Tags.Tags)
                    {
                        if (pair.Key.String.Equals("LIGHTMODE", StringComparison.OrdinalIgnoreCase))
                        {
                            return pair.Value.String;
                        }
                    }
                }
                catch
                {
                }
                return "";
            }
        }
    }

    public static object Dump(IShader shader, string outDir, string stage,
        string[] keywordFilter, string platformFilter, int maxDisassemblies,
        int rawEntryIndex = -1)
    {
        var notes = new List<string>();
        var written = new List<string>();
        var shaderName = shader.ParsedForm.Name_R.String;
        var safeName = string.Join("_", shaderName.Split(Path.GetInvalidFileNameChars().Append('/').Append(' ').ToArray()));

        // --- blob geometry: platforms x segments (flat pre-2019.3, nested after)
        uint[] platforms = shader.Platforms?.ToArray() ?? Array.Empty<uint>();
        uint[][] offsets = Nested(shader, s => s.Offsets_AssetList_AssetList_UInt32, s => s.Offsets_AssetList_UInt32, notes, "offsets");
        uint[][] compLens = Nested(shader, s => s.CompressedLengths_AssetList_AssetList_UInt32, s => s.CompressedLengths_AssetList_UInt32, notes, "compressedLengths");
        uint[][] decompLens = Nested(shader, s => s.DecompressedLengths_AssetList_AssetList_UInt32, s => s.DecompressedLengths_AssetList_UInt32, notes, "decompressedLengths");
        byte[] blob = shader.CompressedBlob ?? Array.Empty<byte>();

        var platformInfos = new List<object>();
        var variantSummaries = new List<object>();
        var disassemblies = new List<object>();
        object? globalsUnion = null;

        // --- variants come from the parsed form, shared across platforms
        List<Variant> variants = CollectVariants(shader, notes);
        var stageVariants = variants
            .Where(v => v.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var v in stageVariants.Take(400))
        {
            variantSummaries.Add(new
            {
                subShader = v.SubShader,
                pass = v.Pass,
                passName = v.PassName,
                lightMode = v.LightMode,
                blobIndex = v.BlobIndex,
                programType = TypeName(v.GpuProgramType),
                keywords = v.Keywords,
            });
        }
        if (stageVariants.Count > 400)
        {
            notes.Add($"variant list truncated to 400 of {stageVariants.Count}");
        }

        var distinctKeywords = stageVariants.SelectMany(v => v.Keywords).Distinct().OrderBy(k => k).ToList();

        // --- pick what to disassemble: keyword filter, else fewest keywords first
        var matched = stageVariants
            .Where(v => keywordFilter.All(f => v.Keywords.Any(k => k.Contains(f, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(v => v.Keywords.Length)
            .ToList();

        for (int p = 0; p < platforms.Length; p++)
        {
            var platformName = ((GPUPlatform)platforms[p]).ToString();
            var segCount = p < offsets.Length ? offsets[p].Length : 0;
            byte[][] segments = new byte[segCount][];
            var segNotes = new List<string>();
            for (int s = 0; s < segCount; s++)
            {
                try
                {
                    segments[s] = DecompressSegment(blob, offsets[p][s], compLens[p][s], decompLens[p][s]);
                }
                catch (Exception ex)
                {
                    segments[s] = Array.Empty<byte>();
                    segNotes.Add($"segment {s}: {ex.Message}");
                }
            }

            var entries = segments.Length > 0 ? ReadEntries(segments, segNotes) : Array.Empty<(int Offset, int Length, int Segment)>();
            platformInfos.Add(new
            {
                platform = platformName,
                id = platforms[p],
                segments = segCount,
                segmentBytes = segments.Select(sg => sg.Length).ToArray(),
                blobEntries = entries.Length,
                notes = segNotes,
            });

            if (!platformName.Contains(platformFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // shader-wide $Globals register map: union of every parseable
            // parameter entry plus all passes' CommonParameters. A name that
            // ever maps to two offsets is reported as conflicted, never trusted.
            if (globalsUnion is null)
            {
                var offsetsByName = new Dictionary<string, HashSet<int>>();
                var slotsByTexture = new Dictionary<string, HashSet<int>>();
                for (int idx = 0; idx < entries.Length; idx++)
                {
                    if (entries[idx].Length < 28 || entries[idx].Length > 65536)
                    {
                        continue;
                    }
                    var bytes = segments[entries[idx].Segment].AsSpan(entries[idx].Offset, entries[idx].Length).ToArray();
                    if (ShaderParameterBlob.TryParse(bytes, out _) is not { } parsedEntry)
                    {
                        continue;
                    }
                    foreach (var cb in parsedEntry.ConstantBuffers.Where(c => c.Name == "$Globals"))
                    {
                        foreach (var vp in cb.Params)
                        {
                            offsetsByName.TryAdd(vp.Name, new HashSet<int>());
                            offsetsByName[vp.Name].Add(vp.ByteOffset);
                        }
                    }
                    foreach (var tex in parsedEntry.Textures)
                    {
                        slotsByTexture.TryAdd(tex.Name, new HashSet<int>());
                        slotsByTexture[tex.Name].Add(tex.Slot);
                    }
                }
                foreach (var (name, offset) in CollectCommonGlobals(shader))
                {
                    offsetsByName.TryAdd(name, new HashSet<int>());
                    offsetsByName[name].Add(offset);
                }
                globalsUnion = new
                {
                    platform = platformName,
                    stable = offsetsByName.Where(kv => kv.Value.Count == 1)
                        .Select(kv => new
                        {
                            name = kv.Key,
                            byteOffset = kv.Value.First(),
                            register = $"c{kv.Value.First() / 16}.{"xyzw"[(kv.Value.First() % 16) / 4]}",
                        })
                        .OrderBy(x => x.byteOffset).ToList(),
                    conflicts = offsetsByName.Where(kv => kv.Value.Count > 1)
                        .Select(kv => new { name = kv.Key, offsets = kv.Value.OrderBy(o => o).ToList() }).ToList(),
                    textureSlots = slotsByTexture
                        .Select(kv => new { name = kv.Key, slots = kv.Value.OrderBy(s => s).ToList() })
                        .OrderBy(x => x.slots[0]).ToList(),
                };
            }

            if (rawEntryIndex >= 0 && rawEntryIndex < entries.Length)
            {
                var rawEntry = entries[rawEntryIndex];
                Directory.CreateDirectory(outDir);
                var rawEntryPath = Path.Combine(outDir, $"{safeName}.{platformName}.entry{rawEntryIndex}.bin");
                File.WriteAllBytes(rawEntryPath,
                    segments[rawEntry.Segment].AsSpan(rawEntry.Offset, rawEntry.Length).ToArray());
                written.Add(rawEntryPath);
            }

            foreach (var v in matched.Take(maxDisassemblies))
            {
                if (v.BlobIndex >= entries.Length)
                {
                    notes.Add($"{platformName}: blobIndex {v.BlobIndex} out of range ({entries.Length} entries)");
                    continue;
                }
                var entry = entries[v.BlobIndex];
                object? parsed = null;
                try
                {
                    var sub = ParseSubProgram(segments[entry.Segment], entry.Offset, entry.Length);
                    var baseName = $"{safeName}.{platformName}.ss{v.SubShader}p{v.Pass}.{v.Stage}.blob{v.BlobIndex}";
                    Directory.CreateDirectory(outDir);
                    var rawPath = Path.Combine(outDir, baseName + ".bin");
                    File.WriteAllBytes(rawPath, sub.Code);
                    written.Add(rawPath);

                    string? disasmPath = null;
                    string? method = null;
                    (disasmPath, method) = Disassemble(sub.Code, sub.ProgramType, Path.Combine(outDir, baseName), notes);
                    if (disasmPath != null)
                    {
                        written.Add(disasmPath);
                    }

                    // 2021.2+ player format: per-variant parameters live in their
                    // own blob entries. There is no serialized link from a player
                    // subprogram to its entry, so select by evidence: the correct
                    // entry's texture slots equal the disassembly's declared
                    // resources exactly.
                    object? paramBlob = null;
                    try
                    {
                        if (v.SubProgram is null && v.Program is not null && disasmPath is not null)
                        {
                            var asmText = File.ReadAllText(disasmPath);
                            var asmSlots = System.Text.RegularExpressions.Regex
                                .Matches(asmText, @"^dcl_resource_\w+[^\r\n]*\bt(\d+)\r?$",
                                    System.Text.RegularExpressions.RegexOptions.Multiline)
                                .Select(m => int.Parse(m.Groups[1].Value))
                                .ToHashSet();
                            // every $Globals register the program actually reads
                            var asmGlobalsRegisters = System.Text.RegularExpressions.Regex
                                .Matches(asmText, @"cb0\[(\d+)\]")
                                .Select(m => int.Parse(m.Groups[1].Value))
                                .ToHashSet();
                            // scan EVERY blob entry: strict validation rejects
                            // program entries, so only genuine parameter payloads
                            // survive. Unity hoists shared params into the
                            // program's CommonParameters, so the right entry
                            // satisfies commons UNION blob == asm's textures.
                            var commonSlots = CommonTextureSlots(v);
                            var commonRegisters = CommonGlobalsRegisters(v);
                            var matchIndices = new List<int>();
                            ShaderParameterBlob.Result? chosen = null;
                            int chosenIdx = -1, chosenRegisters = int.MaxValue;
                            int parseable = 0;
                            for (int idx = 0; idx < entries.Length; idx++)
                            {
                                var paramEntry = entries[idx];
                                if (paramEntry.Length < 28 || paramEntry.Length > 65536)
                                {
                                    continue;
                                }
                                var bytes = segments[paramEntry.Segment].AsSpan(paramEntry.Offset, paramEntry.Length).ToArray();
                                var parsedBlob = ShaderParameterBlob.TryParse(bytes, out _);
                                if (parsedBlob is null)
                                {
                                    continue;
                                }
                                parseable++;
                                var slots = parsedBlob.Textures.Select(t => t.Slot).ToHashSet();
                                slots.UnionWith(commonSlots);
                                if (!slots.SetEquals(asmSlots))
                                {
                                    continue;
                                }
                                // the entry (with commons) must explain every $Globals
                                // register the program reads; tightest coverage wins
                                var registers = new HashSet<int>(commonRegisters);
                                int own = 0;
                                foreach (var cb in parsedBlob.ConstantBuffers.Where(c => c.Name == "$Globals"))
                                {
                                    foreach (var vp in cb.Params)
                                    {
                                        int start = vp.ByteOffset / 16;
                                        int count = Math.Max(1, vp.Fields[4]) * (vp.Fields[3] != 0 ? Math.Max(1, vp.Fields[1]) : 1);
                                        for (int r = 0; r < count; r++)
                                        {
                                            registers.Add(start + r);
                                            own++;
                                        }
                                    }
                                }
                                if (!asmGlobalsRegisters.IsSubsetOf(registers))
                                {
                                    continue;
                                }
                                matchIndices.Add(idx);
                                if (own < chosenRegisters)
                                {
                                    chosen = parsedBlob;
                                    chosenIdx = idx;
                                    chosenRegisters = own;
                                }
                            }
                            if (chosen is not null)
                            {
                                var chosenEntry = entries[chosenIdx];
                                var paramPath = Path.Combine(outDir, baseName + ".params.bin");
                                File.WriteAllBytes(paramPath,
                                    segments[chosenEntry.Segment].AsSpan(chosenEntry.Offset, chosenEntry.Length).ToArray());
                                written.Add(paramPath);
                            }
                            paramBlob = new
                            {
                                parseableEntries = parseable,
                                asmTextureSlots = asmSlots.OrderBy(s => s).ToList(),
                                asmGlobalsRegisters = asmGlobalsRegisters.OrderBy(r => r).ToList(),
                                commonTextureSlots = commonSlots.OrderBy(s => s).ToList(),
                                matches = matchIndices,
                                chosen = chosenIdx,
                                parsed = chosen?.ToReport(),
                            };
                            if (matchIndices.Count != 1)
                            {
                                notes.Add($"parameter blob: {matchIndices.Count} entries match blob {v.BlobIndex}'s texture set");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"parameter blob: {ex.Message}");
                    }

                    parsed = new
                    {
                        platform = platformName,
                        blobIndex = v.BlobIndex,
                        passName = v.PassName,
                        lightMode = v.LightMode,
                        blobVersion = sub.Version,
                        programType = TypeName(sub.ProgramType),
                        blobKeywords = sub.Keywords,
                        variantKeywords = v.Keywords,
                        codeBytes = sub.Code.Length,
                        raw = rawPath,
                        disassembly = disasmPath,
                        method,
                        paramBlob,
                        parameters = v.SubProgram != null ? ParameterInfo(v.SubProgram, v.PassAsset) : CommonParameterInfo(v, notes),
                    };
                }
                catch (Exception ex)
                {
                    parsed = new
                    {
                        platform = platformName,
                        blobIndex = v.BlobIndex,
                        error = ex.Message,
                        headHex = entry.Segment < segments.Length && segments[entry.Segment].Length >= entry.Offset + 32
                            ? Convert.ToHexString(segments[entry.Segment], entry.Offset, Math.Min(64, entry.Length))
                            : null,
                    };
                }
                disassemblies.Add(parsed);
            }
        }

        return new
        {
            shader = shaderName,
            asset = shader.Name_R.String,
            collection = shader.Collection.Name,
            platforms = platformInfos,
            stage,
            stageVariantCount = stageVariants.Count,
            distinctKeywords,
            variants = variantSummaries,
            matchedForDisassembly = matched.Count,
            globalsUnion,
            disassemblies,
            written,
            notes,
        };
    }

    /// <summary>(name, byteOffset) pairs from every pass's CommonParameters
    /// $Globals across the whole shader - both stages, all passes.</summary>
    private static IEnumerable<(string Name, int Offset)> CollectCommonGlobals(IShader shader)
    {
        var results = new List<(string, int)>();
        try
        {
            foreach (var subShader in shader.ParsedForm.SubShaders)
            {
                foreach (var pass in subShader.Passes)
                {
                    var names = new Dictionary<int, string>();
                    foreach (var pair in pass.NameIndices)
                    {
                        names[pair.Value] = pair.Key.String;
                    }
                    foreach (var prog in new[] { pass.ProgVertex, pass.ProgFragment })
                    {
                        if (prog?.CommonParameters is not { } common)
                        {
                            continue;
                        }
                        foreach (var cb in common.ConstantBuffers.Cast<IConstantBuffer>())
                        {
                            if (names.GetValueOrDefault(cb.NameIndex) != "$Globals")
                            {
                                continue;
                            }
                            foreach (var vp in cb.VectorParams)
                            {
                                results.Add((names.GetValueOrDefault(vp.NameIndex, $"#{vp.NameIndex}"), vp.Index));
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return results;
    }

    private static string TypeName(int t) => t >= 0 && t < GpuProgramTypeNames.Length ? GpuProgramTypeNames[t] : $"type{t}";

    private static uint[][] Nested(IShader shader,
        Func<IShader, IEnumerable<IEnumerable<uint>>?> nested,
        Func<IShader, IEnumerable<uint>?> flat,
        List<string> notes, string label)
    {
        try
        {
            var n = nested(shader)?.Select(x => x.ToArray()).ToArray();
            if (n is { Length: > 0 })
            {
                return n;
            }
        }
        catch
        {
            // property not present for this unity version
        }
        try
        {
            var f = flat(shader)?.ToArray();
            if (f is { Length: > 0 })
            {
                notes.Add($"{label}: flat (pre-2019.3) layout");
                return f.Select(x => new[] { x }).ToArray();
            }
        }
        catch
        {
        }
        notes.Add($"{label}: empty");
        return Array.Empty<uint[]>();
    }

    private static byte[] DecompressSegment(byte[] blob, uint offset, uint compLen, uint decompLen)
    {
        if (decompLen == 0)
        {
            return Array.Empty<byte>();
        }
        var target = new byte[decompLen];
        if (compLen == 0 || compLen == decompLen)
        {
            Array.Copy(blob, offset, target, 0, decompLen);
            return target;
        }
        int decoded = LZ4Codec.Decode(blob, (int)offset, (int)compLen, target, 0, (int)decompLen);
        if (decoded != (int)decompLen)
        {
            throw new InvalidDataException($"lz4 decoded {decoded} of {decompLen}");
        }
        return target;
    }

    /// <summary>Entry directory in segment 0: count, then (offset, length[, segment]) per entry.</summary>
    private static (int Offset, int Length, int Segment)[] ReadEntries(byte[][] segments, List<string> notes)
    {
        var seg0 = segments[0];
        if (seg0.Length < 4)
        {
            return Array.Empty<(int, int, int)>();
        }
        int count = BitConverter.ToInt32(seg0, 0);
        if (count < 0 || count > 200_000)
        {
            notes.Add($"entry count {count} implausible");
            return Array.Empty<(int, int, int)>();
        }
        foreach (var entrySize in new[] { 12, 8 })
        {
            if (4 + (long)count * entrySize > seg0.Length)
            {
                continue;
            }
            var entries = new (int Offset, int Length, int Segment)[count];
            bool valid = true;
            for (int i = 0; i < count && valid; i++)
            {
                int pos = 4 + i * entrySize;
                int off = BitConverter.ToInt32(seg0, pos);
                int len = BitConverter.ToInt32(seg0, pos + 4);
                int seg = entrySize == 12 ? BitConverter.ToInt32(seg0, pos + 8) : 0;
                valid = off >= 0 && len >= 0 && seg >= 0 && seg < segments.Length
                    && (long)off + len <= segments[seg].Length;
                entries[i] = (off, len, seg);
            }
            if (valid)
            {
                if (entrySize == 8)
                {
                    notes.Add("entry directory: 8-byte entries (pre-2019.3)");
                }
                return entries;
            }
        }
        notes.Add("entry directory did not validate as 12- or 8-byte entries");
        return Array.Empty<(int, int, int)>();
    }

    private sealed record BlobSubProgram(int Version, int ProgramType, string[] Keywords, byte[] Code);

    /// <summary>
    /// Unity's LoadGpuProgramFromData layout. Version stamps: 201509030=5.3,
    /// 201608170=5.5+, 201806140=2019.1..2021.1 (adds local keywords),
    /// 202012090=2021.2+ (keywords move to the serialized form).
    /// </summary>
    private static BlobSubProgram ParseSubProgram(byte[] segment, int offset, int length)
    {
        using var br = new BinaryReader(new MemoryStream(segment, offset, length));
        int version = br.ReadInt32();
        int programType = br.ReadInt32();
        br.BaseStream.Position += 12;
        if (version >= 201608170)
        {
            br.BaseStream.Position += 4;
        }
        int keywordCount = br.ReadInt32();
        if (keywordCount < 0 || keywordCount > 1024)
        {
            throw new InvalidDataException($"blob version {version}: keyword count {keywordCount}");
        }
        var keywords = new string[keywordCount];
        for (int i = 0; i < keywordCount; i++)
        {
            keywords[i] = ReadAlignedString(br);
        }
        if (version >= 201806140 && version < 202012090)
        {
            int localCount = br.ReadInt32();
            for (int i = 0; i < localCount; i++)
            {
                ReadAlignedString(br);
            }
        }
        int codeLength = br.ReadInt32();
        if (codeLength < 0 || codeLength > br.BaseStream.Length - br.BaseStream.Position)
        {
            throw new InvalidDataException($"blob version {version}: code length {codeLength}");
        }
        return new BlobSubProgram(version, programType, keywords, br.ReadBytes(codeLength));
    }

    private static string ReadAlignedString(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > 4096)
        {
            throw new InvalidDataException($"string length {len}");
        }
        var s = Encoding.UTF8.GetString(br.ReadBytes(len));
        br.BaseStream.Position += (4 - (br.BaseStream.Position & 3)) & 3;
        return s;
    }

    // ----------------------------------------------------------------- variants

    private static List<Variant> CollectVariants(IShader shader, List<string> notes)
    {
        var result = new List<Variant>();
        var form = shader.ParsedForm;
        var globalKeywords = form.KeywordNames.Select(k => k.String).ToArray();

        for (int si = 0; si < form.SubShaders.Count; si++)
        {
            var subShader = form.SubShaders[si];
            for (int pi = 0; pi < subShader.Passes.Count; pi++)
            {
                var pass = subShader.Passes[pi];
                var passName = pass.UseName.String.Length > 0 ? pass.UseName.String : pass.Name_R.String;
                // reverse name table for keyword/parameter indices (pre-2021.2 path)
                var nameByIndex = new Dictionary<int, string>();
                foreach (var pair in pass.NameIndices)
                {
                    nameByIndex[pair.Value] = pair.Key.String;
                }

                foreach (var (stageName, prog) in new (string, ISerializedProgram?)[]
                         {
                             ("vertex", pass.ProgVertex), ("fragment", pass.ProgFragment),
                             ("geometry", pass.ProgGeometry), ("hull", pass.ProgHull), ("domain", pass.ProgDomain),
                         })
                {
                    if (prog is null)
                    {
                        continue;
                    }
                    foreach (var sub in prog.SubPrograms)
                    {
                        result.Add(new Variant(si, pi, passName, stageName, sub.BlobIndex, sub.GpuProgramType,
                            ResolveKeywords(sub, globalKeywords, nameByIndex), sub, pass));
                    }
                    try
                    {
                        for (int tierIndex = 0; tierIndex < prog.PlayerSubPrograms.Count; tierIndex++)
                        {
                            var tier = prog.PlayerSubPrograms[tierIndex];
                            for (int k = 0; k < tier.Count; k++)
                            {
                                var ps = tier[k];
                                var kws = ps.KeywordIndices
                                    .Select(i => i < globalKeywords.Length ? globalKeywords[i] : $"#{i}")
                                    .ToArray();
                                result.Add(new Variant(si, pi, passName, stageName, ps.BlobIndex, ps.GpuProgramType, kws, null, pass,
                                    prog, tierIndex, k));
                            }
                        }
                    }
                    catch
                    {
                        // PlayerSubPrograms absent before 2021.2
                    }
                }
            }
        }
        if (result.Count == 0)
        {
            notes.Add("no subprograms in parsed form");
        }
        return result;
    }

    private static string[] ResolveKeywords(ISerializedSubProgram sub, string[] globalKeywords, Dictionary<int, string> nameByIndex)
    {
        var keywords = new List<string>();
        try
        {
            foreach (var i in sub.KeywordIndices)
            {
                keywords.Add(i < globalKeywords.Length ? globalKeywords[i] : $"#{i}");
            }
        }
        catch
        {
            // 2021.2+ field, absent earlier
        }
        if (keywords.Count == 0)
        {
            try
            {
                foreach (var i in sub.GlobalKeywordIndices)
                {
                    keywords.Add(nameByIndex.GetValueOrDefault(i, $"#{i}"));
                }
                foreach (var i in sub.LocalKeywordIndices)
                {
                    keywords.Add(nameByIndex.GetValueOrDefault(i, $"#{i}"));
                }
            }
            catch
            {
            }
        }
        return keywords.ToArray();
    }

    // ----------------------------------------------------------- reflection info

    /// <summary>Constant buffer layout the game serialized for binding - names
    /// and byte offsets, valid even when the compiled blob is stripped.</summary>
    private static object ParameterInfo(ISerializedSubProgram sub, ISerializedPass pass)
    {
        var names = new Dictionary<int, string>();
        foreach (var pair in pass.NameIndices)
        {
            names[pair.Value] = pair.Key.String;
        }
        string N(int i) => names.GetValueOrDefault(i, $"#{i}");

        return new
        {
            constantBuffers = sub.ConstantBuffers.Select(cb => new
            {
                name = N(cb.NameIndex),
                size = cb.Size,
                vectors = cb.VectorParams.Select(v => new
                {
                    name = N(v.NameIndex),
                    byteOffset = v.Index,
                    register = $"c{v.Index / 16}.{"xyzw"[(v.Index % 16) / 4]}",
                    dim = v.Dim,
                }).ToList(),
            }).ToList(),
            constantBufferBindings = sub.ConstantBufferBindings.Select(b => new { name = N(b.NameIndex), slot = b.Index }).ToList(),
            textures = sub.TextureParams.Select(t => new { name = N(t.NameIndex), slot = t.Index, sampler = t.SamplerIndex }).ToList(),
        };
    }

    /// <summary>Texture slots the program-level CommonParameters already bind:
    /// Unity hoists variant-shared parameters there and stores only the rest
    /// per variant, so the full reflection is commons UNION param blob.</summary>
    private static HashSet<int> CommonTextureSlots(Variant v)
    {
        var slots = new HashSet<int>();
        try
        {
            var prog = v.Stage switch
            {
                "vertex" => v.PassAsset.ProgVertex,
                "fragment" => v.PassAsset.ProgFragment,
                _ => null,
            };
            if (prog?.CommonParameters is { } common)
            {
                foreach (var t in common.TextureParams.Cast<ITextureParameter>())
                {
                    slots.Add(t.Index);
                }
            }
        }
        catch
        {
        }
        return slots;
    }

    /// <summary>$Globals registers covered by the program-level CommonParameters.</summary>
    private static HashSet<int> CommonGlobalsRegisters(Variant v)
    {
        var registers = new HashSet<int>();
        try
        {
            var prog = v.Stage switch
            {
                "vertex" => v.PassAsset.ProgVertex,
                "fragment" => v.PassAsset.ProgFragment,
                _ => null,
            };
            if (prog?.CommonParameters is not { } common)
            {
                return registers;
            }
            var names = new Dictionary<int, string>();
            foreach (var pair in v.PassAsset.NameIndices)
            {
                names[pair.Value] = pair.Key.String;
            }
            foreach (var cb in common.ConstantBuffers.Cast<IConstantBuffer>())
            {
                if (names.GetValueOrDefault(cb.NameIndex) != "$Globals")
                {
                    continue;
                }
                foreach (var p in cb.VectorParams)
                {
                    int start = p.Index / 16;
                    for (int r = 0; r < Math.Max(1, p.ArraySize); r++)
                    {
                        registers.Add(start + r);
                    }
                }
                foreach (var m in cb.MatrixParams)
                {
                    int start = m.Index / 16;
                    for (int r = 0; r < 4 * Math.Max(1, m.ArraySize); r++)
                    {
                        registers.Add(start + r);
                    }
                }
            }
        }
        catch
        {
        }
        return registers;
    }

    private static object? CommonParameterInfo(Variant v, List<string> notes)
    {
        // player subprograms (2021.2+) keep per-variant parameters in separate
        // blob entries; the program-level common parameters cover the shared set
        try
        {
            var prog = v.Stage switch
            {
                "vertex" => v.PassAsset.ProgVertex,
                "fragment" => v.PassAsset.ProgFragment,
                _ => null,
            };
            if (prog?.CommonParameters is not { } common)
            {
                return null;
            }
            var names = new Dictionary<int, string>();
            foreach (var pair in v.PassAsset.NameIndices)
            {
                names[pair.Value] = pair.Key.String;
            }
            string N(int i) => names.GetValueOrDefault(i, $"#{i}");
            return new
            {
                source = "CommonParameters",
                constantBuffers = common.ConstantBuffers.Cast<IConstantBuffer>().Select(cb => new
                {
                    name = N(cb.NameIndex),
                    size = cb.Size,
                    vectors = cb.VectorParams.Select(vp => new
                    {
                        name = N(vp.NameIndex),
                        byteOffset = vp.Index,
                        register = $"c{vp.Index / 16}.{"xyzw"[(vp.Index % 16) / 4]}",
                        dim = vp.Dim,
                    }).ToList(),
                }).ToList(),
                textures = common.TextureParams.Cast<ITextureParameter>().Select(t => new { name = N(t.NameIndex), slot = t.Index, sampler = t.SamplerIndex }).ToList(),
            };
        }
        catch (Exception ex)
        {
            notes.Add($"common parameters unavailable: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------- disassembly

    private static (string? Path, string? Method) Disassemble(byte[] code, int programType, string basePath, List<string> notes)
    {
        // GL and Metal program code is already source text
        if (programType is >= 1 and <= 8 or 23 or 24)
        {
            var path = basePath + ".glsl";
            File.WriteAllText(path, Encoding.UTF8.GetString(code));
            return (path, "source-text");
        }
        // D3D11: unity header (6 bytes historically, 38 in 2021.2+ blobs),
        // then a DXBC container - scan for the magic
        if (programType is >= 15 and <= 22)
        {
            for (int start = 0; start <= Math.Min(256, code.Length - 4); start++)
            {
                if (code.Length >= start + 4 && BitConverter.ToUInt32(code, start) == 0x43425844) // 'DXBC'
                {
                    var dxbc = code[start..];
                    var text = D3DDisassemble(dxbc, notes);
                    if (text != null)
                    {
                        var path = basePath + ".dxbc.asm";
                        File.WriteAllText(path, text);
                        return (path, $"d3dcompiler_47 (container at +{start})");
                    }
                    return (null, null);
                }
            }
            notes.Add("no DXBC magic in first 16 bytes of D3D11 program");
            return (null, null);
        }
        // Vulkan: SMOL-V, possibly behind a small stage-offset header
        if (programType == 25)
        {
            for (int start = 0; start <= 256; start += 4)
            {
                if (code.Length <= start + 24)
                {
                    break;
                }
                var span = code.AsSpan(start);
                if (Smolv.SmolvDecoder.GetDecodedBufferSize(span) > 0)
                {
                    var spirv = Smolv.SmolvDecoder.Decode(code[start..]);
                    if (spirv == null)
                    {
                        continue;
                    }
                    var module = SpirV.Module.ReadFrom(new MemoryStream(spirv));
                    var text = new SpirV.Disassembler().Disassemble(module);
                    var path = basePath + ".spvasm";
                    File.WriteAllText(path, text);
                    return (path, $"smolv+spirv (data at +{start})");
                }
                if (BitConverter.ToUInt32(code, start) == 0x07230203) // raw SPIR-V
                {
                    var module = SpirV.Module.ReadFrom(new MemoryStream(code[start..]));
                    var text = new SpirV.Disassembler().Disassemble(module);
                    var path = basePath + ".spvasm";
                    File.WriteAllText(path, text);
                    return (path, $"raw spirv (data at +{start})");
                }
            }
            notes.Add("no SMOL-V/SPIR-V signature found in vulkan program");
            return (null, null);
        }
        notes.Add($"no disassembler for program type {TypeName(programType)}");
        return (null, null);
    }

    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DDisassemble(byte[] pSrcData, nuint srcDataSize, uint flags, IntPtr szComments, out IntPtr ppDisassembly);

    private delegate IntPtr BlobGetPointerFn(IntPtr self);
    private delegate nuint BlobGetSizeFn(IntPtr self);
    private delegate uint BlobReleaseFn(IntPtr self);

    /// <summary>ID3DBlob through raw vtable calls ([3] GetBufferPointer,
    /// [4] GetBufferSize, [2] Release) - the ComImport RCW path corrupts
    /// the call and fails with garbage HRESULTs.</summary>
    private static string? D3DDisassemble(byte[] dxbc, List<string> notes)
    {
        try
        {
            int hr = D3DDisassemble(dxbc, (nuint)dxbc.Length, 0, IntPtr.Zero, out var blob);
            if (hr != 0 || blob == IntPtr.Zero)
            {
                notes.Add($"D3DDisassemble failed: 0x{hr:X8}");
                return null;
            }
            try
            {
                IntPtr vtbl = Marshal.ReadIntPtr(blob);
                var getPointer = Marshal.GetDelegateForFunctionPointer<BlobGetPointerFn>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
                var getSize = Marshal.GetDelegateForFunctionPointer<BlobGetSizeFn>(Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size));
                var text = Marshal.PtrToStringAnsi(getPointer(blob), (int)getSize(blob));
                return text.TrimEnd('\0');
            }
            finally
            {
                var release = Marshal.GetDelegateForFunctionPointer<BlobReleaseFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob), 2 * IntPtr.Size));
                release(blob);
            }
        }
        catch (Exception ex)
        {
            notes.Add($"d3dcompiler_47 unavailable: {ex.Message}");
            return null;
        }
    }
}
