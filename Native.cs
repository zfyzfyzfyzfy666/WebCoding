using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly:System.Reflection.AssemblyTitle("WebCoding Native Helper")]
[assembly:System.Reflection.AssemblyProduct("WebCoding")]
[assembly:System.Reflection.AssemblyVersion("2.0.2.0")]
namespace WebCodingNative {
internal static class Program {
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=24000000 };
    [STAThread] static int Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        try {
            if(args.Length>0&&args[0]=="run") return RunCommand();
            try {SetProcessDpiAwarenessContext(new IntPtr(-4));} catch {try{SetProcessDPIAware();}catch{}}
            if(args.Length!=2)throw new ArgumentException("Missing native arguments");
            var request=Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
            object response;
            if(args[0]=="image")response=ReadImage(request);
            else if(args[0]=="window")response=Window(request);
            else throw new ArgumentException("Unknown native operation");
            Console.WriteLine(Json.Serialize(response));return 0;
        }catch(Exception e){Console.WriteLine(Json.Serialize(new {ok=false,error=e.Message}));return 1;}
    }
    static string Value(Dictionary<string,object>d,string k){object v;return d.TryGetValue(k,out v)?Convert.ToString(v):"";}
    static int Number(Dictionary<string,object>d,string k,int fallback){object v;return d.TryGetValue(k,out v)?Convert.ToInt32(v):fallback;}
    static bool Flag(Dictionary<string,object>d,string k){object v;return d.TryGetValue(k,out v)&&v is bool&&(bool)v;}
    static int RunCommand() {
        string command=Environment.GetEnvironmentVariable("WC_COMMAND");
        if(string.IsNullOrWhiteSpace(command))throw new ArgumentException("Missing command");
        Environment.SetEnvironmentVariable("WC_COMMAND",null);
        string gateName="Local\\WebCoding-"+Guid.NewGuid().ToString("N");
        string prefix="$ProgressPreference='SilentlyContinue'; $gate=[Threading.EventWaitHandle]::OpenExisting($env:WC_GATE); $null=$gate.WaitOne(); $gate.Dispose(); Remove-Item Env:WC_GATE; [Console]::OutputEncoding=New-Object Text.UTF8Encoding($false); $OutputEncoding=[Console]::OutputEncoding;\n";
        string ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        var info=new ProcessStartInfo(ps,"-NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(prefix+command))) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Environment.CurrentDirectory
        };
        info.EnvironmentVariables["WC_GATE"]=gateName;
        using(var gate=new EventWaitHandle(false,EventResetMode.ManualReset,gateName))
        using(var job=new Job()) using(var p=new Process{StartInfo=info}) {
            p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Console.WriteLine(e.Data);};
            p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)Console.Error.WriteLine(e.Data);};
            p.Start();
            try {job.Add(p.Handle);} catch {try{p.Kill();}catch{} throw;}
            p.StandardInput.Close();p.BeginOutputReadLine();p.BeginErrorReadLine();gate.Set();
            // Wait for the foreground command, then terminate any remaining descendants
            // before draining redirected output (descendants can otherwise hold the pipes open).
            while(!p.WaitForExit(250)){}
            int code=p.ExitCode;job.Dispose();p.WaitForExit();return code;
        }
    }
    static object ReadImage(Dictionary<string,object> request) {
        string file=Value(request,"path");
        if(new FileInfo(file).Length>16*1024*1024)throw new ArgumentException("Image exceeds 16 MB");
        using(var image=Image.FromFile(file)) {
            if((long)image.Width*image.Height>40000000)throw new ArgumentException("Image dimensions exceed 40 million pixels");
            return ImageResult(image,request,new {path=Path.GetFileName(file),source_width=image.Width,source_height=image.Height,method="file",frame="first"});
        }
    }
    static object ImageResult(Image image,Dictionary<string,object> request,object metadata) {
        int edge=Math.Max(256,Math.Min(3840,Number(request,"max_edge",1600)));
        int maxBytes=Math.Max(65536,Math.Min(8388608,Number(request,"max_bytes",6291456)));
        for(int attempt=0;attempt<5;attempt++) {
            double scale=Math.Min(1.0,(double)edge/Math.Max(image.Width,image.Height));
            int w=Math.Max(1,(int)(image.Width*scale)),h=Math.Max(1,(int)(image.Height*scale));
            using(var b=new Bitmap(w,h,PixelFormat.Format32bppArgb)) {
                using(var g=Graphics.FromImage(b)){g.Clear(Color.Transparent);g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;g.DrawImage(image,0,0,w,h);}
                using(var stream=new MemoryStream()) {
                    b.Save(stream,ImageFormat.Png);
                    if(stream.Length<=maxBytes)return new {ok=true,width=w,height=h,mimeType="image/png",metadata=metadata,image=Convert.ToBase64String(stream.ToArray())};
                }
            }
            edge=Math.Max(128,edge*3/4);
        }
        throw new InvalidOperationException("Image still exceeds configured output limit after resizing");
    }
    sealed class WindowInfo {public string hwnd;public int pid;public string process_name;public string title;public bool minimized;public int x,y,width,height;}
    static List<WindowInfo> Windows(Dictionary<string,object> request) {
        var list=new List<WindowInfo>();
        string title=Value(request,"title_contains"),name=Path.GetFileNameWithoutExtension(Value(request,"process_name")),handle=Value(request,"hwnd");
        int wantedPid=Number(request,"pid",0);
        EnumWindows((h,p)=>{
            if(!IsWindowVisible(h))return true;
            var text=new StringBuilder(1024);GetWindowText(h,text,text.Capacity);if(text.Length==0)return true;
            uint pid;GetWindowThreadProcessId(h,out pid);
            string process="";try {using(var proc=Process.GetProcessById((int)pid))process=proc.ProcessName;}catch{}
            if(wantedPid>0&&pid!=wantedPid||name!=""&&!process.Equals(name,StringComparison.OrdinalIgnoreCase)||title!=""&&text.ToString().IndexOf(title,StringComparison.OrdinalIgnoreCase)<0)return true;
            if(handle!=""&&ParseHandle(handle)!=h)return true;
            Rect r;if(!GetWindowRect(h,out r))return true;
            list.Add(new WindowInfo{hwnd="0x"+h.ToInt64().ToString("X"),pid=(int)pid,process_name=process,title=text.ToString(),minimized=IsIconic(h),x=r.Left,y=r.Top,width=r.Right-r.Left,height=r.Bottom-r.Top});
            return true;
        },IntPtr.Zero);
        return list;
    }
    static IntPtr ParseHandle(string value) {
        long n;if(value.StartsWith("0x",StringComparison.OrdinalIgnoreCase))n=long.Parse(value.Substring(2),System.Globalization.NumberStyles.HexNumber);else n=long.Parse(value);
        return new IntPtr(n);
    }
    static bool LooksBlank(Bitmap image) {
        Color first=image.GetPixel(Math.Min(3,image.Width-1),Math.Min(3,image.Height-1));int similar=0,total=0;
        for(int y=0;y<image.Height;y+=Math.Max(1,image.Height/30))for(int x=0;x<image.Width;x+=Math.Max(1,image.Width/30)) {
            Color c=image.GetPixel(x,y);total++;
            if(Math.Abs(c.R-first.R)+Math.Abs(c.G-first.G)+Math.Abs(c.B-first.B)<15)similar++;
        }
        return total>0&&(double)similar/total>0.985;
    }
    static object Window(Dictionary<string,object> request) {
        // Validate a supplied handle before entering a native callback.
        if(Value(request,"hwnd")!="")ParseHandle(Value(request,"hwnd"));
        var windows=Windows(request);
        if(Value(request,"action")=="list")return new {ok=true,windows=windows};
        if(Value(request,"action")!="capture")throw new ArgumentException("Unsupported window action");
        if(Value(request,"hwnd")==""&&Number(request,"pid",0)==0&&Value(request,"process_name")==""&&Value(request,"title_contains")=="")throw new ArgumentException("Select a target window; use action=list first");
        if(windows.Count!=1)return new {ok=true,needs_selection=true,windows=windows,message=windows.Count==0?"No matching visible top-level window":"Multiple windows matched; select one hwnd"};
        var target=windows[0];IntPtr hwnd=ParseHandle(target.hwnd);
        bool activate=Flag(request,"allow_activate");string method=Value(request,"method");if(method=="")method="auto";
        if(target.minimized&&!activate)throw new InvalidOperationException("Window is minimized; allow_activate=true is required to restore it");
        if(method=="screen"&&!activate)throw new InvalidOperationException("Screen capture requires allow_activate=true");
        bool changed=false;
        if(activate){if(IsIconic(hwnd))ShowWindow(hwnd,9);changed=SetForegroundWindow(hwnd);Thread.Sleep(250);}
        if(method!="screen") {
            object printed=PrintTarget(hwnd,target,request,activate,changed,method);
            if(printed!=null)return printed;
            method="screen";
        }
        Rect r;if(!IsWindow(hwnd)||!GetWindowRect(hwnd,out r))throw new InvalidOperationException("Window no longer exists");
        int width=r.Right-r.Left,height=r.Bottom-r.Top;
        if(width<=0||height<=0||(long)width*height>40000000)throw new InvalidOperationException("Window size cannot be captured");
        using(var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
            bool success=false;string used="PrintWindow";
            if(method!="screen")using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Color.Black);IntPtr dc=g.GetHdc();try{success=PrintWindow(hwnd,dc,2);}finally{g.ReleaseHdc(dc);}
            }
            bool blank=LooksBlank(bitmap);
            if(method=="screen"||method=="auto"&&activate&&(!success||blank)) {
                if(GetForegroundWindow()!=hwnd)throw new InvalidOperationException("Target could not be brought to foreground; refusing to capture another window");
                Rectangle bounds=new Rectangle(r.Left,r.Top,width,height);
                if(!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(bounds))throw new InvalidOperationException("Window extends outside the desktop; move it fully on-screen first");
                using(var g=Graphics.FromImage(bitmap))g.CopyFromScreen(r.Left,r.Top,0,0,new Size(width,height),CopyPixelOperation.SourceCopy);
                used="screen";success=true;blank=LooksBlank(bitmap);
            }
            if(!success)throw new InvalidOperationException("PrintWindow failed. Retry with method=screen and allow_activate=true if you permit focus changes");
            string warning=blank?"Image appears uniform/blank; protected or accelerated content may not have rendered.":used=="PrintWindow"?"Accelerated/protected regions may be incomplete; visual verification is recommended.":"Visible pixels captured. Always-on-top overlays may appear in the image.";
            return ImageResult(bitmap,request,new {window=target,method=used,activation_requested=activate,foreground_changed=changed,possibly_blank=blank,warning=warning});
        }
    }
    static object PrintTarget(IntPtr hwnd,WindowInfo target,Dictionary<string,object> request,bool activate,bool changed,string method) {
        IntPtr previous=IntPtr.Zero;
        try {
            // PrintWindow renders in the target's DPI context. Match that context to avoid
            // a logical-size rendering in a larger physical bitmap on scaled desktops.
            try {previous=SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));}catch{}
            Rect r;if(!IsWindow(hwnd)||!GetWindowRect(hwnd,out r))throw new InvalidOperationException("Window no longer exists");
            int width=r.Right-r.Left,height=r.Bottom-r.Top;
            if(width<=0||height<=0||(long)width*height>40000000)throw new InvalidOperationException("Window size cannot be captured");
            using(var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
                bool success;
                using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Black);IntPtr dc=g.GetHdc();try{success=PrintWindow(hwnd,dc,2);}finally{g.ReleaseHdc(dc);}}
                bool blank=LooksBlank(bitmap);
                if((!success||blank)&&method=="auto"&&activate)return null;
                if(!success)throw new InvalidOperationException("PrintWindow failed; screen capture requires explicit activation permission");
                return ImageResult(bitmap,request,new {window=target,method="PrintWindow",activation_requested=activate,foreground_changed=changed,possibly_blank=blank,warning=blank?"Image appears uniform/blank; accelerated/protected content may not have rendered.":"Accelerated/protected regions may be incomplete; visual verification is recommended."});
            }
        } finally {if(previous!=IntPtr.Zero)try{SetThreadDpiAwarenessContext(previous);}catch{}}
    }
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    delegate bool EnumProc(IntPtr h,IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}
internal sealed class Job:IDisposable {
    IntPtr handle;
    public Job(){handle=CreateJobObject(IntPtr.Zero,null);if(handle==IntPtr.Zero)throw new IOException("Cannot create task job");var value=new Info();value.Basic.Flags=0x2000;int size=Marshal.SizeOf(typeof(Info));IntPtr p=Marshal.AllocHGlobal(size);try{Marshal.StructureToPtr(value,p,false);if(!SetInformationJobObject(handle,9,p,(uint)size))throw new IOException("Cannot configure task cleanup");}catch{Dispose();throw;}finally{Marshal.FreeHGlobal(p);}}
    public void Add(IntPtr process){if(!AssignProcessToJobObject(handle,process))throw new IOException("Cannot assign task job");}
    public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential)]struct Basic {public long ProcessTime,JobTime;public uint Flags;public UIntPtr Min,Max;public uint Count;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]struct Io {public ulong A,B,C,D,E,F;}
    [StructLayout(LayoutKind.Sequential)]struct Info {public Basic Basic;public Io Io;public UIntPtr A,B,C,D;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateJobObject(IntPtr a,string n);
    [DllImport("kernel32.dll")]static extern bool SetInformationJobObject(IntPtr h,int t,IntPtr p,uint s);
    [DllImport("kernel32.dll")]static extern bool AssignProcessToJobObject(IntPtr h,IntPtr p);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
}
}
