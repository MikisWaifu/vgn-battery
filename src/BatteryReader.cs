using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace VgnBattery;

internal sealed record BatteryReading(int? Percent, bool Charging, string Message, DateTimeOffset Timestamp, string Transport, string Model = "鼠标电量");

internal sealed class BatteryReader
{
    private static readonly byte[] BatteryQuery = [0x08, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49];
    // Keychron Ultra-Link: report ID 0xB3 with command 0x06, answered by a 0xB4
    // report whose byte 20 holds the level in bits 0-6 and charging in bit 7.
    private static readonly byte[] KeychronBatteryQuery = Command(64, 0xb3, 0x06);
    // The receiver answers to one USB ID for every paired mouse. This query
    // returns up to three paired-device slots of five bytes each, from offset 3.
    private static readonly byte[] KeychronInfoQuery = Command(21, 0xb5, 0x03);
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery");
    private int enumerationInvalidated = 1;
    private DateTimeOffset nextEnumeration;
    private List<HidDevice>? cachedDevices;
    private readonly object readGate = new();
    private readonly object cacheGate = new();
    private static readonly object LogGate = new();
    private readonly Dictionary<string, string> lastTelemetry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelCacheEntry> keychronModels = new(StringComparer.Ordinal);
    private long modelGeneration;
    private static readonly TimeSpan ModelLifetime = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions StatusOptions = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record ModelCacheEntry(string Model, DateTimeOffset ExpiresAt);

    private static byte[] Command(int length, byte reportId, byte command)
    {
        var frame = new byte[length];
        frame[0] = reportId;
        frame[1] = command;
        return frame;
    }

    public void InvalidateDevices()
    {
        lock (cacheGate)
        {
            // Do not let an in-flight lookup repopulate an invalidated cache.
            modelGeneration++;
            keychronModels.Clear();
        }
        Interlocked.Exchange(ref enumerationInvalidated,1);
    }

    private void ForgetModel(string deviceId)
    {
        lock (cacheGate) keychronModels.Remove(deviceId);
    }

    private static string NormalizeDevicePath(string path) => path.Trim().Replace('/', '\\').ToUpperInvariant();

    internal static string DeviceIdForPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDevicePath(path))));

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
        var readings = ReadAll();
        var result = (readings.FirstOrDefault(mouse => mouse.Reading.Percent.HasValue) ?? readings[0]).Reading;
        WriteStatus("status.json", result);
        return result;
    }

    public IReadOnlyList<MouseBattery> ReadAll()
    {
        // UI refreshes already serialize reads. This also protects callers that
        // invoke the CLI-compatible API and ReadAll on the same reader instance.
        lock (readGate)
        {
            var results = ReadAllAsync().GetAwaiter().GetResult();
            WriteStatus("devices-status.json", results);
            return results;
        }
    }

    private async Task<IReadOnlyList<MouseBattery>> ReadAllAsync()
    {
        List<(HidDevice Device, DeviceProfile Profile, string DeviceId)> devices;
        try
        {
            var discovered = GetDevices();
            devices = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var device in discovered)
                if (DeviceCatalog.Find(device.VendorId,device.ProductId) is { } profile && profile.Matches(device))
                {
                    string deviceId = DeviceIdForPath(device.Path);
                    if (seen.Add(deviceId)) devices.Add((device, profile, deviceId));
                }
            devices = devices.OrderBy(d => d.Profile.Wired ? 0 : 1)
                .ThenBy(d => d.Device.VendorId).ThenBy(d => d.Device.ProductId)
                .ThenBy(d => NormalizeDevicePath(d.Device.Path), StringComparer.Ordinal).ToList();
            PruneCaches(seen);
            if (devices.Count == 0)
            {
                BatteryReading result = new(null, false, "未发现受支持的 USB 鼠标", DateTimeOffset.Now, "未连接");
                if (discovered.Count > 0)
                {
                    var candidate = discovered.FirstOrDefault(d => d.UsagePage >= 0xff00) ?? discovered[0];
                    result = new(null, false, "已识别设备，但尚未支持它的电量协议", DateTimeOffset.Now, "USB", DeviceCatalog.Model(candidate, null));
                }
                return [new MouseBattery("", result)];
            }
        }
        catch (Exception ex)
        {
            Log("Enumeration: " + ex.Message);
            return [new MouseBattery("", new(null, false, "读取失败，请稍后重试", DateTimeOffset.Now, "未知"))];
        }

        using var parallelism = new SemaphoreSlim(4, 4);
        // WhenAll retains input order. A sleeping receiver has its own timeout
        // and cannot stop other receivers from being queried concurrently.
        return await Task.WhenAll(devices.Select(d => ReadDeviceAsync(d.Device, d.Profile, d.DeviceId, parallelism)))
            .ConfigureAwait(false);
    }

    private async Task<MouseBattery> ReadDeviceAsync(HidDevice device, DeviceProfile profile, string deviceId, SemaphoreSlim parallelism)
    {
        await parallelism.WaitAsync().ConfigureAwait(false);
        try
        {
            return new(deviceId, await QueryAsync(device, profile, deviceId).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            ForgetModel(deviceId);
            return new(deviceId, Unknown(device, profile, "暂未响应，可能休眠或已切换模式；将自动重试"));
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            ForgetModel(deviceId);
            Interlocked.Exchange(ref enumerationInvalidated, 1);
            Log("Query " + deviceId + ": " + ex.Message);
            return new(deviceId, Unknown(device, profile, "暂时无法读取，请关闭鼠标驱动后重试"));
        }
        catch (Exception ex)
        {
            ForgetModel(deviceId);
            Log("Query " + deviceId + ": " + ex.Message);
            return new(deviceId, Unknown(device, profile, "读取失败，请稍后重试"));
        }
        finally { parallelism.Release(); }
    }

    private static BatteryReading Unknown(HidDevice device, DeviceProfile profile, string message) =>
        new(null, false, message, DateTimeOffset.Now, profile.Wired ? "USB 有线" : "2.4 GHz USB 接收器", DeviceCatalog.Model(device, profile));

    private void PruneCaches(HashSet<string> present)
    {
        lock (cacheGate)
        {
            foreach (string id in keychronModels.Keys.Where(id => !present.Contains(id)).ToArray()) keychronModels.Remove(id);
            foreach (string id in lastTelemetry.Keys.Where(id => !present.Contains(id)).ToArray()) lastTelemetry.Remove(id);
        }
    }

    private static void WriteStatus(string fileName, object value)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, fileName), JsonSerializer.Serialize(value, StatusOptions));
        }
        catch { /* Diagnostic files never interfere with device reads. */ }
    }

    private Task<BatteryReading> QueryAsync(HidDevice device, DeviceProfile profile, string deviceId) => profile.Protocol switch
    {
        BatteryProtocol.Keychron64 => QueryKeychronAsync(device, profile, deviceId),
        BatteryProtocol.Vgn17 => QueryVgnAsync(device, profile, deviceId),
        _ => Task.FromResult(Unknown(device, profile, "暂不支持此电量协议"))
    };

    private async Task<BatteryReading> QueryVgnAsync(HidDevice device, DeviceProfile profile, string deviceId)
    {
        using var handle = NativeHid.Open(device.Path, write:true, overlapped:true);
        if (handle.IsInvalid) throw new Win32Exception();
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync:true);
        // Drop queued unsolicited/old telemetry so a previous value is not presented as fresh.
        for (int i=0;i<4;i++)
        {
            using var drainTimeout = new CancellationTokenSource(30);
            try { if (await stream.ReadAsync(new byte[17], drainTimeout.Token).ConfigureAwait(false) == 0) break; }
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
            if (length == 0) break;
            if (length == 17 && buffer[0] == 0x08 && buffer[1] == 0x04)
            {
                var result = Parse(buffer, device.ProductId, profile.Wired) with { Model=DeviceCatalog.Model(device,profile) };
                if (result.Percent.HasValue)
                {
                    LogTelemetry(deviceId, buffer, result);
                    return result;
                }
            }
        }
        ForgetModel(deviceId);
        return Unknown(device, profile, "未收到有效电量数据");
    }

    private async Task<BatteryReading> QueryKeychronAsync(HidDevice device, DeviceProfile profile, string deviceId)
    {
        using var handle = NativeHid.Open(device.Path, write:true, overlapped:true);
        if (handle.IsInvalid) throw new Win32Exception();
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 64, isAsync:true);
        for (int i=0;i<4;i++)
        {
            using var drainTimeout = new CancellationTokenSource(30);
            try { if (await stream.ReadAsync(new byte[64], drainTimeout.Token).ConfigureAwait(false) == 0) break; }
            catch (OperationCanceledException) { break; }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        var buffer = new byte[64];
        await stream.WriteAsync(KeychronBatteryQuery.AsMemory(), token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            int length = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (length == 0) break;
            if (length >= 21 && buffer[0] == 0xb4 && buffer[1] == 0x06)
            {
                var result = ParseKeychron(buffer, profile.Wired);
                if (result.Percent.HasValue)
                {
                    result = result with { Model=await ResolveKeychronModelAsync(stream, device, profile, deviceId).ConfigureAwait(false) };
                    LogTelemetry(deviceId, buffer, result);
                    return result;
                }
            }
        }
        ForgetModel(deviceId);
        return Unknown(device, profile, "未收到有效电量数据");
    }

    private async Task<string> ResolveKeychronModelAsync(FileStream stream, HidDevice device, DeviceProfile profile, string deviceId)
    {
        string fallback = DeviceCatalog.Model(device, profile);
        if (!profile.QueryModel) return fallback;
        long generation;
        lock (cacheGate)
        {
            if (keychronModels.TryGetValue(deviceId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Model;
            keychronModels.Remove(deviceId);
            generation = modelGeneration;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var token = timeout.Token;
            var buffer = new byte[64];
            await stream.WriteAsync(KeychronInfoQuery.AsMemory(), token).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                int length = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (length == 0) break;
                if (length < 8 || buffer[0] != 0xb6 || buffer[1] != 0x03) continue;
                string model = ParseKeychronModel(buffer.AsSpan(0, length)) ?? fallback;
                lock (cacheGate)
                {
                    if (generation == modelGeneration)
                        keychronModels[deviceId] = new(model, DateTimeOffset.UtcNow.Add(ModelLifetime));
                }
                return model;
            }
        }
        catch (Exception ex)
        {
            // The model name is cosmetic; a failed lookup must not discard a valid reading.
            ForgetModel(deviceId);
            Log("Model query: " + ex.Message);
        }
        return fallback;
    }

    internal static string? ParseKeychronModel(ReadOnlySpan<byte> report)
    {
        if (report.Length < 8 || report[0] != 0xb6 || report[1] != 0x03) return null;
        for (int slot = 0; slot < 3; slot++)
        {
            int offset = 3 + slot * 5;
            if (offset + 5 > report.Length) break;
            if (report[offset + 4] != 1) continue;
            ushort vendor = (ushort)(report[offset] | report[offset + 1] << 8);
            ushort product = (ushort)(report[offset + 2] | report[offset + 3] << 8);
            // Other slots may belong to unknown products. Only name a registered
            // Keychron mouse, and keep scanning rather than guessing another brand.
            if (DeviceCatalog.Find(vendor, product) is { Protocol: BatteryProtocol.Keychron64, Wired: true } profile)
                return profile.FallbackName;
        }
        return null;
    }

    private void LogTelemetry(string deviceId, byte[] report, BatteryReading result)
    {
        string telemetry = $"{result.Model}|{result.Percent}|{result.Charging}|{result.Transport}";
        lock (cacheGate)
        {
            if (lastTelemetry.TryGetValue(deviceId, out string? previous) && previous == telemetry) return;
            lastTelemetry[deviceId] = telemetry;
        }
        Log("Device " + deviceId + " battery response: " + Convert.ToHexString(report, 0, Math.Min(report.Length, 21)));
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

    internal static BatteryReading ParseKeychron(byte[] report, bool wired = false)
    {
        string transport = wired ? "USB 有线" : "2.4 GHz USB 接收器";
        if (report.Length < 21 || report[0] != 0xb4 || report[1] != 0x06 || (report[20] & 0x7f) > 100)
            return new(null, false, "电量数据校验未通过", DateTimeOffset.Now, transport);
        int percent = report[20] & 0x7f;
        bool chargingBit = (report[20] & 0x80) != 0;
        // The charging flag alone is not proof of current flow at 100%.
        bool charging = chargingBit && percent < 100;
        return new(percent, charging, chargingBit ? (percent == 100 ? "已充满（充电中）" : "有线连接 / 充电中") : "无线使用中", DateTimeOffset.Now, transport);
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "diagnostic.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, "");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
