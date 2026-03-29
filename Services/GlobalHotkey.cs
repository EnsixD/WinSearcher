using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinSearcher.Services;

/// <summary>
/// Intercepts Win+Q without letting Windows Search open.
///
/// Core strategy — eat Win-down immediately, decide later:
///   Win-down arrives  → eat it, set _pendingWin=true, remember which Win key
///   Next key is Q     → this is our hotkey: eat Q-down, fire event,
///                        eat the real Win-up when it arrives → clean, no stuck
///   Next key is NOT Q → re-inject Win-down (synthetic, ignored by this hook)
///                        then let the non-Q key through → Win+D / Win+E / etc. work
///   Win-up arrives    → if _pendingWin (no key pressed while Win held) →
///                        re-inject Win-down+up so Start Menu opens
///
/// This prevents Windows from ever seeing Win+Q because Win-down is eaten
/// before Windows can register the combination.
/// </summary>
public class GlobalHotkey : IDisposable
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
                                                   IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    // x64: type(4) + implicit padding(4) + union at offset 8
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint       type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_Q   = 0x51;

    private const int  INPUT_KEYBOARD  = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // Our synthetic events carry this marker — hook ignores them
    private static readonly IntPtr OurMarker = new(0x574E_5351); // "WNSQ"

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _hookProc;  // prevent GC collection
    private readonly Dispatcher _dispatcher;

    // Win key is currently held AND we ate Win-down (hasn't been re-injected yet)
    private bool _pendingWin;
    // Win+Q was our hotkey — we ate Win-down + Q-down, waiting to eat Win-up
    private bool _hotkeyFired;
    // Which Win key (L or R)
    private uint _winVk;

    public event Action? HotkeyPressed;

    // ── Constructor ───────────────────────────────────────────────────────────

    public GlobalHotkey(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc   = HookCallback;

        using var proc   = Process.GetCurrentProcess();
        using var module = proc.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(module.ModuleName!), 0);
    }

    // ── Hook callback ─────────────────────────────────────────────────────────

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Ignore our own injected synthetic events
        if (kbd.dwExtraInfo == OurMarker)
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int  msg  = wParam.ToInt32();
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up   = msg is WM_KEYUP   or WM_SYSKEYUP;
        bool isWin = kbd.vkCode is VK_LWIN or VK_RWIN;

        // ── Win key down ──────────────────────────────────────────────────────
        if (isWin && down)
        {
            _winVk       = kbd.vkCode;
            _pendingWin  = true;   // buffered — not yet sent to OS
            _hotkeyFired = false;
            return (IntPtr)1;      // eat it — we'll decide later
        }

        // ── Win key up ────────────────────────────────────────────────────────
        if (isWin && up)
        {
            if (_hotkeyFired)
            {
                // Win+Q was handled: Win-down was eaten, hotkey fired, eat Win-up too
                _hotkeyFired = false;
                _pendingWin  = false;
                return (IntPtr)1;
            }
            if (_pendingWin)
            {
                // Win pressed and released with no other key → open Start Menu
                _pendingWin = false;
                InjectWinPress(_winVk);   // down + up = Start Menu
                return (IntPtr)1;
            }
            // Win was already re-injected (another key was pressed) — let real up through
            _pendingWin = false;
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // ── Any other key ─────────────────────────────────────────────────────
        if (_pendingWin && down)
        {
            if (kbd.vkCode == VK_Q)
            {
                // Win+Q → our hotkey
                _pendingWin  = false;
                _hotkeyFired = true;
                // Win-down was eaten, Q-down eaten → no Win+Q reaches Windows
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, HotkeyPressed);
                return (IntPtr)1;
            }
            else
            {
                // Win+Something else (Win+D, Win+E, Win+L, ...)
                // Re-inject Win-down first so OS sees it, then let current key through
                _pendingWin = false;
                InjectWinDown(_winVk);
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // ── Input injection ───────────────────────────────────────────────────────

    private void InjectWinDown(uint vk)
    {
        var inputs = new[] { MakeKey(vk, false) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private void InjectWinPress(uint vk)
    {
        var inputs = new[] { MakeKey(vk, false), MakeKey(vk, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKey(uint vk, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        // Win keys are extended keys on some systems
        if (vk is VK_LWIN or VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki   = new KEYBDINPUT
            {
                wVk         = (ushort)vk,
                dwFlags     = flags,
                dwExtraInfo = OurMarker
            }
        };
    }

    // ── Compatibility stubs ───────────────────────────────────────────────────
    public bool Register(uint modifiers, uint key, int id) => _hookHandle != IntPtr.Zero;
    public void Unregister(int id) { }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
