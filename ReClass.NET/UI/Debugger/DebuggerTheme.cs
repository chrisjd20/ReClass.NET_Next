using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ReClassNET.Native;

namespace ReClassNET.UI.Debugger
{
	public enum Severity { Neutral, Info, Success, Attention, Danger }

	/// <summary>
	/// The dark look shared by the debugger windows (Find writes/accesses and the instruction editor).
	/// Everything is painted with GDI+ so it looks the same on Windows and Mono.
	/// </summary>
	public static class DebuggerTheme
	{
		public static readonly Color Background = Color.FromArgb(0x14, 0x18, 0x1F);
		public static readonly Color Panel = Color.FromArgb(0x1B, 0x20, 0x29);
		public static readonly Color Raised = Color.FromArgb(0x23, 0x2A, 0x35);
		public static readonly Color Hover = Color.FromArgb(0x2C, 0x35, 0x43);
		public static readonly Color Border = Color.FromArgb(0x2E, 0x36, 0x44);
		public static readonly Color Text = Color.FromArgb(0xE6, 0xEA, 0xF0);
		public static readonly Color Muted = Color.FromArgb(0x8B, 0x95, 0xA5);
		public static readonly Color Faint = Color.FromArgb(0x5A, 0x63, 0x72);

		public static readonly Color Teal = Color.FromArgb(0x3D, 0xD6, 0xB5);
		public static readonly Color Amber = Color.FromArgb(0xF5, 0xB8, 0x4B);
		public static readonly Color Red = Color.FromArgb(0xFF, 0x6B, 0x6B);
		public static readonly Color Blue = Color.FromArgb(0x6F, 0xB3, 0xFF);
		public static readonly Color Violet = Color.FromArgb(0xB4, 0x8C, 0xFF);

		// Assembly tokens.
		public static readonly Color AsmMnemonic = Color.FromArgb(0x7C, 0xB7, 0xFF);
		public static readonly Color AsmRegister = Color.FromArgb(0x4F, 0xE0, 0xC0);
		public static readonly Color AsmNumber = Color.FromArgb(0xFF, 0xA8, 0x5C);
		public static readonly Color AsmKeyword = Color.FromArgb(0xC3, 0x9B, 0xFF);
		public static readonly Color AsmPunctuation = Color.FromArgb(0x9A, 0xA4, 0xB4);
		public static readonly Color AsmText = Text;

		private static Font uiFont, uiBold, uiSmall, uiSmallBold, uiTitle, mono, monoSmall, monoBold;

		public static Font UiFont => uiFont ?? (uiFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f));
		public static Font UiBold => uiBold ?? (uiBold = new Font(UiFont, FontStyle.Bold));
		public static Font UiSmall => uiSmall ?? (uiSmall = new Font(UiFont.FontFamily, 7.75f));
		public static Font UiSmallBold => uiSmallBold ?? (uiSmallBold = new Font(UiFont.FontFamily, 7.5f, FontStyle.Bold));
		public static Font UiTitle => uiTitle ?? (uiTitle = new Font(UiFont.FontFamily, 13f, FontStyle.Bold));
		public static Font Mono => mono ?? (mono = new Font(MonoFamily(), 9.5f));
		public static Font MonoSmall => monoSmall ?? (monoSmall = new Font(MonoFamily(), 8.25f));
		public static Font MonoBold => monoBold ?? (monoBold = new Font(MonoFamily(), 9.5f, FontStyle.Bold));

		private static FontFamily MonoFamily()
		{
			var installed = FontFamily.Families.Select(f => f.Name).ToList();
			foreach (var name in new[] { "Cascadia Mono", "Consolas", "Liberation Mono", "DejaVu Sans Mono", "Courier New" })
			{
				if (installed.Contains(name)) return new FontFamily(name);
			}
			return FontFamily.GenericMonospace;
		}

		public static int Scale(int pixels) => DpiUtil.ScaleIntX(pixels);

		public static Color Accent(Severity severity)
		{
			switch (severity)
			{
				case Severity.Success: return Teal;
				case Severity.Attention: return Amber;
				case Severity.Danger: return Red;
				case Severity.Info: return Blue;
				default: return Muted;
			}
		}

		public static Color Mix(Color a, Color b, float amount)
		{
			amount = Math.Max(0, Math.Min(1, amount));
			return Color.FromArgb(
				(int)(a.R + (b.R - a.R) * amount),
				(int)(a.G + (b.G - a.G) * amount),
				(int)(a.B + (b.B - a.B) * amount));
		}

		public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
		{
			var path = new GraphicsPath();
			float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
			if (d <= 0.5f) { path.AddRectangle(bounds); return path; }
			path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
			path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
			path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
			path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
			path.CloseFigure();
			return path;
		}

		public static void FillRounded(Graphics g, Color color, RectangleF bounds, float radius)
		{
			using (var path = RoundedRect(bounds, radius))
			using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
		}

		public static void StrokeRounded(Graphics g, Color color, RectangleF bounds, float radius, float width = 1f)
		{
			using (var path = RoundedRect(bounds, radius))
			using (var pen = new Pen(color, width)) g.DrawPath(pen, path);
		}

		public static void Smooth(Graphics g)
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.PixelOffsetMode = PixelOffsetMode.HighQuality;
		}

		public static Size Measure(string text, Font font) => TextRenderer.MeasureText(text ?? "", font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

		public static void DrawText(Graphics g, string text, Font font, Color color, int x, int y)
		{
			TextRenderer.DrawText(g, text ?? "", font, new Point(x, y), color, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
		}

		public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
		{
			TextRenderer.DrawText(g, text ?? "", font, bounds, color, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
		}

		/// <summary>A small rounded label such as CONFIRMED. Returns its width.</summary>
		public static int Pill(Graphics g, string text, Color accent, int x, int y, bool solid = false)
		{
			var size = Measure(text, UiSmallBold);
			int padX = Scale(7), height = size.Height + Scale(4);
			var bounds = new RectangleF(x, y, size.Width + padX * 2, height);
			Smooth(g);
			FillRounded(g, solid ? accent : Color.FromArgb(48, accent), bounds, height / 2f);
			if (!solid) StrokeRounded(g, Color.FromArgb(140, accent), bounds, height / 2f);
			DrawText(g, text, UiSmallBold, solid ? Background : accent, x + padX, y + Scale(2));
			return (int)bounds.Width;
		}

		public static int PillWidth(string text) => Measure(text, UiSmallBold).Width + Scale(14);

		/// <summary>A small monospace byte box. Returns its width.</summary>
		public static int Chip(Graphics g, string text, Color fill, Color border, Color foreground, int x, int y, Font font = null)
		{
			font = font ?? MonoSmall;
			var size = Measure(text, font);
			var bounds = new RectangleF(x, y, size.Width + Scale(8), size.Height + Scale(4));
			Smooth(g);
			FillRounded(g, fill, bounds, Scale(3));
			if (border != Color.Empty) StrokeRounded(g, border, bounds, Scale(3));
			DrawText(g, text, font, foreground, x + Scale(4), y + Scale(2));
			return (int)bounds.Width;
		}

		public static int ChipHeight(Font font = null) => Measure("00", font ?? MonoSmall).Height + Scale(4);

		/// <summary>Severity glyphs drawn as vector shapes, so no font has to contain them.</summary>
		public static void Glyph(Graphics g, Severity severity, Rectangle box)
		{
			Smooth(g);
			var color = Accent(severity);
			float s = box.Width, x = box.X, y = box.Y;
			using (var brush = new SolidBrush(Color.FromArgb(50, color))) g.FillEllipse(brush, x, y, s, s);
			using (var pen = new Pen(color, Math.Max(1.5f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
			{
				switch (severity)
				{
					case Severity.Success:
						g.DrawLines(pen, new[] { new PointF(x + s * .28f, y + s * .52f), new PointF(x + s * .44f, y + s * .68f), new PointF(x + s * .72f, y + s * .34f) });
						break;
					case Severity.Danger:
						g.DrawLine(pen, x + s * .33f, y + s * .33f, x + s * .67f, y + s * .67f);
						g.DrawLine(pen, x + s * .67f, y + s * .33f, x + s * .33f, y + s * .67f);
						break;
					case Severity.Attention:
						g.DrawLine(pen, x + s * .5f, y + s * .26f, x + s * .5f, y + s * .56f);
						g.DrawLine(pen, x + s * .5f, y + s * .72f, x + s * .5f, y + s * .73f);
						break;
					case Severity.Info:
						g.DrawLine(pen, x + s * .5f, y + s * .27f, x + s * .5f, y + s * .28f);
						g.DrawLine(pen, x + s * .5f, y + s * .44f, x + s * .5f, y + s * .73f);
						break;
					default:
						using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x + s * .38f, y + s * .38f, s * .24f, s * .24f);
						break;
				}
			}
		}

		/// <summary>Guesses how a status message should be coloured.</summary>
		public static Severity Classify(string message)
		{
			var text = (message ?? "").ToLowerInvariant();
			if (text.Length == 0) return Severity.Neutral;
			if (text.Contains("cannot") || text.Contains("failed") || text.Contains("error") || text.Contains("refused") || text.Contains("conflict") || text.Contains("invalid") || text.Contains("not saved") || text.Contains("stale") || text.Contains("unavailable") || text.Contains("must ") || text.Contains("requires") || text.Contains("required"))
				return Severity.Danger;
			if (text.Contains("verified") || text.StartsWith("confirmed") || text.StartsWith("saved") || text.Contains("captured.") || text.Contains("produced") || text.Contains("preview ready") || text.Contains("restored"))
				return Severity.Success;
			if (text.Contains("cancel") || text.Contains("changed") || text.Contains("waiting") || text.Contains("dropped") || text.Contains("reached") || text.Contains("holds"))
				return Severity.Attention;
			return Severity.Info;
		}

		/// <summary>Applies the dark palette to a tree of standard WinForms controls.</summary>
		public static void Style(Control root)
		{
			ApplyTo(root);
			foreach (Control child in root.Controls) Style(child);
		}

		private static void ApplyTo(Control control)
		{
			switch (control)
			{
				case DebuggerControl _:
				case ReClassNET.Controls.Debugger.DarkButton _:
				case ReClassNET.Controls.Debugger.ToolGroup _:
				case ReClassNET.Controls.Debugger.Card _:
				case ScrollableControl _ when control.GetType().Namespace == "ReClassNET.Controls.Debugger":
					return;
				case TextBox box:
					box.BackColor = box.ReadOnly ? Panel : Raised;
					box.ForeColor = Text;
					box.BorderStyle = BorderStyle.FixedSingle;
					break;
				case NumericUpDown number:
					number.BackColor = Raised;
					number.ForeColor = Text;
					number.BorderStyle = BorderStyle.FixedSingle;
					break;
				case ComboBox combo:
					combo.BackColor = Raised;
					combo.ForeColor = Text;
					combo.FlatStyle = FlatStyle.Flat;
					break;
				case CheckBox check:
					check.ForeColor = Text;
					check.FlatStyle = FlatStyle.Flat;
					check.FlatAppearance.BorderColor = Muted;
					check.FlatAppearance.CheckedBackColor = Raised;
					break;
				case Button button:
					button.FlatStyle = FlatStyle.Flat;
					button.BackColor = Raised;
					button.ForeColor = Text;
					button.FlatAppearance.BorderColor = Border;
					button.FlatAppearance.MouseOverBackColor = Hover;
					button.FlatAppearance.MouseDownBackColor = Border;
					break;
				case Label label:
					label.ForeColor = label.ForeColor == SystemColors.ControlText ? Text : label.ForeColor;
					label.BackColor = Color.Transparent;
					break;
				case SplitContainer split:
					split.BackColor = Border;
					split.Panel1.BackColor = Background;
					split.Panel2.BackColor = Background;
					break;
				case Form form:
					form.BackColor = Background;
					form.ForeColor = Text;
					break;
				case Panel _:
					if (control.BackColor == SystemColors.Control) control.BackColor = Color.Transparent;
					control.ForeColor = Text;
					break;
			}
		}

		/// <summary>Styles a small modal dialog built from standard controls.</summary>
		public static void StyleDialog(Form dialog)
		{
			dialog.BackColor = Background;
			dialog.ForeColor = Text;
			dialog.Font = UiFont;
			Style(dialog);
			UseDarkChrome(dialog);
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

		[DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

		/// <summary>
		/// On Windows 10/11, gives the window a dark title bar and dark scroll bars. A no-op elsewhere.
		/// </summary>
		public static void UseDarkChrome(Form form)
		{
			if (NativeMethods.IsUnix()) return;
			EventHandler apply = (s, e) =>
			{
				try
				{
					int on = 1;
					if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
				}
				catch (Exception) { }
				DarkScrollBars(form);
			};
			if (form.IsHandleCreated) apply(form, EventArgs.Empty); else form.HandleCreated += apply;
		}

		private static void DarkScrollBars(Control root)
		{
			foreach (Control control in root.Controls)
			{
				if (control is TextBoxBase || control is DataGridView || control is ScrollBar || control is ScrollableControl)
				{
					var target = control;
					EventHandler theme = (s, e) => { try { SetWindowTheme(target.Handle, "DarkMode_Explorer", null); } catch (Exception) { } };
					if (target.IsHandleCreated) theme(target, EventArgs.Empty); else target.HandleCreated += theme;
				}
				DarkScrollBars(control);
			}
		}
	}

	/// <summary>Base for the painted debugger controls: double buffered, dark, resize-redraw.</summary>
	public abstract class DebuggerControl : Control
	{
		protected DebuggerControl()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
			BackColor = DebuggerTheme.Background;
			ForeColor = DebuggerTheme.Text;
			Font = DebuggerTheme.UiFont;
		}

		protected static int S(int pixels) => DebuggerTheme.Scale(pixels);
	}
}
