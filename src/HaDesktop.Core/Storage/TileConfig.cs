namespace HaDesktop.Core.Storage;

public enum TileSize { Small, Wide }

/// <summary>A chosen quick-toggle tile, with optional overrides for the entity's default icon/friendly name/size. List order is display order.</summary>
public sealed record TileConfig(string EntityId, string? CustomLabel = null, string? CustomIcon = null, TileSize Size = TileSize.Small);
