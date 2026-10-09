namespace HaDesktop.Core.Ha;

public sealed class HaEntityState
{
    private string? _domain;

    public required string EntityId { get; init; }
    public required string State { get; set; }

    /// <summary>
    /// Attribute values as parsed by <see cref="HaStateParser"/>: string, double, bool or null for
    /// scalars; string[] or double[] for a uniform array; raw JSON text for anything else nested.
    /// </summary>
    public Dictionary<string, object?> Attributes { get; set; } = new();

    public string Domain => _domain ??= EntityId.Split('.', 2)[0];
    public bool IsOn => State == "on";
}
