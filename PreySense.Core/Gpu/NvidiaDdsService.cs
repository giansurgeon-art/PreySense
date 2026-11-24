using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PreySense.Gpu
{
    public enum DdsDisplayMode
    {
        Optimus = 0,
        Automatic = 1,
        NvidiaGpuOnly = 2
    }

    public enum DdsActiveMux
    {
        Unknown = 0,
        Optimus = 1, // iGPU driving display
        dGPU = 2     // NVIDIA GPU driving display directly
    }

    public record DdsStatus(
        bool IsSupported,
        bool IsAutomatic,
        DdsActiveMux ActiveMux,
        uint KmdResponse,
        uint SrType
    )
    {
        public bool Matches(DdsDisplayMode mode) => IsSupported && KmdResponse == 0 && mode switch
        {
            DdsDisplayMode.Automatic => IsAutomatic && ActiveMux != DdsActiveMux.Unknown,
            DdsDisplayMode.Optimus => !IsAutomatic && ActiveMux == DdsActiveMux.Optimus,
            DdsDisplayMode.NvidiaGpuOnly => !IsAutomatic && ActiveMux == DdsActiveMux.dGPU,
            _ => false
        };
    }

    /// <summary>
    /// Native NVIDIA Advanced Optimus (Dynamic Display Switch / DDS) Service.
    /// Interfaces directly with nvcpl.dll (NvCplApi) exported by NVIDIA App / Control Panel
    /// to provide genuine, reboot-free 3-mode display switching.
    ///
    /// nvcpl.dll is only loaded and initialized for the duration of a single call
    /// (a "session"), then NvCplApiClose + FreeLibrary release it again. Keeping it
    /// initialized for the life of the process leaves a live NVIDIA display-driver
    /// session open, which can stop the driver's own automatic switch back to the
    /// iGPU after a game closes.
    /// </summary>
    public sealed class NvidiaDdsService : IDisposable
    {
        private static readonly Lazy<NvidiaDdsService> _instance = new(() => new NvidiaDdsService());
        public static NvidiaDdsService Instance => _instance.Value;

        private const uint InfoTypeDdsStatus = 0x17;
        private const uint SettingIdDdsState = 0x332;
        private const uint SettingIdBlockingApps = 0x333;
        private const uint CommandCommit = 0x10000;

        // After COMMIT the session stays open while the driver finishes the switch.
        private const int SettleAttempts = 32;
        private const int SettlePollMs = 250;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiInitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiCloseDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiGetInfoDelegate(uint infoType, uint param, IntPtr pData, ref ulong pSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiGetSettingDelegate(uint target, out uint status, uint settingId, IntPtr pData, ulong dataSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiSetSettingDelegate(uint target, out uint status, uint settingId, IntPtr pData, ulong dataSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvCplApiExecuteDelegate(uint command, uint param1, int param2);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct NativeDdsStatusInfo
        {
            public byte IsSupported;   // 1 if supported
            public byte IsAutomatic;   // 1 if Auto, 0 if Manual
            public byte Reserved1;
            public byte Reserved2;
            public uint MuxState;      // 1 = Optimus, 2 = dGPU
            public uint KmdResponse;   // 0 = Response_Ok
            public uint SrType;        // PSR type
        }

        /// <summary>One LoadLibrary + NvCplApiInit ... NvCplApiClose + FreeLibrary lifetime.</summary>
        private sealed class Session : IDisposable
        {
            public IntPtr Module = IntPtr.Zero;
            public bool Initialized;
            public NvCplApiCloseDelegate? Close;
            public NvCplApiGetInfoDelegate? GetInfo;
            public NvCplApiGetSettingDelegate? GetSetting;
            public NvCplApiSetSettingDelegate? SetSetting;
            public NvCplApiExecuteDelegate? Execute;

            public void Dispose()
            {
                if (Initialized)
                {
                    try { Close?.Invoke(); } catch { }
                    Initialized = false;
                }

                GetInfo = null;
                GetSetting = null;
                SetSetting = null;
                Execute = null;
                Close = null;

                if (Module != IntPtr.Zero)
                {
                    try { FreeLibrary(Module); } catch { }
                    Module = IntPtr.Zero;
                }
            }
        }

        private readonly object _syncLock = new();
        private bool _loggedInitialized;
        private string? _lastOpenFailure;
        private DateTime _lastOpenFailureLogTime = DateTime.MinValue;
        private int _lastGetInfoRet;
        private DateTime _lastGetInfoLogTime = DateTime.MinValue;

        public bool IsAvailable
        {
            get
            {
                lock (_syncLock)
                {
                    using var session = OpenSessionLocked();
                    return session != null;
                }
            }
        }

        public NvidiaDdsService()
        {
        }

        private string? ResolveNvCplPath()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] searchPaths =
            [
                Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA App", "NvCpl", "nvcpl.dll"),
                Path.Combine(programFiles, "NVIDIA Corporation", "Control Panel Client", "nvcpl.dll"),
                Path.Combine(Environment.SystemDirectory, "nvcpl.dll")
            ];

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private void LogOpenFailure(string message)
        {
            if (message != _lastOpenFailure || (DateTime.UtcNow - _lastOpenFailureLogTime).TotalSeconds >= 30)
            {
                _lastOpenFailure = message;
                _lastOpenFailureLogTime = DateTime.UtcNow;
                AppLogger.Log(message);
            }
        }

        /// <summary>
        /// Loads nvcpl.dll and initializes its API. The caller owns the returned session
        /// and must dispose it so the NVIDIA session is released again.
        /// </summary>
        private Session? OpenSessionLocked()
        {
            if (DiagSwitches.Has("dds")) return null;

            string? dllPath = ResolveNvCplPath();
            if (string.IsNullOrEmpty(dllPath))
            {
                LogOpenFailure("NvidiaDdsService: nvcpl.dll could not be found.");
                return null;
            }

            var session = new Session();
            try
            {
                session.Module = LoadLibraryW(dllPath);
                if (session.Module == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    LogOpenFailure($"NvidiaDdsService: LoadLibrary failed for {dllPath} (Error {error}).");
                    return null;
                }

                IntPtr pInit = GetProcAddress(session.Module, "NvCplApiInit");
                IntPtr pClose = GetProcAddress(session.Module, "NvCplApiClose");
                IntPtr pGetInfo = GetProcAddress(session.Module, "NvCplApiGetInfo");
                IntPtr pGetSetting = GetProcAddress(session.Module, "NvCplApiGetSetting");
                IntPtr pSetSetting = GetProcAddress(session.Module, "NvCplApiSetSetting");
                IntPtr pExecute = GetProcAddress(session.Module, "NvCplApiExecute");

                if (pInit == IntPtr.Zero || pClose == IntPtr.Zero || pGetInfo == IntPtr.Zero ||
                    pGetSetting == IntPtr.Zero || pSetSetting == IntPtr.Zero || pExecute == IntPtr.Zero)
                {
                    LogOpenFailure("NvidiaDdsService: Required NvCplApi exports not found in nvcpl.dll.");
                    return null;
                }

                var init = Marshal.GetDelegateForFunctionPointer<NvCplApiInitDelegate>(pInit);
                session.Close = Marshal.GetDelegateForFunctionPointer<NvCplApiCloseDelegate>(pClose);
                session.GetInfo = Marshal.GetDelegateForFunctionPointer<NvCplApiGetInfoDelegate>(pGetInfo);
                session.GetSetting = Marshal.GetDelegateForFunctionPointer<NvCplApiGetSettingDelegate>(pGetSetting);
                session.SetSetting = Marshal.GetDelegateForFunctionPointer<NvCplApiSetSettingDelegate>(pSetSetting);
                session.Execute = Marshal.GetDelegateForFunctionPointer<NvCplApiExecuteDelegate>(pExecute);

                int initResult = init();
                if (initResult != 0)
                {
                    LogOpenFailure($"NvidiaDdsService: NvCplApiInit returned {initResult}.");
                    return null;
                }

                session.Initialized = true;
                if (!_loggedInitialized)
                {
                    _loggedInitialized = true;
                    AppLogger.Log($"NvidiaDdsService: nvcpl.dll available at {dllPath} (version {System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath).FileVersion}). It is opened per call and released afterwards.");
                }

                var opened = session;
                session = null!;
                return opened;
            }
            catch (Exception ex)
            {
                LogOpenFailure($"NvidiaDdsService: Initialization exception: {ex.Message}");
                return null;
            }
            finally
            {
                // Any early return or exception above lands here with a still-owned session.
                session?.Dispose();
            }
        }

        public bool IsDdsSupported()
        {
            var status = GetStatus();
            return status?.IsSupported ?? false;
        }

        public DdsStatus? GetStatus()
        {
            lock (_syncLock)
            {
                using var session = OpenSessionLocked();
                if (session == null) return null;
                return ReadStatusLocked(session);
            }
        }

        private DdsStatus? ReadStatusLocked(Session session)
        {
            if (session.GetInfo == null) return null;

            ulong size = 16;
            IntPtr pStatus = Marshal.AllocHGlobal(16);
            try
            {
                for (int i = 0; i < 16; i++) Marshal.WriteByte(pStatus, i, 0);

                int ret = session.GetInfo(InfoTypeDdsStatus, 0, pStatus, ref size);
                if (ret == 0 && _lastGetInfoRet != 0)
                {
                    AppLogger.Log("NvidiaDdsService: NvCplApiGetInfo(0x17) recovered.");
                    _lastGetInfoRet = 0;
                }
                if (ret == 0)
                {
                    var info = Marshal.PtrToStructure<NativeDdsStatusInfo>(pStatus);
                    var activeMux = info.MuxState switch
                    {
                        1 => DdsActiveMux.Optimus,
                        2 => DdsActiveMux.dGPU,
                        _ => DdsActiveMux.Unknown
                    };

                    return new DdsStatus(
                        IsSupported: info.IsSupported != 0,
                        IsAutomatic: info.IsAutomatic != 0,
                        ActiveMux: activeMux,
                        KmdResponse: info.KmdResponse,
                        SrType: info.SrType
                    );
                }

                if (ret != _lastGetInfoRet || (DateTime.UtcNow - _lastGetInfoLogTime).TotalSeconds >= 30)
                {
                    _lastGetInfoRet = ret;
                    _lastGetInfoLogTime = DateTime.UtcNow;
                    AppLogger.Log($"NvidiaDdsService: NvCplApiGetInfo(0x17) status unavailable: ret={ret} ({DescribeStatusReadFailure(ret)}), returned size={size}. No display switch was requested by this read.");
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(pStatus);
            }
        }

        // Verified against NVIDIA App nvcpl.dll 8.17.15.5527's exception-to-result mapping.
        // Keep the raw code: this private API's mappings can change between versions.
        internal static string DescribeStatusReadFailure(int result) => result switch
        {
            1 => "general NVIDIA API failure",
            9 => "access denied in NVIDIA API",
            _ => "unclassified NVIDIA API failure"
        };

        public bool HasBlockingApps()
        {
            lock (_syncLock)
            {
                using var session = OpenSessionLocked();
                if (session == null) return false;
                return HasBlockingAppsLocked(session);
            }
        }

        private static bool HasBlockingAppsLocked(Session session)
        {
            if (session.GetSetting == null) return false;

            IntPtr pBlocker = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(pBlocker, 0);
                uint status;
                int ret = session.GetSetting(1, out status, SettingIdBlockingApps, pBlocker, 1);
                if (ret == 0 && status == 0)
                {
                    byte val = Marshal.ReadByte(pBlocker);
                    return val != 0;
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pBlocker);
            }
        }

        public (bool Success, string Message) SetMode(DdsDisplayMode mode)
        {
            lock (_syncLock)
            {
                using var session = OpenSessionLocked();
                if (session == null || session.SetSetting == null || session.Execute == null)
                {
                    return (false, "NVIDIA DDS Service is unavailable.");
                }

                if (HasBlockingAppsLocked(session))
                {
                    AppLogger.Log("NvidiaDdsService: Warning: Active 3D application or display hook detected during switch.");
                }

                bool isAutomatic = mode == DdsDisplayMode.Automatic;
                uint muxState = mode switch
                {
                    DdsDisplayMode.NvidiaGpuOnly => 2, // dGPU
                    DdsDisplayMode.Optimus => 1,       // Optimus / iGPU
                    _ => 1                             // Auto uses Optimus base
                };

                IntPtr pBuf = Marshal.AllocHGlobal(16);
                try
                {
                    for (int i = 0; i < 16; i++) Marshal.WriteByte(pBuf, i, 0);
                    Marshal.WriteByte(pBuf, 0, 1);                      // Version / Enabled
                    Marshal.WriteByte(pBuf, 1, (byte)(isAutomatic ? 1 : 0)); // IsAutomatic
                    Marshal.WriteInt32(pBuf, 4, (int)muxState);         // Target MUX State

                    uint status = 0;
                    int setRet = session.SetSetting(1, out status, SettingIdDdsState, pBuf, 16);
                    if (setRet != 0 || status != 0)
                    {
                        AppLogger.Log($"NvidiaDdsService: NvCplApiSetSetting failed (ret={setRet}, status=0x{status:X}).");
                        return (false, $"Setting DDS state failed (Code {setRet}, driver status 0x{status:X}).");
                    }

                    // Apply staged display settings immediately (NVCPL_COMMAND_COMMIT)
                    int execRet = session.Execute(CommandCommit, 0, -1);
                    if (execRet != 0)
                    {
                        AppLogger.Log($"NvidiaDdsService: NvCplApiExecute(COMMIT) returned {execRet}.");
                        return (false, $"Commit display mode failed (Code {execRet}).");
                    }

                    // Keep this session open until the driver reports the new state (or the
                    // wait runs out), so closing nvcpl cannot interrupt a switch in progress.
                    bool settled = false;
                    for (int attempt = 0; attempt < SettleAttempts; attempt++)
                    {
                        var current = ReadStatusLocked(session);
                        if (current?.Matches(mode) == true)
                        {
                            settled = true;
                            break;
                        }
                        if (attempt < SettleAttempts - 1) Thread.Sleep(SettlePollMs);
                    }

                    AppLogger.Log(settled
                        ? $"NvidiaDdsService: Display switch applied and confirmed: Mode={mode} (Auto={isAutomatic}, Mux={muxState})."
                        : $"NvidiaDdsService: Display switch committed: Mode={mode} (Auto={isAutomatic}, Mux={muxState}); the driver had not reported the new state yet.");
                    return (true, $"GPU Display Mode set to {mode}.");
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuf);
                }
            }
        }

        public void Dispose()
        {
            // Nothing stays open between calls; sessions are released as each call finishes.
            lock (_syncLock)
            {
                _loggedInitialized = false;
                _lastOpenFailure = null;
            }
        }
    }
}
