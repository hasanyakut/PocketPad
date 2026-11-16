using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PocketPad.Desktop.Services;

/// <summary>System tray icon (WinForms NotifyIcon) with Open / Exit menu.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            Padding = new Padding(4),
        };
        var open = new ToolStripMenuItem("PocketPad'i aç", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold),
            Padding = new Padding(8, 6, 24, 6),
        };
        var exit = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty))
        {
            Padding = new Padding(8, 6, 24, 6),
        };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Text = "PocketPad",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public void Show() => _icon.Visible = true;

    public void SetStatus(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/PocketPad.Desktop;component/Assets/PocketPad.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                // Small size for the notification area; Windows scales for DPI.
                return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (IOException)
        {
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Dark tray menu matching the app (WinForms menus are light by default).</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkMenuColors())
{
    private static readonly Color Text = Color.FromArgb(0xEC, 0xEE, 0xF2);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        using var brush = new SolidBrush(Color.FromArgb(0x32, 0x37, 0x43));
        var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundRect(r, 4);
        e.Graphics.FillPath(brush, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(0x25, 0x29, 0x30);
        private static readonly Color Border = Color.FromArgb(0x38, 0x3D, 0x49);

        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(0x32, 0x37, 0x43);
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
    }
}
