using System;
using System.Diagnostics;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Reacher;

internal sealed class Tray : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Coordinator _coordinator;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _normalIcon = MakeIcon(Drawing.Color.FromArgb(0xFF, 0xB2, 0x24));
    private readonly Drawing.Icon _quietIcon = MakeIcon(Drawing.Color.FromArgb(0x8A, 0x90, 0xA0));

    public Tray(Coordinator coordinator, int port, System.Windows.Application app)
    {
        _coordinator = coordinator;
        _icon = new Forms.NotifyIcon { Icon = _normalIcon, Text = "Reacher", Visible = true };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Reacher · listening on :{port}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Send test bubble", null, (_, _) => coordinator.Submit(new ReachRequest
        {
            Title = "Test reach",
            Message = "This is what a reach looks like. Click to acknowledge, right-click or ✕ to dismiss.",
            Source = "Reacher",
            Priority = "attention",
        }));

        var recent = new Forms.ToolStripMenuItem("Recent");
        recent.DropDownItems.Add("(none)");
        recent.DropDownOpening += (_, _) =>
        {
            recent.DropDownItems.Clear();
            foreach (var item in coordinator.Recent(10))
            {
                var text = $"{item.CreatedAt:HH:mm}  {item.Status.ToString().ToLowerInvariant()} · {item.Source}: {item.Message.ReplaceLineEndings(" ")}";
                if (text.Length > 80) text = text[..77] + "...";
                recent.DropDownItems.Add(text, null, (_, _) => coordinator.Submit(new ReachRequest
                {
                    Title = item.Title,
                    Message = item.Message,
                    Source = item.Source,
                    Open = item.Open,
                    Priority = "info",
                }));
            }
            if (recent.DropDownItems.Count == 0) recent.DropDownItems.Add(new Forms.ToolStripMenuItem("(none)") { Enabled = false });
        };
        menu.Items.Add(recent);
        menu.Items.Add("Dismiss all", null, (_, _) => coordinator.DismissAll());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var dnd = new Forms.ToolStripMenuItem("Do not disturb (urgent still comes through)") { CheckOnClick = true };
        dnd.CheckedChanged += (_, _) => { coordinator.DoNotDisturb = dnd.Checked; UpdateStatus(); };
        menu.Items.Add(dnd);

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = IsStartupEnabled() };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Open log", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _icon.Visible = false; app.Shutdown(); });

        _icon.ContextMenuStrip = menu;
        coordinator.Changed += UpdateStatus;
    }

    private void UpdateStatus()
    {
        var pending = _coordinator.PendingCount;
        _icon.Icon = _coordinator.DoNotDisturb ? _quietIcon : _normalIcon;
        var text = pending > 0 ? $"Reacher · {pending} waiting for you"
            : _coordinator.DoNotDisturb ? "Reacher · do not disturb"
            : "Reacher";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Reacher") is not null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Reacher", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("Reacher", throwOnMissingValue: false);
    }

    private static Drawing.Icon MakeIcon(Drawing.Color accent)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var ring = new Drawing.Pen(accent, 3.2f);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var fill = new Drawing.SolidBrush(accent);
            g.FillEllipse(fill, 10, 10, 12, 12);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
