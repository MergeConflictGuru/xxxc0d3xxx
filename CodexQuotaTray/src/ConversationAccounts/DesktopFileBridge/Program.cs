using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace DesktopFileBridge;
static class Program
{
    public static readonly string PipeName = "CodexDesktopFiles-" + Process.GetCurrentProcess().SessionId;
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length==2&&args[0]=="--self-test")
        {
            try{File.WriteAllText(args[1],SeamlessDrop.SelfTest());}
            catch(Exception error){File.WriteAllText(args[1],"FAIL: "+error.Message);Environment.ExitCode=1;}
            return;
        }
        using var mutex = new Mutex(true, "Local\\" + PipeName, out var first);
        if (!first) return;
        using var form = new BridgeWindow();
        // Create the IPC dispatch handle without ever showing the helper form.
        _ = form.Handle;
        Application.Run();
    }
}
sealed class BridgeWindow : Form
{
    readonly NotifyIcon tray;
    readonly CancellationTokenSource stop = new();
    TaskCompletionSource<string[]>? pending;
    bool explicitPicker;
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && explicitPicker);
    readonly SeamlessDrop overlay;
    readonly System.Collections.Concurrent.ConcurrentQueue<ReceivedDrop> drops = new();
    volatile bool seamlessEnabled;

    static readonly JsonSerializerOptions Options = new(){PropertyNameCaseInsensitive=true};
    public BridgeWindow()
    {
        overlay=new SeamlessDrop((files,target)=>{
            _=Task.Run(()=>{
                try{drops.Enqueue(new ReceivedDrop(Guid.NewGuid().ToString("N"),Stage(files),target.Context,target.Client));}
                catch(Exception error){BeginInvoke(()=>tray!.ShowBalloonTip(5000,"Codex file drop",error.Message,ToolTipIcon.Error));}
            });
        });
        Text = "Drop files for Codex"; Size = new(420,190); StartPosition=FormStartPosition.CenterScreen;
        AllowDrop=true;TopMost=true;
        var label=new Label { Dock=DockStyle.Fill,Text="Drop files here to attach them in Codex\n\nor click to choose files",TextAlign=ContentAlignment.MiddleCenter };
        label.Click+=(_,_)=>Choose();Controls.Add(label);
        DragEnter+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=(_,e)=>Complete(e.Data?.GetData(DataFormats.FileDrop) as string[] ?? []);
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();explicitPicker=false;pending?.TrySetResult([]);}};
        tray=new NotifyIcon{Icon=SystemIcons.Application,Text="Codex desktop file bridge",Visible=false};
        var menu=new ContextMenuStrip();
        // Background desktop integration: no tray icon, menu, or manual enable step.
        seamlessEnabled=true;


        _=Task.Run(Serve);
    }
    void Choose(){using var dialog=new OpenFileDialog{Multiselect=true};if(dialog.ShowDialog(this)==DialogResult.OK)Complete(dialog.FileNames);}
    void Complete(string[] files){pending?.TrySetResult(files);Hide();explicitPicker=false;}
    async Task Serve()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var acl=new PipeSecurity();
                acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,PipeAccessRights.FullControl,AccessControlType.Allow));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),PipeAccessRights.ReadWrite,AccessControlType.Allow));
                using var pipe=NamedPipeServerStreamAcl.Create(Program.PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,acl);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader=new StreamReader(pipe,leaveOpen:true);using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var line=await reader.ReadLineAsync(timeout.Token);
                if(line==null||line.Length>32768)continue;
                try
                {
                using var doc=JsonDocument.Parse(line);var method=doc.RootElement.GetProperty("method").GetString();
                object response;
                if(method=="ping")response=new {user=WindowsIdentity.GetCurrent().Name,session=Process.GetCurrentProcess().SessionId,version="overlay-v3",statusSupported=true};
                else if(method=="status")
                {
                    var status=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginInvoke(()=>status.SetResult(new{enabled=seamlessEnabled,overlay=overlay.Status}));
                    response=await status.Task;
                }
                else if(method=="openBrowser")
                {
                    var value=doc.RootElement.GetProperty("url").GetString();
                    if(value==null||value.Length>16384||value.Any(char.IsControl)||
                       !Uri.TryCreate(value,UriKind.Absolute,out var uri)||
                       (uri.Scheme!=Uri.UriSchemeHttp&&uri.Scheme!=Uri.UriSchemeHttps)||string.IsNullOrEmpty(uri.Host))
                        throw new InvalidOperationException("Only HTTP and HTTPS browser links are supported");
                    // ShellExecute uses this helper's desktop-user associations and token.
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
                    response=new {ok=true};
                }
                else if(method=="reveal")
                {
                    var path=Path.GetFullPath(doc.RootElement.GetProperty("path").GetString()!);
                    if(!File.Exists(path)&&!Directory.Exists(path))throw new IOException("File is not accessible to the desktop user");
                    var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=true};
                    psi.Arguments=File.Exists(path)?"/select,\""+path+"\"":"\""+path+"\"";
                    Process.Start(psi);
                    BeginInvoke(async()=>await FocusExplorer(path));
                    response=new {ok=true};
                }
                else if(method=="target")
                {
                    var target=doc.RootElement.GetProperty("target").Deserialize<DropRegion>(Options);
                    if(target==null||target.ViewWidth<=0||target.ViewHeight<=0||target.Width<=0||target.Height<=0||target.X<0||target.Y<0||target.X+target.Width>target.ViewWidth+1||target.Y+target.Height>target.ViewHeight+1)throw new InvalidOperationException("Invalid composer rectangle");
                    BeginInvoke(()=>overlay.UpdateRegion(seamlessEnabled?target:null));
                    drops.TryPeek(out var received);
                    response=new {enabled=seamlessEnabled,drop=received};
                }
                else if(method=="ack")
                {
                    var id=doc.RootElement.GetProperty("id").GetString();
                    if(drops.TryPeek(out var first)&&first.Id==id)drops.TryDequeue(out _);
                    response=new {ok=true};
                }
                else if(method=="dropError")
                {
                    BeginInvoke(()=>tray.ShowBalloonTip(7000,"Codex file drop", "The chat changed or could not accept the drop. Staged files are preserved; use Attach from desktop to retry.",ToolTipIcon.Warning));
                    response=new {ok=true};
                }
                else if(method=="files")
                {
                    pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginInvoke(()=>{explicitPicker=true;Show();Activate();});
                    var files=await pending.Task.WaitAsync(timeout.Token);pending=null;
                    response=new {files=Stage(files)};
                }
                else throw new InvalidOperationException("Unknown operation");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new{result=response}));
                }
                catch(Exception requestError)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new{error=requestError.Message}));
                }
            }
            catch(OperationCanceledException){ }
            catch(Exception error){Debug.WriteLine(error.Message);}
        }
    }
    async Task FocusExplorer(string path)
    {
        var directory=Directory.Exists(path)?path:Path.GetDirectoryName(path)!;
        for(var attempt=0;attempt<20;attempt++)
        {
            await Task.Delay(150);
            try
            {
                var type=Type.GetTypeFromProgID("Shell.Application");
                if(type==null)return;
                dynamic shell=Activator.CreateInstance(type)!;
                dynamic windows=shell.Windows();
                IntPtr found=IntPtr.Zero;
                try
                {
                    foreach(dynamic window in windows)
                    {
                        try
                        {
                            var url=(string)window.LocationURL;
                            if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.IsFile&&string.Equals(Path.TrimEndingDirectorySeparator(uri.LocalPath),Path.TrimEndingDirectorySeparator(directory),StringComparison.OrdinalIgnoreCase))
                                found=new IntPtr((long)window.HWND);
                        }
                        catch{ }
                        finally{System.Runtime.InteropServices.Marshal.ReleaseComObject(window);}
                    }
                }
                finally{System.Runtime.InteropServices.Marshal.ReleaseComObject(windows);System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);}
                if(found==IntPtr.Zero)continue;
                if(Native.IsIconic(found))Native.ShowWindowAsync(found,9);
                Native.BringWindowToTop(found);
                if(Native.SetForegroundWindow(found))return;
                var current=Native.GetCurrentThreadId();
                var foreground=Native.GetForegroundWindow();
                var thread=Native.GetWindowThreadProcessId(foreground,out _);
                var attached=thread!=0&&thread!=current&&Native.AttachThreadInput(current,thread,true);
                try{Native.BringWindowToTop(found);if(Native.SetForegroundWindow(found))return;}
                finally{if(attached)Native.AttachThreadInput(current,thread,false);}
                tray.ShowBalloonTip(4000,"Open in Explorer","Folder opened, but Windows refused foreground focus. Select Explorer on the taskbar.",ToolTipIcon.Info);
                return;
            }
            catch(Exception error){Debug.WriteLine(error.Message);}
        }
    }
    static string[] Stage(string[] files)
    {
        var staged=new List<string>();
        var directory=Path.Combine(AppContext.BaseDirectory,"dropped",Guid.NewGuid().ToString("N"));
        if(files.Length>0)Directory.CreateDirectory(directory);
        foreach(var file in files.Where(File.Exists))
        {
            var target=Path.Combine(directory,Path.GetFileName(file));
            if(File.Exists(target))target=Path.Combine(directory,Guid.NewGuid().ToString("N")+"-"+Path.GetFileName(file));
            File.Copy(file,target);
            var security=new FileSecurity();
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
            new FileInfo(target).SetAccessControl(security);staged.Add(target);
        }
        return staged.ToArray();
    }
    protected override void Dispose(bool disposing){if(disposing){stop.Cancel();overlay.Dispose();tray.Dispose();}base.Dispose(disposing);}
}
