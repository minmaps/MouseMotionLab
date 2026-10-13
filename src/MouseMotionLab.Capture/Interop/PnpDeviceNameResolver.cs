using System.Runtime.InteropServices;
using System.Text;

namespace MouseMotionLab.Capture.Interop;

public static class PnpDeviceNameResolver
{
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const int MaximumParentDepth = 5;

    private static readonly DevicePropertyKey BusReportedDescription = new(
        new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
        4);

    public static string? ResolveBusReportedName(string rawInputDevicePath)
    {
        var instanceId = TryConvertInterfacePathToInstanceId(rawInputDevicePath);
        if (instanceId is null ||
            ConfigurationManagerNative.LocateDeviceNode(out var deviceNode, instanceId, 0) != CrSuccess)
        {
            return null;
        }

        for (var depth = 0; depth < MaximumParentDepth; depth++)
        {
            var description = ReadStringProperty(deviceNode, BusReportedDescription);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }

            if (ConfigurationManagerNative.GetParent(out deviceNode, deviceNode, 0) != CrSuccess)
            {
                break;
            }
        }

        return null;
    }

    public static string? TryConvertInterfacePathToInstanceId(string rawInputDevicePath)
    {
        if (string.IsNullOrWhiteSpace(rawInputDevicePath))
        {
            return null;
        }

        var path = rawInputDevicePath.Trim();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        var components = path.Split('#');
        if (components.Length < 3 || components.Take(3).Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return string.Join('\\', components.Take(3));
    }

    private static string? ReadStringProperty(uint deviceNode, DevicePropertyKey propertyKey)
    {
        uint propertyType;
        uint requiredSize = 0;
        var key = propertyKey;
        var result = ConfigurationManagerNative.GetDeviceNodeProperty(
            deviceNode,
            ref key,
            out propertyType,
            null,
            ref requiredSize,
            0);
        if (result != CrBufferSmall || requiredSize < sizeof(char))
        {
            return null;
        }

        var buffer = new byte[requiredSize];
        result = ConfigurationManagerNative.GetDeviceNodeProperty(
            deviceNode,
            ref key,
            out propertyType,
            buffer,
            ref requiredSize,
            0);
        if (result != CrSuccess)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, checked((int)requiredSize)).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevicePropertyKey(Guid formatId, uint propertyId)
    {
        private readonly Guid _formatId = formatId;
        private readonly uint _propertyId = propertyId;
    }

    private static class ConfigurationManagerNative
    {
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        internal static extern uint LocateDeviceNode(
            out uint deviceNode,
            string deviceId,
            uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
        internal static extern uint GetParent(
            out uint parentDeviceNode,
            uint deviceNode,
            uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
        internal static extern uint GetDeviceNodeProperty(
            uint deviceNode,
            ref DevicePropertyKey propertyKey,
            out uint propertyType,
            [Out] byte[]? propertyBuffer,
            ref uint propertyBufferSize,
            uint flags);
    }
}
