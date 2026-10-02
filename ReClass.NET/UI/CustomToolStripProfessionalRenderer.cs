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
						e.Graphics.FillRectangle(brush, grip.Right - 4 - (2 - column) * 4 + (2 - row) * 0, grip.Bottom - 4 - (2 - row) * 4, 2, 2);
		}

		// Grey glyph icons (the node-type buttons, for example) vanish on dark backgrounds; Dark draws a lightened copy.
		protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
		{
			if (!AppTheme.Current.IsDark || e.Image == null) { base.OnRenderItemImage(e); return; }
			var image = IconContrast.ForDark(e.Image);
			if (ReferenceEquals(image, e.Image)) { base.OnRenderItemImage(e); return; }
			if (e.Item.Enabled) e.Graphics.DrawImage(image, e.ImageRectangle);
			else
			{
				using (var attributes = new System.Drawing.Imaging.ImageAttributes())
				{
					attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
					e.Graphics.DrawImage(image, e.ImageRectangle, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
				}
			}
		}

		// Items keep their default ForeColor (black); in Dark the renderer supplies readable colours instead.
		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			var p = AppTheme.Current;
			if (p.IsDark && (e.Item.ForeColor == SystemColors.ControlText || e.Item.ForeColor == SystemColors.MenuText || e.Item.ForeColor == SystemColors.WindowText))
				e.TextColor = e.Item.Enabled ? p.Text : p.Faint;
			base.OnRenderItemText(e);
		}

		protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
		{
			if (AppTheme.Current.IsDark) e.ArrowColor = e.Item == null || e.Item.Enabled ? AppTheme.Current.Muted : AppTheme.Current.Faint;
			base.OnRenderArrow(e);
		}
	}

	/// <summary>Lightens icons that are too dark to read on a dark background. Bright icons are returned unchanged.</summary>
	internal static class IconContrast
	{
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image> cache = new System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image>();

		public static Image ForDark(Image image) => cache.GetValue(image, Convert);

		private static Image Convert(Image image)
		{
			if (!(image is Bitmap bitmap) || bitmap.Width > 64 || bitmap.Height > 64) return image;
			// Perceived brightness: navy, maroon and pure blue glyphs count as dark even though they are saturated.
			double luminance = 0; int opaque = 0;
			for (int y = 0; y < bitmap.Height; ++y)
				for (int x = 0; x < bitmap.Width; ++x)
				{
					var c = bitmap.GetPixel(x, y);
					if (c.A < 128) continue;
					luminance += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; ++opaque;
				}
			if (opaque == 0 || luminance / opaque > 0.45) return image;
			var light = new Bitmap(bitmap.Width, bitmap.Height);
			for (int y = 0; y < bitmap.Height; ++y)
				for (int x = 0; x < bitmap.Width; ++x)
				{
					var c = bitmap.GetPixel(x, y);
					// Mix towards white: the glyph keeps its hue (blue stays blue) but becomes readable on dark.
					light.SetPixel(x, y, Color.FromArgb(c.A, c.R + (255 - c.R) * 55 / 100, c.G + (255 - c.G) * 55 / 100, c.B + (255 - c.B) * 55 / 100));
				}
			return light;
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
	}
}
