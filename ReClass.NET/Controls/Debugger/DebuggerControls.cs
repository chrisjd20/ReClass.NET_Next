using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using ReClassNET.UI;
using ReClassNET.UI.Debugger;

namespace ReClassNET.Controls.Debugger
{
	/// <summary>One shared timer that repaints animated controls (pulsing pills, glowing buttons).</summary>
	internal static class Pulse
	{
		private static readonly HashSet<Control> controls = new HashSet<Control>();
		private static Timer timer;

		/// <summary>0..1, a smooth breathing value.</summary>
		public static float Phase => (float)(0.5 + 0.5 * Math.Sin(Environment.TickCount / 260.0));

		public static void Set(Control control, bool animate)
		{
			if (animate)
			{
				if (!controls.Add(control)) return;
				control.Disposed += (s, e) => Set((Control)s, false);
				if (timer == null)
				{
					timer = new Timer { Interval = 60 };
					timer.Tick += (s, e) => { foreach (var c in controls.ToArray()) if (!c.IsDisposed && c.Visible) c.Invalidate(); };
				}
				timer.Start();
			}
			else if (controls.Remove(control) && controls.Count == 0) timer?.Stop();
		}
	}

	public enum DarkButtonStyle { Secondary, Primary, Danger, Ghost }

	/// <summary>A flat dark button with an optional icon. Text is the visible label, unchanged from the classic UI.</summary>
	public class DarkButton : Button, IPaintsOwnTheme
	{
		private bool hover, pressed, glow;
		private DarkButtonStyle variant;

		[DefaultValue(DarkButtonStyle.Secondary)]
		public DarkButtonStyle Variant { get => variant; set { variant = value; Invalidate(); } }

		public Image Icon { get; set; }

		/// <summary>Draws a breathing outline to show "press this next".</summary>
		public bool Glow
		{
			get => glow;
			set { if (glow == value) return; glow = value; Pulse.Set(this, value); Invalidate(); }
		}

		public DarkButton(string text, Image icon = null, DarkButtonStyle variant = DarkButtonStyle.Secondary)
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
			Text = text; Icon = icon; this.variant = variant;
			FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
			BackColor = Color.Transparent; ForeColor = DebuggerTheme.Text; Font = DebuggerTheme.UiFont;
			Cursor = Cursors.Hand; AutoSize = true; Margin = new Padding(DebuggerTheme.Scale(3));
			UseVisualStyleBackColor = false;
		}

		public override Size GetPreferredSize(Size proposedSize)
		{
			var text = DebuggerTheme.Measure(Text, Font);
			int icon = Icon == null ? 0 : DebuggerTheme.Scale(16) + DebuggerTheme.Scale(6);
			return new Size(text.Width + icon + DebuggerTheme.Scale(22), DebuggerTheme.Scale(28));
		}

		protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
		protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
		protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
		protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
		protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			var parentColor = Parent?.BackColor ?? DebuggerTheme.Background;
			if (parentColor == Color.Transparent || parentColor.A == 0) parentColor = DebuggerTheme.Background;
			g.Clear(parentColor);
			DebuggerTheme.Smooth(g);

			Color accent;
			switch (variant)
			{
				case DarkButtonStyle.Primary: accent = DebuggerTheme.Teal; break;
				case DarkButtonStyle.Danger: accent = DebuggerTheme.Red; break;
				default: accent = DebuggerTheme.Blue; break;
			}
			Color fill, border, text;
			if (!Enabled)
			{
				fill = variant == DarkButtonStyle.Ghost ? parentColor : DebuggerTheme.Mix(DebuggerTheme.Panel, parentColor, .4f);
				border = DebuggerTheme.Mix(DebuggerTheme.Border, parentColor, .4f);
				text = DebuggerTheme.Faint;
			}
			else if (variant == DarkButtonStyle.Primary)
			{
				fill = pressed ? DebuggerTheme.Mix(accent, DebuggerTheme.Background, .35f) : hover ? DebuggerTheme.Mix(accent, Color.White, .12f) : accent;
				border = fill; text = DebuggerTheme.Background;
			}
			else if (variant == DarkButtonStyle.Danger)
			{
				fill = pressed ? DebuggerTheme.Mix(accent, DebuggerTheme.Background, .7f) : hover ? DebuggerTheme.Mix(accent, DebuggerTheme.Background, .78f) : DebuggerTheme.Mix(accent, DebuggerTheme.Background, .88f);
				border = DebuggerTheme.Mix(accent, DebuggerTheme.Background, .35f); text = DebuggerTheme.Mix(accent, Color.White, .25f);
			}
			else if (variant == DarkButtonStyle.Ghost)
			{
				fill = pressed ? DebuggerTheme.Border : hover ? DebuggerTheme.Raised : parentColor;
				border = hover ? DebuggerTheme.Border : parentColor; text = hover ? DebuggerTheme.Text : DebuggerTheme.Muted;
			}
			else
			{
				fill = pressed ? DebuggerTheme.Border : hover ? DebuggerTheme.Hover : DebuggerTheme.Raised;
				border = hover ? DebuggerTheme.Mix(DebuggerTheme.Border, accent, .35f) : DebuggerTheme.Border; text = DebuggerTheme.Text;
			}

			var bounds = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
			float radius = DebuggerTheme.Scale(5);
			if (glow && Enabled)
			{
				var glowColor = variant == DarkButtonStyle.Danger ? DebuggerTheme.Red : variant == DarkButtonStyle.Primary ? DebuggerTheme.Teal : DebuggerTheme.Amber;
				int alpha = (int)(70 + 150 * Pulse.Phase);
				DebuggerTheme.StrokeRounded(g, Color.FromArgb(alpha, glowColor), new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius + 1, 2f);
				if (variant != DarkButtonStyle.Primary) border = Color.FromArgb(255, DebuggerTheme.Mix(border, glowColor, .6f));
			}
			DebuggerTheme.FillRounded(g, fill, bounds, radius);
			DebuggerTheme.StrokeRounded(g, border, bounds, radius);

			int x = DebuggerTheme.Scale(11);
			if (Icon != null)
			{
				int size = DebuggerTheme.Scale(16);
				var iconBounds = new Rectangle(x, (Height - size) / 2, size, size);
				if (Enabled) g.DrawImage(Icon, iconBounds);
				else ControlPaint.DrawImageDisabled(g, Icon, iconBounds.X, iconBounds.Y, fill);
				x += size + DebuggerTheme.Scale(6);
			}
			var textSize = DebuggerTheme.Measure(Text, Font);
			DebuggerTheme.DrawText(g, Text, Font, text, x, (Height - textSize.Height) / 2);
			if (Focused && ShowFocusCues) DebuggerTheme.StrokeRounded(g, Color.FromArgb(120, DebuggerTheme.Blue), new RectangleF(3.5f, 3.5f, Width - 7f, Height - 7f), radius - 1);
		}
	}

	/// <summary>The window header: icon, title, subtitle and status pills.</summary>
	public class DebuggerHeader : DebuggerControl
	{
		public sealed class HeaderPill
		{
			public string Text;
			public Severity Severity;
			public bool Pulse, Solid;
		}

		private string title = "", subtitle = "";
		private List<HeaderPill> pills = new List<HeaderPill>();

		public Image Icon { get; set; }
		public string Title { get => title; set { if (title == value) return; title = value ?? ""; Invalidate(); } }
		public string Subtitle { get => subtitle; set { if (subtitle == value) return; subtitle = value ?? ""; Invalidate(); } }

		public DebuggerHeader()
		{
			Dock = DockStyle.Top;
			Height = S(62);
		}

		public void SetPills(params HeaderPill[] values)
		{
			var next = values.Where(p => p != null && !string.IsNullOrEmpty(p.Text)).ToList();
			if (next.Count == pills.Count && next.Zip(pills, (a, b) => a.Text == b.Text && a.Severity == b.Severity && a.Pulse == b.Pulse && a.Solid == b.Solid).All(x => x)) return;
			pills = next;
			Pulse.Set(this, pills.Any(p => p.Pulse));
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			using (var brush = new LinearGradientBrush(ClientRectangle, DebuggerTheme.Mix(DebuggerTheme.Raised, DebuggerTheme.Blue, .08f), DebuggerTheme.Background, LinearGradientMode.Horizontal))
				g.FillRectangle(brush, ClientRectangle);
			using (var line = new LinearGradientBrush(new Rectangle(0, Height - 2, Math.Max(1, Width), 2), DebuggerTheme.Teal, Color.FromArgb(0, DebuggerTheme.Teal), LinearGradientMode.Horizontal))
				g.FillRectangle(line, 0, Height - 2, Width, 2);

			int x = S(16);
			if (Icon != null)
			{
				int size = S(32);
				var box = new Rectangle(x, (Height - size) / 2, size, size);
				DebuggerTheme.Smooth(g);
				DebuggerTheme.FillRounded(g, Color.FromArgb(40, DebuggerTheme.Teal), new RectangleF(box.X - S(5), box.Y - S(5), size + S(10), size + S(10)), S(8));
				g.InterpolationMode = Icon.Width < size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.Half;
				g.DrawImage(Icon, box);
				x += size + S(18);
			}

			int right = Width - S(16);
			for (int i = pills.Count - 1; i >= 0; --i)
			{
				var pill = pills[i];
				int width = DebuggerTheme.PillWidth(pill.Text) + (pill.Pulse ? S(12) : 0);
				right -= width;
				int y = (Height - DebuggerTheme.ChipHeight(DebuggerTheme.UiSmallBold)) / 2;
				var accent = DebuggerTheme.Accent(pill.Severity);
				if (pill.Pulse)
				{
					float phase = Pulse.Phase;
					int dot = S(7);
					using (var halo = new SolidBrush(Color.FromArgb((int)(60 * phase), accent))) g.FillEllipse(halo, right - S(2), y + S(2), dot + S(6), dot + S(6));
					using (var brush = new SolidBrush(Color.FromArgb((int)(150 + 105 * phase), accent))) g.FillEllipse(brush, right + S(1), y + S(5), dot, dot);
					DebuggerTheme.Pill(g, pill.Text, accent, right + S(12), y, pill.Solid);
				}
				else DebuggerTheme.Pill(g, pill.Text, accent, right, y, pill.Solid);
				right -= S(8);
			}

			var titleSize = DebuggerTheme.Measure(title, DebuggerTheme.UiTitle);
			int titleY = string.IsNullOrEmpty(subtitle) ? (Height - titleSize.Height) / 2 : S(10);
			DebuggerTheme.DrawText(g, title, DebuggerTheme.UiTitle, DebuggerTheme.Text, new Rectangle(x, titleY, Math.Max(10, right - x - S(8)), titleSize.Height + S(2)), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
			if (!string.IsNullOrEmpty(subtitle))
				DebuggerTheme.DrawText(g, subtitle, DebuggerTheme.UiFont, DebuggerTheme.Muted, new Rectangle(x, titleY + titleSize.Height + S(3), Math.Max(10, right - x - S(8)), S(18)), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
		}
	}

	/// <summary>A one-line "what to do next" strip.</summary>
	public class CoachBar : DebuggerControl
	{
		private readonly ToolTip tip = new ToolTip();
		private Severity severity = Severity.Info;
		private string message = "";

		public CoachBar()
		{
			Dock = DockStyle.Top;
			Height = S(36);
		}

		public void Show(CoachAdvice advice)
		{
			if (advice == null || (advice.Message == message && advice.Severity == severity)) return;
			message = advice.Message ?? ""; severity = advice.Severity;
			tip.SetToolTip(this, message);
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			var accent = DebuggerTheme.Accent(severity);
			g.Clear(DebuggerTheme.Mix(DebuggerTheme.Background, accent, .07f));
			using (var brush = new SolidBrush(accent)) g.FillRectangle(brush, 0, 0, S(3), Height);
			using (var pen = new Pen(DebuggerTheme.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
			int glyph = S(18);
			DebuggerTheme.Glyph(g, severity, new Rectangle(S(14), (Height - glyph) / 2, glyph, glyph));
			DebuggerTheme.DrawText(g, "NEXT", DebuggerTheme.UiSmallBold, accent, S(40), (Height - DebuggerTheme.Measure("NEXT", DebuggerTheme.UiSmallBold).Height) / 2);
			int x = S(40) + DebuggerTheme.Measure("NEXT", DebuggerTheme.UiSmallBold).Width + S(10);
			DebuggerTheme.DrawText(g, message, DebuggerTheme.UiFont, DebuggerTheme.Text, new Rectangle(x, 0, Width - x - S(12), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
		}

		protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
	}

	/// <summary>Inspect → Edit → Preview → Apply → Restore, with the current step highlighted.</summary>
	public class StepRail : DebuggerControl
	{
		private PatchStep current = PatchStep.Inspect;
		private string hint = "";

		public StepRail()
		{
			Dock = DockStyle.Top;
			Height = S(58);
		}

		public void Show(PatchStep step, string text)
		{
			if (step == current && text == hint) return;
			current = step; hint = text ?? "";
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(DebuggerTheme.Panel);
			using (var pen = new Pen(DebuggerTheme.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
			DebuggerTheme.Smooth(g);
			var names = PatchSteps.Names;
			int slot = S(108), radius = S(11), y = S(14), x0 = S(22);
			for (int i = 0; i < names.Length; ++i)
			{
				int cx = x0 + i * slot + radius;
				bool done = (int)current > i, active = (int)current == i;
				var accent = active ? (current == PatchStep.Restore ? DebuggerTheme.Red : DebuggerTheme.Teal) : done ? DebuggerTheme.Teal : DebuggerTheme.Faint;
				if (i < names.Length - 1)
				{
					using (var pen = new Pen(done ? Color.FromArgb(160, DebuggerTheme.Teal) : DebuggerTheme.Border, S(2)))
						g.DrawLine(pen, cx + radius + S(4), y + radius, cx + slot - radius - S(4), y + radius);
				}
				var circle = new RectangleF(cx - radius, y, radius * 2, radius * 2);
				if (active)
				{
					using (var halo = new SolidBrush(Color.FromArgb(50, accent))) g.FillEllipse(halo, RectangleF.Inflate(circle, S(4), S(4)));
					using (var brush = new SolidBrush(accent)) g.FillEllipse(brush, circle);
				}
				else if (done)
				{
					using (var brush = new SolidBrush(Color.FromArgb(60, accent))) g.FillEllipse(brush, circle);
					using (var pen = new Pen(accent, 1.5f)) g.DrawEllipse(pen, circle);
				}
				else using (var pen = new Pen(accent, 1.5f)) g.DrawEllipse(pen, circle);

				if (done)
				{
					using (var pen = new Pen(DebuggerTheme.Teal, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
						g.DrawLines(pen, new[] { new PointF(cx - radius * .4f, y + radius * 1.05f), new PointF(cx - radius * .08f, y + radius * 1.38f), new PointF(cx + radius * .45f, y + radius * .65f) });
				}
				else
				{
					var number = (i + 1).ToString();
					var size = DebuggerTheme.Measure(number, DebuggerTheme.UiSmallBold);
					DebuggerTheme.DrawText(g, number, DebuggerTheme.UiSmallBold, active ? DebuggerTheme.Background : DebuggerTheme.Muted, cx - size.Width / 2, y + radius - size.Height / 2);
				}
				var label = names[i];
				var font = active ? DebuggerTheme.UiBold : DebuggerTheme.UiFont;
				var labelSize = DebuggerTheme.Measure(label, font);
				DebuggerTheme.DrawText(g, label, font, active ? DebuggerTheme.Text : done ? DebuggerTheme.Muted : DebuggerTheme.Faint, cx - labelSize.Width / 2, y + radius * 2 + S(4));
			}
			int hintX = x0 + names.Length * slot + S(4);
			if (hintX < Width - S(120))
			{
				var accent = current == PatchStep.Restore ? DebuggerTheme.Red : current == PatchStep.Done ? DebuggerTheme.Teal : DebuggerTheme.Blue;
				using (var brush = new SolidBrush(accent)) g.FillRectangle(brush, hintX, S(14), S(2), Height - S(28));
				DebuggerTheme.DrawText(g, hint, DebuggerTheme.UiFont, DebuggerTheme.Text, new Rectangle(hintX + S(12), S(6), Width - hintX - S(24), Height - S(12)), TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
			}
		}
	}

	/// <summary>A row of mutually exclusive options; replaces a drop-down so every choice stays visible.</summary>
	public class SegmentedSelector<T> : DebuggerControl
	{
		public sealed class Option
		{
			public T Value;
			public string Label, Description;
		}

		private readonly List<Option> options = new List<Option>();
		private readonly List<Rectangle> segments = new List<Rectangle>();
		private int selected = -1, hover = -1;
		private readonly ToolTip tip = new ToolTip();

		public event EventHandler SelectedIndexChanged;

		public SegmentedSelector()
		{
			SetStyle(ControlStyles.Selectable, true);
			TabStop = true;
			Height = S(30);
			Margin = new Padding(S(3), S(2), S(3), S(2));
		}

		public SegmentedSelector<T> Add(T value, string label, string description)
		{
			options.Add(new Option { Value = value, Label = label, Description = description });
			if (selected < 0) selected = 0;
			Width = PreferredWidth();
			return this;
		}

		private int PreferredWidth() => options.Sum(o => DebuggerTheme.Measure(o.Label, DebuggerTheme.UiFont).Width + S(26)) + S(4);

		public override Size GetPreferredSize(Size proposedSize) => new Size(PreferredWidth(), S(30));

		public T SelectedValue
		{
			get => selected >= 0 && selected < options.Count ? options[selected].Value : default(T);
			set
			{
				int index = options.FindIndex(o => Equals(o.Value, value));
				if (index < 0 || index == selected) return;
				selected = index; Invalidate();
				SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		public IReadOnlyList<Option> Options() => options;

		public string SelectedDescription => selected >= 0 && selected < options.Count ? options[selected].Description : "";

		private void LayoutSegments()
		{
			segments.Clear();
			int x = S(2);
			foreach (var option in options)
			{
				int width = DebuggerTheme.Measure(option.Label, DebuggerTheme.UiFont).Width + S(26);
				segments.Add(new Rectangle(x, S(2), width, Height - S(4)));
				x += width;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			LayoutSegments();
			int index = segments.FindIndex(r => r.Contains(e.Location));
			if (index != hover) { hover = index; tip.SetToolTip(this, index >= 0 ? options[index].Description : ""); Invalidate(); }
			base.OnMouseMove(e);
		}

		protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (!Enabled) return;
			Focus();
			LayoutSegments();
			int index = segments.FindIndex(r => r.Contains(e.Location));
			if (index >= 0) SelectedValue = options[index].Value;
			base.OnMouseDown(e);
		}

		protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (Enabled && options.Count > 0 && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
			{
				int next = Math.Max(0, Math.Min(options.Count - 1, selected + (e.KeyCode == Keys.Left ? -1 : 1)));
				SelectedValue = options[next].Value;
				e.Handled = true;
			}
			base.OnKeyDown(e);
		}

		protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
		protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
		protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(Parent?.BackColor.A > 0 ? Parent.BackColor : DebuggerTheme.Background);
			LayoutSegments();
			DebuggerTheme.Smooth(g);
			var outer = new RectangleF(0.5f, 0.5f, (segments.Count > 0 ? segments.Last().Right + S(2) : Width) - 1f, Height - 1f);
			DebuggerTheme.FillRounded(g, DebuggerTheme.Raised, outer, S(6));
			DebuggerTheme.StrokeRounded(g, Focused ? Color.FromArgb(150, DebuggerTheme.Blue) : DebuggerTheme.Border, outer, S(6));
			for (int i = 0; i < segments.Count; ++i)
			{
				var r = segments[i];
				var option = options[i];
				bool on = i == selected;
				if (on) DebuggerTheme.FillRounded(g, Enabled ? DebuggerTheme.Blue : DebuggerTheme.Faint, new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), S(5));
				else if (i == hover && Enabled) DebuggerTheme.FillRounded(g, DebuggerTheme.Hover, new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), S(5));
				var color = !Enabled ? DebuggerTheme.Faint : on ? DebuggerTheme.Background : DebuggerTheme.Text;
				DebuggerTheme.DrawText(g, option.Label, on ? DebuggerTheme.UiBold : DebuggerTheme.UiFont, color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
			}
		}

		protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
	}

	/// <summary>A painted tab strip with pages, so the tabs stay dark on every platform.</summary>
	public class InspectorTabs : Panel, IPaintsOwnTheme
	{
		private sealed class Strip : DebuggerControl
		{
			public InspectorTabs Owner;
			public readonly List<Rectangle> TabBounds = new List<Rectangle>();
			private int hover = -1;

			public Strip() { Dock = DockStyle.Top; Height = S(34); BackColor = DebuggerTheme.Panel; Cursor = Cursors.Hand; }

			private void Measure()
			{
				TabBounds.Clear();
				int x = S(10);
				foreach (var page in Owner.pages)
				{
					int width = DebuggerTheme.Measure(page.Title, DebuggerTheme.UiBold).Width + S(28) + (page.Badge != null ? DebuggerTheme.PillWidth(page.Badge) + S(6) : 0);
					TabBounds.Add(new Rectangle(x, 0, width, Height));
					x += width + S(2);
				}
			}

			protected override void OnMouseMove(MouseEventArgs e) { Measure(); int index = TabBounds.FindIndex(b => b.Contains(e.Location)); if (index != hover) { hover = index; Invalidate(); } base.OnMouseMove(e); }
			protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
			protected override void OnMouseDown(MouseEventArgs e) { Measure(); int index = TabBounds.FindIndex(b => b.Contains(e.Location)); if (index >= 0) Owner.SelectedIndex = index; base.OnMouseDown(e); }

			protected override void OnPaint(PaintEventArgs e)
			{
				var g = e.Graphics;
				g.Clear(DebuggerTheme.Panel);
				using (var pen = new Pen(DebuggerTheme.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
				Measure();
				for (int i = 0; i < TabBounds.Count; ++i)
				{
					var page = Owner.pages[i];
					var r = TabBounds[i];
					bool on = i == Owner.selected;
					if (i == hover && !on) using (var brush = new SolidBrush(DebuggerTheme.Raised)) g.FillRectangle(brush, r);
					var textSize = DebuggerTheme.Measure(page.Title, DebuggerTheme.UiBold);
					int x = r.X + S(14);
					DebuggerTheme.DrawText(g, page.Title, DebuggerTheme.UiBold, on ? DebuggerTheme.Text : DebuggerTheme.Muted, x, (Height - textSize.Height) / 2);
					if (page.Badge != null) DebuggerTheme.Pill(g, page.Badge, page.BadgeTone == Severity.Neutral ? DebuggerTheme.Muted : DebuggerTheme.Accent(page.BadgeTone), x + textSize.Width + S(6), (Height - DebuggerTheme.ChipHeight(DebuggerTheme.UiSmallBold)) / 2);
					if (on) using (var brush = new SolidBrush(DebuggerTheme.Teal)) g.FillRectangle(brush, r.X + S(8), Height - S(3), r.Width - S(16), S(3));
				}
			}
		}

		private sealed class Page
		{
			public string Title, Badge;
			public Severity BadgeTone;
			public Control Content;
		}

		private readonly List<Page> pages = new List<Page>();
		private readonly Strip strip;
		private readonly Panel host = new Panel { Dock = DockStyle.Fill, BackColor = DebuggerTheme.Background };
		private int selected = -1;

		public event EventHandler SelectedIndexChanged;

		public InspectorTabs()
		{
			DoubleBuffered = true;
			BackColor = DebuggerTheme.Background;
			strip = new Strip { Owner = this };
			Controls.Add(host);
			Controls.Add(strip);
		}

		public void AddPage(string title, Control content)
		{
			content.Dock = DockStyle.Fill;
			content.Visible = false;
			host.Controls.Add(content);
			pages.Add(new Page { Title = title, Content = content });
			if (selected < 0) SelectedIndex = 0;
			strip.Invalidate();
		}

		public void SetBadge(int index, string badge, Severity tone)
		{
			if (index < 0 || index >= pages.Count) return;
			if (pages[index].Badge == badge && pages[index].BadgeTone == tone) return;
			pages[index].Badge = badge; pages[index].BadgeTone = tone;
			strip.Invalidate();
		}

		public int SelectedIndex
		{
			get => selected;
			set
			{
				if (value < 0 || value >= pages.Count || value == selected) return;
				selected = value;
				for (int i = 0; i < pages.Count; ++i) pages[i].Content.Visible = i == selected;
				pages[selected].Content.BringToFront();
				strip.Invalidate();
				SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>A rounded panel with a caption; content goes in <see cref="Body"/>, small toggles in the header.</summary>
	public class Card : Panel, IPaintsOwnTheme
	{
		private string title;

		public Panel Body { get; } = new Panel { Dock = DockStyle.Fill, BackColor = DebuggerTheme.Panel };
		public FlowLayoutPanel HeaderRight { get; } = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = DebuggerTheme.Panel, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = Padding.Empty };

		public string Title { get => title; set { title = value; Invalidate(); } }

		public Card(string title)
		{
			this.title = title;
			DoubleBuffered = true;
			ResizeRedraw = true;
			BackColor = DebuggerTheme.Background;
			Margin = new Padding(DebuggerTheme.Scale(4));
			Padding = new Padding(DebuggerTheme.Scale(9), DebuggerTheme.Scale(32), DebuggerTheme.Scale(9), DebuggerTheme.Scale(9));
			Dock = DockStyle.Fill;
			Controls.Add(Body);
			Controls.Add(HeaderRight);
			Resize += (s, e) => PlaceHeader();
			HeaderRight.Resize += (s, e) => PlaceHeader();
		}

		private void PlaceHeader() => HeaderRight.Location = new Point(Width - HeaderRight.Width - DebuggerTheme.Scale(10), DebuggerTheme.Scale(5));

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(DebuggerTheme.Background);
			DebuggerTheme.Smooth(g);
			var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
			DebuggerTheme.FillRounded(g, DebuggerTheme.Panel, bounds, DebuggerTheme.Scale(8));
			DebuggerTheme.StrokeRounded(g, DebuggerTheme.Border, bounds, DebuggerTheme.Scale(8));
			DebuggerTheme.DrawText(g, (title ?? "").ToUpperInvariant(), DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, DebuggerTheme.Scale(12), DebuggerTheme.Scale(11));
		}
	}

	/// <summary>A small on/off pill used for "Raw", "Details" and similar view toggles.</summary>
	public class ToggleChip : DebuggerControl
	{
		private bool on, hover;

		public event EventHandler CheckedChanged;

		public bool Checked
		{
			get => on;
			set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
		}

		public ToggleChip(string text, bool initial = false)
		{
			Text = text; on = initial;
			Cursor = Cursors.Hand;
			Size = new Size(DebuggerTheme.PillWidth(text) + S(14), S(20));
			Margin = new Padding(S(4), 0, 0, 0);
			BackColor = DebuggerTheme.Panel;
		}

		protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
		protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
		protected override void OnMouseDown(MouseEventArgs e) { Checked = !Checked; base.OnMouseDown(e); }

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			DebuggerTheme.Smooth(g);
			var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
			var accent = on ? DebuggerTheme.Blue : hover ? DebuggerTheme.Muted : DebuggerTheme.Faint;
			DebuggerTheme.FillRounded(g, on ? Color.FromArgb(60, DebuggerTheme.Blue) : DebuggerTheme.Raised, bounds, Height / 2f);
			DebuggerTheme.StrokeRounded(g, accent, bounds, Height / 2f);
			int dot = S(6);
			using (var brush = new SolidBrush(on ? DebuggerTheme.Blue : DebuggerTheme.Faint)) g.FillEllipse(brush, S(7), (Height - dot) / 2f, dot, dot);
			DebuggerTheme.DrawText(g, Text, DebuggerTheme.UiSmallBold, on ? DebuggerTheme.Text : DebuggerTheme.Muted, new Rectangle(S(16), 0, Width - S(18), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
		}
	}

	/// <summary>A wrapping group of toolbar buttons with a small caption above them.</summary>
	public class ToolGroup : FlowLayoutPanel, IPaintsOwnTheme
	{
		public string Caption { get; }

		public ToolGroup(string caption)
		{
			Caption = caption;
			DoubleBuffered = true;
			AutoSize = true;
			AutoSizeMode = AutoSizeMode.GrowAndShrink;
			WrapContents = false;
			BackColor = DebuggerTheme.Panel;
			Padding = new Padding(DebuggerTheme.Scale(5), DebuggerTheme.Scale(19), DebuggerTheme.Scale(5), DebuggerTheme.Scale(4));
			Margin = new Padding(DebuggerTheme.Scale(4), DebuggerTheme.Scale(4), DebuggerTheme.Scale(2), DebuggerTheme.Scale(4));
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(Parent?.BackColor ?? DebuggerTheme.Background);
			DebuggerTheme.Smooth(g);
			var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
			DebuggerTheme.FillRounded(g, DebuggerTheme.Panel, bounds, DebuggerTheme.Scale(7));
			DebuggerTheme.StrokeRounded(g, DebuggerTheme.Border, bounds, DebuggerTheme.Scale(7));
			DebuggerTheme.DrawText(g, Caption.ToUpperInvariant(), DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, DebuggerTheme.Scale(9), DebuggerTheme.Scale(5));
		}
	}

	/// <summary>The bottom status line. Colour and glyph follow the message.</summary>
	/// <summary>The window's status bar. Long guidance wraps onto up to three lines instead of being cut short.</summary>
	public class StatusLine : DebuggerControl
	{
		private readonly ToolTip tip = new ToolTip();

		public StatusLine()
		{
			Dock = DockStyle.Bottom;
			Height = S(30);
			BackColor = DebuggerTheme.Panel;
		}

		private Rectangle TextBounds => new Rectangle(S(34), S(6), Math.Max(S(40), Width - S(44)), Math.Max(S(10), Height - S(12)));

		private void Fit()
		{
			int lineHeight = DebuggerTheme.UiFont.Height;
			int measured = TextRenderer.MeasureText(Text ?? "", DebuggerTheme.UiFont, new Size(TextBounds.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
			int height = StatusLayout.HeightFor(measured, lineHeight, S(30), S(12));
			if (height != Height) Height = height;
		}

		protected override void OnTextChanged(EventArgs e)
		{
			tip.SetToolTip(this, Text);
			Fit();
			Invalidate();
			base.OnTextChanged(e);
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			if (Width > 0) Fit();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			using (var pen = new Pen(DebuggerTheme.Border)) g.DrawLine(pen, 0, 0, Width, 0);
			var severity = DebuggerTheme.Classify(Text);
			int glyph = S(16), lineHeight = DebuggerTheme.UiFont.Height;
			// The glyph sits beside the first line.
			int glyphTop = Height <= S(30) ? (Height - glyph) / 2 : S(6) + (lineHeight - glyph) / 2;
			DebuggerTheme.Glyph(g, severity, new Rectangle(S(10), glyphTop, glyph, glyph));
			var flags = Height <= S(30) ? TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine : TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
			var bounds = Height <= S(30) ? new Rectangle(S(34), 0, Width - S(44), Height) : TextBounds;
			DebuggerTheme.DrawText(g, Text, DebuggerTheme.UiFont, severity == Severity.Danger ? DebuggerTheme.Mix(DebuggerTheme.Red, Color.White, .2f) : DebuggerTheme.Text, bounds, flags);
		}

		protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
	}

	/// <summary>Shows how the replacement compares with the original selection's size.</summary>
	public class SizeMeter : DebuggerControl
	{
		private int original, replacement = -1;
		private bool hook;

		public event EventHandler SwitchToHookRequested;

		public SizeMeter()
		{
			Dock = DockStyle.Bottom;
			Height = S(34);
			BackColor = DebuggerTheme.Background;
		}

		private SizeFit Fit => hook ? SizeFit.Unknown : PatchSize.Classify(original, replacement);

		public void Show(int originalLength, int replacementLength, bool hookMode)
		{
			if (originalLength == original && replacementLength == replacement && hookMode == hook) return;
			original = originalLength; replacement = replacementLength; hook = hookMode;
			Cursor = Fit == SizeFit.TooLong ? Cursors.Hand : Cursors.Default;
			Invalidate();
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (Fit == SizeFit.TooLong) SwitchToHookRequested?.Invoke(this, EventArgs.Empty);
			base.OnMouseDown(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			DebuggerTheme.Smooth(g);
			int x = S(6);
			if (hook && original > 0)
			{
				int glyph = S(16);
				DebuggerTheme.Glyph(g, Severity.Info, new Rectangle(x, (Height - glyph) / 2, glyph, glyph));
				DebuggerTheme.DrawText(g, "Hook mode: your " + Math.Max(0, replacement) + " bytes run in separate hook memory, so any length works. Only a jump is written over the original " + original + " bytes.", DebuggerTheme.UiFont, DebuggerTheme.Mix(DebuggerTheme.Violet, Color.White, .25f), new Rectangle(x + glyph + S(8), 0, Width - glyph - S(20), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
				return;
			}
			var fit = Fit;
			var accent = fit == SizeFit.Exact ? DebuggerTheme.Teal : fit == SizeFit.Padded ? DebuggerTheme.Amber : fit == SizeFit.TooLong ? DebuggerTheme.Red : DebuggerTheme.Faint;
			int barWidth = S(220), barHeight = S(10), y = (Height - barHeight) / 2;
			var track = new RectangleF(x, y, barWidth, barHeight);
			DebuggerTheme.FillRounded(g, DebuggerTheme.Raised, track, barHeight / 2f);
			if (original > 0 && replacement >= 0)
			{
				float scale = barWidth / (float)Math.Max(original, replacement);
				float used = Math.Min(replacement, original) * scale;
				if (used > 0) DebuggerTheme.FillRounded(g, accent, new RectangleF(x, y, used, barHeight), barHeight / 2f);
				if (fit == SizeFit.TooLong) DebuggerTheme.FillRounded(g, Color.FromArgb(120, DebuggerTheme.Red), new RectangleF(x + original * scale, y, (replacement - original) * scale, barHeight), barHeight / 2f);
				using (var pen = new Pen(DebuggerTheme.Text, 1.5f)) g.DrawLine(pen, x + original * scale, y - S(4), x + original * scale, y + barHeight + S(4));
			}
			DebuggerTheme.DrawText(g, PatchSize.Describe(original, replacement), DebuggerTheme.UiFont, fit == SizeFit.Unknown ? DebuggerTheme.Muted : DebuggerTheme.Mix(accent, Color.White, .2f), new Rectangle(x + barWidth + S(12), 0, Width - barWidth - S(24), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
		}
	}

	/// <summary>A one-line validity/explanation note under an editor ("✔ assembles to 3 bytes").</summary>
	public class NoteLine : DebuggerControl
	{
		private Severity severity = Severity.Neutral;

		public NoteLine()
		{
			Dock = DockStyle.Bottom;
			Height = S(24);
			BackColor = DebuggerTheme.Panel;
		}

		public void Show(Severity tone, string text)
		{
			if (tone == severity && text == Text) return;
			severity = tone; Text = text ?? "";
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			if (string.IsNullOrEmpty(Text)) return;
			int glyph = S(14);
			DebuggerTheme.Glyph(g, severity, new Rectangle(S(2), (Height - glyph) / 2, glyph, glyph));
			DebuggerTheme.DrawText(g, Text, DebuggerTheme.UiFont, severity == Severity.Neutral ? DebuggerTheme.Muted : DebuggerTheme.Mix(DebuggerTheme.Accent(severity), Color.White, .25f), new Rectangle(S(22), 0, Width - S(24), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
		}
	}

	/// <summary>
	/// A thin divider the user drags to share space between two areas. It reports how far the pointer has moved since
	/// the drag began; <see cref="ResizeTable"/> and <see cref="ResizeDocked"/> wire it to a layout.
	/// </summary>
	public class GripBar : DebuggerControl
	{
		private readonly Orientation orientation;
		private bool hover, dragging;
		private int origin;

		public event Action DragStarted;
		/// <summary>Pointer movement along the drag axis, in pixels, measured from where the drag began.</summary>
		public event Action<int> Dragged;
		public event Action DragFinished;

		/// <param name="orientation">Horizontal: the bar lies between stacked areas and drags up and down.</param>
		public GripBar(Orientation orientation)
		{
			this.orientation = orientation;
			Margin = Padding.Empty;
			TabStop = false;
			Cursor = orientation == Orientation.Horizontal ? Cursors.HSplit : Cursors.VSplit;
			if (orientation == Orientation.Horizontal) Height = S(8); else Width = S(8);
		}

		private bool Stacked => orientation == Orientation.Horizontal;

		private int Pointer => Stacked ? MousePosition.Y : MousePosition.X;

		protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
		protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				dragging = true; origin = Pointer; Capture = true; Invalidate();
				DragStarted?.Invoke();
			}
			base.OnMouseDown(e);
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			if (dragging) Dragged?.Invoke(Pointer - origin);
			base.OnMouseMove(e);
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			// The last move before release can be coalesced away; apply where the pointer actually stopped.
			if (dragging) Dragged?.Invoke(Pointer - origin);
			Finish();
			base.OnMouseUp(e);
		}
		protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) Finish(); base.OnMouseCaptureChanged(e); }

		private void Finish()
		{
			if (!dragging) return;
			dragging = false; Capture = false; Invalidate();
			DragFinished?.Invoke();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			DebuggerTheme.Smooth(g);
			int length = S(44), thickness = S(4);
			if (hover || dragging)
			{
				var line = Stacked ? new Rectangle(S(6), Height / 2, Width - S(12), 1) : new Rectangle(Width / 2, S(6), 1, Height - S(12));
				using (var brush = new SolidBrush(DebuggerTheme.Mix(BackColor, DebuggerTheme.Teal, dragging ? .55f : .3f))) g.FillRectangle(brush, line);
			}
			var handle = Stacked
				? new RectangleF((Width - length) / 2f, (Height - thickness) / 2f, length, thickness)
				: new RectangleF((Width - thickness) / 2f, (Height - length) / 2f, thickness, length);
			var color = dragging ? DebuggerTheme.Teal : hover ? DebuggerTheme.Mix(DebuggerTheme.Border, DebuggerTheme.Text, .5f) : DebuggerTheme.Mix(DebuggerTheme.Border, DebuggerTheme.Text, .18f);
			DebuggerTheme.FillRounded(g, color, handle, thickness / 2f);
		}

		/// <summary>
		/// Moves the boundary between the percent-sized rows (stacked) or columns on each side of this bar. The area
		/// growing takes space from the nearest area on the other side, then the next one once that reaches
		/// <paramref name="minimum"/> pixels.
		/// </summary>
		public void ResizeTable(TableLayoutPanel table, int minimum)
		{
			int[] start = null;
			int[] before = null, after = null;
			DragStarted += () =>
			{
				start = Stacked ? table.GetRowHeights() : table.GetColumnWidths();
				int index = Stacked ? table.GetRow(this) : table.GetColumn(this);
				before = PercentCells(table, Stacked, index, -1);
				after = PercentCells(table, Stacked, index, +1);
			};
			Dragged += offset =>
			{
				if (start == null || before.Length == 0 || after.Length == 0 || offset == 0) return;
				var sizes = (int[])start.Clone();
				// Dragging down (or right) grows the nearest area before the bar and shrinks those after it.
				var grow = offset > 0 ? before[0] : after[0];
				int wanted = Math.Abs(offset), moved = 0;
				foreach (int i in offset > 0 ? after : before)
				{
					int take = Math.Max(0, Math.Min(wanted - moved, sizes[i] - minimum));
					sizes[i] -= take; moved += take;
					if (moved == wanted) break;
				}
				sizes[grow] += moved;
				SetPercents(table, Stacked, sizes.Select(v => (float)v).ToArray());
			};
		}

		/// <summary>Percent-sized rows (or columns) from <paramref name="index"/> outwards in the direction of <paramref name="step"/>, nearest first.</summary>
		private static int[] PercentCells(TableLayoutPanel table, bool rows, int index, int step)
		{
			int count = rows ? table.RowStyles.Count : table.ColumnStyles.Count;
			var found = new List<int>();
			for (int i = index + step; i >= 0 && i < count; i += step)
				if (Percent(table, rows, i)) found.Add(i);
			return found.ToArray();
		}

		/// <summary>The share of each row (or column), or 0 for those that are not percent-sized.</summary>
		public static float[] Percents(TableLayoutPanel table, bool rows)
		{
			int count = rows ? table.RowStyles.Count : table.ColumnStyles.Count;
			var shares = new float[count];
			for (int i = 0; i < count; ++i)
			{
				TableLayoutStyle style = rows ? (TableLayoutStyle)table.RowStyles[i] : table.ColumnStyles[i];
				if (style.SizeType == SizeType.Percent) shares[i] = rows ? ((RowStyle)style).Height : ((ColumnStyle)style).Width;
			}
			return shares;
		}

		/// <summary>Sets the percent-sized rows (or columns) in proportion to <paramref name="sizes"/>, normalised to 100.</summary>
		public static void SetPercents(TableLayoutPanel table, bool rows, float[] sizes)
		{
			int count = rows ? table.RowStyles.Count : table.ColumnStyles.Count;
			if (sizes == null || sizes.Length != count) return;
			float total = 0;
			for (int i = 0; i < count; ++i) if (Percent(table, rows, i)) total += Math.Max(0, sizes[i]);
			if (total <= 0) return;
			table.SuspendLayout();
			for (int i = 0; i < count; ++i)
			{
				if (!Percent(table, rows, i)) continue;
				float share = 100f * Math.Max(0, sizes[i]) / total;
				if (rows) table.RowStyles[i].Height = share; else table.ColumnStyles[i].Width = share;
			}
			table.ResumeLayout(true);
		}

		private static bool Percent(TableLayoutPanel table, bool rows, int i) => (rows ? table.RowStyles[i].SizeType : table.ColumnStyles[i].SizeType) == SizeType.Percent;

		/// <summary>
		/// Resizes <paramref name="target"/>, a panel docked on one side of this bar in the same parent, keeping it and
		/// the rest of the parent at least <paramref name="minimum"/> pixels.
		/// </summary>
		public void ResizeDocked(Control target, int minimum)
		{
			int start = 0;
			int sign = target.Dock == DockStyle.Right || target.Dock == DockStyle.Bottom ? -1 : 1;
			Func<int, int> clamp = size =>
			{
				var parent = target.Parent;
				int room = parent == null ? size : (Stacked ? parent.ClientSize.Height - Height : parent.ClientSize.Width - Width) - minimum;
				return Math.Max(minimum, Math.Min(Math.Max(minimum, room), size));
			};
			Action<int> set = size => { if (Stacked) target.Height = size; else target.Width = size; };
			DragStarted += () => start = Stacked ? target.Height : target.Width;
			Dragged += offset => set(clamp(start + sign * offset));
			// A narrower window must not leave the panel wider than the space it shares.
			EventHandler keep = (s, e) =>
			{
				var parent = target.Parent;
				int space = Stacked ? parent.ClientSize.Height : parent.ClientSize.Width;
				if (space >= minimum * 2 + (Stacked ? Height : Width)) set(clamp(Stacked ? target.Height : target.Width));
			};
			if (target.Parent != null) target.Parent.Resize += keep;
			else target.ParentChanged += (s, e) => { if (target.Parent != null) target.Parent.Resize += keep; };
		}
	}
}
