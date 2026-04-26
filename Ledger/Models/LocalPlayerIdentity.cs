namespace Ledger.Models;

public sealed record LocalPlayerIdentity(
    string CharacterName,
    string Ident,
    ushort HomeWorldId,
    ushort CurrentWorldId,
    int DatacenterId,
    int RegionId);
