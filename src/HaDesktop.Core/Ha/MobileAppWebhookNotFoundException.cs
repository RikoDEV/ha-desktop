namespace HaDesktop.Core.Ha;

/// <summary>The webhook_id HA returned 404/410 for — the device registration behind it was deleted (e.g. removed from HA's UI) and needs to be redone.</summary>
public sealed class MobileAppWebhookNotFoundException : Exception
{
    public MobileAppWebhookNotFoundException(string message) : base(message) { }
}
