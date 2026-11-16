namespace VgnBattery;

internal enum MouseDisplayMode
{
    Fixed,
    Rotate,
    Tiled
}

/// <summary>
/// Keeps presentation state by opaque device ID. Call on the UI thread after a
/// complete ReadAll snapshot; this class never queries hardware or saves settings.
/// </summary>
internal sealed class MouseDisplayState
{
    private const int HistoryLimit = 32;
    private readonly Dictionary<string, RememberedMouse> history = new(StringComparer.Ordinal);
    private IReadOnlyList<MouseBattery> devices = Array.Empty<MouseBattery>();
    private MouseBattery emptyPlaceholder = GenericPlaceholder();
    private MouseDisplayMode mode = MouseDisplayMode.Fixed;
    private string fixedDeviceId = "";
    private string rotatingDeviceId = "";
    private string detailsDeviceId = "";
    private long historySequence;

    public MouseDisplayMode Mode
    {
        get => mode;
        set
        {
            var next = value is MouseDisplayMode.Fixed or MouseDisplayMode.Rotate or MouseDisplayMode.Tiled
                ? value : MouseDisplayMode.Fixed;
            if (mode == next) return;
            mode = next;
            ResetRotation();
        }
    }

    public string FixedDeviceId
    {
        get => fixedDeviceId;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "" : value;
            if (string.Equals(fixedDeviceId, next, StringComparison.Ordinal)) return;
            fixedDeviceId = next;
            if (FindCurrent(next) is { } current) Remember(current);
            ResetRotation();
            TrimHistory();
        }
    }

    /// <summary>Current actual devices only; placeholders and removed IDs are excluded.</summary>
    public IReadOnlyList<MouseBattery> Devices => devices;

    public void ApplySnapshot(IReadOnlyList<MouseBattery> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = devices.ToDictionary(mouse => mouse.DeviceId, StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = new List<MouseBattery>();
        MouseBattery? fallback = null;

        foreach (var incoming in snapshot)
        {
            if (incoming is null) continue;
            if (string.IsNullOrWhiteSpace(incoming.DeviceId))
            {
                fallback ??= incoming;
                continue;
            }

            // An ID may appear twice in a malformed snapshot. Keep its first
            // position while folding subsequent readings into that same ID.
            MouseBattery? last = null;
            if (positions.TryGetValue(incoming.DeviceId, out int position)) last = next[position];
            else if (previous.TryGetValue(incoming.DeviceId, out var prior)) last = prior;
            else if (history.TryGetValue(incoming.DeviceId, out var remembered)) last = remembered.Mouse;

            var merged = Merge(incoming, last);
            if (positions.ContainsKey(incoming.DeviceId)) next[position] = merged;
            else
            {
                positions.Add(incoming.DeviceId, next.Count);
                next.Add(merged);
            }
            Remember(merged);
        }

        devices = Array.AsReadOnly(next.ToArray());
        emptyPlaceholder = fallback is null ? GenericPlaceholder() : fallback with
        {
            DeviceId = "",
            Reading = fallback.Reading with { Percent = null, Charging = false },
            Stale = true
        };
        EnsureFixedSelection();
        if (FindCurrent(rotatingDeviceId) is null) ResetRotation();
        TrimHistory();
    }

    public IReadOnlyList<MouseBattery> GetVisible()
    {
        if (mode == MouseDisplayMode.Fixed)
        {
            EnsureFixedSelection();
            if (fixedDeviceId.Length == 0) return [emptyPlaceholder];
            return [FindCurrent(fixedDeviceId) ?? Disconnected(fixedDeviceId, isFixed: true)];
        }

        if (devices.Count == 0) return [emptyPlaceholder];
        if (mode == MouseDisplayMode.Tiled) return devices;
        if (FindCurrent(rotatingDeviceId) is not { } current)
        {
            ResetRotation();
            current = devices[0];
            if (FindCurrent(rotatingDeviceId) is { } reset) current = reset;
        }
        return [current];
    }

    public void RotateNext()
    {
        if (devices.Count == 0)
        {
            rotatingDeviceId = "";
            return;
        }

        int currentIndex = -1;
        for (int i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i].DeviceId, rotatingDeviceId, StringComparison.Ordinal))
            {
                currentIndex = i;
                break;
            }
        rotatingDeviceId = devices[(currentIndex + 1) % devices.Count].DeviceId;
    }

    public MouseBattery GetDetails(string? deviceId = null)
    {
        var result = !string.IsNullOrWhiteSpace(deviceId) ? PeekDevice(deviceId) : GetVisible()[0];
        detailsDeviceId = result.DeviceId;
        // Keep the open details target even if its receiver subsequently leaves.
        // Do not replace cached observations with synthetic disconnected text.
        if (FindCurrent(result.DeviceId) is { } actual) Remember(actual);
        TrimHistory();
        return result;
    }

    // Settings may look up a missing target's name without changing the open
    // details target. An explicit ID must never resolve to another mouse.
    public MouseBattery PeekDevice(string deviceId) => FindCurrent(deviceId)
        ?? Disconnected(deviceId, string.Equals(deviceId, fixedDeviceId, StringComparison.Ordinal));

    private static MouseBattery Merge(MouseBattery incoming, MouseBattery? last)
    {
        if (IsValid(incoming.Reading.Percent))
            return incoming.Stale ? incoming with { Reading = incoming.Reading with { Charging = false } } : incoming;

        if (last is not null && IsValid(last.Reading.Percent))
        {
            string message = string.IsNullOrWhiteSpace(incoming.Reading.Message)
                ? "鼠标暂未响应，显示上次读数" : incoming.Reading.Message;
            string transport = string.IsNullOrWhiteSpace(incoming.Reading.Transport)
                ? last.Reading.Transport : incoming.Reading.Transport;
            return incoming with
            {
                Reading = last.Reading with { Charging = false, Message = message, Transport = transport },
                Stale = true
            };
        }

        return incoming with { Reading = incoming.Reading with { Percent = null, Charging = false }, Stale = true };
    }

    private void EnsureFixedSelection()
    {
        if (fixedDeviceId.Length != 0 || devices.Count == 0) return;
        fixedDeviceId = (devices.FirstOrDefault(mouse => IsValid(mouse.Reading.Percent)) ?? devices[0]).DeviceId;
    }

    private void ResetRotation()
    {
        rotatingDeviceId = FindCurrent(fixedDeviceId)?.DeviceId ?? devices.FirstOrDefault()?.DeviceId ?? "";
    }

    private MouseBattery? FindCurrent(string deviceId) => deviceId.Length == 0 ? null
        : devices.FirstOrDefault(mouse => string.Equals(mouse.DeviceId, deviceId, StringComparison.Ordinal));

    private MouseBattery Disconnected(string deviceId, bool isFixed)
    {
        string message = isFixed ? "固定的鼠标未连接" : "鼠标未连接";
        if (history.TryGetValue(deviceId, out var known))
        {
            var reading = known.Mouse.Reading;
            if (IsValid(reading.Percent)) message += "，显示上次读数";
            return new MouseBattery(deviceId, reading with { Charging = false, Message = message, Transport = "未连接" }, Stale: true);
        }

        return new MouseBattery(deviceId,
            new BatteryReading(null, false, message, DateTimeOffset.MinValue, "未连接"), Stale: true);
    }

    private void Remember(MouseBattery mouse)
    {
        if (mouse.DeviceId.Length == 0) return;
        history[mouse.DeviceId] = new RememberedMouse(mouse, ++historySequence);
    }

    private void TrimHistory()
    {
        // Current devices remain independently available in Devices. Only the
        // reconnect/history cache is bounded; fixed and open details IDs stay pinned.
        while (history.Count > HistoryLimit)
        {
            var oldest = history
                .Where(pair => !string.Equals(pair.Key, fixedDeviceId, StringComparison.Ordinal)
                    && !string.Equals(pair.Key, detailsDeviceId, StringComparison.Ordinal))
                .MinBy(pair => pair.Value.Sequence);
            if (oldest.Key is null) break;
            history.Remove(oldest.Key);
        }
    }

    private static bool IsValid(int? percent) => percent is >= 0 and <= 100;

    private static MouseBattery GenericPlaceholder() => new("",
        new BatteryReading(null, false, "未发现受支持的 USB 鼠标", DateTimeOffset.MinValue, "未连接"), Stale: true);

    private sealed record RememberedMouse(MouseBattery Mouse, long Sequence);
}
