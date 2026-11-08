using System;
using System.Collections.Generic;
using System.IO;

namespace ClearDisk {
 public sealed partial class SafetyPolicy {
  internal readonly object OrganizationGate=new object();
  readonly HashSet<FileStamp> organized=new HashSet<FileStamp>();
  internal bool PersonalOrganizationPath(string path) {return PersonalOrganizationPath(path,true);}
  internal bool PersonalOrganizationPath(string path,bool currentProtection) {
   try {
    string p=NativeFiles.Normalize(path);
    if(!IsWithin(p,profile)||p.Equals(profile,StringComparison.OrdinalIgnoreCase)||(currentProtection&&IsProtected(p)))return false;
    bool personal=false;
    foreach(string name in new[]{"Desktop","Documents","Downloads","Pictures","Videos","Music"})
     if(IsWithin(p,Path.Combine(profile,name))||IsWithin(p,Path.Combine(profile,"OneDrive",name)))personal=true;
    if(!personal)return false;
    foreach(string part in p.Substring(profile.Length).Split('\\'))
     if(Array.IndexOf(new[]{"appdata","windows","programdata","program files","software","apps","packages","env","envs"},part.ToLowerInvariant())>=0)return false;
    string ext=Path.GetExtension(p).ToLowerInvariant();
    if(Array.IndexOf(new[]{".txt",".md",".pdf",".doc",".docx",".odt",".rtf",".xls",".xlsx",".ods",".csv",".ppt",".pptx",".odp",".jpg",".jpeg",".png",".gif",".bmp",".webp",".heic",".heif",".avif",".tif",".tiff",".ico",".svg",".psd",".ai",".eps",".raw",".cr2",".nef",".dng",".arw",".raf",".mp4",".mkv",".mov",".avi",".wmv",".webm",".m4v",".mpg",".mpeg",".mp3",".wav",".flac",".aac",".m4a",".ogg",".opus",".wma",".aiff",".amr",".silk",".zip",".7z",".rar",".tar",".gz",".bz2",".xz",".zst",".tgz",".iso",".epub",".mobi",".azw3",".srt",".ass",".vtt"},ext)<0)return false;
    return true;
   }catch{return false;}
  }
  internal FileStamp OrganizationSnapshot(FileRecord file) {
   if(file==null||file.Stamp==null||file.IsKeeper||!PersonalOrganizationPath(file.Path))throw new IOException("只允许手动整理普通个人文件，保留项、软件、数据库和运行环境受到保护。");
   Snapshot snapshot;
   if(organized.Contains(file.Stamp)||!snapshots.TryGetValue(file.Stamp,out snapshot)||!Same(snapshot.Stamp,file.Stamp)||!String.Equals(snapshot.Root,NativeFiles.Normalize(file.Root),StringComparison.OrdinalIgnoreCase)||!String.Equals(snapshot.Stamp.FinalPath,NativeFiles.Normalize(file.Path),StringComparison.OrdinalIgnoreCase))throw new IOException("文件不是本次扫描核验的有效个人文件，或该快照已经使用。请重新扫描。");
   return snapshot.Stamp;
  }
  public bool CanOrganize(FileRecord file) {
   lock(OrganizationGate) {try {OrganizationSnapshot(file);return true;}catch{return false;}}
  }
  internal void MarkOrganized(FileRecord file) {organized.Add(file.Stamp);}
  internal FileStamp KeeperSnapshot(FileRecord keeper) {
   Snapshot snapshot;
   if(keeper==null||!keeper.IsKeeper||keeper.Stamp==null||organized.Contains(keeper.Stamp)||!snapshots.TryGetValue(keeper.Stamp,out snapshot)||!Same(snapshot.Stamp,keeper.Stamp)||!String.Equals(snapshot.Stamp.FinalPath,NativeFiles.Normalize(keeper.Path),StringComparison.OrdinalIgnoreCase))throw new IOException("重复组保留项不再是有效的扫描记录。");
   return snapshot.Stamp;
  }
 }
}
