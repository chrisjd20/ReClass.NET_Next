using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Threading;
using ReClassNET.MemoryScanner.Comparer;

namespace ReClassNET.MemoryScanner
{
	internal class SimpleScannerWorker : IScannerWorker
	{
		private readonly ScanSettings settings;
		private readonly ISimpleScanComparer comparer;

		public SimpleScannerWorker(ScanSettings settings, ISimpleScanComparer comparer)
		{
			Contract.Requires(settings != null);
			Contract.Requires(comparer != null);

			this.settings = settings;
			this.comparer = comparer;
		}

		public IList<ScanResult> Search(byte[] data, int count, CancellationToken ct)
		{
			return Search(data, count, ct, 0, count);
		}

		internal IList<ScanResult> Search(byte[] data, int count, CancellationToken ct, int firstIndex, int searchLength)
		{
			Contract.Requires(data != null);

			var results = new List<ScanResult>();

			var endIndex = Math.Min(count - comparer.ValueSize, searchLength - 1);

			// Include the last complete value. Overlap bytes only complete values whose
			// start belongs to this chunk. A long counter avoids wrapping on large steps.
			for (long i = firstIndex; i <= endIndex; i += settings.FastScanAlignment)
			{
				if (ct.IsCancellationRequested)
				{
					break;
				}

				if (comparer.Compare(data, (int)i, out var result))
				{
					result.Address = (IntPtr)i;

					results.Add(result);
				}
			}

			return results;
		}

		public IList<ScanResult> Search(byte[] data, int count, IEnumerable<ScanResult> previousResults, CancellationToken ct)
		{
			Contract.Requires(data != null);
			Contract.Requires(previousResults != null);

			var results = new List<ScanResult>();

			var endIndex = count - comparer.ValueSize;

			foreach (var previousResult in previousResults)
			{
				if (ct.IsCancellationRequested)
				{
					break;
				}

				var offset = previousResult.Address.ToInt32();
				if (offset <= endIndex)
				{
					if (comparer.Compare(data, offset, previousResult, out var result))
					{
						result.Address = previousResult.Address;

						results.Add(result);
					}
				}
			}

			return results;
		}
	}
}
