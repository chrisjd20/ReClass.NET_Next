#include <windows.h>
#include <algorithm>
#include <limits>
#include <vector>

#include "NativeCore.hpp"

bool RC_CallConv WriteRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size)
{
    if (!handle || !buffer || offset < 0 || size <= 0) return false;
    const uintptr_t destination = reinterpret_cast<uintptr_t>(address);
    const uintptr_t source = reinterpret_cast<uintptr_t>(buffer);
    if (destination > std::numeric_limits<uintptr_t>::max() - static_cast<uintptr_t>(size - 1) ||
        source > std::numeric_limits<uintptr_t>::max() - static_cast<uintptr_t>(offset)) return false;
    const void* bytes = reinterpret_cast<const void*>(source + offset);
    struct Region { uintptr_t address; SIZE_T length; DWORD protection; bool changed; };
    std::vector<Region> regions;
    try
    {
        uintptr_t cursor = destination;
        SIZE_T remaining = static_cast<SIZE_T>(size);
        while (remaining)
        {
            MEMORY_BASIC_INFORMATION information{};
            if (!VirtualQueryEx(handle, reinterpret_cast<void*>(cursor), &information, sizeof(information)) ||
                information.State != MEM_COMMIT || (information.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
            const uintptr_t base = reinterpret_cast<uintptr_t>(information.BaseAddress);
            if (base > cursor || information.RegionSize <= cursor - base) return false;
            const SIZE_T length = std::min<SIZE_T>(remaining, information.RegionSize - (cursor - base));
            regions.push_back({cursor, length, information.Protect, false});
            remaining -= length;
            if (remaining) cursor += length;
        }
    }
    catch (...) { return false; }
    bool success = true;
    for (auto& region : regions)
    {
        DWORD ignored = 0;
        const DWORD base = region.protection & 0xff;
        const bool executable = base == PAGE_EXECUTE || base == PAGE_EXECUTE_READ ||
            base == PAGE_EXECUTE_READWRITE || base == PAGE_EXECUTE_WRITECOPY;
        if (!VirtualProtectEx(handle, reinterpret_cast<void*>(region.address), region.length,
            executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE, &ignored)) { success = false; break; }
        region.changed = true;
    }
    bool attempted = false;
    if (success)
    {
        attempted = true; SIZE_T written = 0;
        success = WriteProcessMemory(handle, address, bytes, static_cast<SIZE_T>(size), &written) && written == static_cast<SIZE_T>(size);
    }
    // Restore every changed region, including a failed/short-write path.
    for (const auto& region : regions)
    {
        if (!region.changed) continue;
        DWORD ignored = 0;
        if (!VirtualProtectEx(handle, reinterpret_cast<void*>(region.address), region.length, region.protection, &ignored)) success = false;
    }
    if (attempted && !FlushInstructionCache(handle, address, static_cast<SIZE_T>(size))) success = false;
    return success;
}
