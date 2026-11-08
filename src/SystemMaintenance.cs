using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClearDisk {
 internal enum MaintenanceActionId { ComponentCleanup,DeliveryOptimization,RecycleBinC,HibernateOff,HibernateOn,AutomaticPagefile,EventSystem,EventApplication,EventSetup,EventSecurity }
 internal enum MaintenanceCommandKind { Process,RecycleBinC }
 internal enum MaintenanceStatus { Succeeded,RestartRequired,Failed,Unsupported,Cancelled }
 internal sealed class MaintenanceAction {
  internal MaintenanceActionId Id {get;private set;} internal string Name {get;private set;} internal string Warning {get;private set;}
  internal bool DefaultSelected {get{return false;}} internal bool RequiresAdministrator {get{return true;}}
  internal MaintenanceAction(MaintenanceActionId id,string name,string warning){Id=id;Name=name;Warning=warning;}
  public override string ToString(){return Name;}
 }
 internal static class SystemMaintenanceCatalog {
  static readonly ReadOnlyCollection<MaintenanceAction> actions=Array.AsReadOnly(new[]{
   new MaintenanceAction(MaintenanceActionId.ComponentCleanup,"Windows 组件清理","立即清理被替代的组件，不等待通常的 30 天保留期；可能减少组件回退空间。Windows 管理适用组件，耗时可能较长；不使用 ResetBase，不自动重启。"),
   new MaintenanceAction(MaintenanceActionId.DeliveryOptimization,"传递优化缓存","删除 Windows 传递优化的非固定缓存；可能重新下载。命令不可用时报告不支持。此 Windows 接口影响系统缓存，可能跨卷。"),
   new MaintenanceAction(MaintenanceActionId.RecycleBinC,"清空 C 盘回收站","永久删除当前用户 C 盘回收站内容，无法从本工具撤销。其他盘不清空。"),
   new MaintenanceAction(MaintenanceActionId.HibernateOff,"关闭休眠","关闭系统休眠并释放休眠文件；休眠和快速启动将不可用。可用“启用休眠”恢复。"),
   new MaintenanceAction(MaintenanceActionId.HibernateOn,"启用休眠","恢复休眠功能，可能创建或扩展休眠文件并占用磁盘空间。"),
   new MaintenanceAction(MaintenanceActionId.AutomaticPagefile,"恢复自动管理虚拟内存","全局恢复 Windows 自动管理分页文件；可能增加占用，影响所有卷的分页策略，需重启生效。不迁移或手动删除分页文件。"),
   new MaintenanceAction(MaintenanceActionId.EventSystem,"清空 System 事件日志","永久清除系统事件历史，影响故障诊断；不创建备份。"),
   new MaintenanceAction(MaintenanceActionId.EventApplication,"清空 Application 事件日志","永久清除应用事件历史，影响故障诊断；不创建备份。"),
   new MaintenanceAction(MaintenanceActionId.EventSetup,"清空 Setup 事件日志","永久清除安装事件历史，影响故障诊断；不创建备份。"),
   new MaintenanceAction(MaintenanceActionId.EventSecurity,"清空 Security 事件日志","永久清除安全审计历史，可能受组织策略限制；不创建备份；Windows 可能记录清除动作。")});
  internal static ReadOnlyCollection<MaintenanceAction> Actions {get{return actions;}}
  internal static MaintenanceAction Find(MaintenanceActionId id){var result=actions.SingleOrDefault(a=>a.Id==id);if(result==null)throw new ArgumentException("未知系统操作。");return result;}
 }
 internal sealed class MaintenanceContext {
  internal string User {get;private set;}internal bool IsAdministrator {get;private set;}
  internal MaintenanceContext(string user,bool admin){User=user;IsAdministrator=admin;}
  internal bool Same(MaintenanceContext other){return other!=null&&User==other.User&&IsAdministrator==other.IsAdministrator;}
 }
 internal sealed class MaintenanceCommand {
  // Only this typed factory can produce executable commands. No user strings enter an OS command.
  internal MaintenanceActionId Id {get;private set;} internal MaintenanceCommandKind Kind {get;private set;}
  internal string FileName {get;private set;}internal string Arguments {get;private set;}internal string Script {get;private set;}
  internal const string DeliveryScript="$ErrorActionPreference = 'Stop'; try { $module = Join-Path $PSHOME 'Modules\\DeliveryOptimization\\DeliveryOptimization.psd1'; if (!(Test-Path -LiteralPath $module)) { [Console]::Error.WriteLine('DeliveryOptimization module is unavailable'); exit 50 }; Import-Module -Name $module -ErrorAction Stop; if (!(Get-Command 'DeliveryOptimization\\Delete-DeliveryOptimizationCache' -ErrorAction SilentlyContinue)) { [Console]::Error.WriteLine('Delete-DeliveryOptimizationCache is unavailable'); exit 50 }; DeliveryOptimization\\Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
  internal const string PagefileScript="$ErrorActionPreference = 'Stop'; try { Import-Module -Name (Join-Path $PSHOME 'Modules\\CimCmdlets\\CimCmdlets.psd1') -ErrorAction Stop; CimCmdlets\\Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop | CimCmdlets\\Set-CimInstance -Property @{ AutomaticManagedPagefile = $true } -ErrorAction Stop; $state = CimCmdlets\\Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop; if (!$state.AutomaticManagedPagefile) { throw 'AutomaticManagedPagefile verification failed' }; [Console]::WriteLine('AutomaticManagedPagefile enabled. Restart Windows to apply.'); exit 3010 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
  private MaintenanceCommand(){}
  internal static MaintenanceCommand For(MaintenanceActionId id){
   SystemMaintenanceCatalog.Find(id);
   string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
   if(!Path.IsPathRooted(system))throw new InvalidOperationException("无法解析 Windows System32 目录。");
   var c=new MaintenanceCommand{Id=id,Kind=MaintenanceCommandKind.Process,Script=""};
   switch(id){
    case MaintenanceActionId.ComponentCleanup:c.FileName=Path.Combine(system,"Dism.exe");c.Arguments="/Online /Cleanup-Image /StartComponentCleanup /NoRestart";break;
    case MaintenanceActionId.HibernateOff:case MaintenanceActionId.HibernateOn:c.FileName=Path.Combine(system,"powercfg.exe");c.Arguments=id==MaintenanceActionId.HibernateOff?"/hibernate off":"/hibernate on";break;
    case MaintenanceActionId.RecycleBinC:c.Kind=MaintenanceCommandKind.RecycleBinC;c.FileName="SHEmptyRecycleBinW";c.Arguments=@"C:\";break;
    case MaintenanceActionId.DeliveryOptimization:case MaintenanceActionId.AutomaticPagefile:
     c.FileName=Path.Combine(system,@"WindowsPowerShell\v1.0\powershell.exe");c.Script=id==MaintenanceActionId.DeliveryOptimization?DeliveryScript:PagefileScript;
     c.Arguments="-NoLogo -NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(c.Script));break;
    default:c.FileName=Path.Combine(system,"wevtutil.exe");c.Arguments="cl "+(id==MaintenanceActionId.EventSystem?"System":id==MaintenanceActionId.EventApplication?"Application":id==MaintenanceActionId.EventSetup?"Setup":"Security");break;
   }return c;
  }
  internal string Describe(){return Kind==MaintenanceCommandKind.RecycleBinC?"SHEmptyRecycleBinW(NULL, C:\\, 0x7)":FileName+" "+Arguments+(Script.Length==0?"":"\r\nPowerShell literal script:\r\n"+Script);}
 }
 internal sealed class MaintenancePlan {
  internal ReadOnlyCollection<MaintenanceAction> Actions {get;private set;}
  internal ReadOnlyCollection<MaintenanceCommand> Commands {get;private set;}
  internal MaintenanceContext Context {get;private set;}internal DateTime IssuedUtc {get;private set;}internal DateTime ExpiresUtc {get;private set;}
  internal long? EstimatedBytes {get{return null;}}internal int UnknownSpaceActions {get{return Actions.Count;}}
  internal string ConfirmationText {get;private set;}
  internal MaintenancePlan(IEnumerable<MaintenanceAction> actions,MaintenanceContext context,DateTime now){
   Actions=Array.AsReadOnly(actions.ToArray());Commands=Array.AsReadOnly(Actions.Select(a=>MaintenanceCommand.For(a.Id)).ToArray());Context=context;IssuedUtc=now;ExpiresUtc=now.AddMinutes(10);
   var s=new StringBuilder("确认执行以下系统维护操作？\r\n释放空间估计：未知。系统状态会在执行时变化，操作不提供撤销。\r\n");
   for(int i=0;i<Actions.Count;i++)s.Append("\r\n").Append(Actions[i].Name).Append("\r\n").Append(Actions[i].Warning).Append("\r\n").Append(Commands[i].Describe()).Append("\r\n");
   s.Append("\r\n当前账户：").Append(context.User).Append("\r\n管理员：").Append(context.IsAdministrator?"是":"否（必须重新以管理员打开并预览）").Append("\r\n预览有效期至 UTC ").Append(ExpiresUtc.ToString("u")).Append("\r\n取消只会阻止下一项；不会强制终止正在运行的 Windows 操作。不会自动重启。");ConfirmationText=s.ToString();
  }
 }
 internal sealed class MaintenanceToolResult {
  internal int ExitCode {get;private set;}internal string StandardOutput {get;private set;}internal string StandardError {get;private set;}
  internal MaintenanceToolResult(int code,string output,string error){ExitCode=code;StandardOutput=MaintenanceDiagnostics.Bound(output);StandardError=MaintenanceDiagnostics.Bound(error);}
 }
 internal static class MaintenanceDiagnostics {
  internal const int Limit=65536;
  internal static string Bound(string text){text=text??"";return text.Length<=Limit?text:text.Substring(0,Limit)+"\r\n[输出已截断]";}
 }
 internal interface ISystemMaintenanceBackend {
  MaintenanceContext Context(); long? FreeBytes(); MaintenanceToolResult Run(MaintenanceCommand command,IProgress<string> progress);
 }
 internal sealed class WindowsSystemMaintenanceBackend:ISystemMaintenanceBackend {
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,EntryPoint="SHEmptyRecycleBinW")]static extern int SHEmptyRecycleBin(IntPtr hwnd,string root,uint flags);
  public MaintenanceContext Context(){using(var identity=WindowsIdentity.GetCurrent()){return new MaintenanceContext(identity.User==null?identity.Name:identity.User.Value,new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));}}
  public long? FreeBytes(){try{return new DriveInfo(@"C:\").AvailableFreeSpace;}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}}
  public MaintenanceToolResult Run(MaintenanceCommand command,IProgress<string> progress){
   // Reconstruct and compare to deny arbitrary strings even if an internal caller passes a different command.
   var expected=MaintenanceCommand.For(command.Id);
   if(expected.Kind!=command.Kind||expected.FileName!=command.FileName||expected.Arguments!=command.Arguments)throw new InvalidOperationException("固定系统命令校验失败。");
   if(!Context().IsAdministrator)throw new InvalidOperationException("系统维护需要管理员权限。");
   if(command.Kind==MaintenanceCommandKind.RecycleBinC){int hr=SHEmptyRecycleBin(IntPtr.Zero,@"C:\",7);return new MaintenanceToolResult(hr,hr==0?"C drive recycle bin cleared.":"",hr==0?"":"SHEmptyRecycleBinW HRESULT 0x"+unchecked((uint)hr).ToString("X8"));}
   var info=new ProcessStartInfo(command.FileName,command.Arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)};
   using(var process=new Process{StartInfo=info}){
    process.Start();var stdout=new StringBuilder();var stderr=new StringBuilder();
    Task output=Task.Factory.StartNew(()=>Drain(process.StandardOutput,stdout,"stdout",progress),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    Task error=Task.Factory.StartNew(()=>Drain(process.StandardError,stderr,"stderr",progress),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    // Mutation tools are never killed or timed out. Cancellation is checked by the engine between actions.
    process.WaitForExit();Task.WaitAll(output,error);return new MaintenanceToolResult(process.ExitCode,stdout.ToString(),stderr.ToString());
   }
  }
  static void Drain(StreamReader reader,StringBuilder sink,string channel,IProgress<string> progress){
   char[] buffer=new char[2048];int read;bool truncated=false;
   while((read=reader.Read(buffer,0,buffer.Length))>0){
    int keep=Math.Min(read,MaintenanceDiagnostics.Limit-sink.Length);
    if(keep>0){
     string chunk=new string(buffer,0,keep);sink.Append(chunk);
     if(progress!=null){try{progress.Report("["+channel+"] "+chunk);}catch(Exception){/* A diagnostic observer cannot interrupt draining. */}}
    }
    if(read>keep)truncated=true;
   }
   if(truncated)sink.Append("\r\n[输出已截断]");
  }
 }
  internal sealed class MaintenanceActionResult {
  internal MaintenanceActionId Id {get;private set;}internal MaintenanceStatus Status {get;private set;}internal int? ExitCode {get;private set;}
  internal string StandardOutput {get;private set;}internal string StandardError {get;private set;}
  internal MaintenanceActionResult(MaintenanceActionId id,MaintenanceStatus status,int? exit,string output,string error){Id=id;Status=status;ExitCode=exit;StandardOutput=MaintenanceDiagnostics.Bound(output);StandardError=MaintenanceDiagnostics.Bound(error);}
 }
 internal sealed class MaintenanceExecutionResult {
  internal ReadOnlyCollection<MaintenanceActionResult> Actions {get;private set;}internal MaintenanceStatus Status {get;private set;}internal bool RestartRequired {get;private set;}
  internal long? FreeBefore {get;private set;}internal long? FreeAfter {get;private set;}internal long? ObservedChange {get{return FreeBefore.HasValue&&FreeAfter.HasValue?FreeAfter.Value-FreeBefore.Value:(long?)null;}}
  internal MaintenanceExecutionResult(IList<MaintenanceActionResult> actions,MaintenanceStatus status,long? before,long? after){Actions=Array.AsReadOnly(actions.ToArray());Status=status;FreeBefore=before;FreeAfter=after;RestartRequired=actions.Any(a=>a.Status==MaintenanceStatus.RestartRequired);}
 }
 internal sealed class SystemMaintenanceEngine {
  readonly ISystemMaintenanceBackend backend;readonly Func<DateTime> utcNow;readonly object gate=new object();MaintenancePlan issued;bool running;
  internal SystemMaintenanceEngine():this(new WindowsSystemMaintenanceBackend(),()=>DateTime.UtcNow){}
  internal SystemMaintenanceEngine(ISystemMaintenanceBackend backend,Func<DateTime> utcNow){if(backend==null||utcNow==null)throw new ArgumentNullException();this.backend=backend;this.utcNow=utcNow;}
  internal MaintenanceContext Context(){return backend.Context();}
  internal MaintenancePlan Preview(IEnumerable<MaintenanceActionId> selected){lock(gate){if(running)throw new InvalidOperationException("系统维护正在执行。");issued=null;var ids=selected==null?new MaintenanceActionId[0]:selected.ToArray();if(ids.Length==0||ids.Distinct().Count()!=ids.Length)throw new ArgumentException("请选择不同的操作。");if(ids.Contains(MaintenanceActionId.HibernateOn)&&ids.Contains(MaintenanceActionId.HibernateOff))throw new ArgumentException("不能同时开启和关闭休眠。");issued=new MaintenancePlan(ids.Select(SystemMaintenanceCatalog.Find),backend.Context(),utcNow());return issued;}}
  internal void InvalidatePreview(){lock(gate){issued=null;}}
  internal MaintenanceExecutionResult Execute(MaintenancePlan plan,bool confirmed,CancellationToken cancellation,IProgress<string> progress){
   lock(gate){if(!confirmed)throw new InvalidOperationException("尚未明确确认。");if(running||plan==null||!Object.ReferenceEquals(plan,issued))throw new InvalidOperationException("需要本窗口重新预览。");issued=null;DateTime now=utcNow();if(now<plan.IssuedUtc||now>=plan.ExpiresUtc)throw new InvalidOperationException("预览已过期，请重新预览。");if(!plan.Context.IsAdministrator||!plan.Context.Same(backend.Context()))throw new InvalidOperationException("管理员身份变化或权限不足，请重新打开并预览。");running=true;}
   var results=new List<MaintenanceActionResult>();long? before=null,after=null;var status=MaintenanceStatus.Succeeded;
   try{
    before=ObserveFree();
    foreach(var command in plan.Commands){
     if(cancellation.IsCancellationRequested){status=MaintenanceStatus.Cancelled;break;}
     try{
      if(!plan.Context.Same(backend.Context()))throw new InvalidOperationException("管理员身份变化，已停止后续操作。");
      if(progress!=null)progress.Report("开始："+SystemMaintenanceCatalog.Find(command.Id).Name+"\r\n");
      var tool=backend.Run(command,progress);var actionStatus=tool.ExitCode==0?MaintenanceStatus.Succeeded:tool.ExitCode==3010?MaintenanceStatus.RestartRequired:tool.ExitCode==50&&command.Id==MaintenanceActionId.DeliveryOptimization?MaintenanceStatus.Unsupported:MaintenanceStatus.Failed;
      results.Add(new MaintenanceActionResult(command.Id,actionStatus,tool.ExitCode,tool.StandardOutput,tool.StandardError));
      if(actionStatus==MaintenanceStatus.Failed||actionStatus==MaintenanceStatus.Unsupported){status=actionStatus;break;}
      if(actionStatus==MaintenanceStatus.RestartRequired)status=MaintenanceStatus.RestartRequired;
     }catch(Exception ex){results.Add(new MaintenanceActionResult(command.Id,MaintenanceStatus.Failed,null,"",ex.Message));status=MaintenanceStatus.Failed;break;}
    }
    after=ObserveFree();return new MaintenanceExecutionResult(results,status,before,after);
   }finally{lock(gate){running=false;}}
  }
  long? ObserveFree(){try{return backend.FreeBytes();}catch(Exception){return null;}}
 }
}
