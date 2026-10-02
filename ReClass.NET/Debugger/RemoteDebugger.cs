using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using ReClassNET.Extensions;
using ReClassNET.Forms;
using ReClassNET.Memory;

namespace ReClassNET.Debugger
{
	public partial class RemoteDebugger
	{
		private readonly object syncBreakpoint = new object();

		private readonly RemoteProcess process;

		private readonly HashSet<IBreakpoint> breakpoints = new HashSet<IBreakpoint>();

		public RemoteDebugger(RemoteProcess process)
		{
			Contract.Requires(process != null);

			this.process = process;
		}

		public void AddBreakpoint(IBreakpoint breakpoint)
		{
			Contract.Requires(breakpoint != null);

			lock (syncBreakpoint)
			{
				if (!breakpoints.Add(breakpoint))
				{
					throw new BreakpointAlreadySetException(breakpoint);
				}

				breakpoint.Set(process);
			}
		}

		public void RemoveBreakpoint(IBreakpoint bp)
		{
			Contract.Requires(bp != null);

			lock (syncBreakpoint)
			{
				if (breakpoints.Remove(bp))
				{
					bp.Remove(process);
				}
			}
		}

		public void FindWhatAccessesAddress(IntPtr address, int size)
		{
			FindCodeByBreakpoint(address, size, HardwareBreakpointTrigger.Access);
		}

		public void FindWhatWritesToAddress(IntPtr address, int size)
		{
			FindCodeByBreakpoint(address, size, HardwareBreakpointTrigger.Write);
		}

		public void FindCodeByBreakpoint(IntPtr address, int size, HardwareBreakpointTrigger trigger)
		{
			if (process.SupportsAdvancedDebugging)
			{
				if (size > 32)
				{
					// Four debug registers cover at most 4 x 8 aligned bytes.
					if (ReClassNET.UI.ThemedMessageBox.Show($"The selected value is {size} bytes, but a hardware watch covers at most 32 bytes (four 8-byte debug registers).\n\nWatch the first 8 bytes at 0x{address.ToInt64():X} instead?\n\nTip: select a single field inside the structure to watch exactly that field.", "Watch too large", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Information) != System.Windows.Forms.DialogResult.Yes)
					{
						return;
					}
					size = 8;
				}
				new WatchFinderForm(process.DebugWorkspace, unchecked((ulong)address.ToInt64()), size, trigger == HardwareBreakpointTrigger.Write).Show();
				return;
			}
			var register = GetUsableDebugRegister();
			if (register == HardwareBreakpointRegister.InvalidRegister)
			{
				throw new NoHardwareBreakpointAvailableException();
			}

			var breakpointList = SplitBreakpoint(address, size);

			var fcf = new FoundCodeForm(process, breakpointList[0].Address, trigger);

			var localBreakpoints = new List<IBreakpoint>();
			fcf.Stop += (sender, e) =>
			{
				lock (localBreakpoints)
				{
					foreach (var bp in localBreakpoints)
					{
						RemoveBreakpoint(bp);
					}

					localBreakpoints.Clear();
				}
			};

			void HandleBreakpoint(IBreakpoint bp, ref DebugEvent evt)
			{
				fcf.AddRecord(evt.ExceptionInfo);
			}

			var breakpoint = new HardwareBreakpoint(breakpointList[0].Address, register, trigger, (HardwareBreakpointSize)breakpointList[0].Size, HandleBreakpoint);
			try
			{
				AddBreakpoint(breakpoint);
				localBreakpoints.Add(breakpoint);

				fcf.Show();
			}
			catch
			{
				fcf.Dispose();

				throw;
			}

			if (breakpointList.Count > 1)
			{
				foreach (var split in breakpointList.Skip(1))
				{
					register = GetUsableDebugRegister();
					if (register == HardwareBreakpointRegister.InvalidRegister)
					{
						break;
					}

					breakpoint = new HardwareBreakpoint(split.Address, register, trigger, (HardwareBreakpointSize)split.Size, HandleBreakpoint);
					AddBreakpoint(breakpoint);
					localBreakpoints.Add(breakpoint);
				}
			}
		}

		private List<BreakpointSplit> SplitBreakpoint(IntPtr address, int size)
		{
			var splits = new List<BreakpointSplit>();

			while (size > 0)
			{
#if RECLASSNET64
				if (size >= 8)
				{
					if (address.Mod(8) == 0)
					{
						splits.Add(new BreakpointSplit { Address = address, Size = 8 });

						address += 8;
						size -= 8;

						continue;
					}
				}
#endif
				if (size >= 4)
				{
					if (address.Mod(4) == 0)
					{
						splits.Add(new BreakpointSplit { Address = address, Size = 4 });

						address += 4;
						size -= 4;

						continue;
					}
				}
				if (size >= 2)
				{
					if (address.Mod(2) == 0)
					{
						splits.Add(new BreakpointSplit { Address = address, Size = 2 });

						address += 2;
						size -= 2;

						continue;
					}
				}

				splits.Add(new BreakpointSplit { Address = address, Size = 1 });

				address += 1;
				size -= 1;
			}

			return splits;
		}

		private HardwareBreakpointRegister GetUsableDebugRegister()
		{
			var all = new HashSet<HardwareBreakpointRegister>
			{
				HardwareBreakpointRegister.Dr0,
				HardwareBreakpointRegister.Dr1,
				HardwareBreakpointRegister.Dr2,
				HardwareBreakpointRegister.Dr3
			};

			lock (syncBreakpoint)
			{
				foreach (var bp in breakpoints)
				{
					if (bp is HardwareBreakpoint hwbp)
					{
						all.Remove(hwbp.Register);
					}
				}
			}

			if (all.Count > 0)
			{
				return all.First();
			}

			return HardwareBreakpointRegister.InvalidRegister;
		}
	}

	internal class BreakpointSplit
	{
		public IntPtr Address { get; set; }
		public int Size { get; set; }
	}
}
