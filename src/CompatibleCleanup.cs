using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ClearDisk {
 internal sealed class CompatibleOption {
  internal readonly string Id,Name,Description,Warning;
  internal CompatibleOption(string id,string name,string description,string warning){Id=id;Name=name;Description=description;Warning=warning;}
 }
 internal sealed class CompatibleOutput {
  internal int ExitCode;internal string Output="",Error="";
  internal bool Complete,Cleaning;internal long Files;internal int Errors;internal string Size="未知";
  internal bool Success {get{return ExitCode==0&&Complete&&Errors==0&&String.IsNullOrWhiteSpace(Error);}}
  internal static CompatibleOutput Parse(int exit,string output,string error,bool cleaning){
   var result=new CompatibleOutput{ExitCode=exit,Output=output??"",Error=error??"",Cleaning=cleaning};
   string size=cleaning?"Disk space recovered: ":"Disk space to be recovered: ";
   string count=cleaning?"Files deleted: ":"Files to be deleted: ";
   Match bytes=Regex.Match(result.Output,@"(?m)^"+Regex.Escape(size)+@"([^\r\n]+)\r?$");
   Match files=Regex.Match(result.Output,@"(?m)^"+Regex.Escape(count)+@"([0-9]+)\r?$");
   long number;result.Complete=bytes.Success&&files.Success&&Int64.TryParse(files.Groups[1].Value,out number);
   if(result.Complete){result.Size=bytes.Groups[1].Value;result.Files=Int64.Parse(files.Groups[1].Value,CultureInfo.InvariantCulture);}
   foreach(Match m in Regex.Matches(result.Output,@"(?m)^Errors: ([0-9]+)\r?$")){int value;if(!Int32.TryParse(m.Groups[1].Value,out value))value=Int32.MaxValue;result.Errors=Math.Max(result.Errors,value);}
   if(Regex.IsMatch(result.Output,@"(?im)^(Traceback|Error:|Exception while|Failed to clean|not a valid cleaner:)"))result.Errors=Math.Max(1,result.Errors);
   return result;
  }
 }
 internal interface ICompatibleBackend {
  CompatibleOption[] List(CancellationToken token);
  CompatibleOutput Run(string[] ids,bool cleaning,CancellationToken token,Action<string> progress);
 }
 internal sealed class CompatiblePlan {
  readonly string[] ids;internal string[] Ids {get{return (string[])ids.Clone();}}internal readonly string Context;internal readonly DateTime Expires;
  internal readonly CompatibleOutput Preview;
  internal CompatiblePlan(string[] selected,string context,DateTime expires,CompatibleOutput preview){ids=(string[])selected.Clone();Context=context;Expires=expires;Preview=preview;}
 }
 internal sealed class CompatibleCleanupEngine {
  readonly ICompatibleBackend backend;readonly Func<DateTime> clock;readonly Func<string> context;
  readonly HashSet<CompatiblePlan> issued=new HashSet<CompatiblePlan>();CompatibleOption[] catalog;
  internal CompatibleCleanupEngine():this(new BleachBitBackend(AppDomain.CurrentDomain.BaseDirectory),()=>DateTime.UtcNow,CurrentContext){}
  internal CompatibleCleanupEngine(ICompatibleBackend source,Func<DateTime> time,Func<string> identity){backend=source;clock=time;context=identity;}
  internal static string CurrentContext(){using(var user=WindowsIdentity.GetCurrent())return user.User.Value+":"+new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator);}
  internal static bool AllowedId(string id){return id!=null&&Regex.IsMatch(id,@"\A[a-zA-Z0-9_]+\.[a-zA-Z0-9_]+\z")&&!new[]{"system.free_disk_space","system.memory","system.custom","system.wipe_free_space","system.recycle_bin","deepscan.backup","deepscan.tmp"}.Contains(id,StringComparer.OrdinalIgnoreCase)&&!id.StartsWith("deepscan.",StringComparison.OrdinalIgnoreCase);}
  internal CompatibleOption[] List(CancellationToken token){var result=backend.List(token);if(result.Any(o=>!AllowedId(o.Id))||result.Select(o=>o.Id).Distinct(StringComparer.Ordinal).Count()!=result.Length)throw new IOException("清理引擎目录无效。");catalog=result;issued.Clear();return (CompatibleOption[])result.Clone();}
  string[] Validate(IEnumerable<string> ids){if(catalog==null)throw new InvalidOperationException("请先读取规则目录。");var selected=ids==null?new string[0]:ids.Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal).ToArray();if(selected.Length==0||selected.Length>1000||selected.Any(id=>!AllowedId(id)||!catalog.Any(o=>o.Id==id)))throw new InvalidOperationException("请选择目录中明确列出的清理项目。");return selected;}
  // Inspection never issues deletion authority, even after a complete scan.
  internal CompatibleOutput InspectAll(CancellationToken token,Action<string> progress){
   issued.Clear();if(catalog==null)throw new InvalidOperationException("请先读取规则目录。");var ids=Validate(catalog.Select(o=>o.Id));string before=context();token.ThrowIfCancellationRequested();
   var output=backend.Run(ids,false,token,progress);token.ThrowIfCancellationRequested();if(before!=context())throw new InvalidOperationException("权限或用户发生变化，请重新扫描。");return output;
  }
  internal CompatiblePlan Preview(IEnumerable<string> ids,CancellationToken token,Action<string> progress){
   issued.Clear();var selected=Validate(ids);string before=context();var output=backend.Run(selected,false,token,progress);token.ThrowIfCancellationRequested();
   if(before!=context())throw new InvalidOperationException("权限或用户发生变化，请重新扫描。");
   if(!output.Success)throw new IOException("预览未完整成功，不允许开始清理。\r\n"+output.Error+"\r\n"+output.Output);
   var plan=new CompatiblePlan(selected,before,clock().AddMinutes(15),output);issued.Add(plan);return plan;
  }
  internal CompatibleOutput Execute(CompatiblePlan plan,IEnumerable<string> selected,bool confirmed,CancellationToken token,Action<string> progress){
   if(!confirmed)throw new InvalidOperationException("尚未确认不可撤销清理。");
   var ids=Validate(selected);if(plan==null||!issued.Remove(plan)||clock()>plan.Expires||context()!=plan.Context||!ids.SequenceEqual(plan.Ids))throw new InvalidOperationException("预览已失效，请重新预览相同项目。");
   token.ThrowIfCancellationRequested();
   // Mutation deliberately receives no cancellation token: complete the current
   // engine operation instead of killing it midway through registry/database edits.
   return backend.Run(ids,true,CancellationToken.None,progress);
  }
 }

 internal sealed class BleachBitRuntimeLease:IDisposable {
  readonly List<IDisposable> pins=new List<IDisposable>();
  internal readonly string Root;
  internal BleachBitRuntimeLease(string application){
   Root=NativeFiles.Normalize(Path.Combine(application,"vendor","bleachbit-5.0.2-cdrive"));
   try{
    pins.Add(new NativeFiles.ParentLocks(Path.Combine(Root,".pin")));
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearDisk.BleachBit.Hashes")){
     if(stream==null)throw new IOException("缺少固定引擎校验清单。");
     var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     using(var reader=new StreamReader(stream)){string row;while((row=reader.ReadLine())!=null){
      if(row.Length<67||row[64]!=' ')throw new IOException("引擎清单格式无效。");
      string relative=row.Substring(65).Replace('/','\\');if(Path.IsPathRooted(relative)||relative.Split('\\').Any(p=>p==".."||p=="."||p.Length==0))throw new IOException("引擎清单路径无效。");
      string path=NativeFiles.Normalize(Path.Combine(Root,relative));if(!SafetyPolicy.IsWithin(path,Root)||!expected.Add(path))throw new IOException("引擎清单重复或越界。");
      pins.Add(new NativeFiles.ParentLocks(path));var handle=NativeFiles.OpenData(path,false);pins.Add(handle);var stamp=NativeFiles.Stamp(handle);
      if(stamp.Links!=1||(NativeFiles.Info(handle).Attributes&0x410)!=0||!String.Equals(stamp.FinalPath,path,StringComparison.OrdinalIgnoreCase))throw new IOException("引擎文件路径或链接异常。");
      string hash=BitConverter.ToString(Convert.FromBase64String(NativeFiles.Hash(handle,CancellationToken.None))).Replace("-","");if(hash!=row.Substring(0,64))throw new IOException("引擎内容校验失败："+relative);
     }}
     var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);Walk(Root,actual);if(!expected.SetEquals(actual))throw new IOException("引擎目录出现清单外文件，请重新解压完整发行包。");
    }
   }catch{Dispose();throw;}
  }
  void Walk(string directory,HashSet<string> files){foreach(string child in Directory.GetFileSystemEntries(directory)){var attrs=File.GetAttributes(child);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("引擎目录不允许链接。");if((attrs&FileAttributes.Directory)!=0){pins.Add(new NativeFiles.ParentLocks(Path.Combine(child,".pin")));Walk(child,files);}else files.Add(NativeFiles.Normalize(child));}}
  public void Dispose(){for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();}
 }
 internal sealed class BleachBitBackend:ICompatibleBackend {
  readonly string application;readonly Func<string,CompatibleJob> jobFactory;
  internal BleachBitBackend(string directory):this(directory,path=>new CompatibleJob(path)){}
  // Test-only construction can add an owned fixture cleaner to a fresh job.
  // Production entry points always use the default, with no personal cleaners.
  internal BleachBitBackend(string directory,Func<string,CompatibleJob> jobs){application=NativeFiles.Normalize(directory);jobFactory=jobs;}
  public CompatibleOption[] List(CancellationToken token){
   using(var runtime=new BleachBitRuntimeLease(application)){
    var output=RunProcess(runtime.Root,new[]{"--list-cleaners"},false,token,null);
    if(output.ExitCode!=0||!String.IsNullOrWhiteSpace(output.Error))throw new IOException("读取可执行清理目录失败："+output.Error);
    var metadata=Metadata(runtime.Root);var values=new List<CompatibleOption>();
    foreach(string raw in output.Output.Split('\n')){string id=raw.Trim();if(id.Length==0)continue;if(!Regex.IsMatch(id,@"\A[a-zA-Z0-9_]+\.[a-zA-Z0-9_]+\z"))throw new IOException("目录输出包含未知内容："+id);if(!CompatibleCleanupEngine.AllowedId(id))continue;CompatibleOption option;if(!metadata.TryGetValue(id,out option))option=new CompatibleOption(id,id,"内置引擎清理项目","可能永久删除文件、记录或注册表数据，请核对预览。");values.Add(option);}
    if(values.Count==0)throw new IOException("清理目录为空。");return values.ToArray();
   }
  }
  public CompatibleOutput Run(string[] ids,bool cleaning,CancellationToken token,Action<string> progress){using(var runtime=new BleachBitRuntimeLease(application)){if(ids.Any(id=>!CompatibleCleanupEngine.AllowedId(id)))throw new IOException("无效的清理项目。");return RunProcess(runtime.Root,new[]{cleaning?"--clean":"--preview"}.Concat(ids).ToArray(),cleaning,token,progress);}}
  static string Normal(string value){return Regex.Replace(Regex.Replace(value.ToLowerInvariant(),@"[^a-z0-9]","_"),"_+","_").Trim('_');}
  static Dictionary<string,CompatibleOption> Metadata(string root){
   var result=new Dictionary<string,CompatibleOption>(StringComparer.Ordinal);
   var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
   foreach(string path in Directory.GetFiles(Path.Combine(root,"share","cleaners"),"*.xml")){
    var doc=new XmlDocument{XmlResolver=null};using(var reader=XmlReader.Create(path,settings))doc.Load(reader);XmlElement cleaner=doc.DocumentElement;if(cleaner==null||cleaner.Name!="cleaner")continue;string group=cleaner.GetAttribute("id"),label=Text(cleaner,"label");
    foreach(XmlElement option in cleaner.SelectNodes("option")){string id=group+"."+option.GetAttribute("id");result[id]=new CompatibleOption(id,label+" / "+Text(option,"label"),Text(option,"description"),Text(option,"warning"));}
   }
   var groups=new Dictionary<string,string>{{"3001","internet_explorer"},{"3005","edge_classic"},{"3006","edge_chromium"},{"3021","applications"},{"3022","internet"},{"3023","multimedia"},{"3024","utilities"},{"3025","windows"},{"3026","mozilla"},{"3027","opera"},{"3028","safari"},{"3029","google_chrome"},{"3030","thunderbird"},{"3031","windows_store"},{"3033","vivaldi"},{"3034","brave"}};
   using(var reader=new StreamReader(Path.Combine(root,"share","cleaners","winapp2.ini")))foreach(var entry in CommunityRules.Parse(reader).Catalog){List<string> values;string section=entry.Directives.TryGetValue("LangSecRef",out values)?values.Last():entry.Directives.TryGetValue("Section",out values)?values.Last():"Unknown";string mapped;string group=groups.TryGetValue(section,out mapped)?mapped:Normal(section);string id="winapp2_"+group+"."+Normal(entry.Name);string warning=entry.Directives.TryGetValue("Warning",out values)?String.Join("\r\n",values):"";result[id]=new CompatibleOption(id,entry.Name.TrimEnd(' ','*'),"Winapp2 251109；文件规则 "+entry.FileKeys.Count+"，注册表规则 "+entry.RegistryKeys+"。",warning);}
   result["system.updates"]=new CompatibleOption("system.updates","Windows 更新与旧安装清理","使用引擎的系统维护动作，包括更新服务停止/重启与旧数据删除。","会处理整个 SoftwareDistribution、Windows.old 和升级残留。旧系统回退及 Windows.old 中旧账户资料可能永久丢失；正在安装更新时不要执行。请先备份旧资料并核对路径和服务动作。");
   result["system.tmp"]=new CompatibleOption("system.tmp","System / 临时文件","当前进程临时目录及 Windows Temp。","临时目录可能含未保存内容或安装中的文件。请关闭安装程序和相关应用后执行；占用文件保留并报告失败，不安排重启删除。");
   return result;
  }
  static string Text(XmlElement node,string tag){var child=node.SelectSingleNode(tag);return child==null?"":child.InnerText;}
  CompatibleOutput RunProcess(string runtime,string[] args,bool cleaning,CancellationToken token,Action<string> progress){
   using(var job=jobFactory(application)){
    var psi=new ProcessStartInfo(Path.Combine(runtime,"bleachbit_console.exe"),String.Join(" ",args.Select(Quote))){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=runtime,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
    foreach(string key in new[]{"PYTHONPATH","PYTHONHOME","PYTHONSTARTUP","PYTHONINSPECT"})psi.EnvironmentVariables.Remove(key);
    psi.EnvironmentVariables["BLEACHBIT_TEST_OPTIONS_DIR"]=job.Root;psi.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";psi.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"]="1";
    using(var process=new Process{StartInfo=psi}){
     token.ThrowIfCancellationRequested();if(!process.Start())throw new IOException("未能启动清理引擎。");
     var stdout=Task.Factory.StartNew(()=>Drain(process.StandardOutput,progress),TaskCreationOptions.LongRunning);var stderr=Task.Factory.StartNew(()=>Drain(process.StandardError,null),TaskCreationOptions.LongRunning);
     while(!process.WaitForExit(100)){if(!cleaning&&token.IsCancellationRequested){try{process.Kill();}catch(InvalidOperationException){}process.WaitForExit();Task.WaitAll(stdout,stderr);token.ThrowIfCancellationRequested();}}
     Task.WaitAll(stdout,stderr);token.ThrowIfCancellationRequested();return CompatibleOutput.Parse(process.ExitCode,stdout.Result,stderr.Result,cleaning);
    }
   }
  }
  internal static string Drain(TextReader reader,Action<string> progress){var head=new StringBuilder();var tail=new StringBuilder();char[] buffer=new char[4096];int count;long total=0;while((count=reader.Read(buffer,0,buffer.Length))>0){total+=count;if(head.Length<262144)head.Append(buffer,0,Math.Min(count,262144-head.Length));tail.Append(buffer,0,count);if(tail.Length>65536)tail.Remove(0,tail.Length-65536);if(progress!=null){try{progress("正在处理规则；已读取 "+total.ToString("N0")+" 个输出字符。");}catch{}}}return total<=262144?head.ToString():head+"\r\n[中间明细已省略；完整结尾如下]\r\n"+tail;}
  static string Quote(string value){if(value==null||!Regex.IsMatch(value,@"\A[-a-zA-Z0-9_.]+\z"))throw new IOException("不允许任意引擎参数。");return value;}
 }
 internal sealed class CompatibleJob:IDisposable {
  readonly List<IDisposable> pins=new List<IDisposable>();internal readonly string Root;
  internal CompatibleJob(string application){
   try{
    string current=application;pins.Add(new NativeFiles.ParentLocks(Path.Combine(current,".pin")));
    foreach(string part in new[]{"reports","compatible-jobs",Guid.NewGuid().ToString("N")}){current=Path.Combine(current,part);Directory.CreateDirectory(current);pins.Add(new NativeFiles.ParentLocks(Path.Combine(current,".pin")));}Root=current;
    var config=new StringBuilder("[bleachbit]\r\nversion = 5.0.2\r\nfirst_start = False\r\nauto_detect_lang = False\r\nforced_language = en\r\ncheck_online_updates = False\r\nupdate_winapp2 = False\r\nshred = False\r\nunits_iec = False\r\n[whitelist/paths]\r\n");
    var excluded=new List<string>{application,@"\"};for(char drive='A';drive<='Z';drive++)if(drive!='C')excluded.Add(drive+@":\");
    string profile=NativeFiles.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    excluded.Add(Path.Combine(profile,"AppData","Local","CDriveCleaner"));
    excluded.Add(Path.Combine(profile,".CDriveCleanerMigrationV1"));
    // Preserve personal trees and migration artifacts outside AppData. Compatible
    // rules remain able to address current-user AppData caches and system garbage.
    foreach(string child in Directory.GetDirectories(profile))if(!String.Equals(Path.GetFileName(child),"AppData",StringComparison.OrdinalIgnoreCase))excluded.Add(child);
    string profiles=Path.GetDirectoryName(profile);foreach(string child in Directory.GetDirectories(profiles))if(!String.Equals(child,profile,StringComparison.OrdinalIgnoreCase))excluded.Add(child);
    for(int i=0;i<excluded.Count;i++)config.Append(i+"_type = folder\r\n"+i+"_path = "+excluded[i]+"\r\n");
    using(var stream=new FileStream(Path.Combine(Root,"bleachbit.ini"),FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(config);
    // No personal cleaners directory is created. Only the pinned packaged rules load.
   }catch{Dispose();throw;}
  }
  public void Dispose(){for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();}
 }
}
