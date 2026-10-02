using System.Collections.Generic;
using System.Linq;
using NFluent;
using ReClassNET.AssemblyEditing;
using ReClassNET.UI.Debugger;
using Xunit;

namespace ReClass.NET_Tests.UI
{
	public class DebuggerPresentersTest
	{
		[Theory]
		[InlineData("Confirmed access to watched range", true, HitKind.Confirmed)]
		[InlineData("Unlikely: its memory operand doesn't touch the watched data", false, HitKind.Unlikely)]
		[InlineData("After event; preceding candidates need confirmation", false, HitKind.Candidate)]
		[InlineData("Observed", false, HitKind.Observed)]
		[InlineData("Attempted", false, HitKind.Attempted)]
		[InlineData("Writer unavailable", false, HitKind.Unavailable)]
		public void HitKindFollowsAttribution(string status, bool confirmed, HitKind expected)
		{
			Check.That(HitKinds.From(status, confirmed)).IsEqualTo(expected);
		}

		[Fact]
		public void CoachWaitsForTheFirstWrite()
		{
			var advice = WatchCoach.For(new WatchCoachState { WriteOnly = true, Collecting = true });

			Check.That(advice.Message).StartsWith("Waiting for the first write");
			Check.That(advice.Button).IsNull();
		}

		[Fact]
		public void CoachSuggestsConfirmForASelectedCandidate()
		{
			var advice = WatchCoach.For(new WatchCoachState { WriteOnly = true, Collecting = true, Rows = 2, Unlikely = 1, Candidates = 1, Selected = HitKind.Candidate });

			Check.That(advice.Button).IsEqualTo("Confirm next execution");
		}

		[Fact]
		public void CoachWarnsAboutAnUnlikelySelection()
		{
			var advice = WatchCoach.For(new WatchCoachState { WriteOnly = true, Collecting = true, Rows = 2, Unlikely = 1, Candidates = 1, Selected = HitKind.Unlikely });

			Check.That(advice.Severity).IsEqualTo(Severity.Attention);
			Check.That(advice.Message).Contains("Unlikely");
		}

		[Fact]
		public void CoachSendsAConfirmedRowToTheEditor()
		{
			var advice = WatchCoach.For(new WatchCoachState { WriteOnly = true, Collecting = true, Rows = 2, Confirmed = 1, Unlikely = 1, Selected = HitKind.Confirmed });

			Check.That(advice.Severity).IsEqualTo(Severity.Success);
			Check.That(advice.Button).IsEqualTo("Inspect / edit");
		}

		[Fact]
		public void CoachPrefersResumeWhilePaused()
		{
			var advice = WatchCoach.For(new WatchCoachState { Paused = true, Rows = 1, Confirmed = 1, Selected = HitKind.Confirmed });

			Check.That(advice.Button).IsEqualTo("Resume (F5)");
		}

		[Fact]
		public void PatchStepsFollowTheEditorFlow()
		{
			Check.That(PatchSteps.Current(new PatchStepState())).IsEqualTo(PatchStep.Inspect);
			Check.That(PatchSteps.Current(new PatchStepState { OriginalsLoaded = true })).IsEqualTo(PatchStep.Edit);
			Check.That(PatchSteps.Current(new PatchStepState { OriginalsLoaded = true, Edited = true })).IsEqualTo(PatchStep.Preview);
			Check.That(PatchSteps.Current(new PatchStepState { OriginalsLoaded = true, Edited = true, PreviewCurrent = true })).IsEqualTo(PatchStep.Apply);
			Check.That(PatchSteps.Current(new PatchStepState { OriginalsLoaded = true, Edited = true, Active = true })).IsEqualTo(PatchStep.Restore);
			Check.That(PatchSteps.Current(new PatchStepState { OriginalsLoaded = true, Restored = true })).IsEqualTo(PatchStep.Done);
		}

		[Fact]
		public void ByteDiffMarksNopPaddingAndChanges()
		{
			var cells = ByteDiff.Build(new byte[] { 0x83, 0x28, 0x0A }, new byte[] { 0xFF, 0x00, 0x90 }, 1);

			Check.That(cells.Select(c => c.Kind)).ContainsExactly(ByteCellKind.Changed, ByteCellKind.Changed, ByteCellKind.Padding);
		}

		[Fact]
		public void ByteDiffKeepsUnchangedBytesAndMarksLengthChanges()
		{
			Check.That(ByteDiff.Build(new byte[] { 0xFF, 0x08 }, new byte[] { 0xFF, 0x00 }, 0).Select(c => c.Kind)).ContainsExactly(ByteCellKind.Same, ByteCellKind.Changed);
			Check.That(ByteDiff.Build(new byte[] { 0x90 }, new byte[] { 0x90, 0x90 }, 0).Last().Kind).IsEqualTo(ByteCellKind.Added);
			Check.That(ByteDiff.Build(new byte[] { 0x90, 0x90 }, new byte[] { 0x90 }, 0).Last().Kind).IsEqualTo(ByteCellKind.Removed);
		}

		[Fact]
		public void RegistersAreOrderedAndHighlighted()
		{
			var registers = new Dictionary<string, ulong> { ["r9"] = 0x1A2B, ["rax"] = 0x1000, ["rip"] = 0x7000, ["rcx"] = 0x1002 };
			var previous = new Dictionary<string, ulong> { ["r9"] = 0x1A2B, ["rax"] = 0x2000, ["rip"] = 0x7000, ["rcx"] = 0x1002 };

			var views = RegisterHighlights.Build(registers, new[] { "rax" }, 0x1000, 4, previous);

			Check.That(views.Select(v => v.Name)).ContainsExactly("rax", "rcx", "r9", "rip");
			var rax = views.Single(v => v.Name == "rax");
			Check.That(rax.Used).IsTrue();
			Check.That(rax.PointsAtWatched).IsTrue();
			Check.That(rax.Changed).IsTrue();
			Check.That(views.Single(v => v.Name == "rcx").PointsAtWatched).IsTrue();
			Check.That(views.Single(v => v.Name == "r9").PointsAtWatched).IsFalse();
			Check.That(views.Single(v => v.Name == "rip").IsInstructionPointer).IsTrue();
		}

		[Theory]
		[InlineData(3, 3, SizeFit.Exact)]
		[InlineData(3, 2, SizeFit.Padded)]
		[InlineData(2, 5, SizeFit.TooLong)]
		[InlineData(0, 2, SizeFit.Unknown)]
		public void PatchSizeFit(int original, int replacement, SizeFit expected)
		{
			Check.That(PatchSize.Classify(original, replacement)).IsEqualTo(expected);
		}

		[Fact]
		public void DecodedInstructionsAreTokenizedLikeReClassPrintsThem()
		{
			var record = new InstructionService().Decode(new byte[] { 0x83, 0x28, 0x0A }, 0x1000).Instructions.Single();

			var tokens = AsmTokens.Tokenize(record);

			Check.That(string.Concat(tokens.Select(t => t.Text))).IsEqualTo(record.Text);
			Check.That(tokens.First().Kind).IsEqualTo(AsmTokenKind.Mnemonic);
			Check.That(tokens.Any(t => t.Kind == AsmTokenKind.Register && t.Text == "rax")).IsTrue();
			Check.That(tokens.Any(t => t.Kind == AsmTokenKind.Number && t.Text == "0xA")).IsTrue();
			Check.That(tokens.Any(t => t.Kind == AsmTokenKind.Keyword && t.Text == "dword")).IsTrue();
		}

		[Fact]
		public void FreeTextIsTokenizedBestEffort()
		{
			var tokens = AsmTokens.Tokenize("add dword [rax - 0x4], 5");

			Check.That(tokens.First().Kind).IsEqualTo(AsmTokenKind.Mnemonic);
			Check.That(tokens.Single(t => t.Text == "rax").Kind).IsEqualTo(AsmTokenKind.Register);
			Check.That(tokens.Single(t => t.Text == "0x4").Kind).IsEqualTo(AsmTokenKind.Number);
			Check.That(tokens.Single(t => t.Text == "dword").Kind).IsEqualTo(AsmTokenKind.Keyword);
		}

		[Theory]
		[InlineData("Patch applied and verified.", Severity.Success)]
		[InlineData("Cannot save invalid assembly: x", Severity.Danger)]
		[InlineData("Collection stopped.", Severity.Info)]
		[InlineData("Operation cancelled.", Severity.Attention)]
		public void StatusMessagesAreClassified(string message, Severity expected)
		{
			Check.That(DebuggerTheme.Classify(message)).IsEqualTo(expected);
		}
	
		[Fact]
		public void OnlyAddressesCanBeFollowedAndEveryTileReadsInDecimal()
		{
			var registers = new Dictionary<string, ulong> { { "rax", 0x7FF600001000 }, { "r9", 0x26E0 }, { "rflags", 0x246 } };

			var views = RegisterHighlights.Build(registers, null, 0, 0, null, value => value == 0x7FF600001000);

			var rax = views.Single(v => v.Name == "rax");
			var r9 = views.Single(v => v.Name == "r9");
			Check.That(rax.LooksLikeAddress).IsTrue();
			Check.That(r9.LooksLikeAddress).IsFalse();
			Check.That(views.Single(v => v.Name == "rflags").LooksLikeAddress).IsFalse();
			Check.That(r9.Describe()).Contains("0x26E0 = 9,952 in decimal").And.Contains("plain number");
			Check.That(rax.Describe()).Contains("Click to open");
		}

		[Fact]
		public void EndedWatchWindowSaysSoBeforeAnythingElse()
		{
			var advice = WatchCoach.For(new WatchCoachState { Ended = true, Paused = true, Rows = 2, Confirmed = 1 });

			Check.That(advice.Severity).IsEqualTo(Severity.Danger);
			Check.That(advice.Message).Contains("ended");
			Check.That(advice.Button).IsNull();
		}

		[Theory]
		[InlineData(15, 30)]
		[InlineData(30, 42)]
		[InlineData(45, 57)]
		[InlineData(200, 57)]
		public void StatusLineGrowsToThreeLines(int textHeight, int expected)
		{
			Check.That(StatusLayout.HeightFor(textHeight, 15, 30, 12)).IsEqualTo(expected);
		}
	}
}
