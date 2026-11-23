using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Tracks whether the console display is switched off (GUID_CONSOLE_DISPLAY_STATE), so the
    /// pollers can leave the NVIDIA dGPU alone while it does.
    /// </summary>
    /// <remarks>
    /// <para>When Windows turns the display off it sends a device power IRP down the display
    /// adapter's stack. On an OMEN with an RTX 3070 Laptop GPU in discrete-only mode (BIOS 15.42)
    /// and telemetry polling the card every second, that IRP never completed and
    /// Windows bugchecked 0x9F/3 (DRIVER_POWER_STATE_FAILURE, "DXG Power IRP timeout",
    /// dxgkrnl!DpiFdoHandleDevicePower waiting under nvlddmkm). It reproduced on driver
    /// 621.24 and 621.14, only with OmenCore running, and not once with it closed.</para>
    /// <para>The GPU power state probe does not help here: the card is still D0 when the poll
    /// lands, the IRP that would take it down is what the poll collides with. Hence a separate
    /// signal that is true from the moment the display is reported off.</para>
    /// <para>Windows only pushes this state (WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE), so a
    /// message-only window on its own thread receives it, as <c>LidSwitchMonitor</c> does.
    /// Registration delivers the current state straight away. Deliberately dependency-free so
    /// the hardware worker can link it as it links <see cref="GpuPowerStateProbe"/>.</para>
    /// <para>Fails open: if the window or the registration cannot be set up, the display is
    /// reported on and polling behaves as before.</para>
    /// </remarks>
    public static class DisplayStateMonitor
    {
        // {6FE69556-704A-47A0-8F24-C28D936FDA47} - GUID_CONSOLE_DISPLAY_STATE
        private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

        private const string ClassName = "OmenCoreDisplayStateMonitorWindow";
        private const uint WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private const int HWND_MESSAGE = -3;

        private static readonly object StartLock = new();
        private static bool _startAttempted;
        private static volatile bool _isDisplayOff;

        // A static field so the delegate outlives every native pointer to it.
        private static readonly WndProcDelegate WndProcHandler = WndProc;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>
        /// True while Windows reports the display off. False when it is on or dimmed, and when the
        /// state is unknown. Starts the monitor on first access; safe from any thread.
        /// </summary>
        public static bool IsDisplayOff
        {
            get
            {
                EnsureStarted();
                return _isDisplayOff;
            }
        }

        /// <summary>True once the message window and the notification registration both succeeded.</summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>
        /// Interprets a PBT_POWERSETTINGCHANGE payload for GUID_CONSOLE_DISPLAY_STATE: Data is a
        /// DWORD, 0 = off, 1 = on, 2 = dimmed. Null for any other GUID or a payload too short
        /// for a DWORD, in which case the cached state is left as it is.
        /// </summary>
        internal static bool? InterpretDisplayBroadcast(Guid settingGuid, int dataLength, int dataValue)
        {
            if (settingGuid != GuidConsoleDisplayState || dataLength < sizeof(int))
                return null;

            return dataValue == 0;
        }

        private static void EnsureStarted()
        {
            if (_startAttempted)
                return;

            lock (StartLock)
            {
                if (_startAttempted)
                    return;
                _startAttempted = true;

                if (!OperatingSystem.IsWindows())
                    return;

                try
                {
                    var pumpThread = new Thread(RunMessagePump)
                    {
                        IsBackground = true,
                        Name = "OmenCore-DisplayStateMonitor"
                    };
                    pumpThread.SetApartmentState(ApartmentState.STA);
                    pumpThread.Start();
                }
                catch (Exception)
                {
                    IsAvailable = false;
                }
            }
        }

        private static void RunMessagePump()
        {
            try
            {
                var hInstance = GetModuleHandle(null);
                var wndClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = WndProcHandler,
                    hInstance = hInstance,
                    lpszClassName = ClassName,
                    lpszMenuName = null
                };

                if (RegisterClassEx(ref wndClass) == 0)
                    return;

                var hwnd = CreateWindowEx(
                    0, ClassName, ClassName, 0, 0, 0, 0, 0,
                    new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                    return;

                var guid = GuidConsoleDisplayState;
                var notifyHandle = RegisterPowerSettingNotification(hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
                IsAvailable = notifyHandle != IntPtr.Zero;

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception)
            {
                IsAvailable = false;
                _isDisplayOff = false;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                try
                {
                    // POWERBROADCAST_SETTING: GUID PowerSetting (16 bytes), DWORD DataLength,
                    // UCHAR Data[DataLength].
                    var settingGuid = Marshal.PtrToStructure<Guid>(lParam);
                    var dataLength = Marshal.ReadInt32(lParam, 16);
                    var dataValue = dataLength >= sizeof(int) ? Marshal.ReadInt32(lParam, 20) : 1;

                    var interpreted = InterpretDisplayBroadcast(settingGuid, dataLength, dataValue);
                    if (interpreted.HasValue)
                        _isDisplayOff = interpreted.Value;
                }
                catch (Exception)
                {
                    // Keep the last known state on a malformed payload.
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
