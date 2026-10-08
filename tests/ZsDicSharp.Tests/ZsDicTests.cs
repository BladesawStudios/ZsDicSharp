namespace ZsDicSharp.Tests;

public class ZsDicTests
{
    private static string? TotkRoot
        => Environment.GetEnvironmentVariable("TOTK_ROMFS") is { } root && File.Exists(Path.Combine(root, "Pack", "ZsDic.pack.zs")) ? root : null;

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
