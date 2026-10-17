# GlamourSync

A Dalamud plugin that takes a glamour from [Eorzea Collection](https://ffxiv.eorzeacollection.com/glamours)
and applies it to your character through [Glamourer](https://github.com/Ottermandias/Glamourer),
with a preview of every piece, its icon and its dyes, so you can pick exactly what to apply.

## Installation

1. In Dalamud settings, open **Experimental** and add this URL under *Custom Plugin Repositories*:
   `https://raw.githubusercontent.com/ratxiv/GlamourSync/master/repo.json`
2. Open the plugin installer (`/xlplugins`) and install **GlamourSync**.

## How it works

1. Open a glamour page on Eorzea Collection in your browser.
2. Press **Ctrl+A**, then **Ctrl+C** (copying the page source also works).
3. In game, GlamourSync shows the glamour in its window: every slot with its icon, item name and dyes.
4. Untick anything you want to leave as it is, then click **Apply Selected**.

GlamourSync **never contacts the Eorzea Collection website**. It only reads what you copy to your own
clipboard, so you load the page yourself in your own browser.

## Features

- Shows each piece with its icon, name, slot and dye colors before anything is applied.
- Per-slot checkboxes, plus **All**, **None** and **Listed on Template** buttons.
- **Unused slots** can be filled with "The Emperor's New ..." items so you look like the template, including
  the invisible shield for Paladin glamours that don't list one.
- Facewear color variants are matched to the right item, and a glamour without facewear can optionally
  remove your current facewear.
- **Revert to Previous Gear** goes back to the look you had in Glamourer before applying, not just the game's gear.
- **History** of the glamours you applied (20 entries, with favorites that never expire).
- **Save as Design** stores the current look as a Glamourer design.
- Options (the gear icon) are saved between sessions.

## Commands

| Command | What it does |
|---|---|
| `/glamoursync` | Opens or closes the window |
| `/glamoursync apply` | Reads the clipboard and applies the glamour right away, with the window closed |
| `/glamoursync revert` | Goes back to your previous look |

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) (through XIVLauncher)
- [Glamourer](https://github.com/Ottermandias/Glamourer) (which itself needs Penumbra)
- Windows (or the game running under Wine). The game client should be in **English**, because items are
  matched by name.

## Credits and notes

- Glamour data comes from [Eorzea Collection](https://ffxiv.eorzeacollection.com). 
- Applying looks relies on the public API of [Glamourer](https://github.com/Ottermandias/Glamourer) by Ottermandias.
  **Please report problems with GlamourSync here,
  not to the Glamourer developers.**

## License

Licensed under the GNU Affero General Public License v3.0 (see [LICENSE.md](LICENSE.md)).