namespace HaDesktop.Core.Ha;

/// <summary>
/// A "command_*" push notification message, HA -> this app, for actions with no visible
/// notification (mute the desktop, set its volume, etc.) — see HaClient.CommandReceived.
/// </summary>
public sealed record HaRemoteCommand(string Command, double? VolumeLevel = null);
