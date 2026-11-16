namespace VgnBattery;

internal enum BatteryProtocol
{
    // CompX/JM receivers: 17-byte report ID 0x08, command 0x04, checksum 0x55.
    Vgn17,
    // Keychron Ultra-Link receivers: 64-byte report ID 0xB3, command 0x06.
    Keychron64
}

internal sealed record DeviceProfile(ushort VendorId, ushort ProductId, string FallbackName, bool Wired, BatteryProtocol Protocol = BatteryProtocol.Vgn17, bool QueryModel = false)
{
    // Each protocol lives on a different vendor collection. Sending a query to
    // the wrong collection would be interpreted as an unrelated command.
    internal bool Matches(HidDevice device) => Protocol switch
    {
        BatteryProtocol.Vgn17 => device.UsagePage == 0xff02 && device.Usage == 2 && device.InputLength == 17 && device.OutputLength == 17,
        BatteryProtocol.Keychron64 => device.UsagePage == 0xffc1 && device.Usage == 1 && device.InputLength == 64 && device.OutputLength == 64,
        _ => false
    };
}

internal static class DeviceCatalog
{
    // Only documented devices receive a battery query.
    // Product strings remain the primary source of the displayed device name.
    private static readonly DeviceProfile[] Profiles =
    [
        new(0x3554,0xfb3e,"VGN F2 Pro Max",false), new(0x3554,0xfb3d,"VGN F2 Pro Max",true),
        new(0x391d,0x102d,"VGN F2 Master GT",false), new(0x391d,0x1032,"VGN F2 Master GT",true),
        new(0x3554,0xf503,"VGN F1 Pro",false), new(0x3554,0xf502,"VGN F1 Pro",true),
        new(0x3554,0xf505,"VGN Dragonfly 4K 接收器",false), new(0x3554,0xf506,"VGN Dragonfly 系列有线",true),
        new(0x3554,0xf58a,"VXE R1 Pro / Pro Max",false), new(0x3554,0xf58c,"VXE R1 Pro / Pro Max",true),
        new(0x3554,0xf58e,"VXE R1 SE+",false), new(0x3554,0xf58f,"VXE R1 SE+",true),
        new(0x373b,0x1085,"VXE R1 SE+",false),
        new(0x373b,0x1031,"ATK F1 Ultimate",false), new(0x373b,0x102e,"ATK F1 Ultimate",true),
        new(0x373b,0x11d9,"ATK A9 Ultimate",false), new(0x373b,0x11b6,"ATK A9 Ultimate",true),
        new(0x373b,0x104d,"VXE MAD R",false), new(0x373b,0x103f,"VXE MAD R",true),
        new(0x373b,0x1040,"VXE MAD R Major Plus",false), new(0x373b,0x104c,"VXE MAD R Major Plus",true),
        // The receiver answers to one USB ID for every paired mouse, so the
        // model name comes from the receiver's own paired-device query.
        new(0x3434,0xd028,"Keychron Ultra-Link 8K",false,BatteryProtocol.Keychron64,true),
        new(0x3434,0xd048,"Keychron M5",true,BatteryProtocol.Keychron64),
        new(0x3434,0xd049,"Keychron M6 8K",true,BatteryProtocol.Keychron64)
    ];

    internal static DeviceProfile? Find(ushort vendor, ushort product) => Profiles.FirstOrDefault(p=>p.VendorId==vendor && p.ProductId==product);

    internal static bool IsVendor(ushort vendor) => Profiles.Any(p=>p.VendorId==vendor);

    internal static string Model(HidDevice device, DeviceProfile? profile)
    {
        string name = device.Product.Trim();
        if (name.Length > 0 && name.ContainsAny("VGN", "VXE", "ATK", "DragonFly", "Keychron")) return name;
        return profile?.FallbackName ?? (name.Length > 0 ? name : $"USB 鼠标 {device.VendorId:X4}:{device.ProductId:X4}");
    }

    private static bool ContainsAny(this string value, params string[] words) => words.Any(w=>value.Contains(w,StringComparison.OrdinalIgnoreCase));
}
