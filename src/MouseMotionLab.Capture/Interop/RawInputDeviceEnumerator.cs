using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseMotionLab.Capture.Interop;

public sealed record RawInputDeviceDescriptor(nint Handle, string Id, string DisplayName);

internal static partial class RawInputDeviceEnumerator
{
    private static readonly uint ListItemSize = (uint)Marshal.SizeOf<RawInputNative.RawInputDeviceListItem>();

    [GeneratedRegex("VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();

    [GeneratedRegex("(?:^|&)MI_([0-9A-F]{2})(?:&COL([0-9A-F]{2}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceRegex();

    private static readonly IReadOnlyDictionary<string, string> KnownUsbVendors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["046D"] = "Logitech",
            ["31E3"] = "Wooting"
        };

    internal static IReadOnlyList<RawInputDeviceDescriptor> EnumerateMice()
    {
        uint count = 0;
        var result = RawInputNative.GetRawInputDeviceList(null, ref count, ListItemSize);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceList failed.");
        }

        if (count == 0)
        {
            return Array.Empty<RawInputDeviceDescriptor>();
        }

        var items = new RawInputNative.RawInputDeviceListItem[count];
        result = RawInputNative.GetRawInputDeviceList(items, ref count, ListItemSize);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceList failed.");
        }

        var devices = new List<RawInputDeviceDescriptor>();
        var ordinal = 1;
        foreach (var item in items.Take((int)count))
        {
            if (item.Type != RawInputNative.RimTypeMouse || item.Device == nint.Zero)
            {
                continue;
            }

            var id = ReadDeviceName(item.Device);
            var match = VidPidRegex().Match(id);
            var interfaceMatch = InterfaceRegex().Match(id);
            var productName = PnpDeviceNameResolver.ResolveBusReportedName(id);
            var shortName = BuildDisplayName(match, interfaceMatch, productName, ordinal);
            devices.Add(new RawInputDeviceDescriptor(item.Device, id, shortName));
            ordinal++;
        }

        return devices
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string BuildDisplayName(
        Match vidPid,
        Match deviceInterface,
        string? productName,
        int ordinal)
    {
        var parts = new List<string>();
        if (vidPid.Success)
        {
            var vendorId = vidPid.Groups[1].Value.ToUpperInvariant();
            var productId = vidPid.Groups[2].Value.ToUpperInvariant();
            var vendorName = KnownUsbVendors.GetValueOrDefault(vendorId);
            var resolvedProduct = string.IsNullOrWhiteSpace(productName) ? null : productName.Trim();
            if (vendorName is not null &&
                (resolvedProduct is null || !resolvedProduct.Contains(vendorName, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedProduct = resolvedProduct is null
                    ? vendorName
                    : $"{vendorName} {resolvedProduct}";
            }

            parts.Add(resolvedProduct ?? "Souris HID");
            parts.Add($"VID_{vendorId} / PID_{productId}");
        }
        else
        {
            parts.Add(productName ?? "Souris HID");
        }

        if (deviceInterface.Success)
        {
            var interfaceText = $"MI_{deviceInterface.Groups[1].Value.ToUpperInvariant()}";
            if (deviceInterface.Groups[2].Success)
            {
                interfaceText += $" / COL{deviceInterface.Groups[2].Value.ToUpperInvariant()}";
            }

            parts.Add(interfaceText);
        }

        parts.Add($"Raw #{ordinal}");
        return string.Join(" · ", parts);
    }

    private static string ReadDeviceName(nint device)
    {
        uint size = 0;
        var empty = new StringBuilder(1);
        _ = RawInputNative.GetRawInputDeviceInfo(device, RawInputNative.RidiDeviceName, empty, ref size);
        if (size == 0)
        {
            return $"HANDLE_{device.ToInt64():X}";
        }

        var buffer = new StringBuilder((int)size + 1);
        var result = RawInputNative.GetRawInputDeviceInfo(device, RawInputNative.RidiDeviceName, buffer, ref size);
        return result == uint.MaxValue || buffer.Length == 0
            ? $"HANDLE_{device.ToInt64():X}"
            : buffer.ToString();
    }
}

