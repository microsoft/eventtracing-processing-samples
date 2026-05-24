// © Microsoft Corporation. All rights reserved.

using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

namespace MemoryUsageChecker
{
    /// <summary>
    /// IDisposable wrapper around the Win32 power-request API
    /// (<c>PowerCreateRequest</c> / <c>PowerSetRequest</c> / <c>PowerClearRequest</c>).
    /// Used to keep the system awake and the display on while
    /// MemoryUsageChecker is doing long-running work (symbol downloads from
    /// the public symbol server can take many minutes on a cold cache, and
    /// <c>trace.Process()</c> on a large ETL also blocks for a while).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented using the modern <c>PowerCreateRequest</c> family rather than
    /// <c>SetThreadExecutionState</c> so the active request shows up in
    /// <c>powercfg /requests</c> with a human-readable reason string. That makes
    /// it easy for the user (or a support engineer) to confirm why the machine
    /// is not entering sleep / display-off while a run is in flight.
    /// </para>
    /// <para>
    /// Failures (non-Windows host, permission denied, API not present) are
    /// logged and downgraded to a no-op so they cannot break the analysis.
    /// </para>
    /// </remarks>
    internal sealed class PowerRequest : IDisposable
    {
        private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
        private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;

        private enum PowerRequestType
        {
            PowerRequestDisplayRequired = 0,
            PowerRequestSystemRequired = 1,
            PowerRequestAwayModeRequired = 2,
            PowerRequestExecutionRequired = 3,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct REASON_CONTEXT
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern PowerRequestSafeHandle PowerCreateRequest(ref REASON_CONTEXT context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerSetRequest(PowerRequestSafeHandle handle, PowerRequestType type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerClearRequest(PowerRequestSafeHandle handle, PowerRequestType type);

        private readonly PowerRequestSafeHandle _handle;
        private readonly bool _systemSet;
        private readonly bool _displaySet;
        private readonly string _reason;
        private bool _disposed;

        private PowerRequest(PowerRequestSafeHandle handle, bool systemSet, bool displaySet, string reason)
        {
            _handle = handle;
            _systemSet = systemSet;
            _displaySet = displaySet;
            _reason = reason;
        }

        /// <summary>
        /// Registers a power request that prevents system sleep and (by default)
        /// display sleep for as long as the returned object is alive. Always wrap
        /// with <c>using</c> so the request is cleared even when an exception
        /// unwinds the stack.
        /// </summary>
        /// <param name="reason">
        /// Human-readable description shown by <c>powercfg /requests</c> under
        /// "SYSTEM" / "DISPLAY". Should identify the tool and the phase
        /// (e.g. <c>"MemoryUsageChecker: processing ETL trace"</c>).
        /// </param>
        /// <param name="keepDisplayOn">When true (default), also prevent the display from turning off.</param>
        public static IDisposable Create(string reason, bool keepDisplayOn = true)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new NoOp();
            }

            try
            {
                var ctx = new REASON_CONTEXT
                {
                    Version = POWER_REQUEST_CONTEXT_VERSION,
                    Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                    SimpleReasonString = string.IsNullOrEmpty(reason) ? "MemoryUsageChecker" : reason,
                };

                PowerRequestSafeHandle handle = PowerCreateRequest(ref ctx);
                if (handle == null || handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warn($"PowerCreateRequest failed (Win32 0x{err:X8}); system / display may sleep during long operations.");
                    handle?.Dispose();
                    return new NoOp();
                }

                bool systemSet = PowerSetRequest(handle, PowerRequestType.PowerRequestSystemRequired);
                if (!systemSet)
                {
                    Log.Warn($"PowerSetRequest(System) failed (Win32 0x{Marshal.GetLastWin32Error():X8}); system may sleep during long operations.");
                }

                bool displaySet = false;
                if (keepDisplayOn)
                {
                    displaySet = PowerSetRequest(handle, PowerRequestType.PowerRequestDisplayRequired);
                    if (!displaySet)
                    {
                        Log.Warn($"PowerSetRequest(Display) failed (Win32 0x{Marshal.GetLastWin32Error():X8}); display may turn off during long operations.");
                    }
                }

                if (!systemSet && !displaySet)
                {
                    handle.Dispose();
                    return new NoOp();
                }

                Log.Info($"Power request registered (system={systemSet}, display={displaySet}): {ctx.SimpleReasonString}");
                return new PowerRequest(handle, systemSet, displaySet, ctx.SimpleReasonString);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to register power request; system / display may sleep during long operations.", ex);
                return new NoOp();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_handle != null && !_handle.IsInvalid)
                {
                    if (_displaySet) PowerClearRequest(_handle, PowerRequestType.PowerRequestDisplayRequired);
                    if (_systemSet) PowerClearRequest(_handle, PowerRequestType.PowerRequestSystemRequired);
                    _handle.Dispose();
                    Log.Info($"Power request cleared: {_reason}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to clear power request.", ex);
            }
        }

        /// <summary>
        /// SafeHandle wrapper for the HANDLE returned by <c>PowerCreateRequest</c>.
        /// Closed with <c>CloseHandle</c> on dispose (the documented release verb
        /// for power-request handles).
        /// </summary>
        private sealed class PowerRequestSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PowerRequestSafeHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr hObject);
        }

        private sealed class NoOp : IDisposable
        {
            public void Dispose() { }
        }
    }
}
