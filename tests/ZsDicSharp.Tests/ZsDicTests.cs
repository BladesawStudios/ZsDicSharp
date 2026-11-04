using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace ZsDicSharp.Tests;

public class ZsDicTests
{
    private static string? TotkRoot
        => Environment.GetEnvironmentVariable("TOTK_ROMFS") is { } root && File.Exists(Path.Combine(root, "Pack", "ZsDic.pack.zs")) ? root : null;

    [Theory]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x58 }, 0u)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x58, 0x2A }, 0x2Au)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x21, 0x2A }, 0x2Au)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x22, 0x34, 0x12 }, 0x1234u)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x03, 0x58, 0x78, 0x56, 0x34, 0x12 }, 0x12345678u)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x03, 0x58, 0x78 }, 0u)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, 0u)]
    [InlineData(new byte[] { 0x53, 0x41, 0x52, 0x43, 0x03 }, 0u)]
    public void TheFrameHeaderNamesTheDictionary(byte[] frame, uint expected)
        => Assert.Equal(expected, ZsDic.FrameDictionaryId(frame));

    private static byte[] Trained(int seed)
    {
        var random = new Random(seed);
        string[] words = [.. Enumerable.Range(0, 200).Select(i => $"word{random.Next()}_{seed}_{i}")];
        byte[][] samples = [.. Enumerable.Range(0, 2000).Select(_ =>
            Encoding.ASCII.GetBytes(string.Join(' ', Enumerable.Range(0, 100).Select(_ => words[random.Next(words.Length)]))))];
        return DictBuilder.TrainFromBuffer(samples, 16 * 1024);
    }

    private static byte[] Pack(params (string Name, byte[] Bytes)[] files)
    {
        const int sfat = 0x14;
        int names = sfat + 0x0C + files.Length * 0x10 + 8;

        using MemoryStream nameTable = new(), data = new();
        using BinaryWriter name = new(nameTable), blob = new(data);

        byte[] nodes = new byte[files.Length * 0x10];
        for (int i = 0; i < files.Length; i++)
        {
            Span<byte> node = nodes.AsSpan(i * 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(node[4..], 0x01000000u | (uint)(nameTable.Position / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(node[8..], (uint)data.Position);
            name.Write(Encoding.ASCII.GetBytes(files[i].Name));
            do name.Write((byte)0); while (nameTable.Position % 4 != 0);
            blob.Write(files[i].Bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(node[12..], (uint)data.Position);
        }

        int dataOffset = names + (int)nameTable.Length;
        byte[] sarc = new byte[dataOffset + data.Length];
        "SARC"u8.CopyTo(sarc);
        BinaryPrimitives.WriteUInt16LittleEndian(sarc.AsSpan(4), sfat);
        BinaryPrimitives.WriteUInt32LittleEndian(sarc.AsSpan(0x0C), (uint)dataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(sarc.AsSpan(sfat + 6), (ushort)files.Length);
        nodes.CopyTo(sarc, sfat + 0x0C);
        nameTable.ToArray().CopyTo(sarc, names);
        data.ToArray().CopyTo(sarc, dataOffset);
        return sarc;
    }

    [Fact]
    public void APlainPackFindsItsDictionariesByIdWhateverTheyAreCalled()
    {
        byte[] first = Trained(1), second = Trained(2);
        Assert.NotEqual(BitConverter.ToUInt32(first, 4), BitConverter.ToUInt32(second, 4));

        using ZsDic zs = ZsDic.FromPack(Pack(("first.zsdic", first), ("second.zsdic", second)));
        byte[] data = Encoding.ASCII.GetBytes("word and word and word and more words than that");

        foreach (byte[] dictionary in new[] { first, second })
        {
            using Compressor compressor = new(ZsDic.DefaultLevel);
            compressor.LoadDictionary(dictionary);
            byte[] packed = compressor.Wrap(data).ToArray();

            Assert.Equal(BitConverter.ToUInt32(dictionary, 4), ZsDic.FrameDictionaryId(packed));
            Assert.Equal(data, zs.Decompress(packed, "Model/Anything.bfres.zs"));
            Assert.Equal(data, zs.Decompress(packed, "Banc/Anything.bcett.byml.zs"));
        }

        Assert.False(zs.Has(ZsDictionary.Common));
    }

    [Fact]
    public void ACompressedPackAndItsNamesStillWork()
    {
        byte[] dictionary = Trained(3);
        using ZsDic plain = ZsDic.Dictionaryless();

        using ZsDic zs = ZsDic.FromPack(plain.Compress(Pack(("zs.zsdic", dictionary)), ZsDictionary.None));

        Assert.True(zs.Has(ZsDictionary.Common));
        byte[] data = Sample();
        Assert.Equal(data, zs.Decompress(zs.Compress(data, ZsDictionary.Common), ZsDictionary.Common));
    }

    [Fact]
    public void AnUnknownIdFallsBackToTheName()
    {
        using ZsDic zs = ZsDic.FromPack(Pack(("zs.zsdic", Trained(4))));
        using Compressor other = new(ZsDic.DefaultLevel);
        other.LoadDictionary(Trained(5));
        byte[] packed = other.Wrap(Sample()).ToArray();

        Assert.ThrowsAny<ZstdException>(() => zs.Decompress(packed, "Model/Anything.bfres.zs"));
    }

    [Fact]
    public void ShippedFramesOpenByIdEvenUnderTheWrongName()
    {
        if (TotkRoot is not { } root) return;
        using ZsDic zs = ZsDic.FromRomfs(root);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Banc"), "*.bcett.byml.zs", SearchOption.AllDirectories).Take(10)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Pack", "Actor"), "*.pack.zs").Take(10)))
        {
            byte[] raw = File.ReadAllBytes(file);
            Assert.NotEqual(0u, ZsDic.FrameDictionaryId(raw));
            Assert.Equal(zs.Decompress(raw, file), zs.Decompress(raw, "Odd/Name.bin"));
        }
    }

    private static byte[] Sample(int length = 100_000)
    {
        byte[] data = new byte[length];
        var random = new Random(5);
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(random.Next(8) + (i / 50) % 4);
        return data;
    }

    [Theory]
    [InlineData("Pack/Actor/Thing.pack.zs", ZsDictionary.Pack)]
    [InlineData("Banc/MainField/H-5_Static.bcett.byml.zs", ZsDictionary.BcettByml)]
    [InlineData("Model/Thing.bfres.zs", ZsDictionary.Common)]
    [InlineData("Pack/ZsDic.pack.zs", ZsDictionary.None)]
    [InlineData("Thing.byml", ZsDictionary.None)]
    public void TheNameChoosesTheDictionary(string path, ZsDictionary expected)
        => Assert.Equal(expected, ZsDic.DictionaryFor(path));

    [Fact]
    public void WithoutADictionaryDataRoundTrips()
    {
        using ZsDic zs = ZsDic.Dictionaryless();
        byte[] data = Sample();

        byte[] packed = zs.Compress(data, ZsDictionary.None);

        Assert.True(ZsDic.IsCompressed(packed));
        Assert.True(packed.Length < data.Length);
        Assert.Equal(data, zs.Decompress(packed, ZsDictionary.None));
    }

    [Fact]
    public void AMissingDictionaryIsAnErrorNotASilentFallback()
    {
        using ZsDic zs = ZsDic.Dictionaryless();
        Assert.Throws<InvalidOperationException>(() => zs.Compress(Sample(), "Thing.bfres.zs"));
    }

    [Fact]
    public void TheLevelChangesTheOutputAndBothDecompress()
    {
        using ZsDic zs = ZsDic.Dictionaryless();
        byte[] data = Sample();

        byte[] fast = zs.Compress(data, ZsDictionary.None, level: 1);
        byte[] small = zs.Compress(data, ZsDictionary.None, level: 19);

        Assert.NotEqual(fast, small);
        Assert.Equal(data, zs.Decompress(fast, ZsDictionary.None));
        Assert.Equal(data, zs.Decompress(small, ZsDictionary.None));
    }

    [Fact]
    public void RealDictionariesRoundTripAndAFileWithTheWrongNameStillOpens()
    {
        if (TotkRoot is not { } root) return;
        using ZsDic zs = ZsDic.FromRomfs(root);
        byte[] data = Sample();

        foreach (ZsDictionary dictionary in new[] { ZsDictionary.Common, ZsDictionary.Pack, ZsDictionary.BcettByml })
            Assert.Equal(data, zs.Decompress(zs.Compress(data, dictionary), dictionary));

        // Compressed with the common dictionary but named like a pack: the name's dictionary fails, the fallback opens it.
        byte[] common = zs.Compress(data, ZsDictionary.Common);
        Assert.Equal(data, zs.Decompress(common, "Pack/Actor/Odd.pack.zs"));
        Assert.Equal(data, zs.Decompress(common, "Banc/Odd.bcett.byml.zs"));
    }

    [Fact]
    public void ShippedFilesRecompressAndOpenAgain()
    {
        if (TotkRoot is not { } root) return;
        using ZsDic zs = ZsDic.FromRomfs(root);

        string[] files = [.. Directory.EnumerateFiles(Path.Combine(root, "Pack", "Actor"), "*.pack.zs").Take(20)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Banc"), "*.bcett.byml.zs", SearchOption.AllDirectories).Take(20))];
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            byte[] plain = zs.DecompressFile(file);
            Assert.Equal(plain, zs.Decompress(zs.Compress(plain, file), file));
        }
    }
}
