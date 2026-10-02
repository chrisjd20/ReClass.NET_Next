using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ReClassNET.UI.Debugger
{
	public enum Severity { Neutral, Info, Success, Attention, Danger }

	/// <summary>
	/// The dark look shared by the debugger windows (Find writes/accesses and the instruction editor).
	/// Everything is painted with GDI+ so it looks the same on Windows and Mono.
	/// </summary>
	public static class DebuggerTheme
	{
		// The palette follows the application theme (UI/AppTheme.cs); painted controls read it at paint time.
		private static ThemePalette P => AppTheme.Current;
		public static Color Background => P.Background;
		public static Color Panel => P.Panel;
		public static Color Raised => P.Raised;
		public static Color Hover => P.Hover;
		public static Color Border => P.Border;
		public static Color Text => P.Text;
		public static Color Muted => P.Muted;
		public static Color Faint => P.Faint;

		public static Color Teal => P.Teal;
		public static Color Amber => P.Amber;
		public static Color Red => P.Red;
		public static Color Blue => P.Blue;
		public static Color Violet => P.Violet;

		// Assembly tokens.
		public static Color AsmMnemonic => P.AsmMnemonic;
		public static Color AsmRegister => P.AsmRegister;
		public static Color AsmNumber => P.AsmNumber;
		public static Color AsmKeyword => P.AsmKeyword;
		public static Color AsmPunctuation => P.AsmPunctuation;
		public static Color AsmText => P.Text;

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

		/// <summary>Styles the standard WinForms controls under <paramref name="root"/> for the current theme.</summary>
		public static void Style(Control root) => AppTheme.Style(root);

		/// <summary>Styles a small modal dialog built from standard controls.</summary>
		public static void StyleDialog(Form dialog)
		{
			dialog.Font = UiFont;
			AppTheme.Apply(dialog);
		}

		/// <summary>Title bar and scroll bars that match the theme on Windows 10/11. A no-op elsewhere.</summary>
		public static void UseDarkChrome(Form form) => AppTheme.SetChrome(form);
	}

	/// <summary>Base for the painted debugger controls: double buffered, dark, resize-redraw.</summary>
	public abstract class DebuggerControl : Control, IPaintsOwnTheme
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
