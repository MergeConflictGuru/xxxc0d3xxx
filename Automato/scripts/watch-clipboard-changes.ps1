$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class AutomatoClipboardWindow : NativeWindow
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public AutomatoClipboardWindow()
    {
        CreateHandle(new CreateParams());
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

    public void Shutdown()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
"@ -ReferencedAssemblies System.Windows.Forms

$window = [AutomatoClipboardWindow]::new()
try {
    [System.Windows.Forms.Application]::Run()
}
finally {
    $window.Shutdown()
}
