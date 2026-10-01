using System;
using System.Globalization;
using ReClassNET.Util.Conversion;

namespace ReClassNET.UI
{
	public enum NumericPreviewKind { Float, Double, SignedInteger, HexInteger }

	// A preview's interpretation is explicit; its text is never used to infer a type.
	public sealed class NumericPreviewEdit
	{
		public NumericPreviewKind Kind { get; }
		public int ByteWidth { get; }
		public string Description => $"{(Kind == NumericPreviewKind.Float ? "Float32" : Kind == NumericPreviewKind.Double ? "Double" : Kind == NumericPreviewKind.SignedInteger ? "Int" + ByteWidth * 8 : "UInt" + ByteWidth * 8 + " hex")} · {ByteWidth} bytes";

		public NumericPreviewEdit(NumericPreviewKind kind, int byteWidth)
		{
			if ((byteWidth != 4 && byteWidth != 8) ||
				(kind == NumericPreviewKind.Float && byteWidth != 4) ||
				(kind == NumericPreviewKind.Double && byteWidth != 8) || !Enum.IsDefined(typeof(NumericPreviewKind), kind))
				throw new ArgumentOutOfRangeException(nameof(byteWidth));
			Kind = kind;
			ByteWidth = byteWidth;
		}

		public string Format(byte[] bytes, EndianBitConverter converter)
		{
			switch (Kind)
			{
				case NumericPreviewKind.Float: return converter.ToSingle(bytes, 0).ToString("G9", CultureInfo.CurrentCulture);
				case NumericPreviewKind.Double: return converter.ToDouble(bytes, 0).ToString("G17", CultureInfo.CurrentCulture);
				case NumericPreviewKind.SignedInteger: return (ByteWidth == 4 ? converter.ToInt32(bytes, 0) : converter.ToInt64(bytes, 0)).ToString(CultureInfo.CurrentCulture);
				default: return "0x" + (ByteWidth == 4 ? converter.ToUInt32(bytes, 0) : converter.ToUInt64(bytes, 0)).ToString("X", CultureInfo.InvariantCulture);
			}
		}

		public bool TryEncode(string text, EndianBitConverter converter, out byte[] bytes, out string error)
		{
			bytes = null;
			error = "Enter a valid " + Description + " value within its range.";
			text = (text ?? string.Empty).Trim();
			switch (Kind)
			{
				case NumericPreviewKind.Float:
					if (!(float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var f) || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) || float.IsNaN(f) || float.IsInfinity(f)) return false;
					bytes = converter.GetBytes(f); break;
				case NumericPreviewKind.Double:
					if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) || double.IsNaN(d) || double.IsInfinity(d)) return false;
					bytes = converter.GetBytes(d); break;
				case NumericPreviewKind.SignedInteger:
					if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var signed) || (ByteWidth == 4 && (signed < int.MinValue || signed > int.MaxValue))) return false;
					bytes = ByteWidth == 4 ? converter.GetBytes((int)signed) : converter.GetBytes(signed); break;
				case NumericPreviewKind.HexInteger:
					if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
					if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unsigned) || (ByteWidth == 4 && unsigned > uint.MaxValue)) return false;
					bytes = ByteWidth == 4 ? converter.GetBytes((uint)unsigned) : converter.GetBytes(unsigned); break;
			}
			error = null;
			return true;
		}
	}
}
