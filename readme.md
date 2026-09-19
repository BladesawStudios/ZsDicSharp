# ZsDicSharp

Zstd decompression for *Tears of the Kingdom* romfs, including the dictionaries the game ships
and the rule for which one a given file was compressed against.

```csharp
using ZsDic zs = ZsDic.FromRomfs(@"…\romfs");

byte[] placement = zs.DecompressFile(@"…\romfs\Banc\SmallDungeon\Dungeon000_Static.bcett.byml.zs");
byte[] actorPack = zs.DecompressFile(@"…\romfs\Pack\Actor\DgnObj_WoodPole_A.pack.zs");
```

## Build

```bash
dotnet build ZsDicSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
