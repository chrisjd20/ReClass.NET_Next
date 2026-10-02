using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ReClassNET.AssemblyEditing;
using ReClassNET.UI;
using ReClassNET.UI.Debugger;

namespace ReClassNET.Controls.Debugger
{
	/// <summary>
	/// Base for painted views that scroll vertically. It manages its own scroll bar rather than AutoScroll, which
	/// throws under Mono when a hidden view is resized.
	/// </summary>
	public abstract class ScrollingView : Control, IPaintsOwnTheme
	{
		private readonly VScrollBar bar = new VScrollBar { Dock = DockStyle.Right, Visible = false };
		private string emptyText = "";
		private int contentHeight;

		public string EmptyText { get => emptyText; set { emptyText = value ?? ""; Invalidate(); } }

		/// <summary>How far the content is scrolled, in pixels.</summary>
		protected int Offset => bar.Visible ? bar.Value : 0;

		protected int ContentWidth => Math.Max(S(100), ClientSize.Width - (bar.Visible ? bar.Width : 0));

		protected ScrollingView()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
			BackColor = DebuggerTheme.Panel;
			ForeColor = DebuggerTheme.Text;
			Font = DebuggerTheme.UiFont;
			TabStop = true;
			bar.ValueChanged += (s, e) => Invalidate();
			Controls.Add(bar);
		}

		protected static int S(int pixels) => DebuggerTheme.Scale(pixels);

		/// <summary>Lays out content for the given width and returns its height.</summary>
		protected abstract int Arrange(int width);

		protected abstract void PaintContent(Graphics g, int width);

		protected abstract bool IsEmpty { get; }

		public void Relayout()
		{
			int visible = ClientSize.Height;
			if (IsEmpty || visible <= 0) { contentHeight = 0; bar.Visible = false; Invalidate(); return; }
			contentHeight = Arrange(Math.Max(S(100), ClientSize.Width));
			bool overflow = contentHeight > visible;
			if (overflow) contentHeight = Arrange(Math.Max(S(100), ClientSize.Width - bar.Width));
			if (overflow)
			{
				int largest = Math.Max(0, contentHeight - visible);
				bar.Minimum = 0;
				bar.Maximum = Math.Max(1, contentHeight - 1);
				bar.LargeChange = Math.Max(1, visible);
				bar.SmallChange = S(24);
				bar.Value = Math.Max(0, Math.Min(bar.Value, largest));
			}
			else bar.Value = 0;
			bar.Visible = overflow;
			Invalidate();
		}

		protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
		protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }
		protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

		protected override void OnMouseWheel(MouseEventArgs e)
		{
			if (bar.Visible)
			{
				int largest = Math.Max(0, contentHeight - ClientSize.Height);
				bar.Value = Math.Max(0, Math.Min(largest, bar.Value - Math.Sign(e.Delta) * S(48)));
			}
			base.OnMouseWheel(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(BackColor);
			if (IsEmpty)
			{
				if (EmptyText.Length > 0) DebuggerTheme.DrawText(g, EmptyText, DebuggerTheme.UiFont, DebuggerTheme.Muted, new Rectangle(S(16), S(10), Math.Max(10, Width - S(32)), Math.Max(10, Height - S(20))), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
				return;
			}
			g.TranslateTransform(0, -Offset);
			PaintContent(g, ContentWidth);
		}
	}

	public sealed class ListedBadge
	{
		public string Text;
		public Severity Tone;

		public ListedBadge(string text, Severity tone) { Text = text; Tone = tone; }
	}

	public sealed class ListedInstruction
	{
		public ulong Address;
		public byte[] Bytes = new byte[0];
		public List<AsmToken> Tokens = new List<AsmToken>();
		public string Text = "";
		public string Explanation;
		public List<ListedBadge> Badges = new List<ListedBadge>();
		public Severity Accent = Severity.Neutral;
		public bool Dim;
	}

	/// <summary>A disassembly listing: address, byte chips, coloured instruction, badges and a plain-English line.</summary>
	public sealed class InstructionListView : ScrollingView
	{
		private List<ListedInstruction> items = new List<ListedInstruction>();
		private readonly List<int> heights = new List<int>();
		private string header;

		public InstructionListView()
		{
			EmptyText = "No instruction to show yet.";
		}

		public void SetItems(IEnumerable<ListedInstruction> values, string headerText = null)
		{
			items = (values ?? Enumerable.Empty<ListedInstruction>()).ToList();
			header = headerText;
			Relayout();
		}

		protected override bool IsEmpty => items.Count == 0;

		private int BytesWidth => items.Count == 0 ? 0 : items.Max(i => Math.Min(i.Bytes.Length, 10)) * (DebuggerTheme.Measure("00", DebuggerTheme.MonoSmall).Width + S(11));

		private int AsmLeft => S(18) + DebuggerTheme.Measure("00007FF000000000", DebuggerTheme.MonoSmall).Width + S(12) + Math.Max(BytesWidth, S(40)) + S(14);

		private int BadgesWidth(ListedInstruction item) => item.Badges.Sum(b => DebuggerTheme.PillWidth(b.Text) + S(6));

		private static List<AsmToken> TokensOf(ListedInstruction item) => item.Tokens.Count > 0 ? item.Tokens : AsmTokens.Tokenize(item.Text);

		// When the row is too narrow for the instruction beside its bytes, it moves to a line of its own instead of being cut short.
		private bool AsmOnOwnLine(ListedInstruction item, int width)
		{
			int room = width - S(24) - BadgesWidth(item) - AsmLeft;
			return DebuggerTheme.Measure(string.Concat(TokensOf(item).Select(t => t.Text)), DebuggerTheme.MonoBold).Width > room;
		}

		private int AsmLineHeight => DebuggerTheme.ChipHeight() + S(4);

		protected override int Arrange(int width)
		{
			heights.Clear();
			int y = S(8) + (string.IsNullOrEmpty(header) ? 0 : S(22));
			int textLeft = S(18);
			foreach (var item in items)
			{
				int h = DebuggerTheme.ChipHeight() + S(10);
				if (AsmOnOwnLine(item, width)) h += AsmLineHeight;
				if (!string.IsNullOrEmpty(item.Explanation))
					h += TextRenderer.MeasureText(item.Explanation, DebuggerTheme.UiFont, new Size(Math.Max(50, width - textLeft - S(16)), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + S(6);
				heights.Add(h);
				y += h + S(6);
			}
			return y + S(4);
		}

		protected override void PaintContent(Graphics g, int width)
		{
			int y = S(8);
			if (!string.IsNullOrEmpty(header))
			{
				DebuggerTheme.DrawText(g, header, DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, S(12), y);
				y += S(22);
			}
			var addressFont = DebuggerTheme.MonoSmall;
			int addressWidth = DebuggerTheme.Measure("00007FF000000000", addressFont).Width;
			int bytesWidth = BytesWidth;
			for (int i = 0; i < items.Count && i < heights.Count; ++i)
			{
				var item = items[i];
				int h = heights[i];
				var row = new RectangleF(S(6), y, width - S(12), h);
				DebuggerTheme.Smooth(g);
				var accent = DebuggerTheme.Accent(item.Accent);
				DebuggerTheme.FillRounded(g, item.Accent == Severity.Neutral ? DebuggerTheme.Raised : DebuggerTheme.Mix(DebuggerTheme.Raised, accent, .1f), row, S(6));
				if (item.Accent != Severity.Neutral)
				{
					DebuggerTheme.StrokeRounded(g, Color.FromArgb(110, accent), row, S(6));
					using (var brush = new SolidBrush(accent)) g.FillRectangle(brush, row.X, row.Y + S(6), S(3), row.Height - S(12));
				}

				int x = S(18), lineY = y + S(6);
				DebuggerTheme.DrawText(g, item.Address.ToString("X16"), addressFont, item.Dim ? DebuggerTheme.Faint : DebuggerTheme.Muted, x, lineY + S(2));
				x += addressWidth + S(12);
				int bx = x;
				foreach (var b in item.Bytes.Take(10))
					bx += DebuggerTheme.Chip(g, b.ToString("X2"), DebuggerTheme.Panel, DebuggerTheme.Border, item.Dim ? DebuggerTheme.Faint : DebuggerTheme.Text, bx, lineY) + S(3);
				if (item.Bytes.Length > 10) DebuggerTheme.DrawText(g, "+" + (item.Bytes.Length - 10), DebuggerTheme.UiSmall, DebuggerTheme.Muted, bx, lineY + S(2));
				x += Math.Max(bytesWidth, S(40)) + S(14);

				int badgesWidth = BadgesWidth(item);
				bool ownLine = AsmOnOwnLine(item, width);
				int asmY = lineY + DebuggerTheme.ChipHeight() + S(5);
				// Badges stay beside the bytes when there is room, otherwise they move down next to the instruction.
				int badgesY = ownLine && width - S(16) - badgesWidth < bx + S(8) ? asmY : lineY + S(1);
				if (ownLine) AsmTokens.Draw(g, TokensOf(item), DebuggerTheme.MonoBold, S(18), asmY, width - S(24) - (badgesY == asmY ? badgesWidth : 0), item.Dim ? .55f : 0f);
				else AsmTokens.Draw(g, TokensOf(item), DebuggerTheme.MonoBold, x, lineY + S(1), width - S(24) - badgesWidth, item.Dim ? .55f : 0f);
				int px = width - S(16) - badgesWidth;
				foreach (var badge in item.Badges)
					px += DebuggerTheme.Pill(g, badge.Text, badge.Tone == Severity.Neutral ? DebuggerTheme.Muted : DebuggerTheme.Accent(badge.Tone), px, badgesY, badge.Tone == Severity.Success) + S(6);

				if (!string.IsNullOrEmpty(item.Explanation))
				{
					int ey = lineY + DebuggerTheme.ChipHeight() + S(6) + (ownLine ? AsmLineHeight : 0);
					DebuggerTheme.DrawText(g, item.Explanation, DebuggerTheme.UiFont, item.Dim ? DebuggerTheme.Faint : DebuggerTheme.Muted, new Rectangle(S(18), ey, width - S(34), h - (ey - y)), TextFormatFlags.WordBreak);
				}
				y += h + S(6);
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (e.Control && e.KeyCode == Keys.C && items.Count > 0)
			{
				var text = new StringBuilder();
				foreach (var item in items)
				{
					text.AppendLine(item.Address.ToString("X16") + "  " + AssemblyService.FormatHex(item.Bytes) + "  " + (item.Tokens.Count > 0 ? string.Concat(item.Tokens.Select(t => t.Text)) : item.Text));
					if (!string.IsNullOrEmpty(item.Explanation)) text.AppendLine("    " + item.Explanation);
				}
				Clipboard.SetText(text.ToString());
				e.Handled = true;
			}
			base.OnKeyDown(e);
		}
	}

	/// <summary>Register tiles. Used, pointing-at-watched-data and changed registers stand out; click to follow.</summary>
	public sealed class RegisterBoard : ScrollingView
	{
		private List<RegisterView> registers = new List<RegisterView>();
		private readonly List<Rectangle> tiles = new List<Rectangle>();
		private string caption;
		private int hover = -1;
		private readonly ToolTip tip = new ToolTip();

		public event Action<RegisterView> FollowRequested;

		public RegisterBoard()
		{
			EmptyText = "Select a row to see the registers captured at that moment.";
		}

		public void SetRegisters(IEnumerable<RegisterView> values, string captionText)
		{
			registers = (values ?? Enumerable.Empty<RegisterView>()).ToList();
			caption = captionText;
			hover = -1;
			Relayout();
		}

		protected override bool IsEmpty => registers.Count == 0;

		protected override int Arrange(int width)
		{
			tiles.Clear();
			int tileWidth = S(206), tileHeight = S(50), gap = S(8);
			int columns = Math.Max(1, (width - S(12)) / (tileWidth + gap));
			tileWidth = Math.Max(S(170), (width - S(12) - gap * (columns - 1)) / columns);
			int top = S(8) + (string.IsNullOrEmpty(caption) ? 0 : S(24));
			for (int i = 0; i < registers.Count; ++i)
			{
				int c = i % columns, r = i / columns;
				tiles.Add(new Rectangle(S(6) + c * (tileWidth + gap), top + r * (tileHeight + gap), tileWidth, tileHeight));
			}
			return tiles.Count == 0 ? top : tiles.Max(t => t.Bottom) + S(10);
		}

		private int HitTest(Point location)
		{
			var p = new Point(location.X, location.Y + Offset);
			return tiles.FindIndex(t => t.Contains(p));
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			int index = HitTest(e.Location);
			if (index != hover)
			{
				hover = index;
				Cursor = index >= 0 && registers[index].LooksLikeAddress ? Cursors.Hand : Cursors.Default;
				tip.SetToolTip(this, index >= 0 ? registers[index].Describe() : "");
				Invalidate();
			}
			base.OnMouseMove(e);
		}

		protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

		protected override void OnMouseClick(MouseEventArgs e)
		{
			int index = HitTest(e.Location);
			// Only addresses can be opened; a plain number explains itself in the tooltip instead.
			if (index >= 0 && e.Button == MouseButtons.Left && registers[index].LooksLikeAddress) FollowRequested?.Invoke(registers[index]);
			base.OnMouseClick(e);
		}

		protected override void PaintContent(Graphics g, int width)
		{
			if (!string.IsNullOrEmpty(caption)) DebuggerTheme.DrawText(g, caption, DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, S(10), S(10));
			for (int i = 0; i < registers.Count && i < tiles.Count; ++i)
			{
				var reg = registers[i];
				var t = tiles[i];
				DebuggerTheme.Smooth(g);
				var accent = reg.PointsAtWatched ? DebuggerTheme.Amber : reg.IsInstructionPointer ? DebuggerTheme.Violet : reg.Used ? DebuggerTheme.Teal : DebuggerTheme.Border;
				bool highlighted = reg.PointsAtWatched || reg.Used || reg.IsInstructionPointer;
				bool hot = i == hover && reg.LooksLikeAddress;
				var fill = hot ? DebuggerTheme.Hover : highlighted ? DebuggerTheme.Mix(DebuggerTheme.Raised, accent, .1f) : DebuggerTheme.Raised;
				DebuggerTheme.FillRounded(g, fill, t, S(6));
				DebuggerTheme.StrokeRounded(g, highlighted ? Color.FromArgb(170, accent) : DebuggerTheme.Border, t, S(6));

				DebuggerTheme.DrawText(g, reg.Name.ToUpperInvariant(), DebuggerTheme.UiBold, highlighted ? DebuggerTheme.Mix(accent, Color.White, .2f) : DebuggerTheme.Muted, t.X + S(10), t.Y + S(6));
				int px = t.Right - S(8);
				var tags = new List<KeyValuePair<string, Color>>();
				if (reg.PointsAtWatched) tags.Add(new KeyValuePair<string, Color>("-> WATCHED", DebuggerTheme.Amber));
				if (reg.Used) tags.Add(new KeyValuePair<string, Color>("USED", DebuggerTheme.Teal));
				if (reg.Changed) tags.Add(new KeyValuePair<string, Color>("CHANGED", DebuggerTheme.Blue));
				for (int k = tags.Count - 1; k >= 0; --k)
				{
					px -= DebuggerTheme.PillWidth(tags[k].Key);
					DebuggerTheme.Pill(g, tags[k].Key, tags[k].Value, px, t.Y + S(5));
					px -= S(4);
				}
				var value = "0x" + reg.Value.ToString("X16");
				var valueFont = reg.Changed ? DebuggerTheme.MonoBold : DebuggerTheme.Mono;
				DebuggerTheme.DrawText(g, value, valueFont, reg.Changed ? DebuggerTheme.Text : DebuggerTheme.Mix(DebuggerTheme.Text, DebuggerTheme.Muted, .3f), t.X + S(10), t.Y + S(27));
				// Small plain numbers also read in decimal, where it fits.
				if (!reg.LooksLikeAddress && reg.Value < 100000000000UL && !reg.IsInstructionPointer && reg.Name != "rflags")
				{
					var decimalText = "= " + reg.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
					int left = t.X + S(10) + DebuggerTheme.Measure(value, valueFont).Width + S(8), right = t.Right - S(8);
					int needed = DebuggerTheme.Measure(decimalText, DebuggerTheme.UiSmall).Width;
					if (left + needed <= right) DebuggerTheme.DrawText(g, decimalText, DebuggerTheme.UiSmall, DebuggerTheme.Muted, right - needed, t.Y + S(29));
				}
			}
		}

		protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
	}

	/// <summary>
	/// The change preview as a picture: original vs replacement bytes column by column, or for a hook, the jump into
	/// the hook code and back.
	/// </summary>
	public sealed class ByteDiffView : ScrollingView
	{
		public sealed class HookModel
		{
			public ulong Site, Allocation, ReturnAddress;
			/// <summary>End of the user's selection. Displaced instructions past it were only moved to make room for the jump.</summary>
			public ulong SelectionEnd;
			public byte[] EntryBytes = new byte[0];
			public string Semantics = "";
			public List<ListedInstruction> YourCode = new List<ListedInstruction>();
			public List<ListedInstruction> Displaced = new List<ListedInstruction>();
		}

		private List<ByteCell> cells = new List<ByteCell>();
		private List<List<AsmToken>> before = new List<List<AsmToken>>(), after = new List<List<AsmToken>>();
		private ulong address;
		private string summary;
		private HookModel hook;

		private const string DefaultEmptyText = "Preview shows exactly which bytes change here. Nothing is written to the game until you click Apply.";

		public ByteDiffView()
		{
			EmptyText = DefaultEmptyText;
		}

		public void Clear() { cells.Clear(); before.Clear(); after.Clear(); hook = null; summary = null; EmptyText = DefaultEmptyText; Relayout(); }

		public void ShowInPlace(ulong at, IEnumerable<ByteCell> diff, IEnumerable<List<AsmToken>> beforeLines, IEnumerable<List<AsmToken>> afterLines, string text)
		{
			hook = null; address = at; summary = text;
			cells = diff.ToList(); before = beforeLines.ToList(); after = afterLines.ToList();
			Relayout();
		}

		public void ShowHook(HookModel model, string text)
		{
			cells.Clear(); before.Clear(); after.Clear();
			hook = model; summary = text;
			Relayout();
		}

		protected override bool IsEmpty => hook == null && cells.Count == 0;

		private int CellWidth => DebuggerTheme.Measure("00", DebuggerTheme.Mono).Width + S(14);

		protected override int Arrange(int width)
		{
			if (hook != null)
			{
				int lines = Math.Max(hook.YourCode.Count + hook.Displaced.Count + 3, 4);
				return S(44) + lines * S(20) + S(64);
			}
			int perRow = Math.Max(4, (width - S(150)) / CellWidth);
			int rows = (cells.Count + perRow - 1) / perRow;
			int bytesHeight = rows * S(70);
			int asmHeight = S(30) + Math.Max(before.Count, after.Count) * S(20);
			return S(36) + bytesHeight + asmHeight + S(10);
		}

		protected override void PaintContent(Graphics g, int width)
		{
			if (!string.IsNullOrEmpty(summary)) DebuggerTheme.DrawText(g, summary, DebuggerTheme.UiFont, DebuggerTheme.Text, new Rectangle(S(12), S(8), width - S(24), S(20)), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
			if (hook != null) { PaintHook(g, width); return; }

			int perRow = Math.Max(4, (width - S(150)) / CellWidth);
			int y = S(36);
			for (int start = 0; start < cells.Count; start += perRow)
			{
				DebuggerTheme.DrawText(g, "ORIGINAL", DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, S(12), y + S(6));
				DebuggerTheme.DrawText(g, "REPLACEMENT", DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, S(12), y + S(36));
				DebuggerTheme.DrawText(g, "+" + start.ToString("X2"), DebuggerTheme.MonoSmall, DebuggerTheme.Faint, S(100), y + S(21));
				for (int i = start; i < Math.Min(cells.Count, start + perRow); ++i)
				{
					var cell = cells[i];
					int x = S(140) + (i - start) * CellWidth;
					PaintCell(g, cell.Original, cell.Kind, true, x, y);
					PaintCell(g, cell.Replacement, cell.Kind, false, x, y + S(30));
					if (cell.Kind != ByteCellKind.Same)
						using (var pen = new Pen(Color.FromArgb(90, DebuggerTheme.Muted)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, x + CellWidth / 2 - S(2), y + S(24), x + CellWidth / 2 - S(2), y + S(30));
				}
				y += S(70);
			}

			y += S(4);
			int half = (width - S(36)) / 2;
			DebuggerTheme.DrawText(g, "BEFORE", DebuggerTheme.UiSmallBold, DebuggerTheme.Muted, S(12), y);
			DebuggerTheme.DrawText(g, "AFTER", DebuggerTheme.UiSmallBold, DebuggerTheme.Teal, S(24) + half, y);
			y += S(22);
			for (int i = 0; i < Math.Max(before.Count, after.Count); ++i)
			{
				if (i < before.Count) AsmTokens.Draw(g, before[i], DebuggerTheme.Mono, S(12), y, S(12) + half - S(10), .35f);
				if (i < after.Count) AsmTokens.Draw(g, after[i], DebuggerTheme.MonoBold, S(24) + half, y, width - S(12));
				y += S(20);
			}
			using (var pen = new Pen(DebuggerTheme.Border)) g.DrawLine(pen, S(18) + half - S(6), y - Math.Max(before.Count, after.Count) * S(20) - S(22), S(18) + half - S(6), y);
		}

		private void PaintCell(Graphics g, byte? value, ByteCellKind kind, bool original, int x, int y)
		{
			var bounds = new RectangleF(x, y, CellWidth - S(4), S(24));
			DebuggerTheme.Smooth(g);
			Color fill, border, text;
			if (value == null)
			{
				using (var pen = new Pen(DebuggerTheme.Border) { DashStyle = DashStyle.Dash }) using (var path = DebuggerTheme.RoundedRect(bounds, S(4))) g.DrawPath(pen, path);
				return;
			}
			switch (kind)
			{
				case ByteCellKind.Same:
					fill = DebuggerTheme.Raised; border = DebuggerTheme.Border; text = DebuggerTheme.Muted; break;
				case ByteCellKind.Padding:
					fill = original ? DebuggerTheme.Mix(DebuggerTheme.Raised, DebuggerTheme.Red, .14f) : DebuggerTheme.Mix(DebuggerTheme.Raised, DebuggerTheme.Muted, .12f);
					border = original ? DebuggerTheme.Mix(DebuggerTheme.Border, DebuggerTheme.Red, .5f) : DebuggerTheme.Faint;
					text = original ? DebuggerTheme.Mix(DebuggerTheme.Red, Color.White, .3f) : DebuggerTheme.Muted; break;
				default:
					var accent = original ? DebuggerTheme.Red : DebuggerTheme.Teal;
					fill = DebuggerTheme.Mix(DebuggerTheme.Raised, accent, .18f); border = Color.FromArgb(200, accent); text = DebuggerTheme.Mix(accent, Color.White, .35f); break;
			}
			DebuggerTheme.FillRounded(g, fill, bounds, S(4));
			DebuggerTheme.StrokeRounded(g, border, bounds, S(4));
			var label = value.Value.ToString("X2");
			DebuggerTheme.DrawText(g, label, kind == ByteCellKind.Same ? DebuggerTheme.Mono : DebuggerTheme.MonoBold, text, Rectangle.Round(bounds), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
			if (!original && kind == ByteCellKind.Padding) DebuggerTheme.DrawText(g, "nop", DebuggerTheme.UiSmall, DebuggerTheme.Faint, x + S(2), y + S(25));
			if (original && kind != ByteCellKind.Same)
				using (var pen = new Pen(Color.FromArgb(150, DebuggerTheme.Red), 1.2f)) g.DrawLine(pen, bounds.X + S(4), bounds.Y + bounds.Height / 2, bounds.Right - S(4), bounds.Y + bounds.Height / 2);
		}

		private void PaintHook(Graphics g, int width)
		{
			int top = S(40), arrow = S(40);
			int boxWidth = (width - S(24) - arrow * 2) / 3;
			int lines = Math.Max(hook.YourCode.Count + hook.Displaced.Count + 3, 4);
			int boxHeight = S(40) + lines * S(20);
			var site = new Rectangle(S(12), top, boxWidth, boxHeight);
			var code = new Rectangle(site.Right + arrow, top, boxWidth, boxHeight);
			var back = new Rectangle(code.Right + arrow, top, boxWidth, boxHeight);

			Box(g, site, "GAME CODE", "0x" + hook.Site.ToString("X"), DebuggerTheme.Blue);
			int y = site.Y + S(48);
			DebuggerTheme.DrawText(g, "now starts with a jump:", DebuggerTheme.UiSmall, DebuggerTheme.Muted, site.X + S(10), y); y += S(18);
			int x = site.X + S(10);
			foreach (var b in hook.EntryBytes.Take(14)) { if (x > site.Right - S(30)) break; x += DebuggerTheme.Chip(g, b.ToString("X2"), DebuggerTheme.Mix(DebuggerTheme.Raised, DebuggerTheme.Violet, .2f), DebuggerTheme.Violet, DebuggerTheme.Text, x, y) + S(3); }
			y += S(26);
			AsmTokens.Draw(g, AsmTokens.Tokenize("jmp 0x" + hook.Allocation.ToString("X")), DebuggerTheme.MonoSmall, site.X + S(10), y, site.Right - S(8));

			Box(g, code, "HOOK CODE · " + hook.Semantics.ToUpperInvariant(), "0x" + hook.Allocation.ToString("X"), DebuggerTheme.Violet);
			y = code.Y + S(48);
			// The same order the planner writes the hook in: what runs first is listed first.
			var selected = hook.Displaced.Where(i => hook.SelectionEnd == 0 || i.Address < hook.SelectionEnd).ToList();
			var moved = hook.Displaced.Where(i => hook.SelectionEnd != 0 && i.Address >= hook.SelectionEnd).ToList();
			const string MovedLabel = "moved here to make room for the jump";
			var sections = new List<KeyValuePair<string, List<ListedInstruction>>>();
			if (hook.Semantics.IndexOf("after", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>("original selection (moved here)", selected));
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>("your code", hook.YourCode));
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>(MovedLabel, moved));
			}
			else if (hook.Semantics.IndexOf("before", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>("your code", hook.YourCode));
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>("original (moved here)", hook.Displaced));
			}
			else
			{
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>("your code (replaces the selection)", hook.YourCode));
				sections.Add(new KeyValuePair<string, List<ListedInstruction>>(MovedLabel, moved));
			}
			foreach (var section in sections)
			{
				if (section.Value.Count == 0 && !section.Key.StartsWith("your", StringComparison.Ordinal)) continue;
				bool yours = section.Key.StartsWith("your", StringComparison.Ordinal);
				DebuggerTheme.DrawText(g, section.Key, DebuggerTheme.UiSmallBold, yours ? DebuggerTheme.Teal : DebuggerTheme.Muted, code.X + S(10), y); y += S(18);
				foreach (var line in section.Value)
				{
					AsmTokens.Draw(g, line.Tokens.Count > 0 ? line.Tokens : AsmTokens.Tokenize(line.Text), DebuggerTheme.MonoSmall, code.X + S(18), y, code.Right - S(8), yours ? 0 : .4f);
					y += S(18);
				}
				y += S(4);
			}
			AsmTokens.Draw(g, AsmTokens.Tokenize("jmp 0x" + hook.ReturnAddress.ToString("X")), DebuggerTheme.MonoSmall, code.X + S(10), Math.Min(y, code.Bottom - S(22)), code.Right - S(8));

			Box(g, back, "BACK IN THE GAME", "0x" + hook.ReturnAddress.ToString("X"), DebuggerTheme.Teal);
			DebuggerTheme.DrawText(g, "The game carries on right after the replaced bytes, as if nothing happened.", DebuggerTheme.UiFont, DebuggerTheme.Muted, new Rectangle(back.X + S(10), back.Y + S(48), back.Width - S(20), back.Height - S(56)), TextFormatFlags.WordBreak);

			Arrow(g, site.Right + S(4), site.Y + S(24), code.X - S(4), DebuggerTheme.Violet);
			Arrow(g, code.Right + S(4), code.Y + S(24), back.X - S(4), DebuggerTheme.Teal);
			DebuggerTheme.DrawText(g, "Hook memory stays reserved until the game exits, even after Restore original.", DebuggerTheme.UiSmall, DebuggerTheme.Muted, S(12), top + boxHeight + S(10));
		}

		private void Box(Graphics g, Rectangle bounds, string title, string address, Color accent)
		{
			DebuggerTheme.Smooth(g);
			DebuggerTheme.FillRounded(g, DebuggerTheme.Mix(DebuggerTheme.Raised, accent, .06f), bounds, S(8));
			DebuggerTheme.StrokeRounded(g, Color.FromArgb(150, accent), bounds, S(8));
			using (var brush = new SolidBrush(accent)) g.FillRectangle(brush, bounds.X + S(8), bounds.Y, bounds.Width - S(16), S(2));
			DebuggerTheme.DrawText(g, title, DebuggerTheme.UiSmallBold, accent, new Rectangle(bounds.X + S(10), bounds.Y + S(10), bounds.Width - S(20), S(16)), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
			DebuggerTheme.DrawText(g, address, DebuggerTheme.MonoSmall, DebuggerTheme.Text, bounds.X + S(10), bounds.Y + S(27));
		}

		private void Arrow(Graphics g, int x1, int y, int x2, Color color)
		{
			DebuggerTheme.Smooth(g);
			using (var pen = new Pen(color, S(2)) { CustomEndCap = new AdjustableArrowCap(4, 4) }) g.DrawLine(pen, x1, y, x2, y);
		}
	}

	/// <summary>Dark styling and custom cell painting for a hits grid.</summary>
	public static class DarkGrid
	{
		public static void Style(DataGridView grid)
		{
			grid.BackgroundColor = DebuggerTheme.Panel;
			grid.BorderStyle = BorderStyle.None;
			grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
			grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
			grid.EnableHeadersVisualStyles = false;
			grid.GridColor = DebuggerTheme.Border;
			grid.RowHeadersVisible = false;
			grid.AllowUserToResizeRows = false;
			grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
			grid.ColumnHeadersHeight = DebuggerTheme.Scale(32);
			grid.RowTemplate.Height = DebuggerTheme.Scale(30);
			grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = DebuggerTheme.Raised,
				ForeColor = DebuggerTheme.Muted,
				SelectionBackColor = DebuggerTheme.Raised,
				SelectionForeColor = DebuggerTheme.Muted,
				Font = DebuggerTheme.UiSmallBold,
				Padding = new Padding(DebuggerTheme.Scale(6), 0, DebuggerTheme.Scale(6), 0)
			};
			grid.DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = DebuggerTheme.Panel,
				ForeColor = DebuggerTheme.Text,
				SelectionBackColor = DebuggerTheme.Mix(DebuggerTheme.Panel, DebuggerTheme.Blue, .28f),
				SelectionForeColor = DebuggerTheme.Text,
				Font = DebuggerTheme.Mono,
				WrapMode = DataGridViewTriState.False,
				Padding = new Padding(DebuggerTheme.Scale(6), 0, DebuggerTheme.Scale(6), 0)
			};
			grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = DebuggerTheme.Mix(DebuggerTheme.Panel, DebuggerTheme.Raised, .45f) };
			typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true, null);
		}

		/// <summary>Draws a status badge (CONFIRMED, CANDIDATE…) in a cell, on the row's background.</summary>
		public static void PaintBadge(DataGridViewCellPaintingEventArgs e, HitKind kind)
		{
			e.PaintBackground(e.CellBounds, true);
			var label = HitKinds.Label(kind);
			var accent = HitKinds.Tone(kind) == Severity.Neutral ? DebuggerTheme.Muted : DebuggerTheme.Accent(HitKinds.Tone(kind));
			int h = DebuggerTheme.ChipHeight(DebuggerTheme.UiSmallBold);
			int glyph = DebuggerTheme.Scale(14);
			var tone = HitKinds.Tone(kind);
			DebuggerTheme.Glyph(e.Graphics, kind == HitKind.Unlikely ? Severity.Danger : tone == Severity.Neutral ? Severity.Neutral : tone, new Rectangle(e.CellBounds.X + DebuggerTheme.Scale(8), e.CellBounds.Y + (e.CellBounds.Height - glyph) / 2, glyph, glyph));
			DebuggerTheme.Pill(e.Graphics, label, accent, e.CellBounds.X + DebuggerTheme.Scale(28), e.CellBounds.Y + (e.CellBounds.Height - h) / 2, kind == HitKind.Confirmed);
			e.Handled = true;
		}

		/// <summary>Draws coloured assembly tokens in a cell.</summary>
		public static void PaintTokens(DataGridViewCellPaintingEventArgs e, List<AsmToken> tokens, float dim)
		{
			e.PaintBackground(e.CellBounds, true);
			var font = DebuggerTheme.MonoBold;
			int y = e.CellBounds.Y + (e.CellBounds.Height - DebuggerTheme.Measure("X", font).Height) / 2;
			var clip = e.Graphics.Clip;
			e.Graphics.SetClip(e.CellBounds);
			AsmTokens.Draw(e.Graphics, tokens, font, e.CellBounds.X + DebuggerTheme.Scale(8), y, e.CellBounds.Right - DebuggerTheme.Scale(4), dim);
			e.Graphics.Clip = clip;
			e.Handled = true;
		}

		/// <summary>Paints a fading highlight band to show a row's count just went up.</summary>
		public static void PaintFlash(DataGridViewCellPaintingEventArgs e, float strength, Color color)
		{
			if (strength <= 0) return;
			using (var brush = new SolidBrush(Color.FromArgb((int)(80 * strength), color))) e.Graphics.FillRectangle(brush, e.CellBounds);
		}

		public static void PaintEmpty(DataGridView grid, Graphics g, string text)
		{
			if (grid.Rows.Count > 0) return;
			int top = grid.ColumnHeadersHeight;
			var area = new Rectangle(0, top, grid.Width, grid.Height - top);
			int size = DebuggerTheme.Scale(44);
			DebuggerTheme.Smooth(g);
			float phase = Pulse.Phase;
			var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2 - DebuggerTheme.Scale(18));
			using (var halo = new SolidBrush(Color.FromArgb((int)(25 + 30 * phase), DebuggerTheme.Blue))) g.FillEllipse(halo, center.X - size / 2f - 6 * phase, center.Y - size / 2f - 6 * phase, size + 12 * phase, size + 12 * phase);
			using (var pen = new Pen(Color.FromArgb(180, DebuggerTheme.Blue), 2f)) g.DrawEllipse(pen, center.X - size / 4f, center.Y - size / 4f, size / 2f, size / 2f);
			DebuggerTheme.DrawText(g, text, DebuggerTheme.UiFont, DebuggerTheme.Muted, new Rectangle(area.X + DebuggerTheme.Scale(20), center.Y + size / 2, area.Width - DebuggerTheme.Scale(40), DebuggerTheme.Scale(40)), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
		}
	}
}
