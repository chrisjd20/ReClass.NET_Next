using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReClassNET.Extensions;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner.Comparer;
using ReClassNET.Util;

namespace ReClassNET.MemoryScanner
{
	public class Scanner : IDisposable
	{
		/// <summary>
		/// Helper class for consolidated memory regions.
		/// </summary>
		private class ConsolidatedMemoryRegion
		{
			public IntPtr Address { get; set; }
			public long Size { get; set; }
		}

		private const int ScanChunkSize = 1024 * 1024;
		private const int MaximumBufferSize = 0x7FEFFFFF;

		private class ScanChunk
		{
			public IntPtr Address { get; set; }
			public int Size { get; set; }
			public int SearchLength { get; set; }
			public int FirstIndex { get; set; }
		}

		private readonly RemoteProcess process;
		private readonly CircularBuffer<ScanResultStore> stores;

		public ScanSettings Settings { get; }

		private ScanResultStore CurrentStore => stores.Head;

		/// <summary>
		/// Gets the total result count from the last scan.
		/// </summary>
		public int TotalResultCount => CurrentStore?.TotalResultCount ?? 0;

		/// <summary>
		/// Checks if the last scan can be undone.
		/// </summary>
		public bool CanUndoLastScan => stores.Count > 1;

		private bool isFirstScan;

		public Scanner(RemoteProcess process, ScanSettings settings)
		{
			Contract.Requires(process != null);
			Contract.Requires(settings != null);

			stores = new CircularBuffer<ScanResultStore>(3);

			this.process = process;
			Settings = settings;

			isFirstScan = true;
		}

		public void Dispose()
		{
			foreach (var store in stores)
			{
				store?.Dispose();
			}
			stores.Clear();
		}

		/// <summary>
		/// Retrieves the results of the last scan from the store.
		/// </summary>
		/// <returns>
		/// An enumeration of the <see cref="ScanResult"/>s of the last scan.
		/// </returns>
		public IEnumerable<ScanResult> GetResults()
		{
			Contract.Ensures(Contract.Result<IEnumerable<ScanResult>>() != null);

			if (CurrentStore == null)
			{
				return Enumerable.Empty<ScanResult>();
			}

			return CurrentStore.GetResultBlocks().SelectMany(rb => rb.Results.Select(r =>
			{
				// Convert the block offset to a real address.
				var scanResult = r.Clone();
				scanResult.Address = scanResult.Address.Add(rb.Start);
				return scanResult;
			}));
		}

		/// <summary>
		/// Restores the results of the previous scan.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if no previous results are present.</exception>
		public void UndoLastScan()
		{
			if (!CanUndoLastScan)
			{
				throw new InvalidOperationException();
			}

			var store = stores.Dequeue();
			store?.Dispose();
		}

		/// <summary>
		/// Creates a new <see cref="ScanResultStore"/> and uses the system temporary path as file location.
		/// </summary>
		/// <returns>The new <see cref="ScanResultStore"/>.</returns>
		private ScanResultStore CreateStore()
		{
			return new ScanResultStore(Settings.ValueType, Path.GetTempPath());
		}

		/// <summary>
		/// Gets a list of the sections which meet the provided scan settings.
		/// </summary>
		/// <returns>A list of searchable sections.</returns>
		private IList<Section> GetSearchableSections()
		{
			Contract.Ensures(Contract.Result<IList<Section>>() != null);

			return process.Sections
				.Where(s => !s.Protection.HasFlag(SectionProtection.Guard))
				.Where(s => s.Start.IsInRange(Settings.StartAddress, Settings.StopAddress)
							|| Settings.StartAddress.IsInRange(s.Start, s.End)
							|| Settings.StopAddress.IsInRange(s.Start, s.End))
				.Where(s => s.Type switch
				{
					SectionType.Private => Settings.ScanPrivateMemory,
					SectionType.Image => Settings.ScanImageMemory,
					SectionType.Mapped => Settings.ScanMappedMemory,
					_ => false
				})
				.Where(s =>
				{
					var isWritable = s.Protection.HasFlag(SectionProtection.Write);
					return Settings.ScanWritableMemory switch
					{
						SettingState.Yes => isWritable,
						SettingState.No => !isWritable,
						_ => true
					};
				})
				.Where(s =>
				{
					var isExecutable = s.Protection.HasFlag(SectionProtection.Execute);
					return Settings.ScanExecutableMemory switch
					{
						SettingState.Yes => isExecutable,
						SettingState.No => !isExecutable,
						_ => true
					};
				})
				.Where(s =>
				{
					var isCopyOnWrite = s.Protection.HasFlag(SectionProtection.CopyOnWrite);
					return Settings.ScanCopyOnWriteMemory switch
					{
						SettingState.Yes => isCopyOnWrite,
						SettingState.No => !isCopyOnWrite,
						_ => true
					};
				})
				.ToList();
		}

		/// <summary>
		/// Starts an async search with the provided <see cref="IScanComparer"/>.
		/// The results are stored in the store.
		/// </summary>
		/// <param name="comparer">The comparer to scan for values.</param>
		/// <param name="progress">The <see cref="IProgress{T}"/> object to report the current progress.</param>
		/// <param name="ct">The <see cref="CancellationToken"/> to stop the scan.</param>
		/// <returns> The asynchronous result indicating if the scan completed.</returns>
		public Task<bool> Search(IScanComparer comparer, IProgress<int> progress, CancellationToken ct)
		{
			return isFirstScan ? FirstScan(comparer, progress, ct) : NextScan(comparer, progress, ct);
		}

		/// <summary>
		/// Starts an async first scan with the provided <see cref="IScanComparer"/>.
		/// </summary>
		/// <param name="comparer">The comparer to scan for values.</param>
		/// <param name="progress">The <see cref="IProgress{T}"/> object to report the current progress.</param>
		/// <param name="ct">The <see cref="CancellationToken"/> to stop the scan.</param>
		/// <returns> The asynchronous result indicating if the scan completed.</returns>
		private Task<bool> FirstScan(IScanComparer comparer, IProgress<int> progress, CancellationToken ct)
		{
			Contract.Requires(comparer != null);
			Contract.Ensures(Contract.Result<Task<bool>>() != null);

			var sections = GetSearchableSections();
			if (sections.Count == 0)
			{
				return Task.FromResult(true);
			}

			var regions = ConsolidateSections(sections)
				.Select(region => ClipRegion(region, Settings))
				.Where(region => region.Size > 0)
				.ToList();
			ValidateChunkedScan(regions, comparer, Settings);
			var totalSize = regions.Sum(region => region.Size);
			var store = CreateStore();
			progress?.Report(0);
			long counter = 0;

			return Task.Run(() =>
			{
				var result = Parallel.ForEach(
					regions,
					() => new ScannerContext(CreateWorker(Settings, comparer), 0),
					(region, state, _, context) =>
					{
						foreach (var chunk in CreateScanChunks(region, comparer, Settings))
						{
							if (ct.IsCancellationRequested)
							{
								state.Stop();
								break;
							}
							context.EnsureBufferSize(chunk.Size);
							var buffer = context.Buffer;
							if (process.ReadRemoteMemoryIntoBuffer(chunk.Address, ref buffer, 0, chunk.Size))
							{
								var matches = context.Worker is SimpleScannerWorker simpleWorker
									? simpleWorker.Search(buffer, chunk.Size, ct, chunk.FirstIndex, chunk.SearchLength)
									: context.Worker.Search(buffer, chunk.Size, ct);
								var results = matches.OrderBy(r => r.Address, IntPtrComparer.Instance).ToList();
								if (results.Count > 0)
								{
									store.AddBlock(CreateResultBlock(results, chunk.Address));
								}
							}
							progress?.Report((int)(Interlocked.Add(ref counter, chunk.SearchLength) / (double)totalSize * 100));
						}
						if (ct.IsCancellationRequested) state.Stop();
						return context;
					},
					context => { }
				);

				store.Finish();
				var previousStore = stores.Enqueue(store);
				previousStore?.Dispose();
				isFirstScan = false;
				return result.IsCompleted;
			}, ct);
		}

		/// <summary>
		/// Starts an async next scan with the provided <see cref="IScanComparer"/>.
		/// The next scan uses the previous results to refine the results.
		/// </summary>
		/// <param name="comparer">The comparer to scan for values.</param>
		/// <param name="progress">The <see cref="IProgress{T}"/> object to report the current progress.</param>
		/// <param name="ct">The <see cref="CancellationToken"/> to stop the scan.</param>
		/// <returns> The asynchronous result indicating if the scan completed.</returns>
		private Task<bool> NextScan(IScanComparer comparer, IProgress<int> progress, CancellationToken ct)
		{
			Contract.Requires(comparer != null);
			Contract.Ensures(Contract.Result<Task<bool>>() != null);

			var store = CreateStore();

			progress?.Report(0);

			var counter = 0;
			var totalResultCount = (float)CurrentStore.TotalResultCount;

			return Task.Run(() =>
			{
				var result = Parallel.ForEach(
					CurrentStore.GetResultBlocks(),
					() => new ScannerContext(CreateWorker(Settings, comparer), 0),
					(b, state, _, context) =>
					{
						if (!ct.IsCancellationRequested)
						{
							context.EnsureBufferSize(b.Size);
							var buffer = context.Buffer;
							if (process.ReadRemoteMemoryIntoBuffer(b.Start, ref buffer, 0, b.Size))
							{
								var results = context.Worker.Search(buffer, b.Size, b.Results, ct)
									.OrderBy(r => r.Address, IntPtrComparer.Instance)
									.ToList();
								if (results.Count > 0)
								{
									var block = CreateResultBlock(results, b.Start);
									store.AddBlock(block);
								}
							}

							progress?.Report((int)(Interlocked.Add(ref counter, b.Results.Count) / totalResultCount * 100));
						}
						else
						{
							state.Stop();
						}
						return context;
					},
					w => { }
				);

				store.Finish();

				var previousStore = stores.Enqueue(store);
				previousStore?.Dispose();

				return result.IsCompleted;
			}, ct);
		}

		/// <summary>
		/// Consolidate memory sections which are direct neighbours to reduce the number of work items.
		/// </summary>
		/// <param name="sections">A list of sections.</param>
		/// <returns>A list of consolidated memory regions.</returns>
		private static List<ConsolidatedMemoryRegion> ConsolidateSections(IList<Section> sections)
		{
			var regions = new List<ConsolidatedMemoryRegion>();

			if (sections.Count > 0)
			{
				var address = sections[0].Start;
				long size = sections[0].Size.ToInt64Bits();

				for (var i = 1; i < sections.Count; ++i)
				{
					var section = sections[i];
					if (checked(address.ToInt64Bits() + size) != section.Start.ToInt64Bits())
					{
						regions.Add(new ConsolidatedMemoryRegion { Address = address, Size = size });

						address = section.Start;
						size = section.Size.ToInt64Bits();
					}
					else
					{
						size = checked(size + section.Size.ToInt64Bits());
					}
				}

				regions.Add(new ConsolidatedMemoryRegion { Address = address, Size = size });
			}

			return regions;
		}

		private static ConsolidatedMemoryRegion ClipRegion(ConsolidatedMemoryRegion region, ScanSettings settings)
		{
			var start = Math.Max(region.Address.ToInt64Bits(), settings.StartAddress.ToInt64Bits());
			// Preserve the existing exclusive stop-address behavior.
			var end = Math.Min(checked(region.Address.ToInt64Bits() + region.Size), settings.StopAddress.ToInt64Bits());
			return new ConsolidatedMemoryRegion { Address = IntPtrExtension.From(start), Size = Math.Max(0, end - start) };
		}

		private static void ValidateChunkedScan(IEnumerable<ConsolidatedMemoryRegion> regions, IScanComparer comparer, ScanSettings settings)
		{
			if (comparer is ISimpleScanComparer simpleComparer)
			{
				if (simpleComparer.ValueSize <= 0 || simpleComparer.ValueSize > MaximumBufferSize)
				{
					throw new ArgumentOutOfRangeException(nameof(comparer), "The search value must fit in a scan buffer and contain at least one byte.");
				}
				if (settings.FastScanAlignment <= 0)
				{
					throw new ArgumentOutOfRangeException(nameof(settings.FastScanAlignment), "Scan alignment must be positive.");
				}
			}
			else if (regions.Any(region => region.Size > MaximumBufferSize))
			{
				// Regex/custom complex comparers have no bounded match length. Splitting them
				// could change anchors, lookarounds and matches spanning read boundaries.
				throw new NotSupportedException("Regex and custom complex scans require a contiguous buffer smaller than 2 GiB. Narrow the scan address range or use a fixed string/byte-pattern scan.");
			}
		}

		private static IEnumerable<ScanChunk> CreateScanChunks(ConsolidatedMemoryRegion region, IScanComparer comparer, ScanSettings settings)
		{
			if (!(comparer is ISimpleScanComparer simpleComparer))
			{
				yield return new ScanChunk { Address = region.Address, Size = checked((int)region.Size), SearchLength = checked((int)region.Size) };
				yield break;
			}

			var overlap = simpleComparer.ValueSize - 1;
			var chunkSize = Math.Min(ScanChunkSize, MaximumBufferSize - overlap);
			for (long offset = 0; offset < region.Size;)
			{
				var length = (int)Math.Min(chunkSize, region.Size - offset);
				yield return new ScanChunk
				{
					Address = IntPtrExtension.From(checked(region.Address.ToInt64Bits() + offset)),
					Size = (int)Math.Min((long)length + overlap, region.Size - offset),
					SearchLength = length,
					// Keep the same alignment origin as a single read of the clipped region.
					FirstIndex = (int)((settings.FastScanAlignment - offset % settings.FastScanAlignment) % settings.FastScanAlignment)
				};
				offset += length;
			}
		}

		/// <summary>
		/// Creates a result block from the scan results and adjusts the result offset.
		/// </summary>
		/// <param name="results">The results in this block.</param>
		/// <param name="previousStartAddress">The start address of the previous block or section.</param>
		/// <returns>The new result block.</returns>
		private static ScanResultBlock CreateResultBlock(IReadOnlyList<ScanResult> results, IntPtr previousStartAddress)
		{
			var firstResult = results.First();
			var lastResult = results.Last();

			// Calculate start and end address
			var startAddress = firstResult.Address.Add(previousStartAddress);
			var endAddress = lastResult.Address.Add(previousStartAddress) + lastResult.ValueSize;

			// Adjust the offsets of the results
			var firstOffset = firstResult.Address;
			foreach (var result in results)
			{
				result.Address = result.Address.Sub(firstOffset);
			}

			var block = new ScanResultBlock(
				startAddress,
				endAddress,
				results
			);
			return block;
		}

		private static IScannerWorker CreateWorker(ScanSettings settings, IScanComparer comparer)
		{
			if (comparer is ISimpleScanComparer simpleScanComparer)
			{
				return new SimpleScannerWorker(settings, simpleScanComparer);
			}
			if (comparer is IComplexScanComparer complexScanComparer)
			{
				return new ComplexScannerWorker(settings, complexScanComparer);
			}

			throw new Exception();
		}
	}
}
