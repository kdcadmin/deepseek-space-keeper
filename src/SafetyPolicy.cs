using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

namespace ClearDisk {
    public sealed partial class SafetyPolicy {
        readonly string profile, self;
        readonly List<string> protectedRoots=new List<string>();
        readonly Dictionary<string,CacheRule> authorized=new Dictionary<string,CacheRule>(StringComparer.OrdinalIgnoreCase);
        sealed class Snapshot { internal FileStamp Stamp; internal string Root; }
        readonly ConditionalWeakTable<FileStamp,Snapshot> snapshots=new ConditionalWeakTable<FileStamp,Snapshot>();
        public string ProfileRoot { get { return profile; } }
        static readonly string[] RuntimeNames={".git",".svn","node_modules",".venv","venv",".conda",".ollama",".modelscope","huggingface","codex-runtimes","site-packages","envs",".hermes",".codex",".agents",".android",".docker","wsl","models","model"};
        public SafetyPolicy(string userProfile,string selfDirectory) {
            profile=NativeFiles.Normalize(userProfile); self=NativeFiles.Normalize(selfDirectory);
            protectedRoots.Add(Path.Combine(profile,"AppData","Local","CDriveCleaner","Quarantine"));
            foreach(string path in new[]{Environment.GetFolderPath(Environment.SpecialFolder.Windows),"C:\\Windows","C:\\Program Files","C:\\Program Files (x86)","C:\\ProgramData","C:\\Recovery","C:\\System Volume Information","C:\\$Recycle.Bin","C:\\$WINDOWS.~BT","C:\\$Windows.~WS",self})
                if (!String.IsNullOrEmpty(path)) protectedRoots.Add(NativeFiles.Normalize(path));
        }
        public static SafetyPolicy ForCurrentUser() { return new SafetyPolicy(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),AppDomain.CurrentDomain.BaseDirectory); }
        public static bool IsWithin(string path,string root) {
            try {
                string p=NativeFiles.Normalize(path),r=NativeFiles.Normalize(root);
                return p.Equals(r,StringComparison.OrdinalIgnoreCase) || p.StartsWith(r.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }
        public bool IsProtected(string path) {
            try {
                string p=NativeFiles.Normalize(path);
                if(p.Length<=3 || String.Equals(Path.GetDirectoryName(p),"C:\\",StringComparison.OrdinalIgnoreCase) && !Directory.Exists(p)) return true;
                foreach(string root in protectedRoots) if(IsWithin(p,root)) return true;
                if(IsWithin(p,"C:\\Users") && !IsWithin(p,profile) && !p.Equals("C:\\Users",StringComparison.OrdinalIgnoreCase)) return true;
                if(CuratedRoots.IsAiCache(profile,p) && !IsModelWeight(p)) return false;
                if(IsPersonalOrRuntime(p)) return true;
                return false;
            } catch { return true; }
        }
        internal bool IsProtectedScanEntry(string path,bool directory,bool inspection) {
            try {
                string p=NativeFiles.Normalize(path);
                foreach(string root in protectedRoots) if(IsWithin(p,root)) return true;
                if(IsWithin(p,"C:\\Users") && !IsWithin(p,profile) && !p.Equals("C:\\Users",StringComparison.OrdinalIgnoreCase)) return true;
                if(!directory && String.Equals(Path.GetDirectoryName(p),"C:\\",StringComparison.OrdinalIgnoreCase)) return true;
                if(IsMigrationArtifactPath(p)) return true;
                if(CuratedRoots.IsAiCache(profile,p) && !IsModelWeight(p)) return false;
                if(inspection) return !IsWithin(p,profile);
                if(HasRuntimeName(p)) return true;
                return directory && HasProjectMarker(p);
            } catch { return true; }
        }
        static bool IsModelWeight(string full) {
            string ext=Path.GetExtension(full).ToLowerInvariant(),nameOnly=Path.GetFileName(full).ToLowerInvariant();
            if(Array.IndexOf(new[]{".gguf",".ggml",".safetensors",".ckpt",".pt",".pth",".onnx",".tflite",".h5"},ext)>=0) return true;
            return ext==".bin" && (nameOnly.Contains("model") || nameOnly.Contains("weight"));
        }
        static bool HasRuntimeName(string full) {
            if(IsMigrationArtifactPath(full)) return true;
            if(IsModelWeight(full)) return true;
            foreach(string part in full.Split('\\')) foreach(string name in RuntimeNames) if(part.Equals(name,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static bool IsMigrationArtifactPath(string full) {
            foreach(string part in full.Split('\\')) if(part.StartsWith(".cdm-",StringComparison.OrdinalIgnoreCase) || part.Equals(".CDriveCleanerMigrationV1",StringComparison.OrdinalIgnoreCase) || part.Equals("CDriveCleanerMigrationV1",StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static bool HasProjectMarker(string dir) {
            if(Directory.Exists(Path.Combine(dir,".git")) || File.Exists(Path.Combine(dir,".git")) || File.Exists(Path.Combine(dir,"pyvenv.cfg")) || File.Exists(Path.Combine(dir,"package.json")) || File.Exists(Path.Combine(dir,"pyproject.toml")) || File.Exists(Path.Combine(dir,"Cargo.toml"))) return true;
            if(Directory.Exists(Path.Combine(dir,"conda-meta")) || File.Exists(Path.Combine(dir,"project.godot")) || File.Exists(Path.Combine(dir,"go.mod")) || File.Exists(Path.Combine(dir,"pom.xml")) || File.Exists(Path.Combine(dir,"build.gradle")) || File.Exists(Path.Combine(dir,"CMakeLists.txt"))) return true;
            foreach(string pattern in new[]{"*.csproj","*.sln","*.fsproj","*.vbproj"}) if(Directory.GetFiles(dir,pattern,SearchOption.TopDirectoryOnly).Length>0) return true;
            return false;
        }
        public bool IsPersonalOrRuntime(string path) {
            try {
                string full=NativeFiles.Normalize(path);
                if(CuratedRoots.IsAiCache(profile,full) && !IsModelWeight(full)) return false;
                if(HasRuntimeName(full)) return true;
                // Do not walk into project roots or installed portable applications.
                string dir=Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                while(dir!=null && IsWithin(dir,profile)) {
                    if(HasProjectMarker(dir)) return true;
                    if(dir.Equals(profile,StringComparison.OrdinalIgnoreCase)) break;
                    dir=Path.GetDirectoryName(dir);
                }
                return false;
            } catch { return true; }
        }
        public bool ValidateRoot(string path) {
            try {
                string p=NativeFiles.Normalize(path);
                if(IsProtected(p)) return false;
                if(p.Length==3) return false;
                using(var parents=new NativeFiles.ParentLocks(Path.Combine(p,".boundary"))) {
                    return Directory.Exists(p);
                }
            } catch { return false; }
        }
        public bool ValidateInspectionRoot(string path) {
            try {
                string p=NativeFiles.Normalize(path);
                if(IsMigrationArtifactPath(p)) return false;
                if(!IsWithin(p,profile)) return false;
                foreach(string root in protectedRoots) if(IsWithin(p,root)) return false;
                using(var parents=new NativeFiles.ParentLocks(Path.Combine(p,".inspection"))) { return Directory.Exists(p); }
            } catch { return false; }
        }
        public FileStamp Capture(string path,string root) {
            string p=NativeFiles.Normalize(path);
            if(!IsWithin(p,root) || p.Equals(NativeFiles.Normalize(root),StringComparison.OrdinalIgnoreCase) || IsProtected(p)) throw new IOException("路径不在允许的文件范围内。");
            using(var parents=new NativeFiles.ParentLocks(p))
            using(var handle=NativeFiles.Open(p,false,false)) {
                var info=NativeFiles.Info(handle);
                if((info.Attributes & (0x400u|0x4u|0x10u|0x1000u|0x40000u))!=0) throw new IOException("跳过系统、目录、链接或云端占位文件。");
                FileStamp stamp=NativeFiles.Stamp(handle);
                if(!stamp.FinalPath.Equals(p,StringComparison.OrdinalIgnoreCase)) throw new IOException("最终路径与扫描路径不同。");
                snapshots.Add(stamp,new Snapshot {Root=NativeFiles.Normalize(root),Stamp=new FileStamp {Volume=stamp.Volume,FileId=stamp.FileId,Length=stamp.Length,LastWriteTicks=stamp.LastWriteTicks,Links=stamp.Links,FinalPath=stamp.FinalPath}});
                return stamp;
            }
        }
        internal bool IsTrustedCacheRule(CacheRule rule) {
            if(rule==null || rule.Risk!=RiskLevel.Safe || String.IsNullOrEmpty(rule.Id) || rule.MinAgeDays<0) return false;
            try { return CuratedRoots.IsTrusted(profile,NativeFiles.Normalize(rule.Root)); }
            catch { return false; }
        }
        internal bool AuthorizeRule(CacheRule rule) {
            if(!IsTrustedCacheRule(rule) || IsProtected(rule.Root)) return false;
            string key=rule.Id+"\0"+NativeFiles.Normalize(rule.Root);
            authorized[key]=new CacheRule {Id=rule.Id,Root=NativeFiles.Normalize(rule.Root),Risk=rule.Risk,MinAgeDays=rule.MinAgeDays,Extensions=rule.Extensions==null ? null : (string[])rule.Extensions.Clone()};
            return true;
        }
        static bool Same(FileStamp a,FileStamp b) {
            return a!=null && b!=null && a.Volume==b.Volume && a.FileId==b.FileId && a.Length==b.Length && a.LastWriteTicks==b.LastWriteTicks && a.Links==1 && b.Links==1 && String.Equals(a.FinalPath,b.FinalPath,StringComparison.OrdinalIgnoreCase);
        }
        public bool TryDelete(FileRecord file,out string reason) {
            bool failed; return TryDelete(file,out reason,out failed);
        }
        public bool TryDelete(FileRecord file,out string reason,out bool failed) {
            reason="";
            failed=false;
            try {
                if(file==null || !file.CanDelete || file.Risk!=RiskLevel.Safe || file.IsKeeper || file.Stamp==null) throw new IOException("不是允许直接删除的缓存文件。");
                FileStamp keyStamp=file.Stamp;
                string inputRoot=file.Root,inputPath=file.Path,inputRule=file.RuleId;
                Snapshot snapshot;
                if(!snapshots.TryGetValue(keyStamp,out snapshot) || !String.Equals(snapshot.Root,NativeFiles.Normalize(inputRoot),StringComparison.OrdinalIgnoreCase) || !Same(snapshot.Stamp,keyStamp)) throw new IOException("文件快照不是本次扫描签发的有效记录。");
                FileStamp baseline=snapshot.Stamp;
                string p=NativeFiles.Normalize(inputPath);
                CacheRule rule;
                if(!authorized.TryGetValue(inputRule+"\0"+NativeFiles.Normalize(inputRoot),out rule)) throw new IOException("没有可信缓存规则授权，禁止删除。");
                if(new DateTime(baseline.LastWriteTicks,DateTimeKind.Utc)>DateTime.UtcNow.AddDays(-rule.MinAgeDays)) throw new IOException("文件仍在保留期内。");
                if(rule.Extensions!=null && Array.FindIndex(rule.Extensions,x=>x.Equals(Path.GetExtension(p),StringComparison.OrdinalIgnoreCase))<0) throw new IOException("文件扩展名不符合清理规则。");
                if(!IsWithin(p,inputRoot) || IsProtected(p) || !String.Equals(baseline.FinalPath,p,StringComparison.OrdinalIgnoreCase)) throw new IOException("路径受到保护或已变化。");
                using(var parents=new NativeFiles.ParentLocks(p))
                using(var handle=NativeFiles.Open(p,true,false)) {
                    var info=NativeFiles.Info(handle);
                    if((info.Attributes & (0x400u|0x4u|0x10u|0x1000u|0x40000u|0x1u))!=0) throw new IOException("系统、只读、链接或占位文件不清理。");
                    if(!Same(baseline,NativeFiles.Stamp(handle))) throw new IOException("文件身份、大小、时间或硬链接已变化。");
                    NativeFiles.Delete(handle);
                }
                return true;
            } catch(Exception ex) {
                var native=ex as System.ComponentModel.Win32Exception;
                failed=native!=null ? Array.IndexOf(new[]{2,3,5,32,33,1224},native.NativeErrorCode)<0 : !(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException);
                reason=ex.Message; return false;
            }
        }
        public bool TryRecycle(FileRecord file,out string reason) {
            // A path-only shell operation would reopen the path after verification.
            // This release never silently downgrades a protected action to that API.
            reason="当前版本未启用可原子核验的回收操作。请打开文件位置，在资源管理器中处理个人文件。";
            return false;
        }
    }
}
