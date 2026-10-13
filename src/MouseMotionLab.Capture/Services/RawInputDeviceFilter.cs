using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Services;

public static class RawInputDeviceFilter
{
    public static bool IsSelected(RawMouseEvent mouseEvent, string? selectedDeviceId) =>
        !string.IsNullOrWhiteSpace(selectedDeviceId) &&
        string.Equals(
            selectedDeviceId,
            mouseEvent.DeviceId,
            StringComparison.OrdinalIgnoreCase);
}
