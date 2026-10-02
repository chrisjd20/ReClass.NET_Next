using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ReClassNET.UI;
using ReClassNET.Util.Conversion;

namespace ReClassNET.Controls
{
	public class HotSpotTextBox : TextBox
	{
		private HotSpot currentHotSpot;

		private FontEx font;
		private int minimumWidth;
		private readonly Label editHint = new Label { AutoSize = true, BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText, Padding = new Padding(3) };
		private Guid editSession;
		private EndianBitConverter editConverter;
		private string initialText;

		[Browsable(false)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public new FontEx Font
		{
			get => font;
			set
			{
				if (font != value)
				{
					font = value;

					base.Font = font.Font;
				}
			}
		}

		public event HotSpotTextBoxCommitEventHandler Committed;

		public HotSpotTextBox()
		{
			BorderStyle = BorderStyle.None;
		}

		#region Events

		protected override void OnParentChanged(EventArgs e)
		{
			base.OnParentChanged(e);
			if (editHint == null || editHint.IsDisposed) return;
			editHint.Parent = Parent;
			editHint.Visible = false;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing) editHint.Dispose();
			base.Dispose(disposing);
		}

		private void ShowEditHint(string text)
		{
			editHint.Text = text;
			editHint.MaximumSize = new Size(Math.Max(100, Parent?.ClientSize.Width ?? 400), 0);
			editHint.Location = new Point(Math.Max(0, Math.Min(Left, (Parent?.ClientSize.Width ?? Right) - editHint.Width)),
				Bottom + editHint.Height <= (Parent?.ClientSize.Height ?? int.MaxValue) ? Bottom : Math.Max(0, Top - editHint.Height));
			editHint.Visible = Visible;
			editHint.BringToFront();
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (editHint == null || editHint.IsDisposed) return;
			editHint.Visible = false;

			if (Visible)
			{
				BackColor = Program.Settings.BackgroundColor;
				// The edit box sits on the memory view's background, so its text must contrast with it in either theme.
				ForeColor = UI.AppTheme.Current.IsDark ? UI.AppTheme.Current.Text : SystemColors.WindowText;
				editHint.BackColor = UI.AppTheme.Current.IsDark ? UI.AppTheme.Current.Raised : SystemColors.Info;
				editHint.ForeColor = UI.AppTheme.Current.IsDark ? UI.AppTheme.Current.Text : SystemColors.InfoText;

				if (currentHotSpot != null)
				{
					Focus();
					Select(0, TextLength);
					if (currentHotSpot.NumericEdit != null)
						ShowEditHint(currentHotSpot.NumericEdit.Description + " · Enter to write · Esc to cancel");
				}
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Enter)
			{
				OnCommit();

				e.Handled = true;
				e.SuppressKeyPress = true;
			}
			else if (e.KeyCode == Keys.Escape)
			{
				Hide();
				Parent?.Focus();
				e.Handled = true;
				e.SuppressKeyPress = true;
			}

			base.OnKeyDown(e);
		}

		protected override void OnTextChanged(EventArgs e)
		{
			base.OnTextChanged(e);

			var w = (TextLength + 1) * font.Width;
			if (w > minimumWidth)
			{
				Width = w;
			}
		}

		private void OnCommit()
		{
			if (currentHotSpot == null) return;
			if (ReadOnly)
			{
				Hide();
				Parent?.Focus();
				return;
			}
			if (currentHotSpot.NumericEdit != null && Text.Trim() != initialText)
			{
				try
				{
					var process = currentHotSpot.Process;
					if (!process.IsValid || process.SessionIdentity != editSession || process.BitConverter != editConverter)
					{
						ShowEditHint("The process or byte order changed. Cancel and reopen this value.");
						return;
					}
					if (!currentHotSpot.NumericEdit.TryEncode(Text, editConverter, out var bytes, out var error))
					{
						ShowEditHint(error);
						return;
					}
					if (!process.WriteRemoteMemory(currentHotSpot.Address, bytes))
					{
						ShowEditHint("Memory write failed. Check the address and process permissions.");
						return;
					}
				}
				catch (Exception ex)
				{
					ShowEditHint("Could not write value: " + ex.Message);
					return;
				}
			}
			Visible = false;

			currentHotSpot.Text = Text.Trim();

			Committed?.Invoke(this, new HotSpotTextBoxCommitEventArgs(currentHotSpot));
		}

		#endregion

		public void ShowOnHotSpot(HotSpot hotSpot)
		{
			currentHotSpot = hotSpot;

			if (hotSpot == null)
			{
				Visible = false;

				return;
			}

			AlignToRect(hotSpot.Rect);

			Text = hotSpot.Text.Trim();
			ReadOnly = hotSpot.Id == HotSpot.ReadOnlyId;
			if (hotSpot.NumericEdit != null)
			{
				try
				{
					editSession = hotSpot.Process.SessionIdentity;
					editConverter = hotSpot.Process.BitConverter;
					var bytes = new byte[hotSpot.NumericEdit.ByteWidth];
					if (!hotSpot.Process.ReadRemoteMemoryIntoBuffer(hotSpot.Address, ref bytes))
						throw new InvalidOperationException("Could not read this value from the process.");
					Text = hotSpot.NumericEdit.Format(bytes, editConverter);
					ReadOnly = false;
				}
				catch (Exception ex)
				{
					Hide();
					ReClassNET.UI.ThemedMessageBox.Show(Parent, ex.Message, "Edit numeric value", MessageBoxButtons.OK, MessageBoxIcon.Error);
					return;
				}
			}
			initialText = Text.Trim();

			Visible = true;
			Focus();
			SelectAll();
			if (hotSpot.NumericEdit != null) ShowEditHint(hotSpot.NumericEdit.Description + " · Enter to write · Esc to cancel");
		}

		private void AlignToRect(Rectangle rect)
		{
			SetBounds(rect.Left + 2, rect.Top, rect.Width, rect.Height);

			minimumWidth = rect.Width;
		}
	}

	public delegate void HotSpotTextBoxCommitEventHandler(object sender, HotSpotTextBoxCommitEventArgs e);

	public class HotSpotTextBoxCommitEventArgs : EventArgs
	{
		public HotSpot HotSpot { get; set; }

		public HotSpotTextBoxCommitEventArgs(HotSpot hotSpot)
		{
			HotSpot = hotSpot;
		}
	}
}
