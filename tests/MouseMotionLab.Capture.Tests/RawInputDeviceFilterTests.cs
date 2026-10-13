using MouseMotionLab.Capture.Services;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Tests;

public sealed class RawInputDeviceFilterTests
{
    [Fact]
    public void AcceptsOnlyTheExplicitlySelectedRawInputDevice()
    {
        var selected = new RawMouseEvent(1, 2, 3, 0, MouseButtonFlags.None, "mouse-a");
        var other = selected with { DeviceId = "mouse-b" };

        Assert.True(RawInputDeviceFilter.IsSelected(selected, "MOUSE-A"));
        Assert.False(RawInputDeviceFilter.IsSelected(other, "mouse-a"));
        Assert.False(RawInputDeviceFilter.IsSelected(selected, null));
    }
}
