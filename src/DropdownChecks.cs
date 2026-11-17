#if UI_QA
using System.Reflection;

namespace VgnBattery;

// Runs inside the QA application's real WinForms message loop. PerformClick
// uses ToolStrip's own item-click/close path without sending desktop input.
internal static class DropdownChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        if (!Application.MessageLoop || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Dropdown checks require the WinForms STA message loop.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = new TestOwner
        {
            Opacity = 0,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(200, 100)
        };
        using var choice = new StyledChoiceButton { Dock = DockStyle.Top, Height = 30 };
        owner.Controls.Add(choice);
        choice.SetChoices([new("a", "Fixed"), new("b", "Tiled")], "a");

        var menuField = typeof(StyledChoiceButton).GetField("openMenu", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(StyledChoiceButton), "openMenu");
        owner.Show();
        await Task.Yield();

        foreach (var scenario in new[] { "normal", "reentrant", "reopened" })
        {
            timeout.Token.ThrowIfCancellationRequested();
            choice.PerformClick();
            using var menu = menuField.GetValue(choice) as ContextMenuStrip
                ?? throw new InvalidDataException("Choice button did not open its dropdown.");
            // Keep these test-owned menus invisible; no mouse or keyboard input
            // is sent to the user or to another application.
            menu.Opacity = 0;
            bool closed = false;
            bool aliveInClosed = false;
            menu.Closed += (_, _) => { closed = true; aliveInClosed = !menu.IsDisposed; };
            string next = choice.SelectedValue == "a" ? "b" : "a";
            var item = (ToolStripMenuItem)menu.Items[next == "a" ? 0 : 1];
            EventHandler closeDuringSelection = (_, _) =>
            {
                // Reproduce a popup/layout change closing a still-clicking
                // dropdown before ToolStrip's OnItemClicked cleanup returns.
                owner.Width++;
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
            };
            if (scenario == "reentrant") choice.SelectionChanged += closeDuringSelection;
            try
            {
                item.PerformClick();
                check(choice.SelectedValue == next, $"{scenario} dropdown click commits its selection");
                check(closed && aliveInClosed && !menu.IsDisposed,
                    $"{scenario} dropdown survives its Closed event and click cleanup");
                while (!menu.IsDisposed) await Task.Delay(10, timeout.Token);
                check(menuField.GetValue(choice) is null,
                    $"{scenario} dropdown is disposed after the message pump and its owner reference is cleared");
            }
            finally
            {
                if (scenario == "reentrant") choice.SelectionChanged -= closeDuringSelection;
            }
        }
    }

    private sealed class TestOwner : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
#endif
