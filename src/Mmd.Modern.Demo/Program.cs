using Avalonia;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Demo;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try{Run(args);}
        catch(Exception error)
        {
            string log=Path.Combine(AppContext.BaseDirectory,"startup-error.log");
            try{File.WriteAllText(log,error.ToString());}catch(IOException){}
            if(OperatingSystem.IsWindows()&&!args.Contains("--viewport-self-test"))MessageBoxW(IntPtr.Zero,$"Demo 启动失败：{error.Message}\n\n详情：{log}","MMD Modern Demo",0x10);
            Environment.ExitCode=1;
        }
    }
    private static void Run(string[] args)
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MMD_DXC_CACHE")))
        {
            var root=new DirectoryInfo(AppContext.BaseDirectory);
            while(root.Parent is not null&&!Directory.Exists(Path.Combine(root.FullName,"MMD932_modern")))root=root.Parent;
            string cache=Directory.Exists(Path.Combine(root.FullName,"MMD932_modern"))
                ?Path.Combine(root.FullName,".dev-cache","engine-demo","dxc"):Path.Combine(AppContext.BaseDirectory,".cache","dxc");
            Environment.SetEnvironmentVariable("MMD_DXC_CACHE",cache);
        }
        AppBuilder.Configure<Mmd.Modern.App.App>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(["--frame-viewer","--engine-demo",..args]);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBoxW(IntPtr owner,string text,string caption,uint type);
}
