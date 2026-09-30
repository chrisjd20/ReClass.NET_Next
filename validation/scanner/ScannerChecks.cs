using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using ReClassNET.Core;
using ReClassNET.Debugger;
using ReClassNET.Memory;
using ReClassNET.MemoryScanner;
using ReClassNET.MemoryScanner.Comparer;
using ReClassNET.Util.Conversion;

// One opt-in regression batch; synthetic memory avoids allocating/scanning gigabytes
// and requires neither a native debugger nor a running target process.
internal static class ScannerChecks
{
    private const int ChunkSize = 1024 * 1024;
    private const long BaseAddress = 0x100000000;
    private static int Main()
    {
        try
        {
            LargeRegionPlan();
            BoundaryScans();
            LargeOffsetAndRegex();
            Console.WriteLine("PASS scanner overflow, clipping, chunk boundaries, alignment, refinement and regex guard");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(string method, params object[] args)
    {
        return typeof(Scanner).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    }

    private static T Property<T>(object value, string name)
    {
        return (T)value.GetType().GetProperty(name).GetValue(value);
    }

    private static Section Section(long address, long size)
    {
        return new Section { Start = new IntPtr(address), End = new IntPtr(address + size), Size = new IntPtr(size),
            Type = SectionType.Private, Protection = SectionProtection.Read | SectionProtection.Write };
    }

    private static void LargeRegionPlan()
    {
        var sections = new[] { Section(BaseAddress, (long)int.MaxValue + 1), Section(BaseAddress + (long)int.MaxValue + 1, 37) };
        var regions = ((IEnumerable)Invoke("ConsolidateSections", (object)sections)).Cast<object>().ToArray();
        Check(regions.Length == 1 && Property<long>(regions[0], "Size") == (long)int.MaxValue + 38,
            "Adjacent regions above 2 GiB were truncated.");
        var settings = new ScanSettings { FastScanAlignment = 3 };
        var comparer = new LongMemoryComparer(ScanCompareType.Equal, 1, 0, EndianBitConverter.System);
        long offset = 0;
        foreach (var chunk in (IEnumerable)Invoke("CreateScanChunks", regions[0], comparer, settings))
        {
            var length = Property<int>(chunk, "SearchLength");
            var size = Property<int>(chunk, "Size");
            Check(Property<IntPtr>(chunk, "Address").ToInt64() == BaseAddress + offset, "Chunk address wrapped.");
            Check(size > 0 && size <= ChunkSize + 7 && length > 0 && length <= ChunkSize, "Read was not bounded.");
            Check((offset + Property<int>(chunk, "FirstIndex")) % 3 == 0, "Alignment drifted across chunks.");
            Check(offset + size <= (long)int.MaxValue + 38, "Overlap crossed the region end.");
            offset += length;
        }
        Check(offset == (long)int.MaxValue + 38, "The tail of a large region was dropped.");
        var separate = ((IEnumerable)Invoke("ConsolidateSections", (object)new[] { sections[0], Section(BaseAddress + (long)int.MaxValue + 100, 10) })).Cast<object>().ToArray();
        Check(separate.Length == 2, "Unmapped gap was consolidated.");
    }

    private static void BoundaryScans()
    {
        var data = new byte[2 * ChunkSize + 37];
        var pattern = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5 };
        var offsets = new[] { 0, ChunkSize - 2, ChunkSize, data.Length - pattern.Length };
        // The two boundary matches overlap, so run each placement independently.
        foreach (var offset in offsets)
        {
            Array.Clear(data, 0, data.Length);
            Array.Copy(pattern, 0, data, offset, pattern.Length);
            AssertScan(data, new ArrayOfBytesMemoryComparer(pattern), ScanValueType.ArrayOfBytes, 1, new[] { offset });
        }
        Array.Clear(data, 0, data.Length);
        var aligned = new[] { 0, ChunkSize - 1, ChunkSize + 2, (data.Length - 4) / 3 * 3 };
        foreach (var offset in aligned) Array.Copy(BitConverter.GetBytes(0x12345678), 0, data, offset, 4);
        // Avoid overlapping numeric values when checking both sides of the boundary.
        Array.Clear(data, ChunkSize - 1, 7);
        Array.Copy(BitConverter.GetBytes(0x12345678), 0, data, ChunkSize - 1, 4);
        AssertScan(data, new IntegerMemoryComparer(ScanCompareType.Equal, 0x12345678, 0, EndianBitConverter.System),
            ScanValueType.Integer, 3, new[] { aligned[0], aligned[1], aligned[3] });
        Array.Clear(data, 0, data.Length);
        var text = Encoding.Unicode.GetBytes("ammo");
        Array.Copy(text, 0, data, ChunkSize - 2, text.Length);
        AssertScan(data, new StringMemoryComparer("ammo", Encoding.Unicode, true), ScanValueType.String, 2, new[] { ChunkSize - 2 });
        AssertScan(new byte[] { 7 }, new ByteMemoryComparer(ScanCompareType.Equal, 7, 0), ScanValueType.Byte, 1, new[] { 0 });
    }

    private static void AssertScan(byte[] data, IScanComparer comparer, ScanValueType type, int alignment, int[] offsets)
    {
        var core = new FakeCore(data, BaseAddress, new[] { Section(BaseAddress, data.Length) });
        using (var remote = Open(core))
        using (var scanner = new Scanner(remote, new ScanSettings { ValueType = type, FastScanAlignment = alignment }))
        {
            Check(scanner.Search(comparer, null, CancellationToken.None).GetAwaiter().GetResult(), "First scan did not complete.");
            var expected = offsets.Select(offset => BaseAddress + offset).OrderBy(address => address).ToArray();
            Check(scanner.GetResults().Select(r => r.Address.ToInt64()).OrderBy(address => address).SequenceEqual(expected),
                "Boundary scan lost, duplicated or misaligned a match.");
            Check(core.MaximumRead <= ChunkSize + 7,
                "First scan requested a whole-region allocation.");
            Check(scanner.Search(comparer, null, CancellationToken.None).GetAwaiter().GetResult(), "Refinement did not complete.");
            Check(scanner.GetResults().Select(r => r.Address.ToInt64()).OrderBy(address => address).SequenceEqual(expected),
                "Refinement changed unchanged results.");
        }
    }

    private static void LargeOffsetAndRegex()
    {
        var data = Encoding.ASCII.GetBytes("xxammoxx");
        var start = BaseAddress + (long)int.MaxValue + 123;
        var sections = new[] { Section(BaseAddress, (long)int.MaxValue + 4096) };
        var core = new FakeCore(data, start, sections);
        using (var remote = Open(core))
        {
            var settings = new ScanSettings { StartAddress = new IntPtr(start), StopAddress = new IntPtr(start + data.Length),
                FastScanAlignment = 1, ValueType = ScanValueType.Regex };
            using (var scanner = new Scanner(remote, settings))
            {
                var regex = new RegexStringMemoryComparer("ammo", Encoding.ASCII, true);
                Check(scanner.Search(regex, null, CancellationToken.None).GetAwaiter().GetResult(), "Clipped regex scan failed.");
                Check(scanner.GetResults().Single().Address.ToInt64() == start + 2 && core.MaximumRead == data.Length,
                    "Clipping more than 2 GiB from the region start overflowed.");
            }
            using (var scanner = new Scanner(remote, new ScanSettings { ValueType = ScanValueType.Regex }))
            {
                var reads = core.ReadCount;
                try
                {
                    scanner.Search(new RegexStringMemoryComparer("ammo", Encoding.ASCII, true), null, CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Oversized regex scan was silently split or truncated.");
                }
                catch (NotSupportedException e) { Check(e.Message.Contains("Narrow") && core.ReadCount == reads, "Regex guard read oversized memory."); }
            }
        }
    }

    private static RemoteProcess Open(FakeCore core)
    {
        // Bypass only the native-provider constructor; the scanner and RemoteProcess
        // run normally against ICoreProcessFunctions with deterministic memory.
        var manager = (CoreFunctionsManager)FormatterServices.GetUninitializedObject(typeof(CoreFunctionsManager));
        typeof(CoreFunctionsManager).GetField("currentFunctions", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, core);
        var remote = new RemoteProcess(manager);
        remote.Open(new ProcessInfo(new IntPtr(1), "scanner fixture", "fixture"));
        remote.UpdateProcessInformations();
        return remote;
    }

    private sealed class FakeCore : ICoreProcessFunctions
    {
        private readonly byte[] data;
        private readonly long address;
        private readonly Section[] sections;
        public int MaximumRead { get; private set; }
        public int ReadCount { get; private set; }
        public FakeCore(byte[] data, long address, Section[] sections) { this.data = data; this.address = address; this.sections = sections; }
        public IntPtr OpenRemoteProcess(IntPtr pid, ProcessAccess access) => new IntPtr(1);
        public bool IsProcessValid(IntPtr process) => true;
        public void CloseRemoteProcess(IntPtr process) { }
        public bool ReadRemoteMemory(IntPtr process, IntPtr start, ref byte[] buffer, int offset, int size)
        {
            ++ReadCount; MaximumRead = Math.Max(MaximumRead, size);
            var index = start.ToInt64() - address;
            Check(index >= 0 && index + size <= data.Length, "Read escaped the clipped scan range.");
            Array.Copy(data, index, buffer, offset, size);
            return true;
        }
        public void EnumerateRemoteSectionsAndModules(IntPtr process, EnumerateRemoteSectionCallback sectionCallback, EnumerateRemoteModuleCallback moduleCallback)
        {
            foreach (var section in sections)
            {
                var value = new EnumerateRemoteSectionData { BaseAddress = section.Start, Size = section.Size,
                    Type = section.Type, Protection = section.Protection, ModulePath = "" };
                sectionCallback(ref value);
            }
        }
        public void EnumerateProcesses(EnumerateProcessCallback callback) => throw new NotSupportedException();
        public bool WriteRemoteMemory(IntPtr process, IntPtr address, ref byte[] buffer, int offset, int size) => throw new NotSupportedException();
        public void ControlRemoteProcess(IntPtr process, ControlRemoteProcessAction action) => throw new NotSupportedException();
        public bool AttachDebuggerToProcess(IntPtr id) => throw new NotSupportedException();
        public void DetachDebuggerFromProcess(IntPtr id) => throw new NotSupportedException();
        public bool AwaitDebugEvent(ref DebugEvent evt, int timeout) => throw new NotSupportedException();
        public void HandleDebugEvent(ref DebugEvent evt) => throw new NotSupportedException();
        public bool SetHardwareBreakpoint(IntPtr id, IntPtr address, HardwareBreakpointRegister register, HardwareBreakpointTrigger trigger, HardwareBreakpointSize size, bool set) => throw new NotSupportedException();
    }
}
