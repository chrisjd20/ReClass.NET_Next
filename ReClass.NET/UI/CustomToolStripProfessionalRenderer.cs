using System.Drawing;
using System.Windows.Forms;

namespace ReClassNET.UI
{
	internal class CustomToolStripProfessionalRenderer : ToolStripProfessionalRenderer
	{
		private readonly bool renderGrip;
		private readonly bool renderBorder;

		public CustomToolStripProfessionalRenderer(bool renderGrip, bool renderBorder)
			: base(new CustomProfessionalColorTable())
		{
			this.renderGrip = renderGrip;
			this.renderBorder = renderBorder;
			RoundedEdges = false;
		}

		protected override void OnRenderGrip(ToolStripGripRenderEventArgs e)
		{
			if (renderGrip)
			{
				base.OnRenderGrip(e);
			}
		}

		protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (p.IsDark)
			{
				// Base draws status bar and drop-down edges in light system colours.
				if (e.ToolStrip is StatusStrip) return;
				if (e.ToolStrip is ToolStripDropDown)
				{
					using (var pen = new Pen(p.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
					return;
				}
			}
			if (renderBorder || e.ToolStrip is ToolStripDropDown)
			{
				base.OnRenderToolStripBorder(e);
			}
		}

		protected override void OnRenderToolStripPanelBackground(ToolStripPanelRenderEventArgs e)
		{
			if (AppTheme.Current.IsDark)
			{
				using (var brush = new SolidBrush(AppTheme.Current.Background)) e.Graphics.FillRectangle(brush, e.ToolStripPanel.ClientRectangle);
				e.Handled = true;
			}
		}

		// Painted explicitly in Dark: Mono's renderer ignores the colour table for status bars.
		protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (!p.IsDark) { base.OnRenderToolStripBackground(e); return; }
			using (var brush = new SolidBrush(e.ToolStrip is ToolStripDropDown ? p.Raised : p.Panel)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
			if (e.ToolStrip is StatusStrip) using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
		}

		protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
		{
			if (!AppTheme.Current.IsDark) { base.OnRenderStatusStripSizingGrip(e); return; }
			var strip = e.ToolStrip as StatusStrip;
			if (strip == null) return;
			var grip = strip.SizeGripBounds;
			using (var brush = new SolidBrush(AppTheme.Current.Faint))
				for (int row = 0; row < 3; ++row)
					for (int column = row; column < 3; ++column)
						e.Graphics.FillRectangle(brush, grip.Right - 4 - (2 - column) * 4, grip.Bottom - 4 - (2 - row) * 4, 2, 2);
		}

		// Dark glyph icons (the node-type buttons, for example) vanish on dark backgrounds; Dark draws lightened copies,
		// and one readable style for disabled icons.
		protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
		{
			if (!AppTheme.Current.IsDark || e.Image == null || e.ImageRectangle.IsEmpty) { base.OnRenderItemImage(e); return; }
			e.Graphics.DrawImage(e.Item.Enabled ? IconContrast.ForDark(e.Image) : IconContrast.Disabled(e.Image), e.ImageRectangle);
		}

		// Base re-applies system text colours (MenuText for idle top-level menu items, GrayText for disabled items), so
		// Dark draws item text itself.
		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (!p.IsDark || string.IsNullOrEmpty(e.Text) || e.TextDirection != ToolStripTextDirection.Horizontal) { base.OnRenderItemText(e); return; }
			var own = e.Item.ForeColor;
			var color = !e.Item.Enabled ? p.Muted : Luminance(own) > 0.55 ? own : p.Text;
			TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
		}

		private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

		// The stock check mark is a black glyph.
		protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (!p.IsDark) { base.OnRenderItemCheck(e); return; }
			var box = Rectangle.Inflate(e.ImageRectangle, 2, 2);
			using (var back = new SolidBrush(AppTheme.Mix(p.Raised, p.Blue, .35f))) e.Graphics.FillRectangle(back, box);
			using (var border = new Pen(AppTheme.Mix(p.Raised, p.Blue, .7f))) e.Graphics.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);
			if (e.Image != null && !(e.Item is ToolStripMenuItem menu && menu.Image == null))
			{
				e.Graphics.DrawImage(IconContrast.ForDark(e.Image), e.ImageRectangle);
				return;
			}
			var r = e.ImageRectangle;
			var old = e.Graphics.SmoothingMode;
			e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			using (var pen = new Pen(p.Text, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
				e.Graphics.DrawLines(pen, new[] { new PointF(r.X + r.Width * .22f, r.Y + r.Height * .52f), new PointF(r.X + r.Width * .42f, r.Y + r.Height * .72f), new PointF(r.X + r.Width * .78f, r.Y + r.Height * .3f) });
			e.Graphics.SmoothingMode = old;
		}

		// Base draws the overflow chevron and its "customize" bar in light system colours.
		protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (!p.IsDark) { base.OnRenderOverflowButtonBackground(e); return; }
			var bounds = new Rectangle(Point.Empty, e.Item.Size);
			using (var back = new SolidBrush(e.Item.Pressed ? p.Border : e.Item.Selected ? p.Hover : p.Raised)) e.Graphics.FillRectangle(back, bounds);
			int cx = bounds.Width / 2, cy = bounds.Height - 8;
			using (var pen = new Pen(p.Muted, 1.5f))
			{
				e.Graphics.DrawLine(pen, cx - 3, cy - 6, cx + 3, cy - 6);
				e.Graphics.DrawLines(pen, new[] { new Point(cx - 3, cy - 2), new Point(cx, cy + 1), new Point(cx + 3, cy - 2) });
			}
		}

		protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
		{
			if (AppTheme.Current.IsDark) e.ArrowColor = e.Item == null || e.Item.Enabled ? AppTheme.Current.Muted : AppTheme.Current.Faint;
			base.OnRenderArrow(e);
		}
	}

	/// <summary>Contrast helpers for icons drawn on dark backgrounds. Results are cached per image.</summary>
	public static class IconContrast
	{
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image> lifted = new System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image>();
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image> disabled = new System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image>();

		/// <summary>The image with every too-dark pixel lifted (keeping its hue), or the image itself if nothing needed it.</summary>
		public static Image ForDark(Image image) => image == null ? null : lifted.GetValue(image, Lift);

		/// <summary>A disabled look that stays readable on dark: greyscale, lifted towards grey, half transparent.</summary>
		public static Image Disabled(Image image) => image == null ? null : disabled.GetValue(image, MakeDisabled);

		/// <summary>
		/// Lifts one pixel: anything darker than about 0.42 perceived brightness is mixed towards white so it reaches about
		/// 0.62, keeping hue and alpha. Brighter pixels are unchanged. Per pixel, so black outlines brighten too.
		/// </summary>
		public static Color LiftPixel(Color c)
		{
			double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
			if (c.A == 0 || luminance >= 0.42) return c;
			double amount = (0.62 - luminance) / (1 - luminance);
			return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));
		}

		private static Image Lift(Image image)
		{
			if (!(image is Bitmap bitmap) || bitmap.Width > 128 || bitmap.Height > 128) return image;
			bool changed = false;
			var result = new Bitmap(bitmap.Width, bitmap.Height);
			for (int y = 0; y < bitmap.Height; ++y)
				for (int x = 0; x < bitmap.Width; ++x)
				{
					var c = bitmap.GetPixel(x, y);
					var lift = LiftPixel(c);
					if (lift != c) changed = true;
					result.SetPixel(x, y, lift);
				}
			if (changed) return result;
			result.Dispose();
			return image;
		}

		private static Image MakeDisabled(Image image)
		{
			if (!(image is Bitmap bitmap) || bitmap.Width > 128 || bitmap.Height > 128) return image;
			var result = new Bitmap(bitmap.Width, bitmap.Height);
			for (int y = 0; y < bitmap.Height; ++y)
				for (int x = 0; x < bitmap.Width; ++x)
				{
					var c = bitmap.GetPixel(x, y);
					int grey = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
					// Clearly dimmer than an enabled icon, but still easy to read on a dark toolbar.
					grey = 150 + grey * 80 / 255;
					result.SetPixel(x, y, Color.FromArgb(c.A * 72 / 100, grey, grey, grey));
				}
			return result;
		}
	}

	/// <summary>Menu, toolbar and status bar colours for the current theme. Light keeps the classic system look.</summary>
	internal class CustomProfessionalColorTable : ProfessionalColorTable
	{
		private static ThemePalette P => AppTheme.Current;
		private static bool Dark => P.IsDark;

		public override Color MenuStripGradientBegin => Dark ? P.Panel : SystemColors.Control;
		public override Color MenuStripGradientEnd => Dark ? P.Panel : SystemColors.Control;
		public override Color ToolStripGradientBegin => Dark ? P.Panel : SystemColors.Control;
		public override Color ToolStripGradientMiddle => Dark ? P.Panel : SystemColors.Control;
		public override Color ToolStripGradientEnd => Dark ? P.Panel : SystemColors.Control;

		public override Color ToolStripBorder => Dark ? P.Border : base.ToolStripBorder;
		public override Color ToolStripDropDownBackground => Dark ? P.Raised : base.ToolStripDropDownBackground;
		public override Color ToolStripContentPanelGradientBegin => Dark ? P.Background : base.ToolStripContentPanelGradientBegin;
		public override Color ToolStripContentPanelGradientEnd => Dark ? P.Background : base.ToolStripContentPanelGradientEnd;
		public override Color ToolStripPanelGradientBegin => Dark ? P.Background : base.ToolStripPanelGradientBegin;
		public override Color ToolStripPanelGradientEnd => Dark ? P.Background : base.ToolStripPanelGradientEnd;
		public override Color StatusStripGradientBegin => Dark ? P.Panel : base.StatusStripGradientBegin;
		public override Color StatusStripGradientEnd => Dark ? P.Panel : base.StatusStripGradientEnd;

		public override Color MenuBorder => Dark ? P.Border : base.MenuBorder;
		public override Color MenuItemBorder => Dark ? AppTheme.Mix(P.Hover, P.Blue, .4f) : base.MenuItemBorder;
		public override Color MenuItemSelected => Dark ? P.Hover : base.MenuItemSelected;
		public override Color MenuItemSelectedGradientBegin => Dark ? P.Hover : base.MenuItemSelectedGradientBegin;
		public override Color MenuItemSelectedGradientEnd => Dark ? P.Hover : base.MenuItemSelectedGradientEnd;
		public override Color MenuItemPressedGradientBegin => Dark ? P.Raised : base.MenuItemPressedGradientBegin;
		public override Color MenuItemPressedGradientMiddle => Dark ? P.Raised : base.MenuItemPressedGradientMiddle;
		public override Color MenuItemPressedGradientEnd => Dark ? P.Raised : base.MenuItemPressedGradientEnd;

		public override Color ImageMarginGradientBegin => Dark ? P.Raised : base.ImageMarginGradientBegin;
		public override Color ImageMarginGradientMiddle => Dark ? P.Raised : base.ImageMarginGradientMiddle;
		public override Color ImageMarginGradientEnd => Dark ? P.Raised : base.ImageMarginGradientEnd;

		public override Color ButtonSelectedHighlight => Dark ? P.Hover : base.ButtonSelectedHighlight;
		public override Color ButtonSelectedHighlightBorder => Dark ? P.Border : base.ButtonSelectedHighlightBorder;
		public override Color ButtonSelectedBorder => Dark ? AppTheme.Mix(P.Hover, P.Blue, .4f) : base.ButtonSelectedBorder;
		public override Color ButtonSelectedGradientBegin => Dark ? P.Hover : base.ButtonSelectedGradientBegin;
		public override Color ButtonSelectedGradientMiddle => Dark ? P.Hover : base.ButtonSelectedGradientMiddle;
		public override Color ButtonSelectedGradientEnd => Dark ? P.Hover : base.ButtonSelectedGradientEnd;
		public override Color ButtonPressedHighlight => Dark ? P.Border : base.ButtonPressedHighlight;
		public override Color ButtonPressedBorder => Dark ? P.Border : base.ButtonPressedBorder;
		public override Color ButtonPressedGradientBegin => Dark ? P.Border : base.ButtonPressedGradientBegin;
		public override Color ButtonPressedGradientMiddle => Dark ? P.Border : base.ButtonPressedGradientMiddle;
		public override Color ButtonPressedGradientEnd => Dark ? P.Border : base.ButtonPressedGradientEnd;
		public override Color ButtonCheckedHighlight => Dark ? AppTheme.Mix(P.Raised, P.Blue, .3f) : base.ButtonCheckedHighlight;
		public override Color ButtonCheckedHighlightBorder => Dark ? P.Blue : base.ButtonCheckedHighlightBorder;
		public override Color ButtonCheckedGradientBegin => Dark ? AppTheme.Mix(P.Raised, P.Blue, .3f) : base.ButtonCheckedGradientBegin;
		public override Color ButtonCheckedGradientMiddle => Dark ? AppTheme.Mix(P.Raised, P.Blue, .3f) : base.ButtonCheckedGradientMiddle;
		public override Color ButtonCheckedGradientEnd => Dark ? AppTheme.Mix(P.Raised, P.Blue, .3f) : base.ButtonCheckedGradientEnd;
		public override Color CheckBackground => Dark ? AppTheme.Mix(P.Raised, P.Blue, .3f) : base.CheckBackground;
		public override Color CheckSelectedBackground => Dark ? AppTheme.Mix(P.Hover, P.Blue, .3f) : base.CheckSelectedBackground;
		public override Color CheckPressedBackground => Dark ? AppTheme.Mix(P.Hover, P.Blue, .45f) : base.CheckPressedBackground;

		public override Color SeparatorDark => Dark ? P.Border : base.SeparatorDark;
		public override Color SeparatorLight => Dark ? P.Panel : base.SeparatorLight;
		public override Color GripDark => Dark ? P.Border : base.GripDark;
		public override Color GripLight => Dark ? P.Hover : base.GripLight;
		public override Color OverflowButtonGradientBegin => Dark ? P.Raised : base.OverflowButtonGradientBegin;
		public override Color OverflowButtonGradientMiddle => Dark ? P.Raised : base.OverflowButtonGradientMiddle;
		public override Color OverflowButtonGradientEnd => Dark ? P.Raised : base.OverflowButtonGradientEnd;
		public override Color ImageMarginRevealedGradientBegin => Dark ? P.Raised : base.ImageMarginRevealedGradientBegin;
		public override Color ImageMarginRevealedGradientMiddle => Dark ? P.Raised : base.ImageMarginRevealedGradientMiddle;
		public override Color ImageMarginRevealedGradientEnd => Dark ? P.Raised : base.ImageMarginRevealedGradientEnd;
		public override Color RaftingContainerGradientBegin => Dark ? P.Background : base.RaftingContainerGradientBegin;
		public override Color RaftingContainerGradientEnd => Dark ? P.Background : base.RaftingContainerGradientEnd;
	}
}
