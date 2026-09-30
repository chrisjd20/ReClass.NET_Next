using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReClassNET.Patching
{
    public sealed class PatchManager
    {
        public const long PublishedAllocationLimit = 16 * 1024 * 1024;
        private readonly IPatchTarget target;
        private readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        private readonly Dictionary<Guid, ActivePatch> active = new Dictionary<Guid, ActivePatch>();
        private readonly List<PatchAllocation> retained = new List<PatchAllocation>();
        private readonly object stateLock = new object();
        private PreparedHook preparation;
        private long publishedBytes;
        public Func<Guid, int, bool> IsDefinitionCurrent { get; set; }
        public event EventHandler Changed;
        public IPatchTarget Target => target;
        public IReadOnlyList<ActivePatch> ActivePatches { get { lock (stateLock) return active.Values.ToArray(); } }
        public IReadOnlyList<PatchAllocation> RetainedAllocations { get { lock (stateLock) return retained.ToArray(); } }
        public PreparedHook Preparation { get { lock (stateLock) return preparation; } }
        public long PublishedBytes { get { lock (stateLock) return publishedBytes; } }
        public PatchManager(IPatchTarget target, PatchPlanner planner = null)
        {
            this.target = target ?? throw new ArgumentNullException(nameof(target));
        }
        internal async Task<PreparedHook> PrepareAsync(PatchPreview preview, Func<Task<PreparedHook>> prepare, CancellationToken cancellation)
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Validate(preview);
                if (!target.SupportsAllocation) throw new InvalidOperationException("Remote allocation is unavailable.");
                if (publishedBytes + PatchPlanner.HookReservationSize > PublishedAllocationLimit) throw new InvalidOperationException("The attached session has reached its 16 MiB retained hook limit. Restart the target to reclaim published allocations.");
                if (preparation != null)
                {
                    var cancel = await CancelCore(CancellationToken.None).ConfigureAwait(false);
                    if (!cancel.Success) throw new InvalidOperationException(cancel.Message);
                }
                var conflict = FindOverlap(preview.Address, preview.OriginalBytes.Length, null);
                if (conflict != null) throw new InvalidOperationException(conflict);
                var prepared = await prepare().ConfigureAwait(false);
                conflict = FindOverlap(prepared.Preview.Address, prepared.EntryBytes.Length, null);
                if (conflict != null)
                {
                    using (var stop = await target.StopAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        try { target.Free(prepared.Allocation); }
                        catch (Exception e) { lock (stateLock) preparation = prepared; stop.KeepStopped(e.Message); throw; }
                    }
                    throw new InvalidOperationException(conflict);
                }
                lock (stateLock) preparation = prepared;
                RaiseChanged();
                return prepared;
            }
            catch (PatchPreparationCleanupException e)
            {
                lock (stateLock) preparation = new PreparedHook { Preview = preview, Allocation = e.Allocation, EntryBytes = new byte[preview.OriginalBytes.Length] };
                RaiseChanged();
                throw;
            }
            finally { operations.Release(); }
        }
        public Task<PatchResult> ApplyAsync(PatchPreview preview, CancellationToken cancellation = default(CancellationToken))
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (!preview.CanApply) return Task.FromResult(PatchResult.Fail(PatchStatus.Unsupported, "A valid in-place preview is required."));
            return ApplyCoreAsync(preview, preview.ReplacementBytes, null, cancellation);
        }
        public Task<PatchResult> ApplyAsync(PreparedHook hook, CancellationToken cancellation = default(CancellationToken))
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            return ApplyCoreAsync(hook.Preview, hook.EntryBytes, hook, cancellation);
        }
        private async Task<PatchResult> ApplyCoreAsync(PatchPreview preview, byte[] installed, PreparedHook hook, CancellationToken cancellation)
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            ActivePatch reserved = null;
            bool retainRecord = false;
            try
            {
                if (!target.IsAlive) return ClearExited();
                Validate(preview);
                if (hook != null && (preparation != hook || hook.Published || hook.Code == null)) return PatchResult.Fail(PatchStatus.Conflict, "Hook preparation is no longer current or still requires cleanup.");
                lock (stateLock) if (active.ContainsKey(preview.Definition.Id)) return PatchResult.Fail(PatchStatus.Conflict, "Restore the existing revision before applying a new revision.");
                var conflict = FindOverlap(preview.Address, installed.Length, hook);
                if (conflict != null) return PatchResult.Fail(PatchStatus.Conflict, conflict);
                reserved = new ActivePatch { Id = preview.Definition.Id, Preview = preview, InstalledBytes = (byte[])installed.Clone(), Allocation = hook?.Allocation, Status = PatchStatus.Applying };
                lock (stateLock) active[reserved.Id] = reserved;
                using (var stop = await target.StopAsync(cancellation).ConfigureAwait(false))
                {
                    Validate(preview);
                    cancellation.ThrowIfCancellationRequested();
                    conflict = FindOverlap(preview.Address, installed.Length, hook, reserved.Id);
                    if (conflict != null) return PatchResult.Fail(PatchStatus.Conflict, conflict);
                    if (!PatchBytes.Equal(preview.OriginalBytes, PatchPlanner.Exact(target, preview.Address, preview.OriginalBytes.Length))) return PatchResult.Fail(PatchStatus.Conflict, "The original span changed after preview; no bytes were written.");
                    if (HasThreadInside(preview.Address, installed.Length)) return PatchResult.Fail(PatchStatus.Conflict, "A stopped thread's instruction pointer is inside the overwrite span. Resume and retry.");
                    if (hook != null && !PatchBytes.Equal(hook.Code, PatchPlanner.Exact(target, hook.Allocation.Address, hook.Code.Length))) return PatchResult.Fail(PatchStatus.Conflict, "Prepared hook memory changed; prepare again.");
                    var record = reserved;
                    // Once the entry write begins, conservatively treat hook memory as published even
                    // when rollback later succeeds. A partial write may already expose that address.
                    if (hook != null)
                    {
                        lock (stateLock)
                        {
                            hook.Allocation.Published = true;
                            retained.Add(hook.Allocation);
                            publishedBytes += hook.Allocation.Size;
                            preparation = null;
                        }
                    }
                    var failure = WriteVerified(preview.Address, installed);
                    if (failure != null || cancellation.IsCancellationRequested)
                    {
                        string reason = failure ?? "Apply cancelled after writing; the original bytes were restored.";
                        var recovery = WriteVerified(preview.Address, preview.OriginalBytes);
                        if (recovery != null)
                        {
                            record.Status = PatchStatus.RecoveryRequired;
                            record.Message = reason + " Recovery failed: " + recovery;
                            lock (stateLock) active[record.Id] = record;
                            retainRecord = true;
                            stop.KeepStopped(record.Message);
                            RaiseChanged();
                            return new PatchResult { Status = PatchStatus.RecoveryRequired, Message = record.Message, Patch = record };
                        }
                        if (record.Allocation != null) record.Allocation.Retired = true;
                        stop.RecoveryCompleted();
                        RaiseChanged();
                        return new PatchResult { Status = cancellation.IsCancellationRequested ? PatchStatus.Cancelled : PatchStatus.Failed, Message = reason, Recovered = true };
                    }
                    record.Status = PatchStatus.Active;
                    lock (stateLock) active[record.Id] = record;
                    retainRecord = true;
                    RaiseChanged();
                    return new PatchResult { Status = PatchStatus.Active, Patch = record, Message = "Patch applied and verified." };
                }
            }
            catch (OperationCanceledException) { return PatchResult.Fail(PatchStatus.Cancelled, "Apply cancelled before writing."); }
            catch (Exception e) { return PatchResult.Fail(target.IsAlive ? PatchStatus.Failed : PatchStatus.TargetExited, e.Message); }
            finally
            {
                if (reserved != null && !retainRecord) lock (stateLock) active.Remove(reserved.Id);
                operations.Release();
            }
        }
        public async Task<PatchResult> RestoreAsync(Guid id, CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await RestoreCore(id, cancellation).ConfigureAwait(false); }
            finally { operations.Release(); }
        }
        // Writes the saved originals even when another writer changed the installed bytes.
        public async Task<PatchResult> ForceRestoreAsync(Guid id, CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await RestoreCore(id, cancellation, true).ConfigureAwait(false); }
            finally { operations.Release(); }
        }
        // Forgets ownership without writing; published hook memory stays retained in the target.
        public async Task<PatchResult> AbandonAsync(Guid id, CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try { return AbandonCore(id); }
            finally { operations.Release(); }
        }
        private PatchResult AbandonCore(Guid id)
        {
            ActivePatch record;
            lock (stateLock) { if (!active.TryGetValue(id, out record)) return new PatchResult { Status = PatchStatus.Inactive, Message = "Patch is inactive." }; active.Remove(id); }
            record.Status = PatchStatus.Inactive; record.Message = "Ownership abandoned; the current bytes were left unchanged.";
            if (record.Allocation != null) record.Allocation.Retired = true;
            RaiseChanged();
            return new PatchResult { Status = PatchStatus.Inactive, Patch = record, Message = record.Message };
        }
        private async Task<PatchResult> RestoreCore(Guid id, CancellationToken cancellation, bool force = false)
        {
            if (!target.IsAlive) return ClearExited();
            ActivePatch record;
            lock (stateLock) if (!active.TryGetValue(id, out record)) return new PatchResult { Status = PatchStatus.Inactive, Message = "Patch is inactive." };
            try
            {
                // A definition edit does not invalidate restoration of the previously applied bytes.
                PatchPlanner.ValidateIdentity(record.Preview, target);
                using (var stop = await target.StopAsync(cancellation).ConfigureAwait(false))
                {
                    PatchPlanner.ValidateIdentity(record.Preview, target);
                    cancellation.ThrowIfCancellationRequested();
                    var current = PatchPlanner.Exact(target, record.Preview.Address, record.InstalledBytes.Length);
                    if (!force && record.Status != PatchStatus.RecoveryRequired && !PatchBytes.Equal(current, record.InstalledBytes))
                    {
                        record.Status = PatchStatus.Conflict;
                        record.Message = "Installed bytes of " + record.Preview.Definition.Name + " were changed by another writer; originals are retained. Choose Force restore original bytes or Abandon ownership.";
                        RaiseChanged();
                        return new PatchResult { Status = PatchStatus.Conflict, Message = record.Message, Patch = record };
                    }
                    if (HasThreadInside(record.Preview.Address, record.InstalledBytes.Length)) return PatchResult.Fail(PatchStatus.Conflict, "A thread is inside the entry overwrite span. Resume and retry restoration.");
                    var failure = WriteVerified(record.Preview.Address, record.Preview.OriginalBytes);
                    if (failure != null)
                    {
                        // Recovery finishes restoration, rather than re-publishing a possibly damaged entry.
                        var recovery = WriteVerified(record.Preview.Address, record.Preview.OriginalBytes);
                        if (recovery != null)
                        {
                            record.Status = PatchStatus.RecoveryRequired;
                            record.Message = failure + " Recovery failed: " + recovery;
                            stop.KeepStopped(record.Message);
                            RaiseChanged();
                            return new PatchResult { Status = record.Status, Message = record.Message, Patch = record };
                        }
                    }
                    record.Status = PatchStatus.Inactive;
                    stop.RecoveryCompleted();
                    if (record.Allocation != null) record.Allocation.Retired = true;
                    lock (stateLock) active.Remove(id);
                    RaiseChanged();
                    return new PatchResult { Status = PatchStatus.Inactive, Patch = record, Recovered = failure != null, Message = record.Allocation == null ? "Original bytes restored and verified." : "Entry restored. Published hook memory remains executable until target exit." };
                }
            }
            catch (OperationCanceledException) { return PatchResult.Fail(PatchStatus.Cancelled, "Restore cancelled before writing."); }
            catch (Exception e) { return PatchResult.Fail(target.IsAlive ? PatchStatus.Failed : PatchStatus.TargetExited, e.Message); }
        }
        public async Task<PatchResult> CancelPreparationAsync(CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await CancelCore(cancellation).ConfigureAwait(false); }
            finally { operations.Release(); }
        }
        public async Task<PatchResult> CancelPreparationAsync(Guid preparationId, CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (preparation == null || preparation.Id != preparationId) return new PatchResult { Status = PatchStatus.Inactive, Message = "This preparation is no longer current." };
                return await CancelCore(cancellation).ConfigureAwait(false);
            }
            finally { operations.Release(); }
        }
        private async Task<PatchResult> CancelCore(CancellationToken cancellation)
        {
            if (!target.IsAlive) return ClearExited();
            var pending = preparation;
            if (pending == null) return new PatchResult { Status = PatchStatus.Inactive, Message = "No prepared hook." };
            if (pending.Published) return PatchResult.Fail(PatchStatus.Conflict, "Published allocations cannot be freed while the target is alive.");
            try
            {
                // The selection's module can unload while this separate unpublished allocation
                // remains owned. Its release needs process/provider identity, not selection validity.
                if (pending.Preview.SessionId != target.SessionId || pending.Preview.ProviderIdentity != target.ProviderIdentity) throw new InvalidOperationException("The preparation belongs to another process or provider session.");
                using (var stop = await target.StopAsync(cancellation).ConfigureAwait(false))
                {
                    try { target.Free(pending.Allocation); }
                    catch (Exception e)
                    {
                        pending.Preview.Status = PatchStatus.RecoveryRequired;
                        pending.Preview.Message = "Cannot release unpublished preparation: " + e.Message;
                        stop.KeepStopped(pending.Preview.Message);
                        RaiseChanged();
                        return PatchResult.Fail(PatchStatus.RecoveryRequired, pending.Preview.Message);
                    }
                    if (!ActivePatches.Any(p => p.Status == PatchStatus.RecoveryRequired)) stop.RecoveryCompleted();
                    lock (stateLock) preparation = null;
                }
                RaiseChanged();
                return new PatchResult { Status = PatchStatus.Inactive, Message = "Unpublished hook reservation released." };
            }
            catch (Exception e) { return PatchResult.Fail(PatchStatus.Failed, "Cannot release preparation: " + e.Message); }
        }
        public Task<PatchResult> RestoreAllAsync(CancellationToken cancellation = default(CancellationToken)) => RestoreAllCore(cancellation, false);
        public Task<PatchResult> ForceRestoreAllAsync(CancellationToken cancellation = default(CancellationToken)) => RestoreAllCore(cancellation, true);
        // Continues past failures so one conflict cannot hide or block the remaining patches.
        private async Task<PatchResult> RestoreAllCore(CancellationToken cancellation, bool force)
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var failures = new List<PatchResult>(); var names = new List<string>();
                foreach (var patch in ActivePatches)
                {
                    var result = await RestoreCore(patch.Id, cancellation, force).ConfigureAwait(false);
                    if (!result.Success) { failures.Add(result); names.Add((patch.Preview.Definition.Name ?? "Patch") + ": " + result.Message); }
                }
                var cancel = await CancelCore(cancellation).ConfigureAwait(false);
                if (!cancel.Success) { failures.Add(cancel); names.Add("Prepared hook: " + cancel.Message); }
                if (failures.Count == 0) return new PatchResult { Status = PatchStatus.Inactive, Message = "All owned patches restored." };
                var worst = failures.FirstOrDefault(f => f.Status == PatchStatus.RecoveryRequired) ?? failures.FirstOrDefault(f => f.Status == PatchStatus.Conflict) ?? failures[0];
                return new PatchResult { Status = worst.Status, Patch = worst.Patch, Failures = failures, Message = failures.Count + " owned change(s) could not be restored. " + string.Join(" ", names) };
            }
            finally { operations.Release(); }
        }
        public async Task<PatchResult> AbandonAllAsync(CancellationToken cancellation = default(CancellationToken))
        {
            await operations.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                int count = 0; foreach (var patch in ActivePatches) if (AbandonCore(patch.Id).Patch != null) count++;
                var pending = preparation; string message = "";
                if (pending != null)
                {
                    var cancel = await CancelCore(cancellation).ConfigureAwait(false);
                    if (!cancel.Success) { lock (stateLock) preparation = null; message = " The unpublished hook reservation was left in the target."; RaiseChanged(); }
                }
                return new PatchResult { Status = PatchStatus.Inactive, Message = "Ownership of " + count + " patch(es) abandoned; current bytes were left unchanged." + message };
            }
            finally { operations.Release(); }
        }
        public async Task<PatchResult> DeleteDefinitionAsync(PatchRepository repository, Guid id, CancellationToken cancellation = default(CancellationToken))
        {
            var result = await RestoreAsync(id, cancellation).ConfigureAwait(false);
            if (result.Success) repository.Delete(id);
            return result;
        }
        private string WriteVerified(ulong address, byte[] bytes)
        {
            try
            {
                var write = target.WriteCode(address, bytes);
                if (write == null || !write.Success || write.RecoveryRequired) return write?.Error ?? "Code transaction failed.";
                return PatchBytes.Equal(bytes, PatchPlanner.Exact(target, address, bytes.Length)) ? null : "Code readback verification failed.";
            }
            catch (Exception e) { return e.Message; }
        }
        private bool HasThreadInside(ulong address, int length)
        {
            ulong end = checked(address + (ulong)length);
            return target.StoppedInstructionPointers.Any(ip => ip >= address && ip < end);
        }
        private void Validate(PatchPreview preview)
        {
            PatchPlanner.ValidateIdentity(preview, target);
            if (ActivePatches.Any(p => p.Status == PatchStatus.RecoveryRequired)) throw new InvalidOperationException("Restore the patch in RecoveryRequired before preparing or applying another patch.");
            if (!target.SupportsCodeTransactions) throw new InvalidOperationException("The selected provider cannot perform code transactions.");
            if (IsDefinitionCurrent != null && !IsDefinitionCurrent(preview.Definition.Id, preview.Definition.Revision)) throw new InvalidOperationException("Definition changed after preview.");
        }
        private string FindOverlap(ulong address, int length, PreparedHook ignore, Guid? ignoreActive = null)
        {
            ulong end = checked(address + (ulong)length);
            foreach (var item in ActivePatches)
            {
                if (ignoreActive == item.Id) continue;
                ulong otherEnd = checked(item.Preview.Address + (ulong)item.InstalledBytes.Length);
                if (address < otherEnd && item.Preview.Address < end) return "Overlap with active patch: " + item.Preview.Definition.Name;
            }
            if (preparation != null && preparation != ignore)
            {
                ulong otherEnd = checked(preparation.Preview.Address + (ulong)preparation.EntryBytes.Length);
                if (address < otherEnd && preparation.Preview.Address < end) return "Overlap with prepared patch: " + preparation.Preview.Definition.Name;
            }
            var external = target.FindExternalOverlap(address, length);
            return external == null ? null : "Overlap with " + external;
        }
        private PatchResult ClearExited()
        {
            lock (stateLock)
            {
                foreach (var item in active.Values) item.Status = PatchStatus.TargetExited;
                active.Clear(); preparation = null; retained.Clear(); publishedBytes = 0;
            }
            RaiseChanged();
            return new PatchResult { Status = PatchStatus.TargetExited, Message = "Target exited; transient records cleared without writes." };
        }
        public void InvalidateTarget()
        {
            // This invalidates bookkeeping only. Published allocations remain owned by the target
            // process and must never be freed on an address-space/provider/session transition.
            ClearExited();
        }
        public void InvalidateMappings(Func<PatchPreview, bool> invalid)
        {
            if (invalid == null) throw new ArgumentNullException(nameof(invalid));
            foreach (var patch in ActivePatches.Where(p => invalid(p.Preview)))
            {
                patch.Status = PatchStatus.TargetExited;
                patch.Message = "The original module mapping was unloaded or replaced; its old address is no longer owned.";
                if (patch.Allocation != null) patch.Allocation.Retired = true;
                lock (stateLock)
                {
                    ActivePatch current;
                    if (active.TryGetValue(patch.Id, out current) && ReferenceEquals(current, patch)) active.Remove(patch.Id);
                }
            }
            var pending = Preparation;
            if (pending != null && invalid(pending.Preview))
            {
                pending.Preview.Status = PatchStatus.TargetExited;
                pending.Preview.Message = "The original module mapping changed; this unpublished preparation is invalid.";
                if (!pending.Published && target.IsAlive && pending.Preview.SessionId == target.SessionId && pending.Preview.ProviderIdentity == target.ProviderIdentity)
                {
                    // Never wait for the manager semaphore from the debugger worker: an operation
                    // holding it may itself be awaiting that worker. Ownership IDs make queued
                    // cleanup harmless if another operation has already settled this preparation.
                    Task.Run(() => CancelPreparationAsync(pending.Id)).ContinueWith(task =>
                    {
                        if (task.IsFaulted) System.Diagnostics.Debug.WriteLine("Invalidated preparation cleanup failed: " + task.Exception);
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            RaiseChanged();
        }
        public void InvalidateMappings(ulong moduleBase)
        {
            InvalidateMappings(preview => preview.Module != null && preview.Module.BaseAddress == moduleBase);
        }
        private void RaiseChanged()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception e) { System.Diagnostics.Debug.WriteLine("Patch state notification failed: " + e.Message); }
            }
        }
    }
}
