using Glamourer.Api.Enums;

namespace GlamourSync;

public record GlamourPiece(string SlotLabel, string ItemName, string? Dye1, string? Dye2, uint? ItemId = null, bool AutoAdded = false);

// IsFacewear: ItemId is a row of the Glasses sheet and goes through Glamourer's bonus-item IPC (Slot is ignored).
public record ResolvedPiece(ApiEquipSlot Slot, ulong ItemId, byte Stain1, byte Stain2, string ItemName, bool IsFacewear = false);

// What the banner shows for a piece: the game's item name and icon (IconId 0 = no icon).
public record PieceView(string Name, uint IconId, bool Found, uint? Dye1Rgb = null, uint? Dye2Rgb = null, byte DyeSlots = 0);