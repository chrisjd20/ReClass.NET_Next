using System.Drawing;
using System.Linq;
using NFluent;
using ReClassNET;
using ReClassNET.AssemblyEditing;
using ReClassNET.UI;
using ReClassNET.UI.Debugger;
using Xunit;

namespace ReClass.NET_Tests.UI
{
	public class AppThemeTest
	{
		[Fact]
		public void MissingSettingsFileStartsDark()
		{
			var settings = new Settings();

			AppTheme.ResolveLoadedTheme(settings, false, false);

			Check.That(settings.Theme).IsEqualTo(ThemeKind.Dark);
			Check.That(AppTheme.Dark.Memory.Matches(settings)).IsTrue();
		}

		[Fact]
		public void UntouchedClassicColoursMoveToDark()
		{
			var settings = new Settings();

			AppTheme.ResolveLoadedTheme(settings, true, false);

			Check.That(settings.Theme).IsEqualTo(ThemeKind.Dark);
			Check.That(settings.BackgroundColor.ToArgb()).IsEqualTo(AppTheme.Dark.Memory.Background.ToArgb());
		}

		[Fact]
		public void CustomisedColoursStayLight()
		{
			var settings = new Settings { OffsetColor = Color.FromArgb(1, 2, 3) };

			AppTheme.ResolveLoadedTheme(settings, true, false);

			Check.That(settings.Theme).IsEqualTo(ThemeKind.Light);
			Check.That(settings.OffsetColor.ToArgb()).IsEqualTo(Color.FromArgb(1, 2, 3).ToArgb());
		}

		[Fact]
		public void StoredThemeIsKept()
		{
			var settings = new Settings { Theme = ThemeKind.Light };

			AppTheme.ResolveLoadedTheme(settings, true, true);

			Check.That(settings.Theme).IsEqualTo(ThemeKind.Light);
			Check.That(AppTheme.Light.Memory.Matches(settings)).IsTrue();
		}

		[Fact]
		public void LightPresetIsTheClassicDefaults()
		{
			var settings = new Settings();
			AppTheme.Dark.Memory.CopyTo(settings);

			AppTheme.Light.Memory.CopyTo(settings);

			Check.That(AppTheme.Light.Memory.Matches(new Settings())).IsTrue();
			Check.That(AppTheme.Light.Memory.Matches(settings)).IsTrue();
			Check.That(settings.PluginColor.ToArgb()).IsEqualTo(new Settings().PluginColor.ToArgb());
		}

		[Fact]
		public void PaletteRolesAreDistinctSoLiveSwitchingCanRemapThem()
		{
			foreach (var palette in new[] { AppTheme.Light, AppTheme.Dark })
			{
				var roles = palette.Roles.Select(c => c.ToArgb()).ToList();
				Check.That(roles.Distinct().Count()).IsEqualTo(roles.Count);
			}
		}

		[Theory]
		[InlineData(new byte[] { 0x0F, 0xB6, 0x00 }, "filling the extra upper bits with zeros")]
		[InlineData(new byte[] { 0x31, 0xC0 }, "to zero")]
		[InlineData(new byte[] { 0x31, 0xC8 }, "bitwise XOR")]
		[InlineData(new byte[] { 0x55 }, "Push")]
		[InlineData(new byte[] { 0x0F, 0x94, 0xC0 }, "condition holds, otherwise to 0")]
		public void CommonInstructionsHavePlainEnglish(byte[] bytes, string expected)
		{
			var service = new InstructionService();
			var record = service.Decode(bytes, 0x1000).Instructions.Single();

			var explanation = service.Explain(record);

			Check.That(explanation.HasTemplate).IsTrue();
			Check.That(explanation.Text).Contains(expected);
		}

		[Fact]
		public void TokenOffsetsComeFromTheWholePrefix()
		{
			var tokens = AsmTokens.Tokenize("sub dword [rax],0xA");

			// A measurer that pads every call, like GDI does: summing per-token widths would drift, prefixes do not.
			var offsets = AsmTokens.Offsets(tokens, text => text.Length * 7 + 3);

			Check.That(offsets.First()).IsEqualTo(0);
			for (int i = 1; i < offsets.Length; ++i) Check.That(offsets[i]).IsStrictlyGreaterThan(offsets[i - 1] - 1);
			Check.That(offsets.Last()).IsEqualTo("sub dword [rax],0xA".Length * 7);
		}
	}
}
