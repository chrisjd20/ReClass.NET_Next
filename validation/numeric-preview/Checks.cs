using System;
using System.Globalization;
using System.Linq;
using ReClassNET.UI;
using ReClassNET.Util.Conversion;

// Small window-free check of the production preview encoders; no native target needed.
class Checks
{
	static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
	static byte[] Encode(NumericPreviewKind kind, int width, string value, EndianBitConverter converter)
	{
		byte[] bytes; string error;
		Check(new NumericPreviewEdit(kind, width).TryEncode(value, converter, out bytes, out error), error);
		Check(bytes.Length == width, "Unexpected write width");
		return bytes;
	}
	static void Main()
	{
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		foreach (var converter in new EndianBitConverter[] { EndianBitConverter.Little, EndianBitConverter.Big })
		{
			var row = new byte[] { 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78 };
			var edit = Encode(NumericPreviewKind.Float, 4, "95.5", converter);
			Buffer.BlockCopy(edit, 0, row, 0, edit.Length);
			Check(converter.ToSingle(row, 0) == 95.5f && row.Skip(4).SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }), "Float overwrites adjacent bytes");
			Check(converter.ToDouble(Encode(NumericPreviewKind.Double, 8, "0.955", converter), 0) == 0.955, "Double encoding");
			Check(converter.ToInt32(Encode(NumericPreviewKind.SignedInteger, 4, "-2147483648", converter), 0) == int.MinValue, "Int32 range");
			Check(converter.ToInt64(Encode(NumericPreviewKind.SignedInteger, 8, "-9223372036854775808", converter), 0) == long.MinValue, "Int64 range");
			Check(converter.ToUInt64(Encode(NumericPreviewKind.HexInteger, 8, "0xFFFFFFFFFFFFFFFF", converter), 0) == ulong.MaxValue, "UInt64 hex");
			Check(Encode(NumericPreviewKind.HexInteger, 4, "0x12345678", converter).SequenceEqual(converter == EndianBitConverter.Little ? new byte[] { 0x78, 0x56, 0x34, 0x12 } : new byte[] { 0x12, 0x34, 0x56, 0x78 }), "Byte order");
			foreach (var kind in new[] { NumericPreviewKind.Float, NumericPreviewKind.Double })
			{
				var descriptor = new NumericPreviewEdit(kind, kind == NumericPreviewKind.Float ? 4 : 8);
				var original = Encode(kind, descriptor.ByteWidth, "1.2345678901234567", converter);
				Check(Encode(kind, descriptor.ByteWidth, descriptor.Format(original, converter), converter).SequenceEqual(original), "Full precision round trip");
			}
		}
		foreach (var sample in new[] { "bad", "", "NaN", "Infinity", "1e9999" })
		{
			byte[] bytes; string error;
			Check(!new NumericPreviewEdit(NumericPreviewKind.Float, 4).TryEncode(sample, EndianBitConverter.Little, out bytes, out error) && bytes == null, "Invalid float accepted");
			Check(!new NumericPreviewEdit(NumericPreviewKind.Double, 8).TryEncode(sample, EndianBitConverter.Little, out bytes, out error) && bytes == null, "Invalid double accepted");
		}
		byte[] ignored; string reason;
		Check(!new NumericPreviewEdit(NumericPreviewKind.SignedInteger, 4).TryEncode("2147483648", EndianBitConverter.Little, out ignored, out reason), "Int32 overflow accepted");
		Check(!new NumericPreviewEdit(NumericPreviewKind.HexInteger, 4).TryEncode("100000000", EndianBitConverter.Little, out ignored, out reason), "UInt32 overflow accepted");
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
		Check(Encode(NumericPreviewKind.Float, 4, "1,5", EndianBitConverter.Little).SequenceEqual(Encode(NumericPreviewKind.Float, 4, "1.5", EndianBitConverter.Little)), "Decimal separator handling");
		Console.WriteLine("PASS: numeric preview widths, adjacent bytes, precision, byte order, parsing and range checks.");
	}
}
