using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GlamourSync;

// Reads a glamour page (copied from the browser) into a list of pieces. Never contacts the website.
public static class EorzeaCollectionScraper
{
    private static readonly Regex PieceLink = new(@"^/glamours/([^/]+)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex SlotIcon = new(@"/slots/([^/.]+)\.png", RegexOptions.Compiled);

    public const string InvisibleShieldName = "The Emperor's New Shield";

    public static List<GlamourPiece> Parse(string html, bool addInvisibleShield = true)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var pieces = new List<GlamourPiece>();

        // A gear row = a "list-item" div that contains a slot icon.
        var rows = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' list-item ') and .//img[contains(@class,'gear-icon-slot')]]");
        if (rows == null) return pieces;

        foreach (var row in rows)
        {
            // Item name
            var name = row.SelectSingleNode(".//div[contains(@class,'list-item-title')]")?.InnerText;
            name = name == null ? null : HtmlEntity.DeEntitize(name).Trim();
            if (string.IsNullOrEmpty(name)) continue;

            // Slot + item ID from the "/glamours/<slot>/<itemId>" link
            string? slot = null; uint? itemId = null;
            var href = row.SelectSingleNode(".//a[starts-with(@href,'/glamours/')]")?.GetAttributeValue("href", "");
            if (href != null)
            {
                var m = PieceLink.Match(href);
                if (m.Success) { slot = m.Groups[1].Value; itemId = uint.Parse(m.Groups[2].Value); }
            }
            // Fallback: slot icon filename (.../slots/head.png)
            if (slot == null)
            {
                var src = row.SelectSingleNode(".//img[contains(@class,'gear-icon-slot')]")?.GetAttributeValue("src", "");
                var m = src == null ? null : SlotIcon.Match(src);
                if (m is { Success: true }) slot = m.Groups[1].Value;
            }
            if (slot == null) continue;

            // Dyes: each .tag in the description, e.g. "⬤ Metallic Silver"
            var dyes = new List<string>();
            var tags = row.SelectNodes(".//div[contains(@class,'list-item-description')]//div[contains(@class,'tag')]");
            if (tags != null)
                foreach (var t in tags)
                {
                    var raw = HtmlEntity.DeEntitize(t.InnerText);

                    // Real dye tags carry the colored "⬤" marker. Other tags (for example "Online Store Exclusive")
                    // must not take up a dye slot, or they push the second dye out.
                    var isDye = raw.Contains('⬤') || t.SelectSingleNode(".//*[contains(@class,'has-text-dye')]") != null;
                    if (!isDye) continue;

                    var d = raw.Replace("⬤", "").Trim();
                    if (d.Length == 0 || d.Equals("Online Store Exclusive", StringComparison.OrdinalIgnoreCase)) continue;
                    dyes.Add(d);
                }

            pieces.Add(new GlamourPiece(slot, name,
                dyes.Count > 0 ? dyes[0] : null,
                dyes.Count > 1 ? dyes[1] : null,
                itemId));
        }
        if (addInvisibleShield) AddInvisibleShieldIfPaladin(doc, pieces);
        return pieces;
    }

    private static string Letters(string s) => new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    // Paladin glamour with a weapon but no shield listed: add The Emperor's New Shield (invisible shield).
    private static void AddInvisibleShieldIfPaladin(HtmlDocument doc, List<GlamourPiece> pieces)
    {
        var hasWeapon = pieces.Any(p => Letters(p.SlotLabel) is "weapon" or "mainhand");
        var hasOffhand = pieces.Any(p => Letters(p.SlotLabel) is "offhand" or "shield");
        if (!hasWeapon || hasOffhand) return;

        var jobs = ExtractJobs(doc);
        if (!(jobs.Contains("PLD") || jobs.Contains("GLA") || jobs.Contains("Paladin") || jobs.Contains("Gladiator"))) return;

        pieces.Add(new GlamourPiece("offhand", InvisibleShieldName, null, null, null, AutoAdded: true));
    }

    // Reads the "Intended for" row: job icons (alt="PLD" title="Paladin").
    private static HashSet<string> ExtractJobs(HtmlDocument doc)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imgs = doc.DocumentNode.SelectNodes(
            "//span[normalize-space(text())='Intended for']/ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' columns ')][1]//img[contains(@src,'/icons/jobs/')]");
        if (imgs == null) return result;
        foreach (var img in imgs)
        {
            var alt = img.GetAttributeValue("alt", "").Trim();
            var title = img.GetAttributeValue("title", "").Trim();
            if (alt.Length > 0) result.Add(alt);
            if (title.Length > 0) result.Add(title);
        }
        return result;
    }

    
    public static string ExtractTitle(string html)
    {
        var m = Regex.Match(html, @"<title>\s*(.*?)\s*\|\s*Eorzea Collection", RegexOptions.Singleline);
        if (m.Success) return HtmlEntity.DeEntitize(m.Groups[1].Value);

        // A copied selection has no <title>; use the page heading instead.
        var h1 = Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (h1.Success)
        {
            var text = Regex.Replace(h1.Groups[1].Value, "<[^>]+>", " ");
            text = Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
            if (text.Length > 0) return text;
        }
        return "Unknown glamour";
    }

}