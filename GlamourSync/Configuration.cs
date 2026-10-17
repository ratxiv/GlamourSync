using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace GlamourSync;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Options from the cog popup. Add new ones here (with a default) and in MainWindow.
    public bool WatchClipboard { get; set; } = true;
    public bool AddInvisibleShield { get; set; } = true;
    public bool FillUnused { get; set; } = true;
    public bool RemoveFacewear { get; set; } = true;

    // Recently applied glamours (newest first).
    public List<HistoryEntry> History { get; set; } = new();

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}

// Plain classes (not records) so the config serializer handles them without surprises.
public class HistoryPiece
{
    public string SlotLabel { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string? Dye1 { get; set; }
    public string? Dye2 { get; set; }
    public uint? ItemId { get; set; }
    public bool AutoAdded { get; set; }
}

public class HistoryEntry
{
    public string Title { get; set; } = "";
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    public bool Favorite { get; set; }
    public List<HistoryPiece> Pieces { get; set; } = new();
}