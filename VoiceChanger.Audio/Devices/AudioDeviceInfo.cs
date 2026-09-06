namespace VoiceChanger.Audio.Devices;

/// <summary>
/// Represents an audio endpoint device.
/// </summary>
/// <param name="Id">Unique MMDevice endpoint ID.</param>
/// <param name="Name">Friendly display name of the device.</param>
/// <param name="IsDefault">Whether this is the system default for its role.</param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}
