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
            if(args.Contains("--style-compare"))Console.Error.WriteLine(error);
            if(OperatingSystem.IsWindows()&&!args.Contains("--viewport-self-test")&&!args.Contains("--style-compare"))MessageBoxW(IntPtr.Zero,$"Demo 启动失败：{error.Message}\n\n详情：{log}","MMD Modern Demo",0x10);
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
        // Headless dev tool: renders preset comparison strips and exits without creating a window.
        if(args.Contains("--style-compare")){Environment.ExitCode=StyleCompareCli.Run(args);return;}
        AppBuilder.Configure<DemoApp>().UsePlatformDetect().LogToTrace()
            // Double-click opens the built-in engine demo; scripts/run-frame-viewer.ps1 passes
            // explicit frame-viewer arguments (model, project, self-tests) instead.
            .StartWithClassicDesktopLifetime(args.Contains("--frame-viewer")||args.Contains("--legacy-preview")?args:["--frame-viewer","--engine-demo",..args]);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBoxW(IntPtr owner,string text,string caption,uint type);
}
