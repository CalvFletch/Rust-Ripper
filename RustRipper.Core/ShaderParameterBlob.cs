using System.Text;

namespace RustRipper.Core;

/// <summary>
/// Parser for 2021.2+ per-variant shader parameter blob entries. Layout
/// read from actual Rust blobs (blob version 202012090):
///
///   int32 version, int32 hdr1, int32[4] (zero in fragment programs)
///   cbuffer records until the resource section:
///     alignedString name, int32 usedSize, int32 paramCount,
///     paramCount x { alignedString name, int32[6] - [5] = byte offset },
///     int32 structCount (must be 0 - abort otherwise)
///   int32 resourceCount, then typed records:
///     alignedString name, int32 flag,
///     flag 0 (texture): int32 slot, int32 samplerIndex, int32 dim
///     flag 1 (cbuffer binding) / 2 (buffer): int32 slot, int32 extra
///
/// Strictly validated; on any implausible value the parse aborts so a wrong
/// layout can never silently mislabel registers. Correct entry selection is
/// the caller's job (match texture slots against the disassembly).
/// </summary>
public static class ShaderParameterBlob
{
    public sealed record VectorParam(string Name, int ByteOffset, int[] Fields);
    public sealed record CBuffer(string Name, int UsedSize, List<VectorParam> Params);
    public sealed record Resource(string Name, int Flag, int Slot, int Sampler, int Dim);
    public sealed record Result(int Version, int Header1, List<CBuffer> ConstantBuffers, List<Resource> Resources)
    {
        public IEnumerable<Resource> Textures => Resources.Where(r => r.Flag == 0);

        public object ToReport() => new
        {
            source = "ParameterBlob",
            constantBuffers = ConstantBuffers.Select(cb => new
            {
                name = cb.Name,
                size = cb.UsedSize,
                vectors = cb.Params.Select(p => new
                {
                    name = p.Name,
                    byteOffset = p.ByteOffset,
                    register = $"c{p.ByteOffset / 16}.{"xyzw"[(p.ByteOffset % 16) / 4]}",
                    fields = p.Fields,
                }).ToList(),
            }).ToList(),
            textures = Resources.Where(r => r.Flag == 0)
                .Select(r => new { name = r.Name, slot = r.Slot, sampler = r.Sampler, dim = r.Dim }).ToList(),
            buffers = Resources.Where(r => r.Flag != 0)
                .Select(r => new { name = r.Name, flag = r.Flag, slot = r.Slot }).ToList(),
        };
    }

    public static Result? TryParse(byte[] data, out string? error)
    {
        error = null;
        try
        {
            return Parse(data);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static Result Parse(byte[] data)
    {
        int pos = 0;
        int Int()
        {
            if (pos + 4 > data.Length)
            {
                throw new InvalidDataException($"read past end at {pos}");
            }
            int v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }
        string Str()
        {
            int len = Int();
            if (len < 0 || len > 256 || pos + len > data.Length)
            {
                throw new InvalidDataException($"string length {len} at {pos - 4}");
            }
            var s = Encoding.ASCII.GetString(data, pos, len);
            pos += len;
            pos = (pos + 3) & ~3;
            return s;
        }
        bool LooksLikeString()
        {
            if (pos + 4 > data.Length)
            {
                return false;
            }
            int len = BitConverter.ToInt32(data, pos);
            if (len < 1 || len > 64 || pos + 4 + len > data.Length)
            {
                return false;
            }
            for (int i = 0; i < len; i++)
            {
                byte b = data[pos + 4 + i];
                if (b < 0x20 || b >= 0x7F)
                {
                    return false;
                }
            }
            return true;
        }

        int version = Int();
        if (version < 201500000 || version > 210000000)
        {
            throw new InvalidDataException($"blob version {version} implausible");
        }
        int header1 = Int();
        for (int i = 0; i < 4; i++)
        {
            Int();
        }

        var cbuffers = new List<CBuffer>();
        while (LooksLikeString())
        {
            int save = pos;
            string name = Str();
            int usedSize = Int();
            int paramCount = Int();
            if (usedSize < 0 || usedSize > 1 << 20 || paramCount < 0 || paramCount > 4096)
            {
                // not a cbuffer record - rewind, this is the resource section
                pos = save;
                break;
            }
            var parameters = new List<VectorParam>(paramCount);
            for (int i = 0; i < paramCount; i++)
            {
                string paramName = Str();
                var fields = new int[6];
                for (int f = 0; f < 6; f++)
                {
                    fields[f] = Int();
                }
                int offset = fields[5];
                if (offset < 0 || offset > usedSize)
                {
                    throw new InvalidDataException($"{name}.{paramName} offset {offset} exceeds {usedSize}");
                }
                parameters.Add(new VectorParam(paramName, offset, fields));
            }
            int structCount = Int();
            if (structCount < 0 || structCount > 64)
            {
                throw new InvalidDataException($"cbuffer {name}: struct count {structCount}");
            }
            for (int s = 0; s < structCount; s++)
            {
                // {name, index, arraySize, structSize, vectorCount, members}
                string structName = Str();
                int structIndex = Int();
                int arraySize = Int();
                int structSize = Int();
                if (structSize < 0 || structSize > 1 << 16 || arraySize < 0 || arraySize > 4096)
                {
                    throw new InvalidDataException($"struct {structName}: size {structSize} x {arraySize}");
                }
                int memberCount = Int();
                if (memberCount < 0 || memberCount > 256)
                {
                    throw new InvalidDataException($"struct {structName}: {memberCount} members");
                }
                for (int m = 0; m < memberCount; m++)
                {
                    string memberName = Str();
                    var fields = new int[6];
                    for (int f = 0; f < 6; f++)
                    {
                        fields[f] = Int();
                    }
                    // struct members surface as "Struct.Member" at struct-relative offsets
                    parameters.Add(new VectorParam($"{structName}.{memberName}", structIndex + fields[5], fields));
                }
            }
            cbuffers.Add(new CBuffer(name, usedSize, parameters));
        }

        int resourceCount = Int();
        if (resourceCount < 0 || resourceCount > 4096)
        {
            throw new InvalidDataException($"resource count {resourceCount}");
        }
        var resources = new List<Resource>(resourceCount);
        for (int i = 0; i < resourceCount; i++)
        {
            string name = Str();
            int flag = Int();
            if (flag == 0)
            {
                int slot = Int();
                int sampler = Int();
                int dim = Int();
                if (slot < 0 || slot > 256)
                {
                    throw new InvalidDataException($"texture {name} slot {slot}");
                }
                resources.Add(new Resource(name, flag, slot, sampler, dim));
            }
            else if (flag is 1 or 2 or 3 or 4)
            {
                // 1 = cbuffer binding, 2 = buffer, 4 = inline sampler (name empty)
                int slot = Int();
                int extra = Int();
                resources.Add(new Resource(name, flag, slot, extra, -1));
            }
            else
            {
                throw new InvalidDataException($"resource {name} flag {flag}");
            }
        }

        // optional sampler section (present in some entries), then at most two
        // trailing ints close the payload - size-validated, never guessed
        if (data.Length - pos > 8)
        {
            int n = Int();
            long left = data.Length - pos;
            if (n >= 0 && n <= 256 && left - (long)n * 8 is 0 or 4 or 8)
            {
                pos += n * 8;
            }
            else
            {
                throw new InvalidDataException($"unrecognized tail: count {n}, {left} bytes left");
            }
        }
        if (data.Length - pos > 8)
        {
            throw new InvalidDataException($"{data.Length - pos} unread bytes - layout mismatch");
        }
        return new Result(version, header1, cbuffers, resources);
    }
}
