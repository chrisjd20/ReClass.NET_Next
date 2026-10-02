using System;
using System.Windows.Forms;
using ReClassNET.UI;

namespace ReClassNET.Forms
{
	public class IconForm : Form
	{
		public IconForm()
		{
			Icon = Properties.Resources.ReClassNet;
		}

		// Every ReClass window derives from this, so this is where the Light/Dark theme reaches all of them.
		protected override void OnLoad(EventArgs e)
		{
			AppTheme.Apply(this);
			base.OnLoad(e);
		}
	}
}
