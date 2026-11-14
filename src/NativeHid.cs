using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VgnBattery;

internal sealed record HidDevice(string Path, ushort VendorId, ushort ProductId, string Product, ushort UsagePage, ushort Usage, ushort InputLength, ushort OutputLength, ushort FeatureLength);

internal static class NativeHid
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, int index, ref InterfaceData info);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData info, IntPtr detail, int size, out int required, IntPtr data);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    internal static SafeFileHandle Open(string path, bool write=false, bool overlapped=false) => CreateFile(path, write ? 0xc0000000u : 0, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0, IntPtr.Zero);

    internal static List<HidDevice> Enumerate()
    {
        var devices = new List<HidDevice>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (int i=0; ; i++)
            {
                var info = new InterfaceData { Size=Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref info))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception();
                }
                SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required < 6) continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref info, detail, required, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!path.Contains("vid_3554", StringComparison.OrdinalIgnoreCase) && !path.Contains("vid_373b", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = Open(path);
                    if (handle.IsInvalid) continue;
                    var attributes = new Attributes { Size=Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes)) continue;
                    if (!HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) < 0) continue;
                        var product = new byte[512];
                        HidD_GetProductString(handle, product, product.Length);
                        devices.Add(new(path, attributes.VendorId, attributes.ProductId, Encoding.Unicode.GetString(product).TrimEnd('\0'), caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, caps.FeatureLength));
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }
}
