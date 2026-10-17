using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace GlamourSync;

public class GlamourerBridge
{
    private readonly ApiVersion apiVersion;
    private readonly SetItem setItem;
    private readonly SetBonusItem setBonusItem;
    private readonly GetState getState;
    private readonly ApplyState applyState;
    private readonly AddDesign addDesign;
    private readonly RevertState revert;

    // The user's own Glamourer look, saved right before the first template is applied.
    private JObject? snapshot;

    public GlamourerBridge(IDalamudPluginInterface pi)
    {
        apiVersion = new ApiVersion(pi);
        setItem = new SetItem(pi);
        setBonusItem = new SetBonusItem(pi);
        getState = new GetState(pi);
        applyState = new ApplyState(pi);
        addDesign = new AddDesign(pi);
        revert = new RevertState(pi);
    }

    public bool IsAvailable()
    {
        try { _ = apiVersion.Invoke(); return true; } catch { return false; }
    }

    public List<string> Apply(IEnumerable<ResolvedPiece> pieces)
    {
        var errors = new List<string>();
        var list = pieces.ToList();

        // Remember what the user was wearing in Glamourer (only the first time, so applying several
        // templates in a row still reverts to the original look).
        if (snapshot == null) CaptureSnapshot();

        // Off hand last: changing the main hand can affect it.
        var ordered = list.OrderBy(x => !x.IsFacewear && x.Slot == ApiEquipSlot.OffHand ? 1 : 0);

        foreach (var p in ordered)
        {
            // Facewear (ItemId 0 clears the slot).
            if (p.IsFacewear)
            {
                var ecb = setBonusItem.Invoke(0, ApiBonusSlot.Glasses, p.ItemId, 0, ApplyFlag.Once);
                if (ecb != GlamourerApiEc.Success) errors.Add($"{p.ItemName} (facewear): {ecb}");
                continue;
            }

            var ec = setItem.Invoke(0, p.Slot, p.ItemId, new List<byte> { p.Stain1, p.Stain2 }, 0, ApplyFlag.Once);

            // SetItem rejects some off-hands (e.g. shields); retry through the full-state route.
            if (ec == GlamourerApiEc.ItemInvalid && p.Slot == ApiEquipSlot.OffHand)
            {
                var main = list.FirstOrDefault(x => !x.IsFacewear && x.Slot == ApiEquipSlot.MainHand);
                var viaState = ApplyWeaponsViaState(main, p);
                if (viaState != GlamourerApiEc.Success)
                    errors.Add($"{p.ItemName}: SetItem={ec}, ApplyState={viaState}");
                continue;
            }

            if (ec != GlamourerApiEc.Success) errors.Add($"{p.ItemName} ({p.Slot}): {ec}");
        }
        return errors;
    }

    private GlamourerApiEc ApplyWeaponsViaState(ResolvedPiece? main, ResolvedPiece off)
    {
        var (ec, state) = getState.Invoke(0);
        if (ec != GlamourerApiEc.Success || state == null) return ec;

        if (state["Equipment"] is not JObject equip) return GlamourerApiEc.InvalidState;
        if (main != null) SetEquip(equip, "MainHand", main);
        SetEquip(equip, "OffHand", off);

        return applyState.Invoke(state, 0, 0, ApplyFlag.Equipment | ApplyFlag.Once);
    }

    private static void SetEquip(JObject equip, string key, ResolvedPiece p)
    {
        var o = equip[key] as JObject ?? new JObject();
        o["ItemId"] = p.ItemId;
        o["Stain"] = p.Stain1;
        o["Stain2"] = p.Stain2;
        o["Apply"] = true;
        o["ApplyStain"] = true;
        equip[key] = o;
    }

    // Saves the character's CURRENT look as a Glamourer design (apply the glamour first).
    // AddDesign.Invoke(string, string, out Guid): design data first, then the name (order not verified).
    public GlamourerApiEc SaveAsDesign(string name)
    {
        var (ec, state) = getState.Invoke(0);
        if (ec != GlamourerApiEc.Success || state == null) return ec;

        return addDesign.Invoke(state.ToString(), name, out _);
    }

    private void CaptureSnapshot()
    {
        try
        {
            var (ec, state) = getState.Invoke(0);
            if (ec == GlamourerApiEc.Success && state != null) snapshot = state;
        }
        catch { /* no saved look: Revert falls back to the plain Glamourer revert */ }
    }

    // Goes back to the look saved before the first template was applied. If there is none,
    // falls back to Glamourer's plain revert (the game's own gear).
    public GlamourerApiEc RevertToPrevious(out bool restoredSnapshot)
    {
        restoredSnapshot = false;
        var saved = snapshot;
        snapshot = null;   // the next apply saves a fresh look

        if (saved != null)
        {
            var ec = applyState.Invoke((JObject)saved.DeepClone(), 0, 0, ApplyFlag.Equipment | ApplyFlag.Once);
            if (ec == GlamourerApiEc.Success) { restoredSnapshot = true; return ec; }
        }

        return revert.Invoke(0);
    }
}