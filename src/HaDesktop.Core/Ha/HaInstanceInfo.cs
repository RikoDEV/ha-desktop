namespace HaDesktop.Core.Ha;

/// <summary>Mirrors the fields Home Assistant's own Settings → About page shows. Supervisor/OsVersion are null on Core/Container installs — only Home Assistant OS and Supervised installs run a Supervisor.</summary>
public sealed record HaInstanceInfo(string? CoreVersion, string? InstallationType, string? SupervisorVersion, string? OsVersion);
