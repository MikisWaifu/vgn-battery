namespace VgnBattery;

internal static class OtherDeviceChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var at = new DateTimeOffset(2026,11,17,12,0,0,TimeSpan.FromHours(8));
        MouseBattery Mouse(string id,int? percent,string transport="2.4 GHz USB 接收器",bool stale=false) =>
            new(id,new BatteryReading(percent,false,"",at,transport,"Same model"),stale);
        var mice = new[]
        {
            Mouse("current",80), Mouse("fresh",0,"USB 有线"),
            Mouse("history",95,stale:true), Mouse("unknown",null)
        };
        var rows = OtherDevicesPanel.CreateRows("current",mice);
        check(rows.Count == 3 && rows.All(r => r.DeviceId != "current"),"other-device list excludes only the details ID");
        check(rows.Select(r => r.DeviceId).Distinct().Count() == 3,"same-model devices remain separate in the overview");
        var empty = rows.Single(r => r.DeviceId == "fresh");
        check(empty.StatusText == "0%" && !empty.Stale,"overview preserves a fresh real zero percent");
        check(empty.Transport == "USB 有线","overview retains the connection type");
        var history = rows.Single(r => r.DeviceId == "history");
        check(history.Stale && history.StatusText.Contains("95%") && history.StatusText.Contains("上次"),"overview labels old percentages instead of presenting them as fresh");
        var unknown = rows.Single(r => r.DeviceId == "unknown");
        check(unknown.Stale && unknown.StatusText.Contains("未响应"),"never-read devices show an explicit unavailable state");
        check(OtherDevicesPanel.CreateRows("current",[mice[0]]).Count == 0,"a single device does not create an empty overview section");
        var disconnected = OtherDevicesPanel.CreateRows("removed",mice);
        check(disconnected.Count == 4,"a missing pinned details target does not hide other connected devices");
        var shared = new[] { Mouse("wired",100,"USB 有线"),Mouse("receiver",null) };
        var interfaces = OtherDevicesPanel.CreateRows("",shared);
        check(interfaces.Count == 2 && interfaces.Select(r => r.Transport).Distinct().Count() == 2,"wired and receiver interfaces are not guessed to be one device");
        var state = new MouseDisplayState();
        state.ApplySnapshot([Mouse("current",80),Mouse("other",42)]);
        state.ApplySnapshot([Mouse("current",79),Mouse("other",null)]);
        var cached = OtherDevicesPanel.CreateRows("current",state.Devices).Single();
        check(cached.StatusText.Contains("42%") && cached.Stale,"overview consumes the per-device history already held by display state");
    }
}
