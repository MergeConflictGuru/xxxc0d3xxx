$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$source = @'
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class AutomatoClipboardListener : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public AutomatoClipboardListener()
    {
        var parameters = new CreateParams
        {
            Caption = "Automato Clipboard Listener",
            Parent = HWND_MESSAGE
        };
        CreateHandle(parameters);
        if (!AddClipboardFormatListener(Handle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_CLIPBOARDUPDATE)
        {
            Console.Out.WriteLine("CHANGE");
            Console.Out.Flush();
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms
$listener = [AutomatoClipboardListener]::new()
try {
    [System.Windows.Forms.Application]::Run()
}
finally {
    $listener.Dispose()
}
