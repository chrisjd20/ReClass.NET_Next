using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ColorCode;
using ColorCode.Parsing;
using ReClassNET.CodeGenerator;
using ReClassNET.Extensions;
using ReClassNET.Logger;
using ReClassNET.Nodes;
using ReClassNET.Project;
using ReClassNET.UI;
using ReClassNET.Util.Rtf;

namespace ReClassNET.Forms
{
	public partial class CodeForm : IconForm
	{
		public CodeForm(ICodeGenerator generator, IReadOnlyList<ClassNode> classes, IReadOnlyList<EnumDescription> enums, ILogger logger)
		{
			Contract.Requires(generator != null);
			Contract.Requires(classes != null);
			Contract.Requires(enums != null);

			InitializeComponent();

			codeRichTextBox.SetInnerMargin(5, 5, 5, 5);

			var code = generator.GenerateCode(classes, enums, logger);

			var buffer = new StringBuilder(code.Length * 2);
			using (var writer = new StringWriter(buffer))
			{
				new CodeColorizer().Colorize(
					code,
					generator.Language == Language.Cpp ? Languages.Cpp : Languages.CSharp,
					new RtfFormatter(),
					ThemedStyleSheet(),
					writer
				);
			}

			codeRichTextBox.Rtf = buffer.ToString();
		}

		/// <summary>The default code colours, brightened for the Dark theme so they stay readable.</summary>
		private static IStyleSheet ThemedStyleSheet()
		{
			if (!AppTheme.Current.IsDark) return StyleSheets.Default;
			var sheet = new DarkStyleSheet();
			foreach (var style in StyleSheets.Default.Styles)
			{
				sheet.Styles.Add(new Style(style.ScopeName)
				{
					Foreground = Brighten(style.Foreground),
					Background = Color.Empty,
					Bold = style.Bold,
					Italic = style.Italic,
					CssClassName = style.CssClassName
				});
			}
			return sheet;
		}

		private static Color Brighten(Color color)
		{
			if (color.IsEmpty || color.GetBrightness() < 0.12f) return AppTheme.Current.Text;
			return color.GetBrightness() < 0.55f ? AppTheme.Mix(color, Color.White, 0.45f) : color;
		}

		private sealed class DarkStyleSheet : IStyleSheet
		{
			public StyleDictionary Styles { get; } = new StyleDictionary();
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);

			GlobalWindowManager.AddWindow(this);
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			base.OnFormClosed(e);

			GlobalWindowManager.RemoveWindow(this);
		}
	}

	internal class RtfFormatter : IFormatter
	{
		private readonly RtfBuilder builder = new RtfBuilder(RtfFont.Consolas, 20);

		public void Write(string parsedSourceCode, IList<Scope> scopes, IStyleSheet styleSheet, TextWriter textWriter)
		{
			if (scopes.Any())
			{
				builder.SetForeColor(styleSheet.Styles[scopes.First().Name].Foreground).Append(parsedSourceCode);
			}
			else
			{
				builder.Append(parsedSourceCode);
			}
		}

		public void WriteHeader(IStyleSheet styleSheet, ILanguage language, TextWriter textWriter)
		{

		}

		public void WriteFooter(IStyleSheet styleSheet, ILanguage language, TextWriter textWriter)
		{
			textWriter.Write(builder.ToString());
		}
	}
}
