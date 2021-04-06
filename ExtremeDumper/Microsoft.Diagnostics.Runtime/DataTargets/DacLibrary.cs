// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime.DacInterface;
using Microsoft.Diagnostics.Runtime.Utilities;

namespace Microsoft.Diagnostics.Runtime {
	internal sealed class DacLibrary : IDisposable {
		private bool _disposed;
		private SOSDac? _sos;

		internal DacDataTargetWrapper DacDataTarget { get; }

		public RefCountedFreeLibrary? OwningLibrary { get; }

		internal ClrDataProcess InternalDacPrivateInterface { get; }

		public ClrDataProcess DacPrivateInterface => new(this, InternalDacPrivateInterface);

		private SOSDac GetSOSInterfaceNoAddRef() {
			if (_sos is null) {
				_sos = InternalDacPrivateInterface.GetSOSDacInterface();
				if (_sos is null)
					throw new InvalidOperationException("This runtime does not support ISOSDac.");
			}

			return _sos;
		}

		public SOSDac SOSDacInterface {
			get {
				var sos = GetSOSInterfaceNoAddRef();
				return new SOSDac(this, sos);
			}
		}

		public T? GetInterface<T>(in Guid riid)
			where T : CallableCOMWrapper {
			var pUnknown = InternalDacPrivateInterface.QueryInterface(riid);
			if (pUnknown == IntPtr.Zero)
				return null;

			var t = (T)Activator.CreateInstance(typeof(T), this, pUnknown)!;
			return t;
		}

		internal static IntPtr TryGetDacPtr(object ix) {
			if (ix is not IntPtr pUnk) {
				if (Marshal.IsComObject(ix))
					pUnk = Marshal.GetIUnknownForObject(ix);
				else
					pUnk = IntPtr.Zero;
			}

			if (pUnk == IntPtr.Zero)
				throw new ArgumentException("clrDataProcess not an instance of IXCLRDataProcess");

			return pUnk;
		}

		public DacLibrary(DataTarget dataTarget, IntPtr pClrDataProcess) {
			if (dataTarget is null)
				throw new ArgumentNullException(nameof(dataTarget));

			if (pClrDataProcess == IntPtr.Zero)
				throw new ArgumentNullException(nameof(pClrDataProcess));

			InternalDacPrivateInterface = new ClrDataProcess(this, pClrDataProcess);
			DacDataTarget = new DacDataTargetWrapper(dataTarget);
		}

		public unsafe DacLibrary(DataTarget dataTarget, string dacPath) {
			if (dataTarget is null)
				throw new ArgumentNullException(nameof(dataTarget));

			if (dataTarget.ClrVersions.Length == 0)
				throw new ClrDiagnosticsException("Process is not a CLR process!");

			IntPtr dacLibrary;
			try {
				dacLibrary = DataTarget.PlatformFunctions.LoadLibrary(dacPath);
			}
			catch (Exception e) when (e is DllNotFoundException || e is BadImageFormatException) {
				throw new ClrDiagnosticsException("Failed to load dac: " + e.Message, e);
			}

			OwningLibrary = new RefCountedFreeLibrary(dacLibrary);

			var initAddr = DataTarget.PlatformFunctions.GetLibraryExport(dacLibrary, "DAC_PAL_InitializeDLL");
			if (initAddr == IntPtr.Zero)
				initAddr = DataTarget.PlatformFunctions.GetLibraryExport(dacLibrary, "PAL_InitializeDLL");

			if (initAddr != IntPtr.Zero) {
				var dllMain = DataTarget.PlatformFunctions.GetLibraryExport(dacLibrary, "DllMain");
				if (dllMain == IntPtr.Zero)
					throw new ClrDiagnosticsException("Failed to obtain Dac DllMain");

				var main = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)dllMain;
				main(dacLibrary, 1, IntPtr.Zero);
			}

			var addr = DataTarget.PlatformFunctions.GetLibraryExport(dacLibrary, "CLRDataCreateInstance");
			if (addr == IntPtr.Zero)
				throw new ClrDiagnosticsException("Failed to obtain Dac CLRDataCreateInstance");

			DacDataTarget = new DacDataTargetWrapper(dataTarget);

			var func = (delegate* unmanaged[Stdcall]<in Guid, IntPtr, out IntPtr, int>)addr;
			var guid = new Guid("5c552ab6-fc09-4cb3-8e36-22fa03c798b7");
			int res = func(guid, DacDataTarget.IDacDataTarget, out var iUnk);

			if (res != 0)
				throw new ClrDiagnosticsException($"Failure loading DAC: CreateDacInstance failed 0x{res:x}", res);

			InternalDacPrivateInterface = new ClrDataProcess(this, iUnk);
		}

		internal void Flush() {
			DacDataTarget.Flush();
		}

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		~DacLibrary() {
			Dispose(false);
		}

		private void Dispose(bool _) {
			if (!_disposed) {
				InternalDacPrivateInterface?.Dispose();
				_sos?.Dispose();
				OwningLibrary?.Release();

				_disposed = true;
			}
		}
	}
}
