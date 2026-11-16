namespace VgnBattery;

internal static class MultiDeviceChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var at=new DateTimeOffset(2026,11,17,12,0,0,TimeSpan.FromHours(8));
        MouseBattery Sample(string id,int? percent,string model="Same mouse",int minute=0) =>
            new(id,new BatteryReading(percent,false,percent.HasValue?"无线使用中":"暂未响应",at.AddMinutes(minute),"2.4 GHz USB 接收器",model));

        var state=new MouseDisplayState();
        check(state.GetVisible().Count==1 && state.GetVisible()[0].Reading.Percent is null,"empty state keeps one gray icon");
        state.ApplySnapshot([Sample("a",80),Sample("b",30)]);
        check(state.Devices.Count==2,"same-model devices remain distinct");
        check(state.FixedDeviceId=="a" && state.GetVisible().Single().DeviceId=="a","fixed mode initially chooses one device");
        state.FixedDeviceId="b";
        check(state.GetVisible().Single().DeviceId=="b","fixed mode selects the requested device");
        state.FixedDeviceId="a";
        state.ApplySnapshot([Sample("a",null,minute:1),Sample("b",20,minute:1)]);
        var asleep=state.GetVisible().Single();
        check(asleep.DeviceId=="a" && asleep.Stale && asleep.Reading.Percent==80,"sleeping fixed target retains its own reading");
        check(asleep.Reading.Timestamp==at,"stale reading preserves original timestamp");
        check(state.GetDetails("b").Reading.Percent==20,"details select the clicked device");
        state.ApplySnapshot([Sample("b",19,minute:2)]);
        check(state.GetVisible().Single().DeviceId=="a" && state.GetVisible().Single().Stale,"unplugged fixed target does not switch to another mouse");
        state.Mode=MouseDisplayMode.Tiled;
        check(state.GetVisible().Count==1 && state.GetVisible()[0].DeviceId=="b","tiled display removes unplugged icons");
        state.ApplySnapshot([Sample("a",70,minute:3),Sample("b",18,minute:3),Sample("c",null,"Third mouse",3)]);
        check(state.GetVisible().Count==3 && state.GetVisible().Select(x=>x.DeviceId).Distinct().Count()==3,"tiled display shows each current device including unknown levels");
        check(!state.GetDetails("a").Stale && state.GetDetails("a").Reading.Percent==70,"reconnected device receives fresh data");
        state.Mode=MouseDisplayMode.Rotate;
        string first=state.GetVisible().Single().DeviceId;
        state.RotateNext();
        string rotated=state.GetVisible().Single().DeviceId;
        check(first!=rotated,"rotation changes the one displayed device");
        state.ApplySnapshot([Sample("c",15,"Third mouse",4),Sample("b",17,minute:4),Sample("a",69,minute:4)]);
        check(state.GetVisible().Single().DeviceId==rotated,"refresh does not reset the current rotation device");
        var seen=new HashSet<string>();
        for(int i=0;i<3;i++) { seen.Add(state.GetVisible().Single().DeviceId); state.RotateNext(); }
        check(seen.SetEquals(["a","b","c"]),"rotation visits all current devices");
        state.ApplySnapshot([Sample("c",14,"Third mouse",5)]);
        state.RotateNext();
        check(state.GetVisible().Single().DeviceId=="c","rotation with one mouse stays on it");
        state.Mode=MouseDisplayMode.Fixed;
        state.ApplySnapshot([Sample("a",69,minute:5),Sample("b",17,minute:5)]);
        state.ApplySnapshot([Sample("a",null,minute:6),Sample("b",null,minute:6)]);
        check(state.GetDetails("a").Reading.Percent==69 && state.GetDetails("b").Reading.Percent==17,"device history never mixes two same-model readings");
        state.ApplySnapshot([]);
        state.PeekDevice("a");
        check(state.GetDetails("b").DeviceId=="b" && state.GetDetails("b").Reading.Percent==17,"missing fixed-target label cannot replace another device's details");
        check(state.GetDetails("never-seen").DeviceId=="never-seen" && state.GetDetails("never-seen").Reading.Percent is null,"an unknown explicit details target never falls back to another mouse");

        var restored=new MouseDisplayState { FixedDeviceId="later" };
        restored.ApplySnapshot([Sample("a",80)]);
        check(restored.GetVisible().Single().DeviceId=="later" && restored.GetVisible().Single().Reading.Percent is null,"saved fixed target can remain disconnected");
        restored.ApplySnapshot([Sample("a",80),Sample("later",35,"Preferred mouse")]);
        check(restored.GetVisible().Single().DeviceId=="later" && restored.GetVisible().Single().Reading.Percent==35,"saved target restores automatically when reconnected");
        restored.Mode=MouseDisplayMode.Tiled;
        restored.ApplySnapshot([Sample("",null,"未发现支持的鼠标")]);
        check(restored.Devices.Count==0 && restored.GetVisible().Count==1,"no-device placeholder is not a selectable mouse");

        string id=BatteryReader.DeviceIdForPath(@"\\?\hid#vid_3434&pid_d028#receiver-a");
        check(id==BatteryReader.DeviceIdForPath(@"\\?\HID#VID_3434&PID_D028#RECEIVER-A"),"device identity ignores path case");
        check(id!=BatteryReader.DeviceIdForPath(@"\\?\hid#vid_3434&pid_d028#receiver-b"),"two identical receivers have distinct identities");
        check(!id.Contains("receiver",StringComparison.OrdinalIgnoreCase) && id.Length==64,"saved identity is a digest rather than a device path");

        byte[] ModelFrame(ushort product)
        {
            var report=new byte[64]; report[0]=0xb6; report[1]=3; report[2]=1;
            report[3]=0x34; report[4]=0x34; report[5]=(byte)product; report[6]=(byte)(product>>8); report[7]=1;
            return report;
        }
        check(BatteryReader.ParseKeychronModel(ModelFrame(0xd048))=="Keychron M5","receiver model recognizes M5");
        check(BatteryReader.ParseKeychronModel(ModelFrame(0xd049))=="Keychron M6 8K","receiver model recognizes M6");
        var invalid=ModelFrame(0xd048); invalid[0]=0xb4;
        check(BatteryReader.ParseKeychronModel(invalid) is null,"model lookup rejects the wrong reply report ID");
        check(BatteryReader.ParseKeychronModel(new byte[4]) is null,"model lookup rejects short replies");
        check(BatteryReader.ParseKeychronModel(ModelFrame(0xffff)) is null,"unknown paired model remains unidentified");
        var slots=ModelFrame(0xffff); slots[8]=0x34;slots[9]=0x34;slots[10]=0x49;slots[11]=0xd0;slots[12]=1;
        check(BatteryReader.ParseKeychronModel(slots)=="Keychron M6 8K","model lookup can skip an unknown connected slot");
        check(!(DeviceCatalog.Find(0x3434,0xd028)! with { Protocol=(BatteryProtocol)999 }).Matches(new HidDevice("",0x3434,0xd028,"",0xffc1,1,64,64,0)),"unknown protocol does not match a collection");
    }
}
