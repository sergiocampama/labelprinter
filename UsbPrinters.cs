using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LabelPrinter;

/// <summary>
/// A Windows printer whose port is a USB printer that is plugged in right now.
/// <see cref="DevicePath"/> is the USB device itself, which is what we print to.
/// </summary>
internal sealed record UsbPrinter(string Name, string Port, string? Model, string DevicePath)
{
    // What the printer list shows.
    public override string ToString() => Name;
}

/// <summary>
/// Lists the USB printers that are connected right now. Each is named after the
/// Windows printer on its "USBnnn" port, or after the model it reports when no
/// Windows printer uses that port: Windows can give a printer a new port number
/// when it is reconnected, leaving its Windows printer on the old one. We print
/// to the device directly, so the port only matters for the name.
/// </summary>
internal static class UsbPrinters
{
    public static List<UsbPrinter> Find()
    {
        // Port -> Windows printer name (first one wins if several share a port).
        var queues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, ports) in InstalledPrinters())
            foreach (var port in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                queues.TryAdd(port, name);

        var result = new List<UsbPrinter>();
        foreach (var (port, device) in PresentUsbPorts())
        {
            var name = queues.TryGetValue(port, out var queue) ? queue
                : !string.IsNullOrEmpty(device.Model) ? device.Model
                : $"Impresora USB ({port})";
            result.Add(new UsbPrinter(name, port, device.Model, device.Path));
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    // ---- Installed printers (EnumPrinters level 5: name + port) -------------

    private static List<(string Name, string Ports)> InstalledPrinters()
    {
        const uint PRINTER_ENUM_LOCAL = 0x2;
        var list = new List<(string, string)>();

        EnumPrintersW(PRINTER_ENUM_LOCAL, null, 5, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
            return list;

        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrintersW(PRINTER_ENUM_LOCAL, null, 5, buf, needed, out _, out uint count))
                return list;

            int size = Marshal.SizeOf<PRINTER_INFO_5>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_5>(buf + i * size);
                var name = Marshal.PtrToStringUni(info.pPrinterName);
                var port = Marshal.PtrToStringUni(info.pPortName);
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(port))
                    list.Add((name, port));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    // ---- Connected USB printing devices: port name -> (model, device path) --

    private static readonly Guid GUID_DEVINTERFACE_USBPRINT = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    private static Dictionary<string, (string? Model, string Path)> PresentUsbPorts()
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        var ports = new Dictionary<string, (string?, string)>(StringComparer.OrdinalIgnoreCase);
        var guid = GUID_DEVINTERFACE_USBPRINT;

        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
            return ports;
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface))
                    break;

                var port = ReadPortName(set, ref iface);
                if (port is null)
                    continue;
                var (path, dev) = ReadDetail(set, ref iface);
                if (path is null)
                    continue;
                ports[port] = (ReadModel(set, ref dev), path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return ports;
    }

    /// <summary>"Base Name" ("USB") + "Port Number" (3) = "USB003".</summary>
    private static string? ReadPortName(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        const int KEY_READ = 0x20019;
        IntPtr hkey = SetupDiOpenDeviceInterfaceRegKey(set, ref iface, 0, KEY_READ);
        if (hkey == new IntPtr(-1))
            return null;

        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
        if (key.GetValue("Port Number") is not int number)
            return null;
        var prefix = key.GetValue("Base Name") as string;
        return $"{(string.IsNullOrEmpty(prefix) ? "USB" : prefix)}{number:D3}";
    }

    /// <summary>The interface's device path (to open it) and its device info element.</summary>
    private static (string? Path, SP_DEVINFO_DATA Dev) ReadDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0)
            return (null, dev);

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD + one WCHAR, padded on 64-bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, required, out _, ref dev))
                return (null, dev);
            return (Marshal.PtrToStringUni(detail + 4), dev);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>The model the device reports over USB (e.g. "XP-420B"), if any.</summary>
    private static string? ReadModel(IntPtr set, ref SP_DEVINFO_DATA dev)
    {
        var key = DEVPKEY_Device_BusReportedDeviceDesc;
        var buf = new byte[512];
        if (!SetupDiGetDevicePropertyW(set, ref dev, ref key, out _, buf, (uint)buf.Length, out uint size, 0) || size < 2)
            return null;
        var text = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc =>
        new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    // ---- Interop ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_5
    {
        public IntPtr pPrinterName;
        public IntPtr pPortName;
        public uint Attributes;
        public uint DeviceNotSelectedTimeout;
        public uint TransmissionRetryTimeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level, IntPtr buf, uint cbBuf,
        out uint needed, out uint returned);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index,
        ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDeviceInterfaceRegKey(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        uint reserved, int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, uint detailSize, out uint required, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key,
        out uint type, byte[] buf, uint bufSize, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
