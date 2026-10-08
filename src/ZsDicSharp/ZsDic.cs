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
    private const string DictionaryPack = "Pack/ZsDic.pack.zs";

    /// <summary>The level TKVSC's Python backend compressed at.</summary>
    public const int DefaultLevel = 16;

    private readonly byte[]?[] _dictionaries = new byte[]?[4];

    // Keyed by dictionary and, for compressors, level. The dictionary bytes decide which ZsDic a cached
    // object came from, so each instance keeps its own.
    [ThreadStatic] private static Dictionary<(ZsDic, ZsDictionary), Decompressor>? _decompressors;
    [ThreadStatic] private static Dictionary<(ZsDic, ZsDictionary, int), Compressor>? _compressors;

    private ZsDic() { }

    public static ZsDic FromRomfs(string romfs)
    {
        string path = Path.Combine(romfs, DictionaryPack.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No dictionary pack at {path}. Without it only dictionary-less files can be read; " +
                $"use {nameof(Dictionaryless)} if that is what you want.", path);

        return FromPack(File.ReadAllBytes(path));
    }

    public static ZsDic FromPack(ReadOnlySpan<byte> pack)
    {
        ZsDic dic = new();

        byte[] sarc = dic.Decompress(pack, ZsDictionary.None);

        foreach ((string name, byte[] bytes) in Sarc.Read(sarc))
        {
            ZsDictionary? which = name switch
            {
                "zs.zsdic" => ZsDictionary.Common,
                "pack.zsdic" => ZsDictionary.Pack,
                "bcett.byml.zsdic" => ZsDictionary.BcettByml,
                _ => null,
            };

            if (which is { } key) dic._dictionaries[(int)key] = bytes;
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
        => Decompress(File.ReadAllBytes(path), DictionaryFor(path));

    /// <summary>
    /// Decompresses with the dictionary the path's name calls for. A file that doesn't follow the naming is
    /// tried against the common dictionary, and a <c>.pack.zs</c> against the pack dictionary, before giving up.
    /// </summary>
    public byte[] Decompress(ReadOnlySpan<byte> data, string path)
    {
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
        => Compress(data, DictionaryFor(path), level);

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

    public void Dispose()
    {
        System.Array.Clear(_dictionaries);
        if (_decompressors is not null)
            foreach (var key in _decompressors.Keys.Where(k => k.Item1 == this).ToList()) _decompressors.Remove(key);
        if (_compressors is not null)
            foreach (var key in _compressors.Keys.Where(k => k.Item1 == this).ToList()) _compressors.Remove(key);
    }
}
