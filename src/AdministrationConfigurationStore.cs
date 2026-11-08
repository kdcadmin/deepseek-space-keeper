using System;using System.IO;using System.Collections.Generic;using System.Text;using System.Security.Cryptography;using System.Web.Script.Serialization;
namespace ClearDisk {
 internal sealed class AdministrationConfigurationStore {
  readonly string root;static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CDriveCleaner-pagefile-settings-v1");
  internal AdministrationConfigurationStore():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CDriveCleaner","SystemConfigurationBackups")){}
  internal AdministrationConfigurationStore(string directory){root=NativeFiles.Normalize(directory);}
  internal string Save(AdministrationRecord record){
   string parent=Path.GetDirectoryName(root);Directory.CreateDirectory(parent);using(var guard=new NativeFiles.ParentLocks(Path.Combine(parent,".guard")))Directory.CreateDirectory(root);
   string id=Guid.NewGuid().ToString("N");string path=Path.Combine(root,id+".settings");byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record)),Entropy,DataProtectionScope.CurrentUser);
   using(var pins=new NativeFiles.ParentLocks(path))using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}return id;
  }
  internal string[] List(){if(!Directory.Exists(root))return new string[0];using(var pins=new NativeFiles.ParentLocks(Path.Combine(root,".guard"))){var result=new List<string>();foreach(var path in Directory.EnumerateFiles(root,"*.settings")){string id=Path.GetFileNameWithoutExtension(path);Guid parsed;if(Guid.TryParseExact(id,"N",out parsed)){try{Read(id);result.Add(id);}catch{}}if(result.Count>1000)throw new IOException("系统配置备份数量超过上限。");}return result.ToArray();}}
  internal AdministrationRecord Read(string id){Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new IOException("备份身份无效。");string path=Path.Combine(root,id+".settings");using(var pins=new NativeFiles.ParentLocks(path))using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
   var info=NativeFiles.Info(stream.SafeFileHandle);if(info.Links!=1||(info.Attributes&0x410)!=0||stream.Length<1||stream.Length>1048576||!String.Equals(path,NativeFiles.Final(stream.SafeFileHandle),StringComparison.OrdinalIgnoreCase))throw new IOException("配置备份路径或身份异常。");byte[] bytes=new byte[(int)stream.Length];int total=0,n;while(total<bytes.Length&&(n=stream.Read(bytes,total,bytes.Length-total))>0)total+=n;if(total!=bytes.Length)throw new IOException("配置备份不完整。");var value=new JavaScriptSerializer().Deserialize<AdministrationRecord>(Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser)));if(value==null||value.Id!="pagefile-config"||!value.Fields.ContainsKey("Settings")||!value.Fields.ContainsKey("Automatic"))throw new IOException("不是分页配置备份。");return value;
  }}
 }
}
