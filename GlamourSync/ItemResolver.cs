using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GlamourSync;

public class ItemResolver
{
    private readonly IDataManager data;
    private readonly IPluginLog log;

    public ItemResolver(IDataManager data, IPluginLog log) { this.data = data; this.log = log; }

    // Marker name for slots the glamour does not use; resolved to the "The Emperor's New ..." item of that slot.
    public const string InvisibleName = "The Emperor's New ... (invisible)";
    // Marker for "this glamour has no facewear": removes whatever facewear the character currently has.
    public const string NoFacewearName = "(no facewear)";
    private const string InvisiblePrefix = "The Emperor's New";
    private readonly Dictionary<ApiEquipSlot, Item> invisibleCache = new();

    private static readonly (ApiEquipSlot Slot, string Label)[] FillableSlots =
    {
        (ApiEquipSlot.Head, "head"), (ApiEquipSlot.Body, "body"), (ApiEquipSlot.Hands, "hands"),
        (ApiEquipSlot.Legs, "legs"), (ApiEquipSlot.Feet, "feet"),
        (ApiEquipSlot.Ears, "earrings"), (ApiEquipSlot.Neck, "necklace"), (ApiEquipSlot.Wrists, "bracelets"),
    };

    // Adds an auto-added "invisible" piece for every armor/accessory slot the glamour does not list.
    public static void AddInvisibleFillers(List<GlamourPiece> pieces)
    {
        var present = new HashSet<ApiEquipSlot>();
        var rings = 0;
        foreach (var p in pieces)
        {
            var s = MapSlot(p.SlotLabel);
            if (s == null) continue;
            if (s is ApiEquipSlot.RFinger or ApiEquipSlot.LFinger) rings++;
            else present.Add(s.Value);
        }

        foreach (var (slot, label) in FillableSlots)
            if (!present.Contains(slot))
                pieces.Add(new GlamourPiece(label, InvisibleName, null, null, null, true));

        for (var i = rings; i < 2; i++)
            pieces.Add(new GlamourPiece("ring", InvisibleName, null, null, null, true));
    }

    // Facewear has no "Emperor's New" item, so an unused facewear slot is cleared instead (separate option).
    public static void AddFacewearRemoval(List<GlamourPiece> pieces)
    {
        if (!pieces.Any(p => IsFacewearLabel(p.SlotLabel)))
            pieces.Add(new GlamourPiece("face", NoFacewearName, null, null, null, true));
    }

    private Item FindInvisible(Lumina.Excel.ExcelSheet<Item> items, ApiEquipSlot slot)
    {
        var key = slot == ApiEquipSlot.LFinger ? ApiEquipSlot.RFinger : slot;   // a ring item fits either finger
        if (invisibleCache.TryGetValue(key, out var cached)) return cached;

        var found = items
            .Where(i => i.Name.ExtractText().StartsWith(InvisiblePrefix, StringComparison.OrdinalIgnoreCase) && FitsSlot(i, key))
            .OrderBy(i => i.RowId)
            .FirstOrDefault();

        invisibleCache[key] = found;
        log.Debug($"Invisible item for {key}: {(found.RowId == 0 ? "none found" : $"'{found.Name.ExtractText()}' (rowId={found.RowId})")}");
        return found;
    }

    public static ApiEquipSlot? MapSlot(string label)
    {
        var key = new string(label.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return key switch
        {
            "mainhand" or "weapon" => ApiEquipSlot.MainHand,
            "offhand" or "shield" => ApiEquipSlot.OffHand,
            "head" => ApiEquipSlot.Head,
            "body" => ApiEquipSlot.Body,
            "hands" or "gloves" => ApiEquipSlot.Hands,
            "legs" => ApiEquipSlot.Legs,
            "feet" => ApiEquipSlot.Feet,
            "ears" or "earrings" or "earring" => ApiEquipSlot.Ears,
            "neck" or "necklace" => ApiEquipSlot.Neck,
            "wrists" or "bracelets" or "bracelet" => ApiEquipSlot.Wrists,
            "ring" or "finger" or "rightring" => ApiEquipSlot.RFinger,
            "leftring" => ApiEquipSlot.LFinger,
            _ => null,
        };
    }

    private static bool FitsSlot(Item i, ApiEquipSlot s)
    {
        var c = i.EquipSlotCategory.ValueNullable;
        if (c == null) return false;
        return s switch
        {
            ApiEquipSlot.MainHand => c.Value.MainHand != 0,
            ApiEquipSlot.OffHand => c.Value.OffHand != 0,
            ApiEquipSlot.Head => c.Value.Head != 0,
            ApiEquipSlot.Body => c.Value.Body != 0,
            ApiEquipSlot.Hands => c.Value.Gloves != 0,
            ApiEquipSlot.Legs => c.Value.Legs != 0,
            ApiEquipSlot.Feet => c.Value.Feet != 0,
            ApiEquipSlot.Ears => c.Value.Ears != 0,
            ApiEquipSlot.Neck => c.Value.Neck != 0,
            ApiEquipSlot.Wrists => c.Value.Wrists != 0,
            ApiEquipSlot.RFinger or ApiEquipSlot.LFinger => c.Value.FingerR != 0 || c.Value.FingerL != 0,
            _ => false,
        };
    }

    public List<ResolvedPiece> Resolve(IEnumerable<GlamourPiece> pieces)
    {
        var items = data.GetExcelSheet<Item>();
        var stains = data.GetExcelSheet<Stain>();
        var result = new List<ResolvedPiece>();
        var rightRingUsed = false;

        var hasExplicitOffhand = pieces.Any(x => MapSlot(x.SlotLabel) == ApiEquipSlot.OffHand);

        foreach (var p in pieces)
        {
            log.Debug($"Piece: slot='{p.SlotLabel}' name='{p.ItemName}' siteId={p.ItemId}");

            if (p.ItemName == NoFacewearName)
            {
                result.Add(new ResolvedPiece(ApiEquipSlot.Head, 0, 0, 0, NoFacewearName, true));
                continue;
            }

            if (IsFacewearLabel(p.SlotLabel)) { ResolveFacewear(p, result); continue; }

            var slot = MapSlot(p.SlotLabel);
            if (slot == null) { log.Warning($"Unknown slot '{p.SlotLabel}'"); continue; }

            // Second ring goes to the left finger.
            if (slot == ApiEquipSlot.RFinger)
            {
                if (rightRingUsed) slot = ApiEquipSlot.LFinger;
                else rightRingUsed = true;
            }

            // Unused slot: use the matching "The Emperor's New ..." item (no dyes).
            if (p.ItemName == InvisibleName)
            {
                var inv = FindInvisible(items, slot.Value);
                if (inv.RowId == 0) continue;   // no invisible item exists for this slot; leave it alone
                result.Add(new ResolvedPiece(slot.Value, inv.RowId, 0, 0, inv.Name.ExtractText()));
                continue;
            }

            // 1) Try the item ID from the site, 2) fall back to name match.
            Item item = default;
            if (p.ItemId is uint id && items.TryGetRow(id, out var byId) && FitsSlot(byId, slot.Value))
            {
                if (byId.Name.ExtractText().Equals(p.ItemName, StringComparison.OrdinalIgnoreCase))
                    item = byId;
                else
                    log.Warning($"ID {id} is '{byId.Name.ExtractText()}' but site says '{p.ItemName}'; falling back to name match");
            }
            if (item.RowId == 0)
            {
                item = items.FirstOrDefault(i =>
                    i.Name.ExtractText().Equals(p.ItemName, StringComparison.OrdinalIgnoreCase) && FitsSlot(i, slot.Value));
            }
            if (item.RowId == 0) { log.Warning($"No item match for '{p.ItemName}' ({p.SlotLabel})"); continue; }

            log.Debug($"Resolved '{p.ItemName}' -> rowId={item.RowId} slot={slot} equipCat={item.EquipSlotCategory.RowId} modelMain={item.ModelMain} modelSub={item.ModelSub}");
            if (slot is ApiEquipSlot.OffHand or ApiEquipSlot.MainHand)
            {
                foreach (var c in items.Where(i => i.Name.ExtractText().Equals(p.ItemName, StringComparison.OrdinalIgnoreCase)))
                    log.Debug($"   candidate rowId={c.RowId} equipCat={c.EquipSlotCategory.RowId} modelMain={c.ModelMain} modelSub={c.ModelSub}");
            }

            var resolved = new ResolvedPiece(slot.Value, item.RowId, FindStain(stains, p.Dye1), FindStain(stains, p.Dye2), p.ItemName);
            result.Add(resolved);

            // Paired weapons (knuckles, daggers, etc.) carry a second model for the off hand.
            if (slot == ApiEquipSlot.MainHand && item.ModelSub != 0 && !hasExplicitOffhand)
            {
                log.Debug($"'{p.ItemName}' has an off-hand model; also applying to OffHand.");
                result.Add(resolved with { Slot = ApiEquipSlot.OffHand });
            }
        }
        return result;
    }

    // Name -> items index, built once, so previewing many pieces stays fast.
    private ILookup<string, Item>? nameIndex;
    private ILookup<string, Item> NameIndex => nameIndex ??= data.GetExcelSheet<Item>()
        .Where(i => i.RowId != 0)
        .Select(i => (Name: i.Name.ExtractText(), Item: i))
        .Where(x => x.Name.Length > 0)
        .ToLookup(x => x.Name, x => x.Item, StringComparer.OrdinalIgnoreCase);

    // 0xRRGGBB color of a dye, looked up by its in-game name (null if unknown/none).
    private uint? StainRgb(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var st = data.GetExcelSheet<Stain>().FirstOrDefault(x =>
            x.RowId != 0 && x.Name.ExtractText().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return st.RowId == 0 ? null : st.Color & 0xFFFFFF;
    }

    // Quiet lookup used by the banner: same rules as Resolve (site ID if its name matches, else by name).
    public PieceView Preview(GlamourPiece p)
    {
        try
        {
            if (p.ItemName == NoFacewearName) return new PieceView("Remove Current Facewear", 0, true);
            if (IsFacewearLabel(p.SlotLabel))
            {
                var fw = FindGlasses(p, logDetails: false);
                return fw.RowId == 0
                    ? new PieceView(p.ItemName, 0, false)
                    : new PieceView(fw.Name.ExtractText(), (uint)fw.Icon, true);
            }

            var slot = MapSlot(p.SlotLabel);
            if (slot == null) return new PieceView(p.ItemName, 0, false);

            var items = data.GetExcelSheet<Item>();

            if (p.ItemName == InvisibleName)
            {
                var inv = FindInvisible(items, slot.Value);
                return inv.RowId == 0
                    ? new PieceView("(no invisible item for this slot)", 0, false)
                    : new PieceView(inv.Name.ExtractText(), (uint)inv.Icon, true);
            }

            Item item = default;
            if (p.ItemId is uint id && items.TryGetRow(id, out var byId) && FitsSlot(byId, slot.Value)
                && byId.Name.ExtractText().Equals(p.ItemName, StringComparison.OrdinalIgnoreCase))
                item = byId;
            if (item.RowId == 0)
                item = NameIndex[p.ItemName].FirstOrDefault(i => FitsSlot(i, slot.Value));

            return item.RowId == 0
                ? new PieceView(p.ItemName, 0, false)
                : new PieceView(item.Name.ExtractText(), (uint)item.Icon, true, StainRgb(p.Dye1), StainRgb(p.Dye2), (byte)item.DyeCount);
        }
        catch (Exception e)
        {
            log.Debug($"Preview failed for '{p.ItemName}': {e.Message}");
            return new PieceView(p.ItemName, 0, false);
        }
    }

    private static bool IsFacewearLabel(string label)
    {
        var k = new string(label.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return k is "face" or "facewear" or "glasses";
    }

    // Facewear is not in the Item sheet; it lives in the Glasses sheet.
    private void ResolveFacewear(GlamourPiece p, List<ResolvedPiece> result)
    {
        var g = FindGlasses(p, logDetails: true);
        if (g.RowId == 0) return;
        result.Add(new ResolvedPiece(ApiEquipSlot.Head, g.RowId, 0, 0, g.Name.ExtractText(), true));
    }

    // The site shows a facewear "dye" that is really a color variant: "Slim Frame Glasses" + "Black"
    // is the game's "Black Slim Frame Glasses" (a different Glasses row). Try the variant names first.
    private Glasses FindGlasses(GlamourPiece p, bool logDetails)
    {
        var sheet = data.GetExcelSheet<Glasses>();
        var name = p.ItemName.Trim();
        var dye = string.IsNullOrWhiteSpace(p.Dye1) ? null : p.Dye1.Trim();

        bool Is(Glasses g, string text) => g.Name.ExtractText().Equals(text, StringComparison.OrdinalIgnoreCase);
        bool Has(Glasses g, string text) => g.Name.ExtractText().Contains(text, StringComparison.OrdinalIgnoreCase);

        // 1) Variant by exact name, using the dye as the color.
        if (dye != null)
        {
            foreach (var candidate in new[] { $"{dye} {name}", $"{name} ({dye})", $"{name} {dye}" })
            {
                var v = sheet.FirstOrDefault(g => g.RowId != 0 && Is(g, candidate));
                if (v.RowId != 0)
                {
                    if (logDetails) log.Debug($"Facewear '{name}' + '{dye}' -> '{candidate}' (rowId={v.RowId})");
                    return v;
                }
            }
        }

        // 2) The site's ID, if the row it points to is this facewear.
        if (p.ItemId is uint id && sheet.TryGetRow(id, out var byId) && byId.RowId != 0 && Has(byId, name))
            return byId;

        // 3) Exact name (facewear without color variants).
        var exact = sheet.FirstOrDefault(g => g.RowId != 0 && Is(g, name));
        if (exact.RowId != 0) return exact;

        // 4) Any row containing both the name and the color.
        if (dye != null)
        {
            var loose = sheet.FirstOrDefault(g => g.RowId != 0 && Has(g, name) && Has(g, dye));
            if (loose.RowId != 0)
            {
                if (logDetails) log.Debug($"Facewear '{name}' + '{dye}' -> '{loose.Name.ExtractText()}' (rowId={loose.RowId}, loose match)");
                return loose;
            }
        }

        if (logDetails)
        {
            log.Warning($"No facewear match for '{name}' (dye '{dye ?? "none"}'). Rows containing the name:");
            foreach (var g in sheet.Where(x => x.RowId != 0 && Has(x, name)).Take(25))
                log.Warning($"   rowId={g.RowId} name='{g.Name.ExtractText()}'");
        }
        return default;
    }

    private byte FindStain(Lumina.Excel.ExcelSheet<Stain> sheet, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var s = sheet.FirstOrDefault(x => x.RowId != 0 && x.Name.ExtractText().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (s.RowId == 0) log.Warning($"No dye match for '{name}'");
        return (byte)s.RowId;
    }
}