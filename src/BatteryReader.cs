using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace VgnBattery;

internal sealed record BatteryReading(int? Percent, bool Charging, string Message, DateTimeOffset Timestamp, string Transport, string Model = "鼠标电量");

internal sealed class BatteryReader
{
    private static readonly byte[] BatteryQuery = [0x08, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49];
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery");
    private int enumerationInvalidated = 1;
    private DateTimeOffset nextEnumeration;
    private List<HidDevice>? cachedDevices;
    private string? lastTelemetry;

    public void InvalidateDevices() => Interlocked.Exchange(ref enumerationInvalidated,1);

    private List<HidDevice> GetDevices()
    {
        if (Interlocked.Exchange(ref enumerationInvalidated,0) != 0 || cachedDevices is null || DateTimeOffset.UtcNow >= nextEnumeration)
        {
            cachedDevices = NativeHid.Enumerate();
            nextEnumeration = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        return cachedDevices;
    }

    public BatteryReading Read()
    {
        BatteryReading result;
        try
        {
            var discovered = GetDevices();
            var devices = discovered.Where(d => DeviceCatalog.Find(d.VendorId,d.ProductId) is not null
                && d.UsagePage == 0xff02 && d.Usage == 2 && d.InputLength == 17 && d.OutputLength == 17)
                .OrderBy(d => DeviceCatalog.Find(d.VendorId,d.ProductId)!.Wired ? 0 : 1).ToList();
            result = new(null, false, "未发现受支持的 USB 鼠标", DateTimeOffset.Now, "未连接");
            if (devices.Count==0 && discovered.Count>0)
            {
                var candidate=discovered.FirstOrDefault(d=>d.UsagePage >= 0xff00) ?? discovered[0];
                result = new(null,false,"已识别设备，但尚未支持它的电量协议",DateTimeOffset.Now,"USB",DeviceCatalog.Model(candidate,null));
            }
            foreach (var device in devices)
            {
                try
                {
                    result = QueryAsync(device).GetAwaiter().GetResult();
                    if (result.Percent.HasValue) break;
                }
                catch (OperationCanceledException) { result = new(null, false, "暂未响应，可能休眠或已切换模式；将自动重试", DateTimeOffset.Now, "USB 接收器 / 有线", DeviceCatalog.Model(device, DeviceCatalog.Find(device.VendorId,device.ProductId))); }
                catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                {
                    InvalidateDevices();
                    result = new(null, false, "暂时无法读取，请关闭鼠标驱动后重试", DateTimeOffset.Now, "USB 接收器 / 有线", DeviceCatalog.Model(device, DeviceCatalog.Find(device.VendorId,device.ProductId)));
                    Log("Query: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Enumeration: " + ex.Message);
            result = new(null, false, "读取失败，请稍后重试", DateTimeOffset.Now, "未知");
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "status.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented=true, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { /* A status file is diagnostic only. */ }
        return result;
    }

    private async Task<BatteryReading> QueryAsync(HidDevice device)
    {
        using var handle = NativeHid.Open(device.Path, write:true, overlapped:true);
        if (handle.IsInvalid) throw new Win32Exception();
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync:true);
        // Drop queued unsolicited/old telemetry so a previous value is not presented as fresh.
        for (int i=0;i<4;i++)
        {
            using var drainTimeout = new CancellationTokenSource(30);
            try { await stream.ReadAsync(new byte[17], drainTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        var buffer = new byte[17];
        // HID queues the response; write the entire output report without FileStream buffering.
        await stream.WriteAsync(BatteryQuery.AsMemory(), token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            int length = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (length == 17 && buffer[0] == 0x08 && buffer[1] == 0x04)
            {
                var result = Parse(buffer, device.ProductId, DeviceCatalog.Find(device.VendorId,device.ProductId)!.Wired) with { Model=DeviceCatalog.Model(device,DeviceCatalog.Find(device.VendorId,device.ProductId)) };
                if (result.Percent.HasValue)
                {
                    string telemetry = $"{result.Model}|{result.Percent}|{result.Charging}|{result.Transport}";
                    if (lastTelemetry != telemetry)
                    {
                        Log("Battery response: " + Convert.ToHexString(buffer));
                        lastTelemetry = telemetry;
                    }
                    return result;
                }
            }
        }
        return new(null, false, "未收到有效电量数据", DateTimeOffset.Now, "USB 接收器 / 有线");
    }

    internal static BatteryReading Parse(byte[] report, ushort productId, bool? wired = null)
    {
        bool directCable = wired ?? productId == 0xfb3d;
        string transport = directCable ? "USB 有线" : "2.4 GHz USB 接收器";
        if (report.Length != 17 || report[0] != 8 || report[1] != 4 || report[2] != 0 || report.Sum(b=>(int)b) % 256 != 0x55 || report[6] > 100)
            return new(null, false, "电量数据校验未通过", DateTimeOffset.Now, transport);
        // An echoed query / all-zero payload is not a zero-percent battery reading.
        if (report.AsSpan(2, 14).IndexOfAnyExcept((byte)0) < 0)
            return new(null, false, "鼠标未响应，移动鼠标后刷新", DateTimeOffset.Now, transport);
        int percent = report[6];
        // The cable flag is not proof of current flow; expose a conservative label.
        bool cable = report[7] != 0 || directCable;
        return new(percent, cable && percent < 100, cable ? (percent == 100 ? "已充满（有线连接）" : "有线连接 / 充电中") : "无线使用中", DateTimeOffset.Now, transport);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "diagnostic.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
