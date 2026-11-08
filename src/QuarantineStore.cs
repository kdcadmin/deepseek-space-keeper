using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ClearDisk {
 public sealed class QuarantineEntry {
  public string Id,OriginalPath,StoredPath,ReceiptPath,State,Reason;
  public long Length;
 }
 public sealed class QuarantineStore {
  public sealed class Receipt {
   public int Version=2;
   public string Id,OriginalPath,Sha256;
   public uint Volume;
   public ulong FileId;
   public long Length,LastWriteTicks;
  }
  readonly SafetyPolicy policy;
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("ClearDisk-v2-personal-quarantine");
  static readonly Encoding ReceiptUtf8=new UTF8Encoding(false,true);
  public string Root {get;private set;}
  public QuarantineStore(SafetyPolicy safety){if(safety==null)throw new ArgumentNullException("safety");policy=safety;Root=Path.Combine(policy.ProfileRoot,"AppData","Local","CDriveCleaner","Quarantine");}
  void EnsureStore(){
   string local=Path.GetDirectoryName(Path.GetDirectoryName(Root)),app=Path.GetDirectoryName(Root);
   using(var localPins=new NativeFiles.ParentLocks(Path.Combine(local,".guard"))){
    Directory.CreateDirectory(app);
    using(var appPins=new NativeFiles.ParentLocks(Path.Combine(app,".guard"))){Directory.CreateDirectory(Root);}
   }
  }
  static void CheckOrdinary(NativeFiles.Information info){if((info.Attributes&(0x400u|0x4u|0x1u|0x10u|0x1000u|0x40000u))!=0||info.Links!=1)throw new IOException("跳过只读、系统、链接、目录或云端占位文件。");}
  static bool Identity(FileStamp stamp,Receipt receipt){return stamp.Volume==receipt.Volume&&stamp.FileId==receipt.FileId&&stamp.Length==receipt.Length&&stamp.LastWriteTicks==receipt.LastWriteTicks&&stamp.Links==1;}
  static bool Identity(FileStamp current,FileStamp before){return current.Volume==before.Volume&&current.FileId==before.FileId&&current.Length==before.Length&&current.LastWriteTicks==before.LastWriteTicks&&current.Links==1;}
  string ItemPath(Receipt receipt){return Path.Combine(Root,receipt.Id+".data");}
  void WriteReceipt(string path,Receipt receipt){
   byte[] bytes=ProtectedData.Protect(ReceiptUtf8.GetBytes(new JavaScriptSerializer().Serialize(receipt)),Entropy,DataProtectionScope.CurrentUser);
   using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
  }
  Receipt ReadReceipt(string path){
   string full=NativeFiles.Normalize(path);
   if(!String.Equals(Path.GetDirectoryName(full),Root,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full),@"^[a-f0-9]{32}\.receipt$"))throw new IOException("不是本工具隔离区的记录。");
   using(var pins=new NativeFiles.ParentLocks(full))
   using(var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read)){
    CheckOrdinary(NativeFiles.Info(stream.SafeFileHandle));
    if(!String.Equals(NativeFiles.Final(stream.SafeFileHandle),full,StringComparison.OrdinalIgnoreCase)||stream.Length<=0||stream.Length>65536)throw new IOException("隔离记录路径或大小异常。");
    byte[] bytes=new byte[(int)stream.Length];int total=0,n;while(total<bytes.Length&&(n=stream.Read(bytes,total,bytes.Length-total))>0)total+=n;if(total!=bytes.Length)throw new IOException("隔离记录不完整。");
    byte[] plain=ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser);
    var receipt=new JavaScriptSerializer().Deserialize<Receipt>(ReceiptUtf8.GetString(plain));
    if(receipt==null||receipt.Version!=2||receipt.Id+".receipt"!=Path.GetFileName(full)||!policy.PersonalOrganizationPath(receipt.OriginalPath,false)||receipt.Length<0||String.IsNullOrEmpty(receipt.Sha256)||Convert.FromBase64String(receipt.Sha256).Length!=32)throw new IOException("记录不属于允许的个人文件范围，或完整内容指纹缺失。");
    return receipt;
   }
  }
  public bool Move(FileRecord file,out string receiptPath,out string reason){return Move(file,out receiptPath,out reason,System.Threading.CancellationToken.None);}
  public bool Move(FileRecord file,out string receiptPath,out string reason,System.Threading.CancellationToken token){
   receiptPath=null;reason="";
   lock(policy.OrganizationGate){try {
    if(file==null||!file.CanOrganize)throw new IOException("该文件没有手动整理资格。");
    FileStamp baseline=policy.OrganizationSnapshot(file);
    EnsureStore();
    using(var sourcePins=new NativeFiles.ParentLocks(file.Path))
    using(var destinationPins=new NativeFiles.ParentLocks(Path.Combine(Root,".guard")))
    using(var keeperGuard=new RetainedGuard(policy,file.KeeperRecord,token))
    using(var handle=NativeFiles.OpenData(file.Path,true)){
     CheckOrdinary(NativeFiles.Info(handle));var current=NativeFiles.Stamp(handle);
     if(!Identity(current,baseline)||!String.Equals(current.FinalPath,baseline.FinalPath,StringComparison.OrdinalIgnoreCase))throw new IOException("文件自扫描后已变化，请重新扫描。");
     string hash=NativeFiles.Hash(handle,token);
     if(file.KeeperRecord!=null&&!String.Equals(hash,file.ContentHash,StringComparison.Ordinal))throw new IOException("重复候选内容自扫描后已变化，拒绝整理。");
     var receipt=new Receipt {Id=Guid.NewGuid().ToString("N"),OriginalPath=baseline.FinalPath,Volume=baseline.Volume,FileId=baseline.FileId,Length=baseline.Length,LastWriteTicks=baseline.LastWriteTicks,Sha256=hash};
     receiptPath=Path.Combine(Root,receipt.Id+".receipt");
     WriteReceipt(receiptPath,receipt); // Durable journal exists even if the process stops immediately after rename.
     token.ThrowIfCancellationRequested();
     NativeFiles.Rename(handle,ItemPath(receipt));policy.MarkOrganized(file);
     reason="已移入可恢复隔离区；仍占用 C 盘空间。";return true;
    }
   }catch(Exception e){if(!Expected(e))throw;reason=e.Message;return false;}}
  }
  sealed class RetainedGuard : IDisposable {
   NativeFiles.ParentLocks parents;Microsoft.Win32.SafeHandles.SafeFileHandle handle;
   internal RetainedGuard(SafetyPolicy policy,FileRecord keeper,System.Threading.CancellationToken token){
    if(keeper==null)return;
    try {var saved=policy.KeeperSnapshot(keeper);parents=new NativeFiles.ParentLocks(keeper.Path);handle=NativeFiles.OpenData(keeper.Path,false);
     CheckOrdinary(NativeFiles.Info(handle));var current=NativeFiles.Stamp(handle);
     if(!Identity(current,saved)||!String.Equals(current.FinalPath,saved.FinalPath,StringComparison.OrdinalIgnoreCase))throw new IOException("重复组保留项已变化，拒绝整理。");
     if(String.IsNullOrEmpty(keeper.ContentHash)||NativeFiles.Hash(handle,token)!=keeper.ContentHash)throw new IOException("重复组保留项内容已变化，拒绝整理。");
    }catch{Dispose();throw;}
   }
   public void Dispose(){if(handle!=null)handle.Dispose();if(parents!=null)parents.Dispose();}
  }
  public bool Restore(string receiptPath,out string reason){return Restore(receiptPath,out reason,System.Threading.CancellationToken.None);}
  public bool Restore(string receiptPath,out string reason,System.Threading.CancellationToken token){
   reason="";try {
    Receipt receipt=ReadReceipt(receiptPath);string stored=ItemPath(receipt);
    if(!policy.PersonalOrganizationPath(receipt.OriginalPath))throw new IOException("原位置现在属于受保护目录，不能恢复到此位置；隔离文件仍然保留。");
    using(var qp=new NativeFiles.ParentLocks(stored))
    using(var op=new NativeFiles.ParentLocks(receipt.OriginalPath))
    using(var handle=NativeFiles.OpenData(stored,true)){
     VerifyItem(handle,stored,receipt,true,token);token.ThrowIfCancellationRequested();
     NativeFiles.Rename(handle,receipt.OriginalPath);reason="已恢复原位置，原位置已有文件时绝不覆盖。";return true;
    }
   }catch(Exception e){if(!Expected(e))throw;reason=e.Message;return false;}
  }
  public bool Purge(string receiptPath,out string reason){return Purge(receiptPath,out reason,System.Threading.CancellationToken.None);}
  public bool Purge(string receiptPath,out string reason,System.Threading.CancellationToken token){
   reason="";try {
    Receipt receipt=ReadReceipt(receiptPath);string stored=ItemPath(receipt);
    using(var pins=new NativeFiles.ParentLocks(stored))
    using(var handle=NativeFiles.OpenData(stored,true)){VerifyItem(handle,stored,receipt,true,token);token.ThrowIfCancellationRequested();NativeFiles.Delete(handle);}
    reason="已彻底删除核验过的隔离文件，无法恢复。";return true;
   }catch(Exception e){if(!Expected(e))throw;reason=e.Message;return false;}
  }
  static void VerifyItem(Microsoft.Win32.SafeHandles.SafeFileHandle handle,string stored,Receipt receipt,bool verifyHash,System.Threading.CancellationToken token){
   CheckOrdinary(NativeFiles.Info(handle));var stamp=NativeFiles.Stamp(handle);
   if(!Identity(stamp,receipt)||!String.Equals(stamp.FinalPath,stored,StringComparison.OrdinalIgnoreCase))throw new IOException("隔离文件身份或内容已变化，不执行操作。");
   if(verifyHash&&NativeFiles.Hash(handle,token)!=receipt.Sha256)throw new IOException("隔离文件完整 SHA-256 与原始记录不同，拒绝恢复或删除。");
  }
  public List<QuarantineEntry> List(){return List(System.Threading.CancellationToken.None);}
  public List<QuarantineEntry> List(System.Threading.CancellationToken token){
   var entries=new List<QuarantineEntry>();if(!Directory.Exists(Root))return entries;
   using(var pins=new NativeFiles.ParentLocks(Path.Combine(Root,".guard"))){
    foreach(string path in Directory.EnumerateFiles(Root,"*.receipt")){
     token.ThrowIfCancellationRequested();
     var entry=new QuarantineEntry {ReceiptPath=path};
     try {
      var receipt=ReadReceipt(path);entry.Id=receipt.Id;entry.OriginalPath=receipt.OriginalPath;entry.StoredPath=ItemPath(receipt);entry.Length=receipt.Length;
      if(File.Exists(entry.StoredPath)){
       using(var handle=NativeFiles.Open(entry.StoredPath,false,false)){VerifyItem(handle,entry.StoredPath,receipt,false,token);}
       entry.State="可恢复";entry.Reason=policy.PersonalOrganizationPath(receipt.OriginalPath)?"隔离仍占用空间；可尝试恢复原位置，或另行确认彻底删除。":"原位置目前受保护，无法恢复；隔离数据仍在，可核对后彻底删除。";
      }else {entry.State=File.Exists(entry.OriginalPath)?"未移入 / 已恢复":"隔离文件不存在";entry.Reason="可能已恢复、彻底删除或被外部移动；没有可操作的隔离文件。";}
     }catch(Exception e){if(!Expected(e))throw;entry.State="记录损坏";entry.Reason=e.Message;}
     entries.Add(entry);
    }
   }
   return entries;
  }
  static bool Expected(Exception e){return e is IOException||e is UnauthorizedAccessException||e is System.ComponentModel.Win32Exception||e is CryptographicException||e is ArgumentException||e is System.Security.SecurityException||e is InvalidOperationException||e is FormatException;}
 }
}
