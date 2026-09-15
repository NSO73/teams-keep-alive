using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TeamsKeepAlive
{
  // Decides nothing: TrayApp hands it what to show through Mirror, and gets the user's requests
  // back through the callbacks.
  internal sealed class TrayMenu : IDisposable
  {
    private readonly Font font = new Font("Segoe UI Variable Text", 9.5f);   // Windows 11's UI font
    private readonly Action<AutoStopRule> setAutoStop;
    private readonly ToolStripMenuItem rowActive, rowAutoStop, rowAtTime, rowAutoStart;
    private readonly ToolStripMenuItem[] rowDurations;   // one per AutoStopRule.OfferedHours
    // No MaxLength: typing stops at it, and the part kept can read as another time, 18h 3 as 18:03.
    private readonly ToolStripTextBox timeField = new ToolStripTextBox
    {
      BorderStyle = BorderStyle.FixedSingle,
      TextBoxTextAlign = HorizontalAlignment.Center,
      Width = Dpi.Px(120),   // once: sizing on open would re-lay out the popup while it is shown
      Margin = new Padding(Dpi.Px(6), 0, Dpi.Px(6), Dpi.Px(5)),
      AccessibleName = Strings.TimeFieldName,   // on the item: it passes it on to the edit control
    };
    private readonly ToolStripRenderer lightRenderer = new Win11MenuRenderer(Win11Theme.Light);
    private readonly ToolStripRenderer darkRenderer = new Win11MenuRenderer(Win11Theme.Dark);
    private bool dark;
    private AutoStopRule autoStop;   // seeds the time field

    internal TrayMenu(Action toggleActive, Action<AutoStopRule> setAutoStop,
                      Action toggleAutoStart, Action quit)
    {
      this.setAutoStop = setAutoStop;
      Strip.Font = font;
      // Set before the rows join the strip: unset, the field keeps the system menu font, and the
      // join resets it to its default width, which no later font brings back.
      timeField.Font = font;
      // No row has an image, but the tick is centred in a box of this size.
      Strip.ImageScalingSize = new Size(Dpi.Px(16), Dpi.Px(16));
      Strip.Padding = new Padding(Dpi.Px(2), Dpi.Px(4), Dpi.Px(2), Dpi.Px(4));

      rowDurations = Array.ConvertAll(AutoStopRule.OfferedHours, hours =>
        Row(Strings.MenuDuration(hours), () => setAutoStop(AutoStopRule.After(hours))));
      rowAtTime = Row(Strings.MenuAtTime, CommitTimeField);   // applies whatever the field shows
      rowAutoStop = Row(Strings.MenuAutoStop, null);
      rowAutoStop.DropDownItems.AddRange(rowDurations);
      rowAutoStop.DropDownItems.AddRange(new ToolStripItem[]
      {
        new ToolStripSeparator(), rowAtTime, timeField
      });

      rowActive = Row(Strings.MenuActive, toggleActive);
      rowAutoStart = Row(Strings.MenuAutoStart, toggleAutoStart);
      Strip.Items.AddRange(new ToolStripItem[]
      {
        rowActive, rowAutoStop, rowAutoStart, new ToolStripSeparator(), Row(Strings.MenuQuit, quit)
      });

      // Enter only: the drop-down takes Escape before the field sees it.
      timeField.KeyDown += (sender, e) =>
      {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;   // also marks it handled: an unhandled Enter beeps
        CommitTimeField();
      };
      // A click on the row closes the menu before it could say why it did nothing, so the row takes
      // no click while the field holds no time.
      timeField.TextBox.TextChanged += delegate
      {
        rowAtTime.Enabled = TimeOfDay.TryParse(timeField.Text, out _);
      };
      // On focus and on click, since a click places the caret after focus: typing overwrites.
      timeField.TextBox.GotFocus += delegate { timeField.TextBox.SelectAll(); };
      timeField.TextBox.Click += delegate { timeField.TextBox.SelectAll(); };

      SyncTheme();
      Strip.Opening += delegate { SyncTheme(); };
      Strip.Opened += delegate { Native.RoundCorners(Strip.Handle, dark); };
      rowAutoStop.DropDown.Opened += delegate
      {
        Native.RoundCorners(rowAutoStop.DropDown.Handle, dark);
        // The time in force, or the next half hour, so Enter alone always applies something.
        timeField.Text =
          TimeOfDay.Format(autoStop.AtMinutes ?? TimeOfDay.NextHalfHour(DateTime.Now));
      };
    }

    internal ContextMenuStrip Strip { get; } = new ContextMenuStrip();

    internal void Mirror(bool active, AutoStopRule autoStop, bool autoStart, string activeNote,
                         string autoStartNote)
    {
      this.autoStop = autoStop;
      rowActive.Checked = active;
      rowActive.ShortcutKeyDisplayString = activeNote;
      rowAutoStart.Checked = autoStart;
      rowAutoStart.ShortcutKeyDisplayString = autoStartNote;
      rowAtTime.Checked = autoStop.AtMinutes.HasValue;
      for (int i = 0; i < rowDurations.Length; i++)
        rowDurations[i].Checked = !autoStop.AtMinutes.HasValue
                                  && autoStop.AfterHours == AutoStopRule.OfferedHours[i];
    }

    public void Dispose()
    {
      Strip.Dispose();
      font.Dispose();   // after the strip, which paints with it
    }

    // On a typo, Enter keeps the menu open with the text selected; the row takes no click then.
    // Otherwise the menu closes before the rule applies, as it does before a row's Click runs.
    private void CommitTimeField()
    {
      if (!TimeOfDay.TryParse(timeField.Text, out int minutes))
      {
        timeField.TextBox.Focus();
        timeField.TextBox.SelectAll();
        return;
      }
      Strip.Close();
      setAutoStop(AutoStopRule.At(minutes));
    }

    // On every open, so a theme switch needs no restart. A contrast theme gets the stock renderer
    // back, which null stands for, and system colours. The drop-down inherits the strip's renderer,
    // not its BackColor; the time field is a hosted control, which no renderer draws.
    private void SyncTheme()
    {
      bool highContrast = SystemInformation.HighContrast;
      dark = !highContrast && Win11Theme.SystemIsDark();
      Win11Theme theme = dark ? Win11Theme.Dark : Win11Theme.Light;
      Strip.Renderer = highContrast ? null : dark ? darkRenderer : lightRenderer;
      Strip.BackColor = rowAutoStop.DropDown.BackColor =
        highContrast ? SystemColors.Menu : theme.Background;
      timeField.BackColor = highContrast ? SystemColors.Window : theme.Field;
      timeField.ForeColor = highContrast ? SystemColors.WindowText : theme.Text;
    }

    // Padding gives the Windows 11 row height; Margin, a thin gap between pills.
    private static ToolStripMenuItem Row(string text, Action onClick)
    {
      var row = new ToolStripMenuItem(text)
      {
        Padding = new Padding(0, Dpi.Px(4), 0, Dpi.Px(4)),
        Margin = new Padding(0, Dpi.Px(1), 0, Dpi.Px(1)),
      };
      if (onClick != null) row.Click += delegate { onClick(); };
      return row;
    }
  }

  // app.manifest makes the process system DPI aware: metrics written at 96 dpi are scaled by hand,
  // and the scale cannot change while the process runs.
  internal static class Dpi
  {
    internal static readonly float Scale = ReadScale();

    internal static int Px(int at96Dpi) => (int)Math.Round(at96Dpi * Scale);

    private static float ReadScale()
    {
      using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
        return g.DpiX / 96f;
    }
  }

  // Every colour of a Windows 11 flyout is a neutral grey.
  internal sealed class Win11Theme
  {
    private const string Personalize =
      @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal static readonly Win11Theme
      Dark = new Win11Theme(background: 0x2B, text: 0xFF, textSecondary: 0x9A, hover: 0x3A,
                            separator: 0x3D, field: 0x33),
      Light = new Win11Theme(background: 0xF9, text: 0x1A, textSecondary: 0x8D, hover: 0xED,
                             separator: 0xE5, field: 0xFF);

    internal readonly Color Background, Text, TextSecondary, Hover, Separator, Field;

    private Win11Theme(int background, int text, int textSecondary, int hover, int separator,
                       int field)
    {
      Background = Grey(background);
      Text = Grey(text);
      TextSecondary = Grey(textSecondary);
      Hover = Grey(hover);
      Separator = Grey(separator);
      Field = Grey(field);
    }

    // The Windows mode, which the taskbar follows, not the app mode: this menu opens from the
    // taskbar.
    internal static bool SystemIsDark() =>
      Registry.GetValue(Personalize, "SystemUsesLightTheme", 1) is int light && light == 0;

    private static Color Grey(int level) => Color.FromArgb(level, level, level);
  }

  // Paints only the states this menu reaches: At a set time is the one entry ever disabled. No
  // background: ToolStrip fills BackColor before the renderer runs. No border: DWM draws one around
  // the window it rounds, see Native.RoundCorners.
  internal sealed class Win11MenuRenderer : ToolStripRenderer
  {
    private readonly Win11Theme theme;

    internal Win11MenuRenderer(Win11Theme theme) => this.theme = theme;

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
      if (!e.Item.Selected) return;
      int inset = Dpi.Px(4);
      var r = new Rectangle(inset, Dpi.Px(1), e.Item.Width - inset * 2, e.Item.Height - Dpi.Px(2));
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      using (GraphicsPath path = RoundedRect(r, Dpi.Px(4)))
      using (var brush = new SolidBrush(theme.Hover))
        e.Graphics.FillPath(brush, path);
    }

    // The caption pass carries the item's own Text; the shortcut pass, the note beside it. Drawn
    // here: the base pass paints a disabled entry in the system grey, whatever the theme.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
      Color color = e.Item.Enabled && e.Text == e.Item.Text ? theme.Text : theme.TextSecondary;
      TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont,
                            CenterVertically(e.TextRectangle, e.Item.Height), color, e.TextFormat);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
      e.ArrowColor = theme.Text;
      e.ArrowRectangle = CenterVertically(e.ArrowRectangle, e.Item.Height);
      base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
      int y = e.Item.Height / 2, inset = Dpi.Px(10);
      using (var pen = new Pen(theme.Separator))
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
      int cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2, cy = e.Item.Height / 2;
      Color ink = e.Item.Enabled ? theme.Text : theme.TextSecondary;
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      using (var pen = new Pen(ink, 1.6f * Dpi.Scale)
      {
        StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
      })
        e.Graphics.DrawLines(pen, new[]
        {
          new Point(cx - Dpi.Px(4), cy),
          new Point(cx - Dpi.Px(1), cy + Dpi.Px(3)),
          new Point(cx + Dpi.Px(4), cy - Dpi.Px(4))
        });
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
      var path = new GraphicsPath();
      int d = radius * 2;
      path.AddArc(r.X, r.Y, d, d, 180, 90);
      path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
      path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      path.CloseFigure();
      return path;
    }

    // Rows are padded taller than the layout measured, and the extra height lands below the
    // content: this recentres it.
    private static Rectangle CenterVertically(Rectangle r, int itemHeight)
    {
      r.Y = (itemHeight - r.Height) / 2;
      return r;
    }
  }
}
