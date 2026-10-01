using System;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using ReClassNET.Controls;
using ReClassNET.Extensions;
using ReClassNET.UI;

namespace ReClassNET.Nodes
{
	public abstract class BaseHexCommentNode : BaseHexNode
	{
		private int AddNumericPreview(DrawContext view, int x, int y, string text, NumericPreviewKind kind, int width)
		{
			if (MemorySize < width || (width != 4 && width != 8))
				return AddText(view, x, y, view.Settings.ValueColor, HotSpot.ReadOnlyId, text) + view.Font.Width;
			var count = view.HotSpots.Count;
			var end = AddText(view, x, y, view.Settings.ValueColor, 200 + (int)kind, text);
			if (view.HotSpots.Count > count) view.HotSpots.Last().NumericEdit = new NumericPreviewEdit(kind, width);
			return end + view.Font.Width;
		}

		protected int AddComment(DrawContext view, int x, int y, float fvalue, IntPtr ivalue, UIntPtr uvalue)
		{
			Contract.Requires(view != null);

			if (view.Settings.ShowCommentFloat)
			{
				x = AddNumericPreview(view, x, y, fvalue > -999999.0f && fvalue < 999999.0f ? fvalue.ToString("0.000") : "#####", NumericPreviewKind.Float, 4);
			}
			if (view.Settings.ShowCommentInteger)
			{
				if (ivalue == IntPtr.Zero)
				{
					x = AddNumericPreview(view, x, y, "0", NumericPreviewKind.SignedInteger, MemorySize);
				}
				else
				{
					x = AddNumericPreview(view, x, y, ivalue.ToInt64().ToString(), NumericPreviewKind.SignedInteger, MemorySize);
					x = AddNumericPreview(view, x, y, $"0x{uvalue.ToUInt64():X}", NumericPreviewKind.HexInteger, MemorySize);
				}
			}

			if (ivalue != IntPtr.Zero)
			{
				var namedAddress = view.Process.GetNamedAddress(ivalue);
				if (!string.IsNullOrEmpty(namedAddress))
				{
					if (view.Settings.ShowCommentPointer)
					{
						x = AddText(view, x, y, view.Settings.OffsetColor, HotSpot.NoneId, "->") + view.Font.Width;
						x = AddText(view, x, y, view.Settings.OffsetColor, HotSpot.ReadOnlyId, namedAddress) + view.Font.Width;
					}

					if (view.Settings.ShowCommentRtti)
					{
						var rtti = view.Process.ReadRemoteRuntimeTypeInformation(ivalue);
						if (!string.IsNullOrEmpty(rtti))
						{
							x = AddText(view, x, y, view.Settings.OffsetColor, HotSpot.ReadOnlyId, rtti) + view.Font.Width;
						}
					}

					if (view.Settings.ShowCommentSymbol)
					{
						var module = view.Process.GetModuleToPointer(ivalue);
						if (module != null)
						{
							var symbols = view.Process.Symbols.GetSymbolsForModule(module);
							var symbol = symbols?.GetSymbolString(ivalue, module);
							if (!string.IsNullOrEmpty(symbol))
							{
								x = AddText(view, x, y, view.Settings.OffsetColor, HotSpot.ReadOnlyId, symbol) + view.Font.Width;
							}
						}
					}

					if (view.Settings.ShowCommentString)
					{
						var data = view.Process.ReadRemoteMemory(ivalue, 64);

						var isWideString = false;
						string text = null;

						// First check if it could be an UTF8 string and if not try UTF16.
						if (data.Take(IntPtr.Size).InterpretAsSingleByteCharacter().IsPrintableData())
						{
							text = new string(Encoding.UTF8.GetChars(data).TakeWhile(c => c != 0).ToArray());
						}
						else if (data.Take(IntPtr.Size * 2).InterpretAsDoubleByteCharacter().IsPrintableData())
						{
							isWideString = true;

							text = new string(Encoding.Unicode.GetChars(data).TakeWhile(c => c != 0).ToArray());
						}

						if (text != null)
						{
							x = AddText(view, x, y, view.Settings.TextColor, HotSpot.NoneId, isWideString ? "L'" : "'");
							x = AddText(view, x, y, view.Settings.TextColor, HotSpot.ReadOnlyId, text);
							x = AddText(view, x, y, view.Settings.TextColor, HotSpot.NoneId, "'") + view.Font.Width;
						}
					}

					if (view.Settings.ShowCommentPluginInfo)
					{
						var nodeAddress = view.Address + Offset;

						foreach (var reader in NodeInfoReader)
						{
							var info = reader.ReadNodeInfo(this, view.Process, view.Memory, nodeAddress, ivalue);
							if (info != null)
							{
								x = AddText(view, x, y, view.Settings.PluginColor, HotSpot.ReadOnlyId, info) + view.Font.Width;
							}
						}
					}
				}
			}

			return x;
		}
	}
}
