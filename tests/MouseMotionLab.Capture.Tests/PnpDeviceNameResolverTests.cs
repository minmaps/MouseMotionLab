using MouseMotionLab.Capture.Interop;

namespace MouseMotionLab.Capture.Tests;

public sealed class PnpDeviceNameResolverTests
{
    [Theory]
    [InlineData(
        @"\\?\HID#VID_31E3&PID_1402&MI_03&COL03#A&2380B4DD&0&0002#{4D1E55B2-F16F-11CF-88CB-001111000030}",
        @"HID\VID_31E3&PID_1402&MI_03&COL03\A&2380B4DD&0&0002")]
    [InlineData(
        @"\??\HID#VID_046D&PID_C54D&MI_00#8&256EBFD7&0&0000#{4D1E55B2-F16F-11CF-88CB-001111000030}",
        @"HID\VID_046D&PID_C54D&MI_00\8&256EBFD7&0&0000")]
    public void ConvertsRawInputInterfacePathToPnpInstanceId(string path, string expected)
    {
        Assert.Equal(expected, PnpDeviceNameResolver.TryConvertInterfacePathToInstanceId(path));
    }

    [Fact]
    public void InvalidInterfacePathReturnsNull()
    {
        Assert.Null(PnpDeviceNameResolver.TryConvertInterfacePathToInstanceId("not-a-device-path"));
    }
}
