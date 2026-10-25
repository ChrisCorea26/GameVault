namespace GameVault.ViewModels.Services;

/// <summary>Top-level destinations in the navigation shell.</summary>
public enum AppPage
{
    Library,
    Settings,
    Player,

    /// <summary>One game: metadata, collection fields, timeline, properties. Parameter: the game key.</summary>
    GameDetails,

    /// <summary>The Catalog Import wizard.</summary>
    CatalogImport,

    /// <summary>The Dump Cartridge wizard (SPEC §8).</summary>
    DumpCartridge,
}
