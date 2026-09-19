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

    private readonly byte[]?[] _dictionaries = new byte[]?[4];

    [ThreadStatic] private static Decompressor?[]? _decompressors;

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

    public byte[] Decompress(ReadOnlySpan<byte> data, string path)
        => Decompress(data, DictionaryFor(path));

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

    private static Decompressor For(ZsDictionary dictionary, byte[]? bytes)
    {
        _decompressors ??= new Decompressor?[4];

        if (_decompressors[(int)dictionary] is { } existing) return existing;

        Decompressor made = new();
        if (bytes is not null) made.LoadDictionary(bytes);

        return _decompressors[(int)dictionary] = made;
    }

    public void Dispose()
    {
        System.Array.Clear(_dictionaries);
    }
}
