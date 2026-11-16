namespace VgnBattery;

// DeviceId is a hash of the normalized HID collection path, never the raw path.
// ReadAll returns fresh snapshots; the display layer owns cached/stale readings.
internal sealed record MouseBattery(string DeviceId, BatteryReading Reading, bool Stale = false);
