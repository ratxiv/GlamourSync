using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace GlamourSync;

public class MainWindow : Window, IDisposable
{
    private readonly ItemResolver resolver;
    private readonly GlamourerBridge glamourer;
    private readonly ITextureProvider textures;
    private readonly Configuration config;

    private string status = "";
    private bool statusIsError;

    // Options (shown in the cog popup; add new ones in DrawOptionsButton)
    private bool watchClipboard = true;
    private bool addInvisibleShield = true;
    private bool fillUnused = true;
    private bool removeFacewear = true;

    // Clipboard watching
    private DateTime nextPoll = DateTime.MinValue;
    private uint lastClipSeq;

    // "No facewear" slot icon (embedded resource, loaded once)
    private IDalamudTextureWrap? noFacewearIcon;
    private bool noFacewearLoading;

    // A glamour waiting for the user's slot selection
    private sealed class PendingGlamour
    {
        public string Title = "";
        public List<GlamourPiece> Pieces = new();
        public List<bool> Selected = new();
        public List<PieceView> Views = new();
        public bool Applied;   // true while the character shows exactly this selection
    }
    private PendingGlamour? pending;
    private string designName = "";
    private const int MaxHistory = 20;

    // Chat commands
    private bool echoStatus;      // true while a chat command runs: status messages also go to chat
    public Action<string>? Notify { get; set; }

    public MainWindow(ItemResolver r, GlamourerBridge g, ITextureProvider t, Configuration c)
        : base("GlamourSync")
    {
        resolver = r; glamourer = g; textures = t;
        config = c;

        // Restore the saved options.
        watchClipboard = config.WatchClipboard;
        addInvisibleShield = config.AddInvisibleShield;
        fillUnused = config.FillUnused;
        removeFacewear = config.RemoveFacewear;
        Size = new Vector2(900, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() => noFacewearIcon?.Dispose();

    public override void Draw()
    {
        if (!glamourer.IsAvailable())
        {
            ImGui.TextColored(new Vector4(1, .4f, .4f, 1), "Glamourer not detected. Make sure it is installed, enabled, and up to date.");
            return;
        }

        if (watchClipboard) PollClipboard();

        // ---- Top bar: load from the browser, revert, options (cog on the right)
        if (ImGui.Button("Load from Clipboard"))
            LoadFromHtml(ReadClipboardPage());
        ImGui.SameLine();
        if (ImGui.Button("Revert to Previous Gear")) DoRevert();
        ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.GetFrameHeight() * 2 - 44f);
        DrawHistoryButton();
        ImGui.SameLine();
        DrawOptionsButton();

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.6f, .6f, .6f, 1f));
        ImGui.TextWrapped("In your browser, open the glamour, press Ctrl+A then Ctrl+C (page source works too). The pieces appear below.");
        ImGui.PopStyleColor();

        if (pending != null) DrawPending();

        // ---- Result / error line 
        if (status.Length > 0)
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, statusIsError ? new Vector4(1f, .45f, .45f, 1f) : new Vector4(.8f, .8f, .8f, 1f));
            ImGui.TextWrapped(status);
            ImGui.PopStyleColor();
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        status = text; statusIsError = error;
        if (echoStatus && text.Length > 0) Notify?.Invoke(text);
    }

    private void DoRevert()
    {
        try
        {
            var ec = glamourer.RevertToPrevious(out var restored);
            if (pending != null) pending.Applied = false;   
            if (ec != GlamourerApiEc.Success) SetStatus($"Revert failed: {ec}", true);
            else if (restored) SetStatus("Restored the look you had in Glamourer before applying a template.");
            else SetStatus("Reverted to your game gear (no earlier Glamourer look was saved).");
        }
        catch (Exception e) { SetStatus("Revert failed: " + e.Message, true); }
    }

    private void ApplySelected(PendingGlamour pg)
    {
        var chosen = pg.Pieces.Where((_, i) => pg.Selected[i]).ToList();
        if (!ApplyPieces(chosen)) return;
        pg.Applied = true;
        AddToHistory(pg);
    }

    // ---- Chat commands -------------------------------------------------------------------

    public void HandleCommand(string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "apply":
                // Works with the window closed, like revert.
                WithEcho(() =>
                {
                    if (!glamourer.IsAvailable()) { Notify?.Invoke("Glamourer not detected."); return; }
                    LoadFromHtml(ReadClipboardPage());
                    if (pending != null) ApplySelected(pending);
                });
                break;

            case "revert":
                WithEcho(() =>
                {
                    if (!glamourer.IsAvailable()) { Notify?.Invoke("Glamourer not detected."); return; }
                    DoRevert();
                });
                break;

            default:
                Notify?.Invoke("/glamoursync - open or close the window");
                Notify?.Invoke("/glamoursync apply - read the clipboard and apply it right away");
                Notify?.Invoke("/glamoursync revert - go back to your previous look");
                break;
        }
    }

    private void WithEcho(Action a)
    {
        echoStatus = true;
        try { a(); }
        catch (Exception e) { Notify?.Invoke("GlamourSync: " + e.Message); }
        finally { echoStatus = false; }
    }

    // Cog button; the toggles stay hidden until it is clicked. Add future options inside the popup.
    private void DrawOptionsButton()
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog)) ImGui.OpenPopup("##glamsync_options");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Options");

        if (ImGui.BeginPopup("##glamsync_options"))
        {
            ImGui.TextDisabled("Options");
            ImGui.Separator();
            var changed = false;
            changed |= ImGui.Checkbox("Watch clipboard for glamour page source", ref watchClipboard);
            changed |= ImGui.Checkbox("Paladin glamours with no shield: add The Emperor's New Shield (hides the shield)", ref addInvisibleShield);
            changed |= ImGui.Checkbox("Unused slots: fill with The Emperor's New items (invisible)", ref fillUnused);
            changed |= ImGui.Checkbox("Glamours without facewear: remove my current facewear", ref removeFacewear);
            if (changed) SaveOptions();
            ImGui.EndPopup();
        }
    }

    // ---- History -------------------------------------------------------------------------

    private void DrawHistoryButton()
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.History)) ImGui.OpenPopup("##glamsync_history");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("History");

        if (ImGui.BeginPopup("##glamsync_history"))
        {
            ImGui.TextDisabled("Recently applied");
            ImGui.Separator();

            if (config.History.Count == 0) ImGui.TextDisabled("Nothing yet.");

            var shown = HistoryShown();
            for (var i = 0; i < shown.Count; i++)
            {
                var e = shown[i];
                ImGui.PushID(i);

                if (ImGui.SmallButton("X")) { config.History.Remove(e); config.Save(); ImGui.PopID(); break; }
                ImGui.SameLine();

                ImGui.PushStyleColor(ImGuiCol.Text, e.Favorite ? new Vector4(1f, .82f, .2f, 1f) : new Vector4(.55f, .55f, .55f, 1f));
                var star = ImGuiComponents.IconButton(FontAwesomeIcon.Star);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(e.Favorite ? "Remove from favorites" : "Add to favorites");
                if (star) { e.Favorite = !e.Favorite; config.Save(); ImGui.PopID(); break; }
                ImGui.SameLine();

                if (ImGui.Selectable($"{e.Title}   ({e.SavedAt.ToLocalTime():dd/MM HH:mm})##entry"))
                {
                    LoadHistory(e);
                    ImGui.CloseCurrentPopup();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }

            if (config.History.Count > 0)
            {
                ImGui.Separator();
                if (ImGui.SmallButton("Clear History")) { config.History.RemoveAll(h => !h.Favorite); config.Save(); }   // favorites stay
            }
            ImGui.EndPopup();
        }
    }

    // Stores the glamour exactly as shown (including auto-added rows), newest first.
    private void AddToHistory(PendingGlamour pg)
    {
        static string Signature(IEnumerable<(string, string, string?, string?)> pieces) =>
            string.Join("|", pieces.Select(p => $"{p.Item1}:{p.Item2}:{p.Item3}:{p.Item4}"));

        var entry = new HistoryEntry
        {
            Title = pg.Title,
            SavedAt = DateTime.UtcNow,
            Pieces = pg.Pieces.Select(p => new HistoryPiece
            {
                SlotLabel = p.SlotLabel, ItemName = p.ItemName, Dye1 = p.Dye1, Dye2 = p.Dye2,
                ItemId = p.ItemId, AutoAdded = p.AutoAdded,
            }).ToList(),
        };

        var sig = Signature(entry.Pieces.Select(p => (p.SlotLabel, p.ItemName, p.Dye1, p.Dye2)));
        bool Same(HistoryEntry h) => h.Title == entry.Title
            && Signature(h.Pieces.Select(p => (p.SlotLabel, p.ItemName, p.Dye1, p.Dye2))) == sig;

        entry.Favorite = config.History.FirstOrDefault(Same)?.Favorite ?? false;   // re-applying keeps the star
        config.History.RemoveAll(Same);                                              // same glamour: move to top
        config.History.Insert(0, entry);
        TrimHistory();
        config.Save();
    }

    // Favorites first, then the rest (newest first).
    private List<HistoryEntry> HistoryShown() => config.History.OrderByDescending(h => h.Favorite).ToList();

    // Favorites never expire; the other entries are limited to MaxHistory (newest kept).
    private void TrimHistory()
    {
        var others = config.History.Where(h => !h.Favorite).ToList();
        for (var i = MaxHistory; i < others.Count; i++) config.History.Remove(others[i]);
    }

    private void LoadHistory(HistoryEntry e)
    {
        var pieces = e.Pieces
            .Select(h => new GlamourPiece(h.SlotLabel, h.ItemName, h.Dye1, h.Dye2, h.ItemId, h.AutoAdded))
            .ToList();
        SetPending(pieces, e.Title);
    }

    // ---- Glamourer design ----------------------------------------------------------------

    private void SaveDesign(string name)
    {
        try
        {
            var ec = glamourer.SaveAsDesign(name);
            if (ec == GlamourerApiEc.Success) SetStatus($"Saved \"{name}\" as a Glamourer design.");
            else SetStatus($"Could not save the design: {ec}", true);
        }
        catch (Exception ex) { SetStatus("Could not save the design: " + ex.Message, true); }
    }

    private void SaveOptions()
    {
        config.WatchClipboard = watchClipboard;
        config.AddInvisibleShield = addInvisibleShield;
        config.FillUnused = fillUnused;
        config.RemoveFacewear = removeFacewear;
        config.Save();
    }

    // Page text from the clipboard: page source if that is what was copied, otherwise the HTML a browser
    // puts there on a normal Ctrl+A / Ctrl+C of the rendered page.
    private static string ReadClipboardPage()
    {
        var text = ClipboardHelper.GetText();
        if (!string.IsNullOrEmpty(text) && text.Contains("gear-icon-slot", StringComparison.Ordinal)) return text;

        var html = ClipboardHelper.GetHtml();
        if (!string.IsNullOrEmpty(html) && html.Contains("gear-icon-slot", StringComparison.Ordinal)) return html;

        return text ?? "";
    }

    // Checks the clipboard about once a second, but only re-reads it when something new was copied.
    private void PollClipboard()
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);

        var seq = ClipboardHelper.SequenceNumber();
        if (seq == lastClipSeq) return;   
        lastClipSeq = seq;

        var text = ReadClipboardPage();
        if (text.Length < 2000 || !text.Contains("gear-icon-slot", StringComparison.Ordinal)) return;

        var pieces = ParsePage(text);
        if (pieces.Count == 0) return;

        SetPending(pieces, EorzeaCollectionScraper.ExtractTitle(text));
    }

    private void SetPending(List<GlamourPiece> pieces, string title)
    {
        SetStatus("");
        pending = new PendingGlamour
        {
            Title = title,
            Pieces = pieces,
            Selected = pieces.Select(_ => true).ToList(),   // everything ticked by default
            Views = pieces.Select(resolver.Preview).ToList(),
        };
    }

    private void DrawPending()
    {
        var pg = pending!;
        ImGui.Separator();
        ImGui.TextColored(new Vector4(.5f, 1f, .5f, 1f), $"Glamour: {pg.Title}");

        // Apply right under the glamour name.
        var count = pg.Selected.Count(x => x);
        if (count == 0)
        {
            ImGui.TextDisabled("Nothing selected");
        }
        else if (ImGui.Button($"Apply Selected ({count})"))
        {
            // The glamour stays on screen after applying, so you can tweak the selection and apply again.
            ApplySelected(pg);
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismiss Template")) { pending = null; return; }
        ImGui.SameLine();
        if (ImGui.Button("Save as Design"))
        {
            if (!pg.Applied) SetStatus("Apply the glamour first: the design is saved from your character's current look.", true);
            else { designName = pg.Title; ImGui.OpenPopup("##glamsync_savedesign"); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Saves your character's current look as a design in Glamourer (apply first)");

        if (ImGui.BeginPopup("##glamsync_savedesign"))
        {
            ImGui.TextUnformatted("Design Name");
            ImGui.InputText("##designname", ref designName, 100);
            if (ImGui.Button("Save") && designName.Trim().Length > 0)
            {
                SaveDesign(designName.Trim());
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.TextDisabled("Untick the slots you want to leave as they are.");

        // Selection helpers under the hint.
        if (ImGui.Button("All")) { for (var i = 0; i < pg.Selected.Count; i++) pg.Selected[i] = true; pg.Applied = false; }
        ImGui.SameLine();
        if (ImGui.Button("None")) { for (var i = 0; i < pg.Selected.Count; i++) pg.Selected[i] = false; pg.Applied = false; }
        ImGui.SameLine();
        if (ImGui.Button("Listed on Template")) { for (var i = 0; i < pg.Selected.Count; i++) pg.Selected[i] = !pg.Pieces[i].AutoAdded; pg.Applied = false; }

        // Canonical order; armor + weapons on the left, accessories + facewear on the right.
        var order = Enumerable.Range(0, pg.Pieces.Count).OrderBy(i => SlotOrder(pg.Pieces[i])).ToList();
        var left = order.Where(i => !IsRightColumn(pg.Pieces[i])).ToList();
        var right = order.Where(i => IsRightColumn(pg.Pieces[i])).ToList();

        if (ImGui.BeginTable("##pieces", 2, ImGuiTableFlags.None))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawColumn(pg, left);
            ImGui.TableNextColumn();
            DrawColumn(pg, right);
            ImGui.EndTable();
        }
    }

    private static string Letters(string s) => new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    private static bool IsRightColumn(GlamourPiece p)
    {
        if (Letters(p.SlotLabel) is "face" or "facewear" or "glasses") return true;
        return ItemResolver.MapSlot(p.SlotLabel) is ApiEquipSlot.Ears or ApiEquipSlot.Neck or ApiEquipSlot.Wrists
            or ApiEquipSlot.RFinger or ApiEquipSlot.LFinger;
    }

    private static int SlotOrder(GlamourPiece p)
    {
        if (Letters(p.SlotLabel) is "face" or "facewear" or "glasses") return 20;
        return ItemResolver.MapSlot(p.SlotLabel) switch
        {
            ApiEquipSlot.Head => 0,
            ApiEquipSlot.Body => 1,
            ApiEquipSlot.Hands => 2,
            ApiEquipSlot.Legs => 3,
            ApiEquipSlot.Feet => 4,
            ApiEquipSlot.MainHand => 5,
            ApiEquipSlot.OffHand => 6,
            ApiEquipSlot.Ears => 10,
            ApiEquipSlot.Neck => 11,
            ApiEquipSlot.Wrists => 12,
            ApiEquipSlot.RFinger => 13,
            ApiEquipSlot.LFinger => 14,
            _ => 99,
        };
    }

    private void DrawColumn(PendingGlamour pg, List<int> indices)
    {
        var availW = ImGui.GetContentRegionAvail().X;
        var iconSize = ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeight();   // spans name line + dye line
        var boxSize = ImGui.GetFrameHeight();

        // All name chips in a column share one width, so the checkboxes line up.
        var nameW = 160f;
        foreach (var i in indices)
        {
            var v = pg.Views[i];
            nameW = MathF.Max(nameW, ImGui.CalcTextSize(v.Name + (v.Found ? "" : "  (not found)")).X + 24f);
        }
        nameW = MathF.Min(nameW, MathF.Max(120f, availW - iconSize - boxSize - 100f));

        foreach (var i in indices) DrawPieceRow(pg, i, iconSize, nameW);
    }

    private void DrawPieceRow(PendingGlamour pg, int i, float iconSize, float nameW)
    {
        var p = pg.Pieces[i];
        var view = pg.Views[i];
        var on = pg.Selected[i];

        // Slot type on its own line, above the whole row.
        ImGui.TextColored(new Vector4(.55f, .75f, 1f, 1f), SlotDisplay(p.SlotLabel));

        if (p.ItemName == ItemResolver.NoFacewearName) DrawNoFacewearIcon(iconSize);
        else DrawIcon(view.IconId, iconSize);
        ImGui.SameLine();

        ImGui.BeginGroup();

        // Line 1: name chip, then the checkbox right after it.
        var nameBg = view.Found ? new Vector4(.20f, .22f, .27f, 1f) : new Vector4(.45f, .15f, .15f, 1f);
        Chip(view.Name + (view.Found ? "" : "  (not found)"), nameBg, new Vector4(1, 1, 1, 1), $"n_{i}", nameW, nameW);
        ImGui.SameLine();
        if (ImGui.Checkbox($"##piece{i}", ref on)) { pg.Selected[i] = on; pg.Applied = false; }

        // Line 2: one chip per dye slot the item really has (none for undyeable gear).
        var listed = (string.IsNullOrEmpty(p.Dye1) ? 0 : 1) + (string.IsNullOrEmpty(p.Dye2) ? 0 : 1);
        var isFacewear = Letters(p.SlotLabel) is "face" or "facewear" or "glasses";
        var slots = isFacewear ? 0 : Math.Max((int)view.DyeSlots, listed);   // facewear colors are separate items, not dyes
        if (slots >= 1)
        {
            DyeBox(p.Dye1, view.Dye1Rgb, $"d1_{i}");
            if (slots >= 2)
            {
                ImGui.SameLine();
                DyeBox(p.Dye2, view.Dye2Rgb, $"d2_{i}");
            }
        }

        ImGui.EndGroup();
        ImGui.Spacing();
    }

    private void DrawIcon(uint iconId, float iconSize)
    {
        var size = new Vector2(iconSize, iconSize);
        if (iconId == 0) { ImGui.Dummy(size); return; }
        var wrap = textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, size);
    }

    private void DrawNoFacewearIcon(float iconSize)
    {
        var size = new Vector2(iconSize, iconSize);
        var wrap = GetNoFacewearIcon();
        if (wrap == null) { ImGui.Dummy(size); return; }
        ImGui.Image(wrap.Handle, size);
    }

    // Loads the embedded "empty facewear slot" image once, in the background.
    private IDalamudTextureWrap? GetNoFacewearIcon()
    {
        if (noFacewearIcon != null || noFacewearLoading) return noFacewearIcon;
        noFacewearLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("NoFacewear.png");
                if (stream == null) return;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                noFacewearIcon = await textures.CreateFromImageAsync(ms.ToArray());
            }
            catch { }
        });
        return null;
    }

    // Display-only names; the underlying slot labels used by the logic are untouched.
    private static string SlotDisplay(string label)
    {
        var key = new string(label.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return key switch
        {
            "" => label,
            "weapon" or "mainhand" => "Weapon",
            "offhand" => "Off hand",
            "face" or "facewear" or "glasses" => "Facewear",
            "ears" or "earrings" or "earring" => "Earrings",
            "neck" or "necklace" => "Necklace",
            "wrists" or "bracelets" or "bracelet" => "Bracelets",
            "ring" or "rings" or "finger" or "fingers" or "rightring" => "Ring",
            "leftring" => "Left ring",
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };
    }

    // Read-only dye swatch: a box in the dye's color with its name, like Glamourer's own rows.
    private static void DyeBox(string? name, uint? rgb, string id)
    {
        var empty = string.IsNullOrEmpty(name);
        Vector4 bg;
        if (empty) bg = new Vector4(0f, 0f, 0f, 0f);   // Undyed: transparent, only the border + text show
        else if (rgb is uint c) bg = new Vector4(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
        else bg = new Vector4(.35f, .35f, .35f, 1f);   // unknown dye name

        var luminance = 0.299f * bg.X + 0.587f * bg.Y + 0.114f * bg.Z;
        var fg = luminance > 0.55f ? new Vector4(0, 0, 0, 1) : new Vector4(1, 1, 1, 1);
        Chip(empty ? "○ Undyed" : name!, bg, empty ? new Vector4(.75f, .75f, .75f, 1f) : fg, id, 90f);
    }

    // Rounded, read-only "pill": colored background with text inside. Clicks do nothing.
    private static void Chip(string text, Vector4 bg, Vector4 fg, string id, float minWidth, float maxWidth = float.MaxValue)
    {
        var width = MathF.Min(MathF.Max(minWidth, ImGui.CalcTextSize(text).X + 24f), maxWidth);
        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, bg);   // same color on hover/click
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, bg);
        ImGui.PushStyleColor(ImGuiCol.Text, fg);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, .30f));   // keeps near-black dyes visible
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f);   // raise for rounder pills
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.Button($"{text}##{id}", new Vector2(width, 0));
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
    }

    // One place that turns page HTML into the piece list (shield rule + invisible fillers + facewear option).
    private List<GlamourPiece> ParsePage(string html)
    {
        var pieces = EorzeaCollectionScraper.Parse(html, addInvisibleShield);
        if (pieces.Count > 0)
        {
            if (fillUnused) ItemResolver.AddInvisibleFillers(pieces);
            if (removeFacewear) ItemResolver.AddFacewearRemoval(pieces);
        }
        return pieces;
    }

    private void LoadFromHtml(string html)
    {
        try
        {
            html ??= "";
            lastClipSeq = ClipboardHelper.SequenceNumber();  
            pending = null;

            var raw = ParsePage(html);
            if (raw.Count == 0)
            {
                SetStatus("No gear found in the clipboard. On the glamour page press Ctrl+A, then Ctrl+C, and try again.", true);
                return;
            }
            SetPending(raw, EorzeaCollectionScraper.ExtractTitle(html));
        }
        catch (Exception e) { SetStatus("Failed: " + e.Message, true); }
    }

    // Returns false if applying failed outright (for example after a Glamourer update changed its API).
    private bool ApplyPieces(List<GlamourPiece> chosen)
    {
        try
        {
            var resolved = resolver.Resolve(chosen);
            var errs = glamourer.Apply(resolved);
            if (errs.Count > 0) SetStatus($"Applied {resolved.Count} pieces. Errors: {string.Join("; ", errs)}", true);
            else SetStatus($"Applied {resolved.Count} pieces.");
            return true;
        }
        catch (Exception e)
        {
            SetStatus("Could not apply: " + e.Message + " (Glamourer may have changed; try updating both plugins)", true);
            return false;
        }
    }
}