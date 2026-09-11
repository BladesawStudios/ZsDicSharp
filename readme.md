# ZsDicSharp

Zstd decompression for *Tears of the Kingdom* romfs, including the dictionaries the game ships
and the rule for which one a given file was compressed against.

```csharp
using ZsDic zs = ZsDic.FromRomfs(@"…\romfs");

byte[] placement = zs.DecompressFile(@"…\romfs\Banc\SmallDungeon\Dungeon000_Static.bcett.byml.zs");
byte[] actorPack = zs.DecompressFile(@"…\romfs\Pack\Actor\DgnObj_WoodPole_A.pack.zs");
```

## Why this is its own thing

Decompressing is the easy half — `ZstdSharp` does that. The part worth owning is that **the
dictionary is not recorded in the file**. Three 128 KB dictionaries ship inside
`Pack/ZsDic.pack.zs`, and which one applies follows from the file's name:

| Ends with | Dictionary |
|---|---|
| `.bcett.byml.zs` | `bcett.byml.zsdic` |
| `.pack.zs` | `pack.zsdic` |
| `.zs` | `zs.zsdic` |
| `ZsDic.pack.zs` | none — this is the bootstrap |

The compound endings have to be tested before the plain `.zs`, since every one of them ends
that way without wanting the dictionary that ending implies.

Every format in romfs needs this — 15,062 actor packs, 1,481 models, the placement files — so
it sits under the format readers rather than inside any one of them.

## The bootstrap

The dictionaries live inside a SARC archive, and that archive is the one thing compressed
without them. This carries just enough SARC to open it; anything wanting SARC for its own
sake should have a reader of its own.

A detail that surprises: once a dictionary is loaded, the dictionary pack still decompresses
fine. Zstd only applies a dictionary when the frame asks for one, so loading the wrong
dictionary is harmless for a frame that references none — which is why a mismatch shows up as
a failure only on files that genuinely used one.

## Threading

A decompressor holds its dictionary and is not safe to share, so each thread keeps its own
set. Loading 128 KB per call would otherwise cost more than reading the file.

## Checked against

Each kind of file decompresses with exactly one of the three dictionaries and fails with the
others, which is what makes the table above a rule rather than a guess. A sweep of 180
placement files and actor packs decompresses clean.

## Build

```bash
dotnet build ZsDicSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
