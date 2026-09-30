using System;

namespace ReClassNET.Memory
{
	public class Module
	{
		public Guid InstanceId { get; internal set; } = Guid.NewGuid();
		public IntPtr Start { get; set; }
		public IntPtr End { get; set; }
		public IntPtr Size { get; set; }
		public string Name { get; set; }
		public string Path { get; set; }
	}
}
