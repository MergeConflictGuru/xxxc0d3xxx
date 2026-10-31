using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopFileBridge;
record DropRegion(int Pid, double X, double Y, double Width, double Height, double ViewWidth, double ViewHeight, string Context, string Client);
record ReceivedDrop(string Id, string[] Files, string Context, string Client);
sealed class SeamlessDrop : Form
{
    readonly Action<string[],DropRegion> receive;
    readonly System.Windows.Forms.Timer timer=new(){Interval=40};
    readonly Native.WinEvent callback;
    IntPtr hook; bool dragging; DateTime dragStarted;
    DropRegion? region; DateTime updated;
    bool mouseHeld, explorerGesture; Point mouseStart; int explorerSourcePid, gestureStarts, dragEnters, dropsReceived;
    string? explorerSourceSid, targetSid;
    public SeamlessDrop(Action<string[],DropRegion> receive)
    {
        this.receive=receive;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        BackColor=Color.Black;Opacity=0.01;AllowDrop=true;
        callback=OnEvent;
        hook=Native.SetWinEventHook(14,15,IntPtr.Zero,callback,0,0,0x0002);
        timer.Tick+=(_,_)=>TickOverlay();timer.Start();
        DragEnter+=(_,e)=>{dragEnters++;e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;};
        DragOver+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=(_,e)=>
        {
            dropsReceived++;
            var target=region;
            var files=e.Data?.GetData(DataFormats.FileDrop) as string[];
            // The target lease is short and must still be valid at the actual drop.
            var valid=target!=null && DateTime.UtcNow-updated<TimeSpan.FromSeconds(2);
            dragging=false;Hide();
            if(valid&&files is {Length:>0})receive(files!,target!);
        };
    }
    public static string SelfTest()
    {
        using var overlay=new SeamlessDrop((_,_)=>throw new Exception("Unexpected file receipt"));
        var before=Native.GetForegroundWindow();
        overlay.Bounds=new Rectangle(-30000,-30000,20,20);
        overlay.Show();Application.DoEvents();
        if(Native.GetForegroundWindow()!=before)throw new Exception("Overlay stole foreground focus");
        overlay.Hide();
        overlay.UpdateRegion(new DropRegion(Environment.ProcessId,0,0,100,100,1000,700,"test","test"));
        overlay.TickOverlay();
        if(overlay.Visible)throw new Exception("Idle overlay intercepted the desktop");
        overlay.OnEvent(IntPtr.Zero,14,overlay.Handle,0,0,0,0);
        overlay.TickOverlay();
        if(overlay.Visible)throw new Exception("Non-Explorer drag source was accepted");
        overlay.UpdateRegion(null);
        if(overlay.Visible)throw new Exception("Disabled overlay remained visible");
        if(GestureReady(false,true,Point.Empty,new Point(100,100)))throw new Exception("Non-Explorer gesture accepted");
        if(GestureReady(true,false,Point.Empty,new Point(100,100)))throw new Exception("Released gesture accepted");
        if(GestureReady(true,true,Point.Empty,Point.Empty))throw new Exception("Click treated as drag");
        if(!GestureReady(true,true,Point.Empty,new Point(100,100)))throw new Exception("Explorer drag fallback rejected");
        if(ShouldProxy("S-1-5-21-1","S-1-5-21-1"))throw new Exception("Same-user drop incorrectly selected proxy path");
        if(!ShouldProxy("S-1-5-21-1","S-1-5-21-2"))throw new Exception("Cross-user drop bypassed proxy path");
        if(!ShouldProxy(null,"S-1-5-21-1"))throw new Exception("Unknown identity must preserve cross-user fallback");
        if(overlay.hook==IntPtr.Zero)throw new Exception("Windows drag-event hook could not be installed");
        return "PASS: no foreground activation; same-user drags stay native; cross-user drags use the proxy; unknown identity fails closed; overlay remains hidden while idle.";
    }
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams { get {var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000080;return p;} }
    protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
    public object Status => new { dragging, visible=Visible, hasTarget=region!=null, targetAgeMs=(DateTime.UtcNow-updated).TotalMilliseconds, hookInstalled=hook!=IntPtr.Zero, gestureStarts, dragEnters, dropsReceived, dragEvents, lastSourcePid, target=region };
    int dragEvents; uint lastSourcePid;
    public void UpdateRegion(DropRegion? value){var oldPid=region?.Pid;region=value;if(value is null)targetSid=null;else if(oldPid!=value.Pid||targetSid is null)targetSid=ProcessUserSid(value.Pid);updated=DateTime.UtcNow;if(value==null){dragging=false;Hide();}}
    void OnEvent(IntPtr h,uint kind,IntPtr window,int obj,int child,uint thread,uint timestamp)
    {
        if(kind==15){dragging=false;Hide();return;}
        if(kind!=14)return;
        // Use only explicit Explorer OLE drag-start events, never ordinary mouse clicks.
        dragEvents++;
        Native.GetWindowThreadProcessId(window,out var pid);
        if(pid==0&&thread!=0)
        {
            var sourceThread=Native.OpenThread(0x0800,false,thread);
            if(sourceThread!=IntPtr.Zero)
            {
                try{pid=Native.GetProcessIdOfThread(sourceThread);}
                finally{Native.CloseHandle(sourceThread);}
            }
        }
        lastSourcePid=pid;
        try {using var source=Process.GetProcessById((int)pid);if(!source.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase))return;}
        catch{return;}
        if(region is not null && !ShouldProxy(ProcessUserSid((int)pid),targetSid))
        {dragging=false;Hide();return;}
        dragging=true;dragStarted=DateTime.UtcNow;TickOverlay();
    }
    internal static bool GestureReady(bool explorer, bool held, Point start, Point current)
        => explorer && held && (Math.Abs(current.X-start.X)>=SystemInformation.DragSize.Width || Math.Abs(current.Y-start.Y)>=SystemInformation.DragSize.Height);
    internal static bool ShouldProxy(string? sourceSid,string? targetSid)
        => string.IsNullOrEmpty(sourceSid)||string.IsNullOrEmpty(targetSid)||!string.Equals(sourceSid,targetSid,StringComparison.OrdinalIgnoreCase);
    static string? ProcessUserSid(int pid)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if(!Native.OpenProcessToken(process.Handle,0x0008,out var token))return null;
            try
            {
                Native.GetTokenInformation(token,1,IntPtr.Zero,0,out var needed);
                if(needed<=0)return null;
                var buffer=Marshal.AllocHGlobal(needed);
                try
                {
                    if(!Native.GetTokenInformation(token,1,buffer,needed,out _))return null;
                    var sid=Marshal.ReadIntPtr(buffer);
                    if(!Native.ConvertSidToStringSid(sid,out var text))return null;
                    try{return Marshal.PtrToStringUni(text);}
                    finally{Native.LocalFree(text);}
                }
                finally{Marshal.FreeHGlobal(buffer);}
            }
            finally{Native.CloseHandle(token);}
        }
        catch{return null;}
    }
    void TrackExplorerGesture()
    {
        var held=(Native.GetAsyncKeyState(1)&0x8000)!=0;
        if(held&&!mouseHeld)
        {
            mouseStart=Cursor.Position;
            var source=Native.WindowFromPoint(new Native.Point{X=mouseStart.X,Y=mouseStart.Y});
            Native.GetWindowThreadProcessId(source,out var pid);
            try{using var process=Process.GetProcessById((int)pid);explorerGesture=process.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase);explorerSourcePid=(int)pid;explorerSourceSid=explorerGesture?ProcessUserSid(explorerSourcePid):null;}
            catch{explorerGesture=false;explorerSourceSid=null;}
        }
        if(!held)explorerGesture=false;
        if(explorerGesture&&region is not null&&!ShouldProxy(explorerSourceSid,targetSid))explorerGesture=false;
        if(!dragging&&GestureReady(explorerGesture,held,mouseStart,Cursor.Position))
        {dragging=true;dragStarted=DateTime.UtcNow;gestureStarts++;}
        mouseHeld=held;
    }
    void TickOverlay()
    {
        TrackExplorerGesture();
        if(!dragging||region==null||DateTime.UtcNow-updated>TimeSpan.FromSeconds(2)||DateTime.UtcNow-dragStarted>TimeSpan.FromMinutes(2)||(Native.GetAsyncKeyState(1)&0x8000)==0||(Native.GetAsyncKeyState(27)&0x8000)!=0)
        {dragging=false;if(Visible)Hide();return;}
        IntPtr hwnd;
        try{using var process=Process.GetProcessById(region.Pid);if(!process.ProcessName.Equals("ChatGPT",StringComparison.OrdinalIgnoreCase)){Hide();return;}hwnd=process.MainWindowHandle;}
        catch{Hide();return;}
        if(hwnd==IntPtr.Zero||!Native.IsWindowVisible(hwnd)||Native.IsIconic(hwnd)){Hide();return;}
        if(!Native.GetClientRect(hwnd,out var client)){Hide();return;}
        var origin=new Native.Point();Native.ClientToScreen(hwnd,ref origin);
        var scale=(client.Right-client.Left)/region.ViewWidth;
        var top=(client.Bottom-client.Top)-region.ViewHeight*scale;
        if(!double.IsFinite(scale)||scale<=0||top< -4){Hide();return;}
        var rectangle=new Rectangle((int)Math.Round(origin.X+region.X*scale),(int)Math.Round(origin.Y+top+region.Y*scale),Math.Max(1,(int)Math.Round(region.Width*scale)),Math.Max(1,(int)Math.Round(region.Height*scale)));
        var cursor=Cursor.Position;
        if(!rectangle.Contains(cursor)){if(Visible)Hide();return;}
        var under=Native.GetAncestor(Native.WindowFromPoint(new Native.Point{X=cursor.X,Y=cursor.Y}),2);
        if(under!=hwnd&&(!IsHandleCreated||under!=Handle)){if(Visible)Hide();return;}
        Bounds=rectangle;
        if(!Visible)Show();
        Native.SetWindowPos(Handle,new IntPtr(-1),rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height,0x0010|0x0040);
    }
    protected override void Dispose(bool disposing){if(disposing){timer.Dispose();if(hook!=IntPtr.Zero){Native.UnhookWinEvent(hook);hook=IntPtr.Zero;}}base.Dispose(disposing);}
}
static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] public struct Rect{public int Left,Top,Right,Bottom;}
    public delegate void WinEvent(IntPtr hook,uint kind,IntPtr hwnd,int obj,int child,uint thread,uint time);
    [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool ShowWindowAsync(IntPtr hwnd,int command);
    [DllImport("user32.dll")]public static extern bool AttachThreadInput(uint first,uint second,bool attach);
    [DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]public static extern IntPtr OpenThread(uint access,bool inherit,uint thread);
    [DllImport("kernel32.dll")]public static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",SetLastError=true)]public static extern bool OpenProcessToken(IntPtr processHandle,uint desiredAccess,out IntPtr tokenHandle);
    [DllImport("advapi32.dll",SetLastError=true)]public static extern bool GetTokenInformation(IntPtr tokenHandle,int tokenInformationClass,IntPtr tokenInformation,int tokenInformationLength,out int returnLength);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool ConvertSidToStringSid(IntPtr sid,out IntPtr stringSid);
    [DllImport("kernel32.dll")]public static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll")]public static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")]public static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")]public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]public static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
