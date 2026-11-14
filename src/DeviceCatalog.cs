namespace VgnBattery;

internal sealed record DeviceProfile(ushort VendorId, ushort ProductId, string FallbackName, bool Wired);

internal static class DeviceCatalog
{
    // Only documented Nordic52 devices receive the 17-byte battery query.
    // Product strings remain the primary source of the displayed device name.
    private static readonly DeviceProfile[] Profiles =
    [
        new(0x3554,0xfb3e,"VGN F2 Pro Max",false), new(0x3554,0xfb3d,"VGN F2 Pro Max",true),
        new(0x3554,0xf503,"VGN F1 Pro",false), new(0x3554,0xf502,"VGN F1 Pro",true),
        new(0x3554,0xf505,"VGN Dragonfly 4K 接收器",false), new(0x3554,0xf506,"VGN Dragonfly 系列有线",true),
        new(0x3554,0xf58a,"VXE R1 Pro / Pro Max",false), new(0x3554,0xf58c,"VXE R1 Pro / Pro Max",true),
        new(0x3554,0xf58e,"VXE R1 SE+",false), new(0x3554,0xf58f,"VXE R1 SE+",true),
        new(0x373b,0x1085,"VXE R1 SE+",false),
        new(0x373b,0x1031,"ATK F1 Ultimate",false), new(0x373b,0x102e,"ATK F1 Ultimate",true),
        new(0x373b,0x11d9,"ATK A9 Ultimate",false), new(0x373b,0x11b6,"ATK A9 Ultimate",true),
        new(0x373b,0x104d,"VXE MAD R",false), new(0x373b,0x103f,"VXE MAD R",true),
        new(0x373b,0x1040,"VXE MAD R Major Plus",false), new(0x373b,0x104c,"VXE MAD R Major Plus",true)
    ];

    internal static DeviceProfile? Find(ushort vendor, ushort product) => Profiles.FirstOrDefault(p=>p.VendorId==vendor && p.ProductId==product);

    internal static string Model(HidDevice device, DeviceProfile? profile)
    {
        string name = device.Product.Trim();
        if (name.Length > 0 && name.ContainsAny("VGN", "VXE", "ATK", "DragonFly")) return name;
        return profile?.FallbackName ?? (name.Length > 0 ? name : $"USB 鼠标 {device.VendorId:X4}:{device.ProductId:X4}");
    }

    private static bool ContainsAny(this string value, params string[] words) => words.Any(w=>value.Contains(w,StringComparison.OrdinalIgnoreCase));
}
