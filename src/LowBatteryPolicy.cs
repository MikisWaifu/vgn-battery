namespace VgnBattery;

internal sealed class LowBatteryPolicy
{
    private bool notified;
    private string? model;
    private int threshold;
    private bool enabled;

    internal void Reset() => notified = false;

    internal bool ShouldNotify(BatteryReading reading, bool isEnabled, int belowPercent)
    {
        belowPercent = Math.Clamp(belowPercent,1,99);
        if (threshold != belowPercent || enabled != isEnabled) Reset();
        threshold = belowPercent;
        enabled = isEnabled;
        if (!isEnabled || reading.Percent is not int percent) return false;
        if (model != reading.Model) Reset();
        model = reading.Model;
        if (reading.Charging || percent >= Math.Min(100, belowPercent + 5))
        {
            Reset();
            return false;
        }
        if (percent < belowPercent && !notified)
        {
            notified = true;
            return true;
        }
        return false;
    }
}
