using System;using System.Collections.Generic;using System.ComponentModel;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.Web.Script.Serialization;using Microsoft.Win32;using Microsoft.Win32.SafeHandles;
namespace ClearDisk {
 // Transactions make verification and deletion one conflict-checked operation.
 // If TxR is unavailable the settings operation is refused, never downgraded.
 internal sealed class StructuredRegistrySnapshot {
  public sealed class Value {public string Name;public uint Kind;public byte[] Data;}
  public sealed class Node {public string Name;public Value[] Values;public Node[] Children;}
  public string Key,ValueName;public bool IsValue;public Node Tree;public Value Setting;public string View="Registry64";
  static IntPtr Hive(string key,out string sub){int split=(key??"").IndexOf('\\');if(split<1)throw new IOException("注册表路径缺少明确子键。");string prefix=key.Substring(0,split);sub=key.Substring(split+1);if(sub.Split('\\').Any(s=>s.Length==0||s=="."||s=="..")||sub.IndexOfAny("*?%/|\0".ToCharArray())>=0||sub.Length>4096)throw new IOException("注册表路径含不支持的字符。");if(prefix=="HKEY_CURRENT_USER"||prefix=="HKCU")return new IntPtr(unchecked((int)0x80000001));if(prefix=="HKEY_LOCAL_MACHINE"||prefix=="HKLM")return new IntPtr(unchecked((int)0x80000002));throw new IOException("仅支持明确的 HKCU/HKLM 设置。");}
  internal static StructuredRegistrySnapshot Capture(string key,string value){string sub;var hive=Hive(key,out sub);if(sub.Split('\\').Length<2)throw new IOException("拒绝处理注册表顶层根键。");using(var tx=new Transaction())using(var opened=tx.Open(hive,sub,false)){var result=new StructuredRegistrySnapshot{Key=key,ValueName=value,IsValue=!String.IsNullOrEmpty(value)};int budget=20000;if(result.IsValue)result.Setting=ReadValue(opened,value);else result.Tree=ReadTree(opened,"",ref budget);return result;}}
  static Value ReadValue(RegistryKey key,string name){uint size=0,kind;int code=RegQueryValueEx(key.Handle,name,IntPtr.Zero,out kind,null,ref size);if(code!=0)throw new Win32Exception(code);if(size>16777216)throw new IOException("设置内容过大，拒绝自动处理。");byte[] data=new byte[(int)size];code=RegQueryValueEx(key.Handle,name,IntPtr.Zero,out kind,data,ref size);if(code!=0)throw new Win32Exception(code);if(size!=data.Length)Array.Resize(ref data,(int)size);return new Value{Name=name,Kind=kind,Data=data};}
  static Node ReadTree(RegistryKey key,string name,ref int budget){if(--budget<0)throw new IOException("设置树过大，拒绝自动处理。");var node=new Node{Name=name,Values=key.GetValueNames().OrderBy(n=>n,StringComparer.Ordinal).Select(n=>ReadValue(key,n)).ToArray()};var children=new List<Node>();foreach(string child in key.GetSubKeyNames().OrderBy(n=>n,StringComparer.Ordinal)){using(var opened=key.OpenSubKey(child,false)){if(opened==null)throw new IOException("注册表扫描期间变化。");children.Add(ReadTree(opened,child,ref budget));}}node.Children=children.ToArray();return node;}
  static string Canonical(object value){return new JavaScriptSerializer{MaxJsonLength=67108864,RecursionLimit=256}.Serialize(value);}
  void Verify(RegistryKey opened){if(IsValue){if(Canonical(ReadValue(opened,ValueName))!=Canonical(Setting))throw new IOException("设置自预览后变化。");}else{int budget=20000;if(Canonical(ReadTree(opened,"",ref budget))!=Canonical(Tree))throw new IOException("注册表键或子项自预览后变化，全部保留。");}}
  internal void DeleteFrozen(){string sub;IntPtr hive=Hive(Key,out sub);using(var tx=new Transaction()){
   using(var opened=tx.Open(hive,sub,true)){Verify(opened);if(IsValue)opened.DeleteValue(ValueName,true);else DeleteChildren(opened);}
   if(!IsValue)tx.Delete(hive,sub);tx.Commit();
  }}
  static void DeleteChildren(RegistryKey key){foreach(string name in key.GetSubKeyNames()){using(var child=key.OpenSubKey(name,true)){if(child==null)throw new IOException("事务注册表子项缺失。");DeleteChildren(child);}key.DeleteSubKey(name,true);}foreach(string value in key.GetValueNames())key.DeleteValue(value,true);}
  internal void RestoreFrozen(){string sub;IntPtr hive=Hive(Key,out sub);using(var tx=new Transaction()){
   if(IsValue){using(var opened=tx.Open(hive,sub,true)){if(opened.GetValueNames().Any(n=>n.Equals(ValueName,StringComparison.OrdinalIgnoreCase)))throw new IOException("原设置已存在，拒绝覆盖。");WriteValue(opened,Setting);}}
   else{using(var opened=tx.CreateNew(hive,sub)){WriteTree(opened,Tree);}}tx.Commit();
  }}
  static void WriteValue(RegistryKey key,Value value){int code=RegSetValueEx(key.Handle,value.Name,0,value.Kind,value.Data,(uint)value.Data.Length);if(code!=0)throw new Win32Exception(code);}
  static void WriteTree(RegistryKey key,Node node){foreach(var value in node.Values)WriteValue(key,value);foreach(var child in node.Children)using(var created=key.CreateSubKey(child.Name)){WriteTree(created,child);}}
  sealed class Transaction:IDisposable {
   readonly SafeFileHandle handle;bool completed;
   internal Transaction(){handle=CreateTransaction(IntPtr.Zero,IntPtr.Zero,0,0,0,0,"CDriveCleaner frozen settings");if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code,"Windows 注册表事务不可用；设置清理未执行。");}}
   internal RegistryKey Open(IntPtr root,string sub,bool write){SafeRegistryHandle key;int code=RegOpenKeyTransacted(root,sub,0,(write?0xF003Fu:0x20019u)|0x100u,out key,handle,IntPtr.Zero);if(code!=0){if(key!=null)key.Dispose();throw new Win32Exception(code);}return RegistryKey.FromHandle(key,RegistryView.Registry64);}
   internal RegistryKey CreateNew(IntPtr root,string sub){SafeRegistryHandle key;uint disposition;int code=RegCreateKeyTransacted(root,sub,0,null,0,0xF003Fu|0x100u,IntPtr.Zero,out key,out disposition,handle,IntPtr.Zero);if(code!=0){if(key!=null)key.Dispose();throw new Win32Exception(code);}if(disposition!=1){key.Dispose();throw new IOException("原设置键已存在，拒绝合并覆盖。");}return RegistryKey.FromHandle(key,RegistryView.Registry64);}
   internal void Delete(IntPtr root,string sub){int code=RegDeleteKeyTransacted(root,sub,0x100,0,handle,IntPtr.Zero);if(code!=0)throw new Win32Exception(code);}
   internal void Commit(){if(!CommitTransaction(handle))throw new Win32Exception(Marshal.GetLastWin32Error(),"设置事务提交冲突或失败；没有提交清理。");completed=true;}
   public void Dispose(){if(!completed)RollbackTransaction(handle);handle.Dispose();}
  }
  [DllImport("KtmW32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateTransaction(IntPtr security,IntPtr id,uint options,uint isolation,uint flags,uint timeout,string description);
  [DllImport("KtmW32.dll",SetLastError=true)]static extern bool CommitTransaction(SafeFileHandle tx);
  [DllImport("KtmW32.dll",SetLastError=true)]static extern bool RollbackTransaction(SafeFileHandle tx);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegOpenKeyTransacted(IntPtr root,string sub,uint options,uint access,out SafeRegistryHandle key,SafeFileHandle tx,IntPtr extended);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegCreateKeyTransacted(IntPtr root,string sub,uint reserved,string cls,uint options,uint access,IntPtr security,out SafeRegistryHandle key,out uint disposition,SafeFileHandle tx,IntPtr extended);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegDeleteKeyTransacted(IntPtr root,string sub,uint access,uint reserved,SafeFileHandle tx,IntPtr extended);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegQueryValueEx(SafeRegistryHandle key,string name,IntPtr reserved,out uint kind,byte[] data,ref uint size);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegSetValueEx(SafeRegistryHandle key,string name,uint reserved,uint kind,byte[] data,uint size);
 }
}
