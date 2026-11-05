using System.Buffers.Binary;
using ZstdSharp;

namespace ZsDicSharp;

public enum ZsDictionary
{
    None,

    Common,

    Pack,

    BcettByml,
}

public sealed class ZsDic : IDisposable
{
    private const string DictionaryPack = "Pack/ZsDic.pack";

    private const uint DictionaryMagic = 0xEC30A437;

    /// <summary>The level TKVSC's Python backend compressed at.</summary>
    public const int DefaultLevel = 16;

    private readonly byte[]?[] _dictionaries = new byte[]?[4];

    private readonly Dictionary<uint, byte[]> _byId = [];

    // Dictionaries past the three every game has, by the name before ".zsdic": files ending ".<name>.zs" use them.
    private readonly Dictionary<string, byte[]> _extra = new(StringComparer.OrdinalIgnoreCase);

    // Keyed by dictionary and, for compressors, level. The dictionary bytes decide which ZsDic a cached
    // object came from, so each instance keeps its own.
    [ThreadStatic] private static Dictionary<(ZsDic, ZsDictionary), Decompressor>? _decompressors;
    [ThreadStatic] private static Dictionary<(ZsDic, ZsDictionary, int), Compressor>? _compressors;
    [ThreadStatic] private static Dictionary<(ZsDic, uint), Decompressor>? _decompressorsById;
    [ThreadStatic] private static Dictionary<(ZsDic, string, int), Compressor>? _extraCompressors;

    private ZsDic() { }

    public static ZsDic FromRomfs(string romfs)
    {
        string path = Path.Combine(romfs, DictionaryPack.Replace('/', Path.DirectorySeparatorChar));
        string? found = new[] { path + ".zs", path }.FirstOrDefault(File.Exists);
        if (found is null)
            throw new FileNotFoundException(
                $"No dictionary pack at {path}[.zs]. Without it only dictionary-less files can be read; " +
                $"use {nameof(Dictionaryless)} if that is what you want.", path);

        return FromPack(File.ReadAllBytes(found));
    }

    public static ZsDic FromPack(ReadOnlySpan<byte> pack)
    {
        ZsDic dic = new();

        byte[] sarc = IsCompressed(pack) ? dic.Decompress(pack, ZsDictionary.None) : pack.ToArray();

        foreach ((string name, byte[] bytes) in Sarc.Read(sarc))
        {
            if (bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == DictionaryMagic
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) is not 0 and var id)
                dic._byId[id] = bytes;

            ZsDictionary? which = name switch
            {
                "zs.zsdic" => ZsDictionary.Common,
                "pack.zsdic" => ZsDictionary.Pack,
                "bcett.byml.zsdic" => ZsDictionary.BcettByml,
                _ => null,
            };

            if (which is { } key) dic._dictionaries[(int)key] = bytes;
            else if (name.EndsWith(".zsdic", StringComparison.OrdinalIgnoreCase)) dic._extra[name[..^".zsdic".Length]] = bytes;
        }

        return dic;
    }

    public static ZsDic Dictionaryless() => new();

    public bool Has(ZsDictionary dictionary)
        => dictionary is ZsDictionary.None || _dictionaries[(int)dictionary] is not null;

    public static ZsDictionary DictionaryFor(string path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(path.AsSpan());

        if (name.EndsWith("ZsDic.pack.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.None;
        if (name.EndsWith(".bcett.byml.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.BcettByml;
        if (name.EndsWith(".pack.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.Pack;
        if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.Common;

        return ZsDictionary.None;
    }

    public byte[] DecompressFile(string path)
        => Decompress(File.ReadAllBytes(path), path);

    /// <summary>The dictionary id a zstd frame's header names, or 0 when it names none.</summary>
    public static uint FrameDictionaryId(ReadOnlySpan<byte> frame)
    {
        if (!IsCompressed(frame) || frame.Length < 5) return 0;

        byte descriptor = frame[4];
        int at = (descriptor & 0x20) != 0 ? 5 : 6;

        return (descriptor & 3) switch
        {
            1 when frame.Length >= at + 1 => frame[at],
            2 when frame.Length >= at + 2 => BinaryPrimitives.ReadUInt16LittleEndian(frame[at..]),
            3 when frame.Length >= at + 4 => BinaryPrimitives.ReadUInt32LittleEndian(frame[at..]),
            _ => 0,
        };
    }

    /// <summary>
    /// Decompresses with the dictionary the frame's header names. A frame naming none this pack holds falls back
    /// to the path's name: a file that doesn't follow the naming is tried against the common dictionary, and a
    /// <c>.pack.zs</c> against the pack dictionary, before giving up.
    /// </summary>
    public byte[] Decompress(ReadOnlySpan<byte> data, string path)
    {
        uint id = FrameDictionaryId(data);
        if (_byId.TryGetValue(id, out byte[]? named))
            return ForId(id, named).Unwrap(data).ToArray();

        ZsDictionary primary = DictionaryFor(path);
        try
        {
            return Decompress(data, primary);
        }
        catch (ZstdException) when (primary is not ZsDictionary.Common && _dictionaries[(int)ZsDictionary.Common] is not null)
        {
            try
            {
                return Decompress(data, ZsDictionary.Common);
            }
            catch (ZstdException) when (primary is not ZsDictionary.Pack && path.EndsWith(".pack.zs", StringComparison.OrdinalIgnoreCase)
                                        && _dictionaries[(int)ZsDictionary.Pack] is not null)
            {
                return Decompress(data, ZsDictionary.Pack);
            }
        }
    }

    /// <summary>Compresses a file for <paramref name="path"/> with the dictionary its name calls for.</summary>
    public byte[] Compress(ReadOnlySpan<byte> data, string path, int level = DefaultLevel)
    {
        if (ExtraNameFor(path) is not { } extra) return Compress(data, DictionaryFor(path), level);

        _extraCompressors ??= [];
        if (!_extraCompressors.TryGetValue((this, extra, level), out Compressor? compressor))
        {
            compressor = new Compressor(level);
            compressor.LoadDictionary(_extra[extra]);
            _extraCompressors[(this, extra, level)] = compressor;
        }

        return compressor.Wrap(data).ToArray();
    }

    // The longest extra dictionary name the file's name ends in, followed by ".zs".
    private string? ExtraNameFor(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)) return null;

        string? best = null;
        foreach (string extra in _extra.Keys)
            if ((best is null || extra.Length > best.Length) && name.EndsWith($".{extra}.zs", StringComparison.OrdinalIgnoreCase))
                best = extra;
        return best;
    }

    public byte[] Compress(ReadOnlySpan<byte> data, ZsDictionary dictionary, int level = DefaultLevel)
    {
        byte[]? bytes = _dictionaries[(int)dictionary];
        if (dictionary is not ZsDictionary.None && bytes is null)
            throw new InvalidOperationException(
                $"The {dictionary} dictionary was not loaded; build this from a romfs to get it.");

        _compressors ??= [];
        if (!_compressors.TryGetValue((this, dictionary, level), out Compressor? compressor))
        {
            compressor = new Compressor(level);
            if (bytes is not null) compressor.LoadDictionary(bytes);
            _compressors[(this, dictionary, level)] = compressor;
        }

        return compressor.Wrap(data).ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, ZsDictionary dictionary)
    {
        byte[]? bytes = _dictionaries[(int)dictionary];
        if (dictionary is not ZsDictionary.None && bytes is null)
            throw new InvalidOperationException(
                $"The {dictionary} dictionary was not loaded; build this from a romfs to get it.");

        Decompressor decompressor = For(dictionary, bytes);

        return decompressor.Unwrap(data).ToArray();
    }

    public static bool IsCompressed(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;

    private Decompressor For(ZsDictionary dictionary, byte[]? bytes)
    {
        _decompressors ??= [];

        if (_decompressors.TryGetValue((this, dictionary), out Decompressor? existing)) return existing;

        Decompressor made = new();
        if (bytes is not null) made.LoadDictionary(bytes);

        return _decompressors[(this, dictionary)] = made;
    }

    private Decompressor ForId(uint id, byte[] bytes)
    {
        _decompressorsById ??= [];

        if (_decompressorsById.TryGetValue((this, id), out Decompressor? existing)) return existing;

        Decompressor made = new();
        made.LoadDictionary(bytes);

        return _decompressorsById[(this, id)] = made;
    }

    public void Dispose()
    {
        System.Array.Clear(_dictionaries);
        _byId.Clear();
        _extra.Clear();
        if (_decompressorsById is not null)
            foreach (var key in _decompressorsById.Keys.Where(k => k.Item1 == this).ToList()) _decompressorsById.Remove(key);
        if (_decompressors is not null)
            foreach (var key in _decompressors.Keys.Where(k => k.Item1 == this).ToList()) _decompressors.Remove(key);
        if (_extraCompressors is not null)
            foreach (var key in _extraCompressors.Keys.Where(k => k.Item1 == this).ToList()) _extraCompressors.Remove(key);
        if (_compressors is not null)
            foreach (var key in _compressors.Keys.Where(k => k.Item1 == this).ToList()) _compressors.Remove(key);
    }
}
