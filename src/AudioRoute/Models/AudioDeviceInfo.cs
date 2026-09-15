using System;
using NAudio.CoreAudioApi;

namespace AudioRoute.Models;

/// <summary>
/// Plain-data representation of an audio endpoint (output).
/// Decoupled from NAudio's MMDevice so the UI layer can be tested
/// without spinning up CoreAudio.
/// </summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public DeviceState State { get; init; }
    public bool IsDefault { get; set; }
    public string IconKind { get; init; } = "Speaker";

    public bool IsActive => State == DeviceState.Active;

    public string StatusText => State switch
    {
        DeviceState.Active => "Active",
        DeviceState.Unplugged => "Unplugged",
        DeviceState.NotPresent => "Not present",
        DeviceState.Disabled => "Disabled",
        _ => State.ToString()
    };
}
