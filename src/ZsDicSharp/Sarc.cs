using System.Buffers.Binary;

namespace ZsDicSharp;

/// <summary>
/// Just enough SARC to get the dictionaries out of the pack that holds them.
/// </summary>
/// <remarks>
/// The node table, the name table and the file bytes, and nothing else. This is here only to
/// break the bootstrap - the dictionaries live inside an archive, and that archive is the one
/// thing compressed without them. Anything that wants SARC for its own sake should have a
/// reader of its own rather than reach for this one.
/// </remarks>
internal static class Sarc
{
    public static List<(string Name, byte[] Bytes)> Read(byte[] d)
    {
        List<(string, byte[])> files = [];
        if (d.Length < 0x20 || d[0] != 'S' || d[1] != 'A' || d[2] != 'R' || d[3] != 'C') return files;

        int U32(int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

        int dataOffset = U32(0x0C);
        int sfat = U16(0x04);                     // the SARC header's own length
        int nodeCount = U16(sfat + 0x06);
        int nodes = sfat + 0x0C;
        int names = nodes + nodeCount * 0x10 + 0x08;

        for (int i = 0; i < nodeCount; i++)
        {
            int n = nodes + i * 0x10;
            if (n + 0x10 > d.Length) break;

            int nameOffset = names + (U32(n + 4) & 0xFFFFFF) * 4;
            int start = dataOffset + U32(n + 8);
            int end = dataOffset + U32(n + 12);
            if (nameOffset < 0 || nameOffset >= d.Length) continue;
            if (start < 0 || end > d.Length || end < start) continue;

            int z = Array.IndexOf(d, (byte)0, nameOffset);
            if (z < 0) continue;

            files.Add((System.Text.Encoding.ASCII.GetString(d, nameOffset, z - nameOffset), d[start..end]));
        }
        return files;
    }
}
