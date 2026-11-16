using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoCopySelection
{
    static class Program
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint LLMHF_INJECTED = 0x00000001;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const ushort VK_C = 0x43;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const uint GA_ROOT = 2;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static LowLevelMouseProc _hookProc;
        private static IntPtr _hookHandle = IntPtr.Zero;
        private static readonly object _hookLock = new object();

        private static bool _hasDown = false;
        private static bool _ignoreDrag = false;
        private static int _downX;
        private static int _downY;
        private static uint _downTime;
        private static IntPtr _downHwnd;

        private static int _lastUpX = -9999;
        private static int _lastUpY = -9999;
        private static uint _lastUpTime = 0;

        private static readonly BlockingCollection<CopyRequest> _queue = new BlockingCollection<CopyRequest>(4);

        private struct CopyRequest
        {
            public int X;
            public int Y;
            public IntPtr DownHwnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string arg = args[0].ToLowerInvariant();
                if (arg == "--uninstall" || arg == "/uninstall")
                {
                    SignalExistingToExit();
                    UninstallAutostart();
                    return;
                }
                if (arg == "--stop" || arg == "/stop")
                {
                    SignalExistingToExit();
                    return;
                }
            }

            EnsureAutostart();
            TerminateOtherInstances();
            CleanupOldExecutable();

            bool createdNew;
            using (var mutex = new Mutex(true, @"Global\AutoCopySelection_Singleton", out createdNew))
            {
                if (!createdNew) return;

                StartExitListener();

                _hookProc = HookCallback;
                InstallHook();

                var worker = new Thread(WorkerLoop)
                {
                    IsBackground = true
                };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();

                SystemEvents.PowerModeChanged += (s, e) =>
                {
                    if (e.Mode == PowerModes.Resume)
                    {
                        ReinstallHook();
                    }
                };

                SystemEvents.SessionSwitch += (s, e) =>
                {
                    if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
                    {
                        ReinstallHook();
                    }
                };

                var watchdog = new System.Windows.Forms.Timer { Interval = 15000 };
                watchdog.Tick += (s, e) =>
                {
                    lock (_hookLock)
                    {
                        if (_hookHandle == IntPtr.Zero)
                        {
                            InstallHook();
                        }
                    }
                };
                watchdog.Start();

                Application.Run();
                UninstallHook();
            }
        }

        private static void InstallHook()
        {
            lock (_hookLock)
            {
                if (_hookHandle != IntPtr.Zero) return;
                using (var curProcess = Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
        }

        private static void UninstallHook()
        {
            lock (_hookLock)
            {
                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
            }
        }

        private static void ReinstallHook()
        {
            UninstallHook();
            InstallHook();
        }

        private static bool IsModifierHeld(bool includeShift)
        {
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) return true;
            if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) return true;
            if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) return true;
            if ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) return true;
            if (includeShift && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) return true;
            return false;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Не реагируем на программно сгенерированные клики (например, от скриптов)
                if ((info.flags & LLMHF_INJECTED) == 0)
                {
                    if (msg == WM_LBUTTONDOWN)
                    {
                        _hasDown = true;
                        _downX = info.pt.x;
                        _downY = info.pt.y;
                        _downTime = info.time;
                        _downHwnd = WindowFromPoint(info.pt);
                        _ignoreDrag = IsModifierHeld(false);
                    }
                    else if (msg == WM_LBUTTONUP && _hasDown)
                    {
                        _hasDown = false;
                        bool ignore = _ignoreDrag || IsModifierHeld(false);
                        _ignoreDrag = false;

                        if (!ignore)
                        {
                            int dx = info.pt.x - _downX;
                            int dy = info.pt.y - _downY;
                            bool isDrag = (dx * dx + dy * dy) >= 36; // >= 6px

                            uint dblMs = GetDoubleClickTime();
                            int lx = info.pt.x - _lastUpX;
                            int ly = info.pt.y - _lastUpY;
                            bool isMultiClick = (info.time - _lastUpTime) <= (dblMs + 60) && (lx * lx + ly * ly) <= 64;

                            _lastUpX = info.pt.x;
                            _lastUpY = info.pt.y;
                            _lastUpTime = info.time;

                            if (isDrag || isMultiClick)
                            {
                                _queue.TryAdd(new CopyRequest
                                {
                                    X = info.pt.x,
                                    Y = info.pt.y,
                                    DownHwnd = _downHwnd
                                });
                            }
                        }
                        else
                        {
                            _lastUpTime = 0;
                        }
                    }
                }
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowProcessName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return "";
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName.ToLowerInvariant();
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool ShouldSkipWindow(IntPtr downHwnd, int x, int y)
        {
            IntPtr fg = GetForegroundWindow();
            IntPtr target = downHwnd != IntPtr.Zero ? downHwnd : fg;
            if (target == IntPtr.Zero) return true;

            // Проверяем hit-test: игнорируем перетаскивание заголовков окон, рамок и скроллбаров
            IntPtr hitResult;
            IntPtr lParam = (IntPtr)((((short)y) << 16) | ((ushort)(short)x));
            IntPtr ok = SendMessageTimeout(target, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, 40, out hitResult);
            if (ok != IntPtr.Zero && hitResult.ToInt32() != HTCLIENT)
            {
                return true;
            }

            string cls = GetWindowClassName(target);
            IntPtr root = GetAncestor(target, GA_ROOT);
            string rootCls = root != IntPtr.Zero ? GetWindowClassName(root) : cls;

            // Панель задач, рабочий стол, списки файлов в Проводнике
            if (rootCls == "Shell_TrayWnd" || rootCls == "Shell_SecondaryTrayWnd" ||
                rootCls == "Progman" || rootCls == "WorkerW" ||
                cls == "SysListView32" || cls == "DirectUIHWND" || cls == "SysTreeView32")
            {
                return true;
            }

            // Терминалы: нельзя слать Ctrl+C (иначе отправит SIGINT и убьёт команду)
            if (cls == "ConsoleWindowClass" || rootCls == "ConsoleWindowClass" ||
                cls == "CASCADIA_HOSTING_WINDOW_CLASS" || rootCls == "CASCADIA_HOSTING_WINDOW_CLASS" ||
                cls == "mintty" || rootCls == "mintty")
            {
                return true;
            }

            return false;
        }

        private static void TerminateOtherInstances()
        {
            try
            {
                SignalExistingToExit();
                int currentId = Process.GetCurrentProcess().Id;
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.Id != currentId &&
                            proc.ProcessName.StartsWith("AutoCopySelection", StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Kill();
                            proc.WaitForExit(1500);
                        }
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }
        }

        private static void CleanupOldExecutable()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string oldPath = System.IO.Path.Combine(dir, "AutoCopySelection.old.exe");
                if (System.IO.File.Exists(oldPath))
                {
                    System.IO.File.Delete(oldPath);
                }
            }
            catch { }
        }

        private static void SignalExistingToExit()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(@"Global\AutoCopySelection_ExitEvent"))
                {
                    ev.Set();
                }
            }
            catch { }
        }

        private static void StartExitListener()
        {
            try
            {
                var security = new EventWaitHandleSecurity();
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                security.AddAccessRule(new EventWaitHandleAccessRule(everyone, EventWaitHandleRights.FullControl, AccessControlType.Allow));

                bool created;
                var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, @"Global\AutoCopySelection_ExitEvent", out created, security);
                var listener = new Thread(() =>
                {
                    exitEvent.WaitOne();
                    UninstallHook();
                    Environment.Exit(0);
                })
                {
                    IsBackground = true
                };
                listener.Start();
            }
            catch { }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureAutostart()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null)
                    {
                        string expected = "\"" + exePath + "\"";
                        string current = rk.GetValue("AutoCopySelection") as string;
                        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            rk.SetValue("AutoCopySelection", expected, RegistryValueKind.String);
                        }
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки реестра
            }
        }

        private static void UninstallAutostart()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null && rk.GetValue("AutoCopySelection") != null)
                    {
                        rk.DeleteValue("AutoCopySelection", false);
                    }
                }
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Delete /TN \"AutoCopySelectionTask\" /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static string TryReadSelectionViaUIA()
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused == null) return null;

                object patternObj;
                if (focused.TryGetCurrentPattern(TextPattern.Pattern, out patternObj))
                {
                    var textPattern = (TextPattern)patternObj;
                    var ranges = textPattern.GetSelection();
                    if (ranges != null && ranges.Length > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var r in ranges)
                        {
                            sb.Append(r.GetText(-1));
                        }
                        string text = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
            catch
            {
                // Не все приложения поддерживают UIA TextPattern
            }
            return null;
        }

        private static void SendCtrlC()
        {
            var inputs = new INPUT[4];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } };
            inputs[1] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_C } } };
            inputs[2] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_C, dwFlags = KEYEVENTF_KEYUP } } };
            inputs[3] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } };
            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void WorkerLoop()
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Thread.Sleep(40);
                    if (ShouldSkipWindow(req.DownHwnd, req.X, req.Y)) continue;

                    // 1. Сначала пробуем прочитать выделенный текст без эмуляции Ctrl+C
                    string uiaText = TryReadSelectionViaUIA();
                    if (!string.IsNullOrEmpty(uiaText))
                    {
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                Clipboard.SetText(uiaText);
                                break;
                            }
                            catch
                            {
                                Thread.Sleep(25);
                            }
                        }
                        continue;
                    }

                    // 2. Иначе ждём отпускания Shift (чтобы при Shift+клик не ушёл Ctrl+Shift+C) и шлём Ctrl+C
                    int waited = 0;
                    while ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 && waited < 450)
                    {
                        Thread.Sleep(25);
                        waited += 25;
                    }
                    if (IsModifierHeld(true)) continue;

                    SendCtrlC();
                }
                catch
                {
                    // Никогда не роняем фоновый цикл
                }
            }
        }
    }
}
