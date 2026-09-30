using System;
using System.Runtime.InteropServices;
using ReClassNET.Native;

namespace ReClassNET.Core
{
    public partial class NativeCoreWrapper : IAdvancedDebugProvider
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong QueryAdvancedDelegate(uint abi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ExecuteAdvancedDelegate(ref AdvancedRequest request, ref AdvancedContext context, [In,Out] byte[] buffer, ulong length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WaitAdvancedDelegate(uint timeout, ref AdvancedEvent evt);
        private ExecuteAdvancedDelegate executeAdvanced;
        private WaitAdvancedDelegate waitAdvanced;
        public AdvancedCapabilities Capabilities { get; private set; }
        private void LoadAdvanced(IntPtr handle)
        {
            var queryAddress=NativeMethods.GetProcAddress(handle,"RcDebugQueryV1");
            if(queryAddress==IntPtr.Zero) return;
            var query=Marshal.GetDelegateForFunctionPointer<QueryAdvancedDelegate>(queryAddress);
            var caps=(AdvancedCapabilities)query(1);
            if ((caps&AdvancedCapabilities.Session)==0) return;
            var executeAddress=NativeMethods.GetProcAddress(handle,"RcDebugExecuteV1");
            var waitAddress=NativeMethods.GetProcAddress(handle,"RcDebugWaitV1");
            if(executeAddress==IntPtr.Zero || waitAddress==IntPtr.Zero) return;
            executeAdvanced=Marshal.GetDelegateForFunctionPointer<ExecuteAdvancedDelegate>(executeAddress);
            waitAdvanced=Marshal.GetDelegateForFunctionPointer<WaitAdvancedDelegate>(waitAddress);
            Capabilities=caps;
        }
        public int Execute(ref AdvancedRequest request,ref AdvancedContext context,byte[] buffer)
        {
            if(executeAdvanced==null) throw new NotSupportedException("The selected provider has no advanced debugger ABI v1.");
            return executeAdvanced(ref request,ref context,buffer,(ulong)(buffer?.LongLength??0));
        }
        public int Wait(uint timeoutMilliseconds,ref AdvancedEvent evt)
        {
            if(waitAdvanced==null) throw new NotSupportedException("The selected provider has no advanced debugger ABI v1.");
            return waitAdvanced(timeoutMilliseconds,ref evt);
        }
    }
}
