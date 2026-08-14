using MeetingScribe.Audio;

namespace MeetingScribe.App.ViewModels;

/// <summary>One entry in a device-picker ComboBox: a real device, or the "System Default" sentinel (Id = null).</summary>
public sealed class DeviceItem
{
    public string? Id { get; }
    public string DisplayName { get; }

    private DeviceItem(string? id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public static DeviceItem SystemDefault { get; } = new(null, "System Default");

    public static DeviceItem FromInfo(AudioDeviceInfo info) =>
        new(info.Id, info.IsDefault ? $"{info.Name} (Default)" : info.Name);

    public override string ToString() => DisplayName;
}
