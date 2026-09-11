using ZstdSharp;

namespace ZsDicSharp;

/// <summary>The dictionaries the game ships, and the option of using none.</summary>
public enum ZsDictionary
{
    /// <summary>Plain zstd. What the dictionary pack itself is compressed with.</summary>
    None,

    /// <summary>zs.zsdic - everything that is not a pack or a placement file.</summary>
    Common,

    /// <summary>pack.zsdic - SARC archives.</summary>
    Pack,

    /// <summary>bcett.byml.zsdic - actor placement.</summary>
    BcettByml,
}

/// <summary>
/// Decompresses romfs files, picking the dictionary each one was compressed against.
/// </summary>
/// <remarks>
/// Almost everything shipped is zstd, and most of it is compressed against one of three
/// 128 KB dictionaries the game carries. Which one applies is not recorded in the file - it
/// follows from where the file sits and what it is called - so decompressing anything means
/// knowing that rule, which is why it lives here rather than in each format reader.
///
/// The dictionaries are themselves inside <c>Pack/ZsDic.pack.zs</c>, and that archive is the
/// one thing compressed without them; reading it is the bootstrap.
/// </remarks>
public sealed class ZsDic : IDisposable
{
    private const string DictionaryPack = "Pack/ZsDic.pack.zs";

    private readonly byte[]?[] _dictionaries = new byte[]?[4];

    // A decompressor holds a dictionary once loaded and is not safe to share across threads,
    // so each thread keeps its own set. Loading 128 KB per call would otherwise dominate the
    // cost of reading a few thousand small files.
    [ThreadStatic] private static Decompressor?[]? _decompressors;

    private ZsDic() { }

    /// <summary>Reads the dictionaries out of a romfs.</summary>
    /// <param name="romfs">The romfs root, the folder holding <c>Pack</c>.</param>
    /// <exception cref="FileNotFoundException">The dictionary pack is not there.</exception>
    public static ZsDic FromRomfs(string romfs)
    {
        string path = Path.Combine(romfs, DictionaryPack.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No dictionary pack at {path}. Without it only dictionary-less files can be read; " +
                $"use {nameof(Dictionaryless)} if that is what you want.", path);

        return FromPack(File.ReadAllBytes(path));
    }

    /// <summary>Reads the dictionaries from the bytes of <c>ZsDic.pack.zs</c>.</summary>
    public static ZsDic FromPack(ReadOnlySpan<byte> pack)
    {
        ZsDic dic = new();

        // The pack that holds the dictionaries cannot itself need one.
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

    /// <summary>A decompressor with no dictionaries, for files that were compressed without one.</summary>
    public static ZsDic Dictionaryless() => new();

    /// <summary>True when the dictionary was found in the pack.</summary>
    public bool Has(ZsDictionary dictionary)
        => dictionary is ZsDictionary.None || _dictionaries[(int)dictionary] is not null;

    /// <summary>
    /// The dictionary a file was compressed against, from its name.
    /// </summary>
    /// <remarks>
    /// The extension decides it, and the compound ones have to be tested before the plain
    /// <c>.zs</c>: a placement file is <c>.bcett.byml.zs</c> and an archive is <c>.pack.zs</c>,
    /// both of which end in <c>.zs</c> without wanting the dictionary that ending implies.
    /// The pack holding the dictionaries is the exception that has none.
    /// </remarks>
    public static ZsDictionary DictionaryFor(string path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(path.AsSpan());

        if (name.EndsWith("ZsDic.pack.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.None;
        if (name.EndsWith(".bcett.byml.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.BcettByml;
        if (name.EndsWith(".pack.zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.Pack;
        if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)) return ZsDictionary.Common;

        return ZsDictionary.None;
    }

    /// <summary>Reads a file and decompresses it with whichever dictionary its name calls for.</summary>
    public byte[] DecompressFile(string path)
        => Decompress(File.ReadAllBytes(path), DictionaryFor(path));

    /// <summary>Decompresses data with whichever dictionary <paramref name="path"/> calls for.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> data, string path)
        => Decompress(data, DictionaryFor(path));

    /// <summary>Decompresses data with a named dictionary.</summary>
    /// <exception cref="InvalidOperationException">That dictionary was never loaded.</exception>
    public byte[] Decompress(ReadOnlySpan<byte> data, ZsDictionary dictionary)
    {
        byte[]? bytes = _dictionaries[(int)dictionary];
        if (dictionary is not ZsDictionary.None && bytes is null)
            throw new InvalidOperationException(
                $"The {dictionary} dictionary was not loaded; build this from a romfs to get it.");

        Decompressor decompressor = For(dictionary, bytes);

        // Zstd records the decompressed size in the frame, so a single call is enough; the
        // hint only matters for frames that do not carry it.
        return decompressor.Unwrap(data).ToArray();
    }

    /// <summary>True when the data starts with a zstd frame.</summary>
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
        // The decompressors are per thread and hold only what the runtime can reclaim; the
        // dictionaries are plain arrays. Nothing here owns an unmanaged handle of its own.
        System.Array.Clear(_dictionaries);
    }
}
