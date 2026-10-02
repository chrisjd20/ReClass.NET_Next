using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ReClassNET.Native;

namespace ReClassNET.UI
{
	/// <summary>The memory view's colours for one theme (the Settings → Colors values).</summary>
	public sealed class MemoryColors
	{
		public Color Background, Selected, Hidden, Offset, Address, Hex, Type, Name, Value, Index, Comment, Text, VTable, Plugin;

		public void CopyTo(Settings settings)
		{
			settings.BackgroundColor = Background; settings.SelectedColor = Selected; settings.HiddenColor = Hidden;
			settings.OffsetColor = Offset; settings.AddressColor = Address; settings.HexColor = Hex; settings.TypeColor = Type;
			settings.NameColor = Name; settings.ValueColor = Value; settings.IndexColor = Index; settings.CommentColor = Comment;
			settings.TextColor = Text; settings.VTableColor = VTable; settings.PluginColor = Plugin;
		}

		public bool Matches(Settings s)
		{
			bool Same(Color a, Color b) => a.ToArgb() == b.ToArgb();
			return Same(s.BackgroundColor, Background) && Same(s.SelectedColor, Selected) && Same(s.HiddenColor, Hidden)
				&& Same(s.OffsetColor, Offset) && Same(s.AddressColor, Address) && Same(s.HexColor, Hex) && Same(s.TypeColor, Type)
				&& Same(s.NameColor, Name) && Same(s.ValueColor, Value) && Same(s.IndexColor, Index) && Same(s.CommentColor, Comment)
				&& Same(s.TextColor, Text) && Same(s.VTableColor, VTable);
		}
	}

	/// <summary>The window colours for one theme. Each role has a distinct colour, so a live switch can remap them.</summary>
	public sealed class ThemePalette
	{
		public ThemeKind Kind;
		public bool IsDark => Kind == ThemeKind.Dark;
		public Color Background, Panel, Raised, Hover, Border, Text, Muted, Faint;
		public Color Teal, Amber, Red, Blue, Violet;
		public Color AsmMnemonic, AsmRegister, AsmNumber, AsmKeyword, AsmPunctuation;
		/// <summary>Status colours for good/bad outcomes in lists (static scan results, active patches...).</summary>
		public Color Good, Bad, Changed;
		public MemoryColors Memory;

		public Color[] Roles => new[] { Background, Panel, Raised, Hover, Border, Text, Muted, Faint, Teal, Amber, Red, Blue, Violet };
	}

	/// <summary>Opt-out for windows that style their own controls (the debugger windows). They are re-styled through this on a live theme switch.</summary>
	public interface ISelfThemed
	{
		void ApplyTheme();
	}

	/// <summary>Marker for painted controls that read the palette themselves; the styler leaves them (not their children) alone.</summary>
	public interface IPaintsOwnTheme { }

	/// <summary>
	/// The application theme: Light (classic ReClass) or Dark. Everything that picks colours goes through here, so the main
	/// window, dialogs and debugger windows always match.
	/// </summary>
	public static class AppTheme
	{
		public static readonly ThemePalette Dark = new ThemePalette
		{
			Kind = ThemeKind.Dark,
			Background = Color.FromArgb(0x14, 0x18, 0x1F), Panel = Color.FromArgb(0x1B, 0x20, 0x29), Raised = Color.FromArgb(0x23, 0x2A, 0x35),
			Hover = Color.FromArgb(0x2C, 0x35, 0x43), Border = Color.FromArgb(0x2E, 0x36, 0x44), Text = Color.FromArgb(0xE6, 0xEA, 0xF0),
			Muted = Color.FromArgb(0x8B, 0x95, 0xA5), Faint = Color.FromArgb(0x5A, 0x63, 0x72),
			Teal = Color.FromArgb(0x3D, 0xD6, 0xB5), Amber = Color.FromArgb(0xF5, 0xB8, 0x4B), Red = Color.FromArgb(0xFF, 0x6B, 0x6B),
			Blue = Color.FromArgb(0x6F, 0xB3, 0xFF), Violet = Color.FromArgb(0xB4, 0x8C, 0xFF),
			AsmMnemonic = Color.FromArgb(0x7C, 0xB7, 0xFF), AsmRegister = Color.FromArgb(0x4F, 0xE0, 0xC0), AsmNumber = Color.FromArgb(0xFF, 0xA8, 0x5C),
			AsmKeyword = Color.FromArgb(0xC3, 0x9B, 0xFF), AsmPunctuation = Color.FromArgb(0x9A, 0xA4, 0xB4),
			Good = Color.FromArgb(0x4F, 0xD1, 0x8B), Bad = Color.FromArgb(0xFF, 0x7A, 0x7A), Changed = Color.FromArgb(0xFF, 0x7A, 0x7A),
			Memory = new MemoryColors
			{
				Background = Color.FromArgb(0x16, 0x1A, 0x21), Selected = Color.FromArgb(0x2A, 0x33, 0x41), Hidden = Color.FromArgb(0x22, 0x28, 0x32),
				Offset = Color.FromArgb(0xFF, 0x7A, 0x7A), Address = Color.FromArgb(0x4F, 0xD1, 0x8B), Hex = Color.FromArgb(0xD7, 0xDC, 0xE4),
				Type = Color.FromArgb(0x6F, 0xB3, 0xFF), Name = Color.FromArgb(0xC9, 0xB6, 0xFF), Value = Color.FromArgb(0xFF, 0xA8, 0x5C),
				Index = Color.FromArgb(0x4F, 0xE0, 0xD0), Comment = Color.FromArgb(0x86, 0xD4, 0x8F), Text = Color.FromArgb(0x7C, 0xB7, 0xFF),
				VTable = Color.FromArgb(0x9B, 0xE5, 0x64), Plugin = Color.FromArgb(0xFF, 0x7B, 0xEA)
			}
		};

		public static readonly ThemePalette Light = new ThemePalette
		{
			Kind = ThemeKind.Light,
			Background = Color.FromArgb(0xF3, 0xF5, 0xF8), Panel = Color.FromArgb(0xFF, 0xFF, 0xFE), Raised = Color.FromArgb(0xEC, 0xEF, 0xF4),
			Hover = Color.FromArgb(0xE1, 0xE7, 0xEF), Border = Color.FromArgb(0xC9, 0xD1, 0xDC), Text = Color.FromArgb(0x1C, 0x22, 0x2E),
			Muted = Color.FromArgb(0x5A, 0x64, 0x73), Faint = Color.FromArgb(0x98, 0xA1, 0xAE),
			Teal = Color.FromArgb(0x0C, 0x95, 0x78), Amber = Color.FromArgb(0xB8, 0x72, 0x0A), Red = Color.FromArgb(0xD0, 0x3B, 0x3B),
			Blue = Color.FromArgb(0x2F, 0x6E, 0xD3), Violet = Color.FromArgb(0x74, 0x4B, 0xD0),
			AsmMnemonic = Color.FromArgb(0x1F, 0x5F, 0xBF), AsmRegister = Color.FromArgb(0x0B, 0x84, 0x69), AsmNumber = Color.FromArgb(0xB8, 0x52, 0x0A),
			AsmKeyword = Color.FromArgb(0x74, 0x3D, 0xC4), AsmPunctuation = Color.FromArgb(0x67, 0x70, 0x7C),
			Good = Color.ForestGreen, Bad = Color.DarkRed, Changed = Color.Red,
			Memory = new MemoryColors
			{
				Background = Color.FromArgb(255, 255, 255), Selected = Color.FromArgb(240, 240, 240), Hidden = Color.FromArgb(240, 240, 240),
				Offset = Color.FromArgb(255, 0, 0), Address = Color.FromArgb(0, 200, 0), Hex = Color.FromArgb(0, 0, 0),
				Type = Color.FromArgb(0, 0, 255), Name = Color.FromArgb(32, 32, 128), Value = Color.FromArgb(255, 128, 0),
				Index = Color.FromArgb(32, 200, 200), Comment = Color.FromArgb(0, 200, 0), Text = Color.FromArgb(0, 0, 255),
				VTable = Color.FromArgb(0, 255, 0), Plugin = Color.FromArgb(255, 0, 255)
			}
		};

		public static ThemePalette Of(ThemeKind kind) => kind == ThemeKind.Light ? Light : Dark;

		/// <summary>The active palette. Defaults to Dark before settings exist (designer, tests).</summary>
		public static ThemePalette Current => Of(Program.Settings?.Theme ?? ThemeKind.Dark);

		public static event EventHandler Changed;

		/// <summary>
		/// Decides the theme after settings were read. A file that already chose a theme keeps it. Without a choice, a missing
		/// file or untouched classic colours become Dark; customised colours stay Light so the customisation survives.
		/// </summary>
		public static void ResolveLoadedTheme(Settings settings, bool fileRead, bool themeStored)
		{
			if (themeStored) return;
			if (!fileRead || Light.Memory.Matches(settings))
			{
				settings.Theme = ThemeKind.Dark;
				Dark.Memory.CopyTo(settings);
			}
			else settings.Theme = ThemeKind.Light;
		}

		/// <summary>Switches the theme live: memory-view colours, every open window and the menus.</summary>
		public static void Switch(ThemeKind kind)
		{
			var settings = Program.Settings;
			if (settings == null || settings.Theme == kind) return;
			settings.Theme = kind;
			Of(kind).Memory.CopyTo(settings);
			foreach (var form in GlobalWindowManager.Windows.ToList()) Apply(form);
			foreach (Form form in Application.OpenForms) { if (!GlobalWindowManager.Windows.Contains(form)) Apply(form); form.Invalidate(true); }
			Changed?.Invoke(null, EventArgs.Empty);
		}

		private sealed class Original
		{
			public Color Back, Fore;
			public BorderStyle? Border;
			public FlatStyle? Flat;
			public bool? VisualStyleBack;
			public TabDrawMode? TabDraw;
			public DrawMode? ItemDraw;
			public Color? Line;
			public TreeViewDrawMode? TreeDraw;
			public DataGridViewCellStyle Cells, Headers, RowHeaders, Alternating;
			public Color GridBack, GridLines;
			public bool HeadersVisual;
			public Color? LinkColor, ActiveLinkColor, VisitedLinkColor;
		}

		private static readonly ConditionalWeakTable<Control, Original> originals = new ConditionalWeakTable<Control, Original>();
		private static readonly ConditionalWeakTable<Form, ThemePalette> appliedTo = new ConditionalWeakTable<Form, ThemePalette>();

		/// <summary>Themes a window and its controls, and gives it a matching title bar on Windows.</summary>
		public static void Apply(Form form)
		{
			var palette = Current;
			ThemePalette previous;
			if (!appliedTo.TryGetValue(form, out previous)) previous = null;
			else appliedTo.Remove(form);
			appliedTo.Add(form, palette);
			if (previous != null && previous != palette) Remap(form, previous, palette);
			if (form is ISelfThemed self) self.ApplyTheme();
			else Style(form, palette);
			SetChrome(form);
			form.Invalidate(true);
		}

		/// <summary>Styles standard controls under <paramref name="root"/> for the current theme.</summary>
		public static void Style(Control root) => Style(root, Current);

		private static void Style(Control root, ThemePalette palette)
		{
			if (!(root is IPaintsOwnTheme)) StyleOne(root, palette);
			foreach (Control child in root.Controls) Style(child, palette);
			if (root is ToolStrip strip)
				foreach (var host in strip.Items.OfType<ToolStripControlHost>()) Style(host.Control, palette);
		}

		/// <summary>Moves colours from one palette's role to the same role in another (for controls coloured in code).</summary>
		private static void Remap(Control root, ThemePalette from, ThemePalette to)
		{
			var source = from.Roles; var target = to.Roles;
			Color Map(Color c) { int i = Array.FindIndex(source, s => s.ToArgb() == c.ToArgb()); return i >= 0 ? target[i] : c; }
			if (!originals.TryGetValue(root, out _))
			{
				var back = Map(root.BackColor); if (back != root.BackColor) root.BackColor = back;
				var fore = Map(root.ForeColor); if (fore != root.ForeColor) root.ForeColor = fore;
			}
			foreach (Control child in root.Controls) Remap(child, from, to);
		}

		private static Original Remember(Control control)
		{
			Original original;
			if (originals.TryGetValue(control, out original)) return original;
			original = new Original { Back = control.BackColor, Fore = control.ForeColor };
			switch (control)
			{
				case TextBox box: original.Border = box.BorderStyle; break;
				case RichTextBox rich: original.Border = rich.BorderStyle; break;
				case ListBox list: original.Border = list.BorderStyle; original.ItemDraw = list.DrawMode; break;
				case TreeView tree: original.Border = tree.BorderStyle; original.Line = tree.LineColor; original.TreeDraw = tree.DrawMode; break;
				case ListView view: original.Border = view.BorderStyle; break;
				case NumericUpDown number: original.Border = number.BorderStyle; break;
				case ComboBox combo: original.Flat = combo.FlatStyle; original.ItemDraw = combo.DrawMode; break;
				case ButtonBase button: original.Flat = button.FlatStyle; original.VisualStyleBack = button.UseVisualStyleBackColor; break;
				case GroupBox group: original.Flat = group.FlatStyle; break;
				case TabPage page: original.VisualStyleBack = page.UseVisualStyleBackColor; break;
				case TabControl tabs: original.TabDraw = tabs.DrawMode; break;
				case LinkLabel link: original.LinkColor = link.LinkColor; original.ActiveLinkColor = link.ActiveLinkColor; original.VisitedLinkColor = link.VisitedLinkColor; break;
				case DataGridView grid:
					original.Cells = grid.DefaultCellStyle.Clone(); original.Headers = grid.ColumnHeadersDefaultCellStyle.Clone();
					original.RowHeaders = grid.RowHeadersDefaultCellStyle.Clone(); original.Alternating = grid.AlternatingRowsDefaultCellStyle.Clone();
					original.GridBack = grid.BackgroundColor; original.GridLines = grid.GridColor; original.HeadersVisual = grid.EnableHeadersVisualStyles;
					break;
			}
			originals.Add(control, original);
			return original;
		}

		private static void Restore(Control control)
		{
			Original o;
			if (!originals.TryGetValue(control, out o)) return;
			originals.Remove(control);
			control.BackColor = o.Back; control.ForeColor = o.Fore;
			switch (control)
			{
				case TextBox box: box.BorderStyle = o.Border.Value; break;
				case RichTextBox rich: rich.BorderStyle = o.Border.Value; break;
				case ListBox list: list.BorderStyle = o.Border.Value; list.DrawMode = o.ItemDraw.Value; break;
				case TreeView tree: tree.BorderStyle = o.Border.Value; tree.LineColor = o.Line.Value; tree.DrawMode = o.TreeDraw.Value; break;
				case ListView view: view.BorderStyle = o.Border.Value; break;
				case NumericUpDown number: number.BorderStyle = o.Border.Value; break;
				case ComboBox combo: combo.FlatStyle = o.Flat.Value; combo.DrawMode = o.ItemDraw.Value; break;
				case ButtonBase button: button.FlatStyle = o.Flat.Value; button.UseVisualStyleBackColor = o.VisualStyleBack.Value; break;
				case GroupBox group: group.FlatStyle = o.Flat.Value; group.Paint -= PaintGroup; break;
				case TabPage page: page.UseVisualStyleBackColor = o.VisualStyleBack.Value; break;
				case TabControl tabs: tabs.DrawMode = o.TabDraw.Value; break;
				case LinkLabel link: link.LinkColor = o.LinkColor.Value; link.ActiveLinkColor = o.ActiveLinkColor.Value; link.VisitedLinkColor = o.VisitedLinkColor.Value; break;
				case ToolStrip strip: strip.RenderMode = ToolStripRenderMode.System; break;
				case DataGridView grid:
					grid.DefaultCellStyle = o.Cells; grid.ColumnHeadersDefaultCellStyle = o.Headers; grid.RowHeadersDefaultCellStyle = o.RowHeaders;
					grid.AlternatingRowsDefaultCellStyle = o.Alternating; grid.BackgroundColor = o.GridBack; grid.GridColor = o.GridLines; grid.EnableHeadersVisualStyles = o.HeadersVisual;
					break;
			}
		}

		private static bool IsSystemBack(Color c) => c == SystemColors.Control || c == SystemColors.ButtonFace || c == SystemColors.Window || c == SystemColors.Info;

		private static void StyleOne(Control control, ThemePalette p)
		{
			if (!p.IsDark) { Restore(control); return; }
			switch (control)
			{
				case Form form:
					Remember(form); form.BackColor = p.Background; form.ForeColor = p.Text; break;
				case TabPage page:
					Remember(page); page.UseVisualStyleBackColor = false; page.BackColor = p.Background; page.ForeColor = p.Text; break;
				case TabControl tabs:
					Remember(tabs); tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
					tabs.DrawItem -= DrawTab; tabs.DrawItem += DrawTab; break;
				case GroupBox group:
					Remember(group); group.ForeColor = p.Text; group.FlatStyle = FlatStyle.Flat;
					group.Paint -= PaintGroup; group.Paint += PaintGroup; group.Invalidate(); break;
				case LinkLabel link:
					Remember(link); link.LinkColor = p.Blue; link.ActiveLinkColor = p.Teal; link.VisitedLinkColor = p.Violet; link.ForeColor = p.Text; break;
				case Label label:
					if (label.ForeColor == SystemColors.ControlText || label.ForeColor == SystemColors.WindowText) { Remember(label); label.ForeColor = p.Text; }
					if (IsSystemBack(label.BackColor)) { Remember(label); label.BackColor = p.Background; }
					break;
				case ButtonBase button when !(button is Controls.Debugger.DarkButton):
					Remember(button);
					if (button is CheckBox || button is RadioButton) { button.ForeColor = p.Text; if (IsSystemBack(button.BackColor)) button.BackColor = Color.Transparent; break; }
					button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false; button.BackColor = p.Raised; button.ForeColor = p.Text;
					button.FlatAppearance.BorderColor = p.Border; button.FlatAppearance.MouseOverBackColor = p.Hover; button.FlatAppearance.MouseDownBackColor = p.Border;
					break;
				case TextBox box:
					Remember(box); box.BackColor = box.ReadOnly ? p.Panel : p.Raised; box.ForeColor = p.Text;
					if (box.BorderStyle == BorderStyle.Fixed3D) box.BorderStyle = BorderStyle.FixedSingle;
					break;
				case RichTextBox rich:
					Remember(rich); rich.BackColor = p.Panel; rich.ForeColor = p.Text;
					if (rich.BorderStyle == BorderStyle.Fixed3D) rich.BorderStyle = BorderStyle.FixedSingle;
					break;
				case NumericUpDown number:
					Remember(number); number.BackColor = p.Raised; number.ForeColor = p.Text; number.BorderStyle = BorderStyle.FixedSingle; break;
				case ComboBox combo:
					Remember(combo); combo.BackColor = p.Raised; combo.ForeColor = p.Text; combo.FlatStyle = FlatStyle.Flat;
					if (combo.DropDownStyle == ComboBoxStyle.DropDownList && combo.DrawMode == DrawMode.Normal)
					{
						combo.DrawMode = DrawMode.OwnerDrawFixed;
						combo.DrawItem -= DrawComboItem; combo.DrawItem += DrawComboItem;
					}
					break;
				case ListBox list:
					Remember(list); list.BackColor = p.Panel; list.ForeColor = p.Text;
					if (list.BorderStyle == BorderStyle.Fixed3D) list.BorderStyle = BorderStyle.FixedSingle;
					break;
				case TreeView tree:
					Remember(tree); tree.BackColor = p.Panel; tree.ForeColor = p.Text; tree.LineColor = p.Faint;
					if (tree.DrawMode == TreeViewDrawMode.Normal) { tree.DrawMode = TreeViewDrawMode.OwnerDrawText; tree.DrawNode -= DrawNode; tree.DrawNode += DrawNode; }
					if (tree.BorderStyle == BorderStyle.Fixed3D) tree.BorderStyle = BorderStyle.FixedSingle;
					break;
				case ListView view:
					Remember(view); view.BackColor = p.Panel; view.ForeColor = p.Text;
					if (view.BorderStyle == BorderStyle.Fixed3D) view.BorderStyle = BorderStyle.FixedSingle;
					break;
				case DataGridView grid:
					Remember(grid); StyleGrid(grid, p); break;
				case SplitContainer split:
					if (IsSystemBack(split.BackColor)) { Remember(split); split.BackColor = p.Border; }
					break;
				case ToolStrip strip:
					// The theme renderer paints menus, toolbars and status bars; hosted text boxes and combos are styled through the strip.
					if (strip.RenderMode == ToolStripRenderMode.System) { Remember(strip); strip.Renderer = ToolStripManager.Renderer; }
					strip.Invalidate();
					break;
				default:
					if (control is ScrollableControl && IsSystemBack(control.BackColor)) { Remember(control); control.BackColor = p.Background; }
					if (control.ForeColor == SystemColors.ControlText) { Remember(control); control.ForeColor = p.Text; }
					break;
			}
		}

		/// <summary>Dark cells for any grid, keeping its fonts and layout.</summary>
		public static void StyleGrid(DataGridView grid, ThemePalette p)
		{
			grid.BackgroundColor = p.Panel;
			grid.GridColor = p.Border;
			grid.EnableHeadersVisualStyles = false;
			var cells = grid.DefaultCellStyle.Clone();
			cells.BackColor = p.Panel; cells.ForeColor = p.Text;
			cells.SelectionBackColor = Mix(p.Panel, p.Blue, .3f); cells.SelectionForeColor = p.Text;
			grid.DefaultCellStyle = cells;
			var headers = grid.ColumnHeadersDefaultCellStyle.Clone();
			headers.BackColor = p.Raised; headers.ForeColor = p.Muted; headers.SelectionBackColor = p.Raised; headers.SelectionForeColor = p.Muted;
			grid.ColumnHeadersDefaultCellStyle = headers;
			var rows = grid.RowHeadersDefaultCellStyle.Clone();
			rows.BackColor = p.Raised; rows.ForeColor = p.Muted; rows.SelectionBackColor = Mix(p.Raised, p.Blue, .3f); rows.SelectionForeColor = p.Text;
			grid.RowHeadersDefaultCellStyle = rows;
			var alternating = grid.AlternatingRowsDefaultCellStyle.Clone();
			if (!alternating.BackColor.IsEmpty) alternating.BackColor = Mix(p.Panel, p.Raised, .45f);
			grid.AlternatingRowsDefaultCellStyle = alternating;
		}

		public static Color Mix(Color a, Color b, float amount)
		{
			amount = Math.Max(0, Math.Min(1, amount));
			return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
		}

		// GroupBox borders are drawn in system colours on every platform; Dark paints its own border and caption.
		private static void PaintGroup(object sender, PaintEventArgs e)
		{
			var group = (GroupBox)sender;
			var p = Current;
			if (!p.IsDark) return;
			e.Graphics.Clear(group.BackColor);
			var caption = TextRenderer.MeasureText(group.Text ?? "", group.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
			int top = caption.Height / 2;
			using (var pen = new Pen(p.Border)) e.Graphics.DrawRectangle(pen, 0, top, group.Width - 1, group.Height - top - 1);
			if (!string.IsNullOrEmpty(group.Text))
			{
				var label = new Rectangle(8, 0, caption.Width + 6, caption.Height);
				using (var back = new SolidBrush(group.BackColor)) e.Graphics.FillRectangle(back, label);
				TextRenderer.DrawText(e.Graphics, group.Text, group.Font, new Point(label.X + 3, 0), group.Enabled ? p.Muted : p.Faint, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
			}
		}

		// Tree selection is drawn in system colours (white on Mono); Dark draws the node text and selection itself.
		private static void DrawNode(object sender, DrawTreeNodeEventArgs e)
		{
			var tree = (TreeView)sender;
			var p = Current;
			if (!p.IsDark || e.Node == null || e.Bounds.IsEmpty) { e.DrawDefault = true; return; }
			bool selected = (e.State & TreeNodeStates.Selected) != 0;
			var bounds = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, TextRenderer.MeasureText(e.Node.Text, e.Node.NodeFont ?? tree.Font).Width + 4), e.Bounds.Height);
			using (var back = new SolidBrush(selected ? Mix(p.Panel, p.Blue, tree.Focused ? .4f : .22f) : tree.BackColor)) e.Graphics.FillRectangle(back, bounds);
			TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? tree.Font, bounds, e.Node.ForeColor.IsEmpty || e.Node.ForeColor == SystemColors.WindowText ? p.Text : e.Node.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
		}

		private static void DrawTab(object sender, DrawItemEventArgs e)
		{
			var tabs = (TabControl)sender;
			if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;
			var p = Current;
			var page = tabs.TabPages[e.Index];
			bool selected = e.Index == tabs.SelectedIndex;
			using (var back = new SolidBrush(selected ? p.Panel : p.Background)) e.Graphics.FillRectangle(back, e.Bounds);
			var bounds = e.Bounds;
			int x = bounds.X + 6;
			if (tabs.ImageList != null && page.ImageIndex >= 0 && page.ImageIndex < tabs.ImageList.Images.Count)
			{
				var image = tabs.ImageList.Images[page.ImageIndex];
				e.Graphics.DrawImage(image, x, bounds.Y + (bounds.Height - image.Height) / 2, image.Width, image.Height);
				x += image.Width + 4;
			}
			TextRenderer.DrawText(e.Graphics, page.Text, tabs.Font, new Rectangle(x, bounds.Y, bounds.Right - x - 4, bounds.Height), selected ? p.Text : p.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
			if (selected) using (var accent = new SolidBrush(p.Teal)) e.Graphics.FillRectangle(accent, bounds.X + 4, bounds.Bottom - 3, bounds.Width - 8, 2);
		}

		private static void DrawComboItem(object sender, DrawItemEventArgs e)
		{
			var combo = (ComboBox)sender;
			var p = Current;
			bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
			using (var back = new SolidBrush(selected ? Mix(p.Raised, p.Blue, .35f) : p.Raised)) e.Graphics.FillRectangle(back, e.Bounds);
			if (e.Index < 0) return;
			TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height), combo.Enabled ? p.Text : p.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

		[DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

		/// <summary>
		/// On Windows 10/11: a dark or light title bar, scroll bars and list/tree/combo chrome to match the theme. A no-op elsewhere.
		/// </summary>
		public static void SetChrome(Form form)
		{
			if (NativeMethods.IsUnix()) return;
			EventHandler apply = (s, e) =>
			{
				bool dark = Current.IsDark;
				try
				{
					int value = dark ? 1 : 0;
					if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0) DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
				}
				catch (Exception) { }
				ThemeChildren(form, dark);
			};
			if (form.IsHandleCreated) apply(form, EventArgs.Empty); else form.HandleCreated += apply;
		}

		private static void ThemeChildren(Control root, bool dark)
		{
			foreach (Control control in root.Controls)
			{
				string theme = null;
				if (control is ComboBox) theme = dark ? "DarkMode_CFD" : "CFD";
				else if (control is TextBoxBase || control is ListBox || control is TreeView || control is ListView || control is DataGridView || control is ScrollBar || control is NumericUpDown || control is ScrollableControl) theme = dark ? "DarkMode_Explorer" : "Explorer";
				if (theme != null)
				{
					var target = control; var name = theme;
					EventHandler set = (s, e) => { try { SetWindowTheme(target.Handle, name, null); } catch (Exception) { } };
					if (target.IsHandleCreated) set(target, EventArgs.Empty); else target.HandleCreated += set;
				}
				ThemeChildren(control, dark);
			}
		}
	}
}
