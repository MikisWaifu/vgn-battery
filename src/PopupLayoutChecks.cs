#if UI_QA
namespace VgnBattery;

internal static class PopupLayoutChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        using var popup=new BatteryPopup { Opacity=0,ShowInTaskbar=false,Enabled=false };
        var at=DateTimeOffset.Now;
        MouseBattery Device(string id,int? percent=50) => new(id,new(percent,false,"",at,"USB 有线","Layout test mouse"));
        var pair=new[] { Device("current"),Device("other") };
        var area=Screen.PrimaryScreen!.WorkingArea;
        popup.Location=new Point(area.Left+24,area.Top+24);
        popup.Show();
        try
        {
            popup.SetOtherDevices("current",pair);
            await Task.Delay(30);
            check(area.Contains(popup.Bounds),"overview fits within the working area");
            var position=popup.Location;
            popup.SetOtherDevices("current",[Device("current",49),Device("other",48)]);
            check(popup.Location==position,"a reading refresh preserves the popup position");
            foreach(var mode in Enum.GetValues<MouseDisplayMode>())
            {
                popup.SetDisplayOptions(mode,"current",[new("current","Current mouse"),new("other","Other mouse")],30,5);
                popup.SetOtherDevices("current",pair);
            }
            var many=Enumerable.Range(0,30).Select(i=>Device(i==0?"current":"other-"+i)).ToArray();
            popup.SetOtherDevices("current",many);
            await Task.Delay(30);
            var viewport=popup.Controls.OfType<Panel>().Single();
            check(area.Contains(popup.Bounds) && viewport.AutoScroll && viewport.VerticalScroll.Visible,"a long device list stays on screen and scrolls");
            viewport.AutoScrollPosition=new Point(0,80);
            var scroll=viewport.AutoScrollPosition;
            popup.SetOtherDevices("current",many.Select(d=>d with { Reading=d.Reading with { Percent=51 } }).ToArray());
            check(viewport.AutoScrollPosition==scroll,"a reading refresh preserves the overview scroll position");
            int expandedHeight=popup.Height;
            popup.SetOtherDevices("current",[Device("current")]);
            await Task.Delay(30);
            check(popup.Height<=expandedHeight && !viewport.VerticalScroll.Visible,"removing other devices removes their extra space");
        }
        finally { popup.Hide(); }
    }
}
#endif
