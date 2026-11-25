using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nightcall;

/// <summary>
/// Global low-level mouse side-button trigger. Swallows matched Down+Up pairs when a long-press (>= 350 ms)
/// or Back+Forward chord triggers Nightcall so Windows never gets stuck with an unmatched button-down state,
/// and replays a clean synthetic Down+Up pair for normal quick clicks.
/// </summary>
public sealed class MouseTriggerHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const int HoldMs = 350;
    private static readonly IntPtr SyntheticTag = new(0x4E434D54); // "NCMT"

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public Point Pt; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelMouseProc proc;
    private readonly Timer holdTimer;
    private readonly object gate = new();
    private IntPtr hook;
    private string mode = "off";
    private bool holding, holdTriggered;
    private long downTick;
    private bool down1, down2, chordTriggered;

    public event Action? Triggered;

    public MouseTriggerHook()
    {
        proc = HookCallback;
        holdTimer = new Timer(OnHoldElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Configure(string? value)
    {
        string next = value is "hold-forward" or "hold-back" or "chord" ? value : "off";
        lock (gate)
        {
            mode = next;
            holding = holdTriggered = down1 = down2 = chordTriggered = false;
            holdTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (next == "off") Uninstall();
        else Install();
    }

    private void Install()
    {
        if (hook != IntPtr.Zero) return;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
    }

    private void Uninstall()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private void OnHoldElapsed(object? _)
    {
        bool fire = false;
        lock (gate)
        {
            if (holding && !holdTriggered) { holdTriggered = true; fire = true; }
        }
        if (fire) Triggered?.Invoke();
    }

    private static void ReplayXClick(uint xbutton)
    {
        var inputs = new[]
        {
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { MouseData = xbutton, Flags = MOUSEEVENTF_XDOWN, ExtraInfo = SyntheticTag } } },
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { MouseData = xbutton, Flags = MOUSEEVENTF_XUP, ExtraInfo = SyntheticTag } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (nCode < 0 || (msg != WM_XBUTTONDOWN && msg != WM_XBUTTONUP))
            return CallNextHookEx(hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if (info.ExtraInfo == SyntheticTag)
            return CallNextHookEx(hook, nCode, wParam, lParam);

        int button = (int)((info.MouseData >> 16) & 0xFFFF); // 1 = Back (XBUTTON1), 2 = Forward (XBUTTON2)
        if (button != 1 && button != 2)
            return CallNextHookEx(hook, nCode, wParam, lParam);

        bool swallow = false;
        bool fire = false;
        uint replayButton = 0;

        lock (gate)
        {
            if (mode == "hold-forward" || mode == "hold-back")
            {
                int target = mode == "hold-forward" ? 2 : 1;
                if (button == target)
                {
                    swallow = true;
                    if (msg == WM_XBUTTONDOWN)
                    {
                        holding = true;
                        holdTriggered = false;
                        downTick = Environment.TickCount64;
                        holdTimer.Change(HoldMs, Timeout.Infinite);
                    }
                    else
                    {
                        holdTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        bool wasHolding = holding;
                        bool alreadyTriggered = holdTriggered;
                        holding = false;
                        holdTriggered = false;
                        if (alreadyTriggered) { /* already fired */ }
                        else if (wasHolding && Environment.TickCount64 - downTick >= HoldMs) fire = true;
                        else if (wasHolding) replayButton = (uint)button;
                    }
                }
            }
            else if (mode == "chord")
            {
                swallow = true;
                if (msg == WM_XBUTTONDOWN)
                {
                    if (button == 1) down1 = true; else down2 = true;
                    if (down1 && down2 && !chordTriggered) { chordTriggered = true; fire = true; }
                }
                else
                {
                    bool wasDown = button == 1 ? down1 : down2;
                    if (button == 1) down1 = false; else down2 = false;
                    if (!chordTriggered && wasDown) replayButton = (uint)button;
                    if (!down1 && !down2) chordTriggered = false;
                }
            }
        }

        if (fire) Triggered?.Invoke();
        else if (replayButton != 0) ThreadPool.QueueUserWorkItem(_ => ReplayXClick(replayButton));
        return swallow ? (IntPtr)1 : CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        holdTimer.Dispose();
        Uninstall();
    }
}
