using System;using System.IO;using System.Linq;using System.Text;using System.Security.Cryptography;using System.Web.Script.Serialization;
namespace ClearDisk {
 internal sealed class RuleRelease {
  internal readonly string Version,FileName,SHA256;internal RuleRelease(string v,string f,string h){Version=v;FileName=f;SHA256=h;}
 }
 internal sealed class RuleReleaseManager {
  readonly string application,storage;
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CDriveCleaner-verified-rule-release-v1");
  static readonly RuleRelease[] Known={new RuleRelease("251109","Winapp2-251109.ini","359543D47AC69C5300686ED55A36736C82BE17E6EE53F10CE82F2AD52FB558F8"),new RuleRelease("260915","Winapp2.ini","54CE86839B5C5E6ED796CC17B0CE879A795920BA5FCB930DC84BBA7ECE535C5B")};
  internal RuleReleaseManager(string root):this(root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CDriveCleaner","Rules")){}
  internal RuleReleaseManager(string root,string state){application=NativeFiles.Normalize(root);storage=NativeFiles.Normalize(state);}
  internal RuleRelease[] List(){return (RuleRelease[])Known.Clone();}
  static string Hash(Stream file){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
  internal static string ValidateKnown(string file){
   string path=NativeFiles.Normalize(file);using(var pins=new NativeFiles.ParentLocks(path))using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var info=NativeFiles.Info(stream.SafeFileHandle);if(info.Links!=1||(info.Attributes&0x410)!=0||!String.Equals(path,NativeFiles.Final(stream.SafeFileHandle),StringComparison.OrdinalIgnoreCase))throw new IOException("规则文件链接或身份异常。");
    string hash=Hash(stream);var release=Known.FirstOrDefault(r=>r.SHA256==hash);if(release==null)throw new IOException("不是本版本审核并固定哈希的规则库。");return release.Version;
   }
  }
  string Resolve(string version){var release=Known.FirstOrDefault(r=>r.Version==version);if(release==null)throw new IOException("未知规则版本；请安装经验证的完整程序发行包。");string path=Path.Combine(application,"data",release.FileName);if(ValidateKnown(path)!=release.Version)throw new IOException("规则版本不匹配。");return path;}
  string Receipt{get{return Path.Combine(storage,"active-rules.dat");}}
  internal string GetActiveRules(){
   if(!File.Exists(Receipt))return Resolve("251109");
   using(var pins=new NativeFiles.ParentLocks(Receipt))using(var file=new FileStream(Receipt,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var info=NativeFiles.Info(file.SafeFileHandle);if(info.Links!=1||(info.Attributes&0x410)!=0||file.Length<1||file.Length>4096||!String.Equals(NativeFiles.Final(file.SafeFileHandle),Receipt,StringComparison.OrdinalIgnoreCase))throw new IOException("规则选择记录异常。");
    byte[] bytes=new byte[(int)file.Length];int count=0,n;while(count<bytes.Length&&(n=file.Read(bytes,count,bytes.Length-count))>0)count+=n;if(count!=bytes.Length)throw new IOException("规则选择记录不完整。");
    return Resolve(Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser)));
   }
  }
  internal string SelectionRevision(){
   string active=ValidateKnown(GetActiveRules());if(!File.Exists(Receipt))return active+":default";
   using(var pins=new NativeFiles.ParentLocks(Receipt))using(var stream=new FileStream(Receipt,FileMode.Open,FileAccess.Read,FileShare.Read)){var info=NativeFiles.Info(stream.SafeFileHandle);if(info.Links!=1||(info.Attributes&0x410)!=0||stream.Length>4096)throw new IOException("规则选择记录异常。");return active+":"+Hash(stream);}
  }
  internal void Activate(string version){
   Resolve(version);string parent=Path.GetDirectoryName(storage);Directory.CreateDirectory(parent);
   using(var guard=new NativeFiles.ParentLocks(Path.Combine(parent,".guard"))){Directory.CreateDirectory(storage);}
   using(var pins=new NativeFiles.ParentLocks(Receipt)){
    string temp=Path.Combine(storage,Guid.NewGuid().ToString("N")+".new");byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(version),Entropy,DataProtectionScope.CurrentUser);
    using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
    if(File.Exists(Receipt)){using(var existing=NativeFiles.OpenData(Receipt,false)){if(NativeFiles.Info(existing).Links!=1)throw new IOException("规则记录硬链接异常。");}File.Replace(temp,Receipt,null);}else File.Move(temp,Receipt);
   }
  }
  internal string Diagnostics(){return new JavaScriptSerializer().Serialize(new{Version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),OS=Environment.OSVersion.VersionString,Is64Bit=Environment.Is64BitProcess,Context=CompatibleCleanupEngine.CurrentContext(),ActiveRules=ValidateKnown(GetActiveRules()),RuleReleases=Known.Select(r=>new{r.Version,r.SHA256}).ToArray(),Engine="BleachBit 5.0.2 C-drive adaptation",Timestamp=DateTime.UtcNow.ToString("o"),MutationRun=false});}
 }
}
