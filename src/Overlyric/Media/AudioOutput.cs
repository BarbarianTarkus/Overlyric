namespace Overlyric.Media;

/// <summary>
/// The default output device, reduced to the facts the timing profiles need: a stable key, a label
/// and whether Windows says the endpoint is Bluetooth.
/// </summary>
/// <remarks>
/// The id (<c>IMMDevice::GetId</c>) is the only stable key: friendly names are user-editable and two
/// outputs can share one, while the Bluetooth flag is advisory (it comes from the endpoint's
/// enumerator name, not from the codec) and never decides an offset on its own.
/// </remarks>
internal sealed record AudioOutput(string Id, string Name, bool IsBluetooth)
{
    /// <summary>The answer when no render endpoint can be read; it matches no saved profile.</summary>
    public static AudioOutput None { get; } = new("", "No output device", false);

    public bool IsKnown => Id.Length > 0;
}
