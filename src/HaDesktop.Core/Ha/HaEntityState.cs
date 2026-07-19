namespace HaDesktop.Core.Ha;

public sealed class HaEntityState
{
    public required string EntityId { get; init; }
    public required string State { get; set; }
    public Dictionary<string, object?> Attributes { get; set; } = new();

    public string Domain => EntityId.Split('.', 2)[0];
    public bool IsOn => State == "on";
}
