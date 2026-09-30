using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using ReClassNET.MemoryScanner;

namespace ReClassNET.Patching
{
    public enum PatchResolutionStatus { Resolved, NoMatch, MultipleMatches, IdentityMismatch, ModuleUnloaded, IncompleteScan, Unsupported, Cancelled }
    public sealed class PatchResolution
    {
        public PatchResolutionStatus Status { get; internal set; }
        public ulong Address { get; internal set; }
        public PatchModule Module { get; internal set; }
        public string Message { get; internal set; }
    }
    public sealed class PatchTargetResolver
    {
        private readonly Dictionary<string, Tuple<long, DateTime, DateTime, string>> hashes = new Dictionary<string, Tuple<long, DateTime, DateTime, string>>(StringComparer.Ordinal);
        public string Fingerprint(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new IOException("The module image file is unavailable.");
            var file = new FileInfo(path);
            Tuple<long, DateTime, DateTime, string> cached;
            lock (hashes)
            {
                if (hashes.TryGetValue(file.FullName, out cached) && cached.Item1 == file.Length && cached.Item2 == file.LastWriteTimeUtc && cached.Item3 == file.CreationTimeUtc) return cached.Item4;
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    long beforeLength = file.Length;
                    var beforeWrite = file.LastWriteTimeUtc;
                    var beforeCreation = file.CreationTimeUtc;
                    var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    file.Refresh();
                    if (stream.Length != file.Length || beforeLength != file.Length || beforeWrite != file.LastWriteTimeUtc || beforeCreation != file.CreationTimeUtc) throw new IOException("The module image changed during fingerprinting.");
                    hashes[file.FullName] = Tuple.Create(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, hash);
                    return hash;
                }
            }
        }
        public PatchResolution Resolve(PatchDefinition definition, IPatchTarget target, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!string.Equals(definition.Architecture, "x64", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(definition.Platform) && definition.Platform != target.Platform)) return Failure(PatchResolutionStatus.Unsupported, "Patch platform or architecture differs from the target.");
            if (definition.LocatorKind == PatchLocatorKind.SessionAddress)
            {
                if (definition.SessionId != target.SessionId) return Failure(PatchResolutionStatus.IdentityMismatch, "This address belongs to another process session.");
                var containing = target.Modules.FirstOrDefault(m => definition.SessionAddress >= m.BaseAddress && definition.SessionAddress - m.BaseAddress < m.Size);
                return new PatchResolution { Status = PatchResolutionStatus.Resolved, Address = definition.SessionAddress, Module = containing };
            }
            var modules = target.Modules.Where(m => string.Equals(m.Name, definition.ModuleName, StringComparison.Ordinal)).ToArray();
            if (modules.Length == 0) return Failure(PatchResolutionStatus.ModuleUnloaded, "The saved module is not loaded.");
            if (modules.Length != 1) return Failure(PatchResolutionStatus.MultipleMatches, "Several module instances have the saved name.");
            var module = modules[0];
            string actualHash;
            try { actualHash = Fingerprint(module.Path); }
            catch (Exception e) { return Failure(PatchResolutionStatus.IdentityMismatch, "Cannot identify module image: " + e.Message); }
            if (string.IsNullOrEmpty(definition.ImageSha256) || !string.Equals(actualHash, definition.ImageSha256, StringComparison.OrdinalIgnoreCase)) return Failure(PatchResolutionStatus.IdentityMismatch, "Module image SHA-256 differs from the saved definition.");
            if (!target.IsModuleCurrent(module)) return Failure(PatchResolutionStatus.ModuleUnloaded, "The module instance changed.");
            if (definition.LocatorKind == PatchLocatorKind.ModuleOffset)
            {
                if (definition.Offset >= module.Size || (ulong)definition.SelectionLength > module.Size - definition.Offset) return Failure(PatchResolutionStatus.NoMatch, "The module offset is outside the image.");
                return new PatchResolution { Status = PatchResolutionStatus.Resolved, Address = checked(module.BaseAddress + definition.Offset), Module = module };
            }
            if (definition.LocatorKind != PatchLocatorKind.ModulePattern) return Failure(PatchResolutionStatus.Unsupported, "Unknown locator kind.");
            var pattern = BytePattern.Parse(definition.Pattern);
            if (pattern.Length == 0 || pattern.Length > 65536) return Failure(PatchResolutionStatus.Unsupported, "Patterns must contain 1 to 65536 bytes.");
            var matches = new HashSet<ulong>();
            bool incomplete = false;
            foreach (var range in module.ExecutableRanges)
            {
                ulong cursor = range.Address;
                var end = range.End;
                byte[] tail = new byte[0];
                while (cursor < end)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(65536UL, end - cursor);
                    byte[] chunk;
                    try { chunk = target.ReadExact(cursor, count); if (chunk == null || chunk.Length != count) throw new IOException("Short read."); }
                    catch { incomplete = true; tail = new byte[0]; cursor = checked(cursor + (ulong)count); continue; }
                    var buffer = new byte[tail.Length + count];
                    Buffer.BlockCopy(tail, 0, buffer, 0, tail.Length);
                    Buffer.BlockCopy(chunk, 0, buffer, tail.Length, count);
                    ulong origin = cursor - (ulong)tail.Length;
                    for (int i = 0; i <= buffer.Length - pattern.Length; i++)
                    {
                        if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                        if (pattern.Equals(buffer, i))
                        {
                            matches.Add(checked(origin + (ulong)i));
                            if (matches.Count == 2) return Failure(PatchResolutionStatus.MultipleMatches, "The module pattern is ambiguous.");
                        }
                    }
                    int overlap = Math.Min(pattern.Length - 1, buffer.Length);
                    tail = new byte[overlap];
                    Buffer.BlockCopy(buffer, buffer.Length - overlap, tail, 0, overlap);
                    cursor = checked(cursor + (ulong)count);
                }
            }
            if (incomplete) return Failure(PatchResolutionStatus.IncompleteScan, "Unreadable executable ranges prevent establishing uniqueness.");
            if (matches.Count == 0) return Failure(PatchResolutionStatus.NoMatch, "The module pattern was not found.");
            ulong address = PatchBytes.AddSigned(matches.First(), definition.EntryOffset);
            if (address < module.BaseAddress || address >= checked(module.BaseAddress + module.Size)) return Failure(PatchResolutionStatus.NoMatch, "The pattern entry offset is outside the module.");
            return new PatchResolution { Status = PatchResolutionStatus.Resolved, Address = address, Module = module };
        }
        private static PatchResolution Failure(PatchResolutionStatus status, string message) => new PatchResolution { Status = status, Message = message };
    }
}
