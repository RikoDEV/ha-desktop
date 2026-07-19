using System;

namespace HaDesktop.Tray;

public sealed record NotificationHistoryEntry(string? Title, string Message, DateTimeOffset ReceivedAt);
