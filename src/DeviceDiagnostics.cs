using System.Globalization;

namespace VgnBattery;

internal static class DeviceDiagnostics
{
    internal static object Collect()
    {
        var failures = new List<HidProbeFailure>();
        // Metadata access uses a zero-access handle. This path never opens a
        // read/write stream or sends an input/output/feature report.
        var devices = NativeHid.Enumerate(includeAll: true, failures: failures);
        return CreateReport(devices, failures);
    }

    internal static object CreateReport(IEnumerable<HidDevice> devices, IEnumerable<HidProbeFailure> failures) => new
    {
        AppVersion = typeof(DeviceDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown",
        Devices = devices.Select(device => new
        {
            device.Product,
            VendorId = Hex(device.VendorId),
            ProductId = Hex(device.ProductId),
            UsagePage = Hex(device.UsagePage),
            Usage = Hex(device.Usage),
            InputReportLength = (int)device.InputLength,
            OutputReportLength = (int)device.OutputLength,
            FeatureReportLength = (int)device.FeatureLength
        }).ToList(),
        Failures = failures.Select(failure => new
        {
            VendorId = failure.VendorId.HasValue ? Hex(failure.VendorId.Value) : null,
            ProductId = failure.ProductId.HasValue ? Hex(failure.ProductId.Value) : null,
            failure.Stage,
            failure.Win32Error
        }).ToList()
    };

    private static string Hex(ushort value) => value.ToString("X4", CultureInfo.InvariantCulture);
}
