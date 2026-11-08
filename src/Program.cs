using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using System.Linq;

[assembly:AssemblyTitle("大肥鱼空间管家")]
[assembly:AssemblyDescription("本地扫描、预览和可信缓存清理")]
[assembly:AssemblyVersion("5.2.0.0")]
[assembly:AssemblyFileVersion("5.2.0.0")]
[assembly:AssemblyInformationalVersion("5.2.0-functional")]

namespace ClearDisk {
    static class Program {
        [STAThread]
        static int Main(string[] args) {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
            try {
                var policy=SafetyPolicy.ForCurrentUser();
                if(args.Length==2&&args[0]=="--scan-reference") {string target=ReferenceInventoryForm.ReportPath(policy,args[1]);using(var parents=new NativeFiles.ParentLocks(target)){if(File.Exists(target))throw new IOException("报告已经存在，请选择新的文件名。");ReportWriter.WriteScanNew(target,CommunityScanner.CreateReferenceInventory(policy).Scan(CancellationToken.None,null));}return 0;}
                if(args.Length==4&&args[0]=="--similar") {MediaKind kind;if(!Enum.TryParse<MediaKind>(args[1],true,out kind)||!Enum.IsDefined(typeof(MediaKind),kind))throw new ArgumentException("相似文件类型无效。");var result=new MediaSimilarityScanner(policy).Scan(args[2],new MediaSearchOptions{Kind=kind,MinimumBytes=1},CancellationToken.None,null);ReportWriter.WriteScan(Path.GetFullPath(args[3]),result);return result.Cancelled?2:0;}
                if(args.Length>=2 && new[]{"--scan-community","--scan-chat","--scan-downloads","--scan-system","--scan-unknown-caches"}.Contains(args[0])) {
                    var inspector=new InspectionScanner(policy);
                    ScanReport report=args[0]=="--scan-community"?new CommunityScanner(policy).Scan(CancellationToken.None,null):args[0]=="--scan-chat"?inspector.ScanChat(CancellationToken.None,null):args[0]=="--scan-downloads"?inspector.ScanDownloads(CancellationToken.None,null):args[0]=="--scan-system"?inspector.ScanSystem(CancellationToken.None,null):inspector.ScanUnknownCaches(CancellationToken.None,null);
                    ReportWriter.WriteScan(Path.GetFullPath(args[1]),report);return 0;
                }
                if(args.Length>=2 && args[0]=="--scan-caches") {
                    ScanReport report=new Scanner(policy).ScanCaches(Rules.Discover(policy.ProfileRoot),CancellationToken.None,null);
                    ReportWriter.WriteScan(Path.GetFullPath(args[1]),report); return report.Cancelled ? 2 : 0;
                }
                if(args.Length>=3 && (args[0]=="--scan-disk" || args[0]=="--duplicates")) {
                    ScanReport report=args[0]=="--scan-disk" ? new Scanner(policy).ScanDisk(args[1],CancellationToken.None,null) : new DuplicateFinder(policy).Scan(args[1],CancellationToken.None,null);
                    ReportWriter.WriteScan(Path.GetFullPath(args[2]),report); return report.Cancelled ? 2 : 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if(args.Length==1&&args[0]=="--full-main-scan-window"){Application.Run(new MainForm(policy,true));return 0;}
                if(args.Length==1&&args[0]=="--full-rule-scan-window"){Application.Run(new StructuredCleanupForm(true));return 0;}
                if(args.Length==1&&args[0]=="--compatible-cleanup-window"){Application.Run(new StructuredCleanupForm(false));return 0;}
                if(args.Length==1&&args[0]=="--advanced-administration-window"){Application.Run(new AdvancedAdministrationForm());return 0;}
                if(args.Length==2&&args[0]=="--diagnose-environment"){EnvironmentDiagnostics.SaveNew(args[1],EnvironmentDiagnostics.Capture(new WindowsAdministrationBackend(),CancellationToken.None,null));return 0;}
                if(args.Length==1&&args[0]=="--system-maintenance-window"){Application.Run(new SystemMaintenanceForm());return 0;}
                if(args.Length==1&&args[0]=="--readonly-reference-window"){Application.Run(new ReferenceInventoryForm(policy));return 0;}
                if(args.Length>=2 && args[0]=="--smoke") return GuiSmoke.Run(args[1]);
                return RunResident();
            } catch(Exception ex) {
                if(args.Length>0) {
                    WriteError(ex);
                    return 1;
                }
                MessageBox.Show("操作未完成："+ex.Message,"大肥鱼空间管家",MessageBoxButtons.OK,MessageBoxIcon.Warning); return 1;
            }
        }
        // Even an elevated read-only entry point may log only a new ordinary file
        // under this user's profile. Never follow or overwrite last-error.txt.
        static int RunResident(){
            using(var gate=new Mutex(false,@"Local\DaFeiYu.SpaceKeeper")){
                bool mine;
                try{mine=gate.WaitOne(0);}
                catch(AbandonedMutexException){mine=true;}
                if(!mine){PostMessage((IntPtr)0xFFFF,RegisterWindowMessage("DaFeiYu.SpaceKeeper.Show"),IntPtr.Zero,IntPtr.Zero);return 0;}
                try{MainForm.EnableDesktopPet=true;Application.Run(new MainForm());return 0;}
                finally{gate.ReleaseMutex();}
            }
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
        static void WriteError(Exception error){
            try{string directory=NativeFiles.Normalize(AppDomain.CurrentDomain.BaseDirectory);string profile=NativeFiles.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));if(!SafetyPolicy.IsWithin(directory,profile))return;string path=Path.Combine(directory,"last-error-"+Guid.NewGuid().ToString("N")+".txt");using(var parents=new NativeFiles.ParentLocks(path))using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){using(var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(true))){string message=error.ToString();writer.Write(DateTime.UtcNow.ToString("o")+"\r\n"+message.Substring(0,Math.Min(message.Length,16384)));}}}catch{}
        }
    }
}
