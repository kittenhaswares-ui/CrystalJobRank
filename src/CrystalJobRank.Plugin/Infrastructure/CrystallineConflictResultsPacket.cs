using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CrystalJobRank.Plugin.Infrastructure;

// Interoperability facts for the post-match payload. See THIRD_PARTY_NOTICES.md.
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct CrystallineConflictResultsPacket
{
    [FieldOffset(0x10)] public ushort MatchLength;
    [FieldOffset(0x3C)] public byte Result;
    [FieldOffset(0x40)] public uint AstraProgress;
    [FieldOffset(0x44)] public uint UmbraProgress;
    [FieldOffset(0x48)] public fixed byte Players[0x50 * 10];

    public Span<ResultPlayer> PlayerSpan => new(Unsafe.AsPointer(ref Players[0]), 10);
}

[StructLayout(LayoutKind.Explicit, Size = 0x50)]
internal unsafe struct ResultPlayer
{
    [FieldOffset(0x08)] public ulong ContentId;
    [FieldOffset(0x10)] public int DamageDealt;
    [FieldOffset(0x14)] public int DamageTaken;
    [FieldOffset(0x18)] public int HpRestored;
    [FieldOffset(0x1C)] public ushort WorldId;
    [FieldOffset(0x1E)] public byte ClassJobId;
    [FieldOffset(0x1F)] public byte Kills;
    [FieldOffset(0x20)] public byte Deaths;
    [FieldOffset(0x21)] public byte Assists;
    [FieldOffset(0x22)] public ushort TimeOnCrystal;
    [FieldOffset(0x25)] public byte Team;
    [FieldOffset(0x26)] public fixed byte PlayerName[42];
}

