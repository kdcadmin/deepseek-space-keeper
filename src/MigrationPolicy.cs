using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;

namespace ClearDisk {
    internal sealed class MigrationPolicy {
        readonly string profile,self;
        readonly Func<CancellationToken,DateTime,IEnumerable<string>> installedRootInventory;
        readonly List<string> wholeKnownRoots=new List<string>();
        readonly List<string> cloudRoots=new List<string>();
        static readonly string[] RuntimeNames={".git",".svn","node_modules",".venv","venv",".conda",".ollama",".modelscope","huggingface","codex-runtimes","site-packages","envs",".hermes",".codex",".agents",".android",".docker","wsl","models","model","conda-meta","runtime","runtimes","quarantine","logs"};
        static readonly string[] ProjectNames={".git",".svn","pyvenv.cfg","package.json","pyproject.toml","Cargo.toml","project.godot","go.mod","pom.xml","build.gradle","CMakeLists.txt","conda-meta"};
        internal string ProfileRoot {get{return profile;}}
        internal string SelfRoot {get{return self;}}
        [DllImport("shell32.dll",ExactSpelling=true)]
        static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
        static string CurrentDownloadsRoot() {
            Guid downloads=new Guid("374DE290-123F-4565-9164-39C4925E467B"); IntPtr path=IntPtr.Zero;
            try {
                // No CREATE or DEFAULT_PATH flag: resolve the actual current redirected folder without modifying it.
                int result=SHGetKnownFolderPath(ref downloads,0,IntPtr.Zero,out path);
                if(result!=0 || path==IntPtr.Zero) throw new IOException("Cannot resolve the current Downloads known-folder boundary.");
                return Marshal.PtrToStringUni(path);
            } finally {if(path!=IntPtr.Zero) Marshal.FreeCoTaskMem(path);}
        }
        // Explicit fixture roots are internal test infrastructure; production always uses ForCurrentUser().
        internal MigrationPolicy(string profileRoot,string selfDirectory) : this(profileRoot,selfDirectory,CurrentDownloadsRoot) {}
        // Tests inject only the resolution result; the real user's Known Folder configuration is never changed.
        internal MigrationPolicy(string profileRoot,string selfDirectory,Func<string> downloadsResolver) : this(profileRoot,selfDirectory,downloadsResolver,MigrationInstalledRoots.EmptyFixtureInventory) {}
        // Explicit fixture constructors do not inspect the host registry. Production opts in below.
        internal MigrationPolicy(string profileRoot,string selfDirectory,Func<string> downloadsResolver,Func<CancellationToken,DateTime,IEnumerable<string>> installationRootInventory) {
            if(installationRootInventory==null) throw new IOException("Missing installation-root inventory.");
            installedRootInventory=installationRootInventory;
            profile=MigrationNativeFiles.Normalize(profileRoot); self=MigrationNativeFiles.Normalize(selfDirectory);
            if(profile[0]!='C' || profile.Length<=3) throw new IOException("Migration source profile must be on C.");
            foreach(string name in new[]{"Desktop","Documents","Downloads","Pictures","Videos","Music"}) wholeKnownRoots.Add(Path.Combine(profile,name));
            if(downloadsResolver==null) throw new IOException("Missing Downloads known-folder resolver.");
            string downloads=downloadsResolver();
            if(String.IsNullOrEmpty(downloads)) throw new IOException("Downloads known-folder lookup was unresolved; migration remains disabled.");
            wholeKnownRoots.Add(MigrationNativeFiles.Normalize(downloads));
            foreach(Environment.SpecialFolder folder in new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyVideos,Environment.SpecialFolder.MyMusic}) {
                string known=Environment.GetFolderPath(folder); if(!String.IsNullOrEmpty(known)) wholeKnownRoots.Add(MigrationNativeFiles.Normalize(known));
            }
            foreach(string variable in new[]{"OneDrive","OneDriveConsumer","OneDriveCommercial"}) {
                string cloud=Environment.GetEnvironmentVariable(variable); if(!String.IsNullOrEmpty(cloud)) cloudRoots.Add(MigrationNativeFiles.Normalize(cloud));
            }
        }
        internal static MigrationPolicy ForCurrentUser() {return new MigrationPolicy(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),AppDomain.CurrentDomain.BaseDirectory,CurrentDownloadsRoot,MigrationInstalledRoots.ReadRegistry);}
        // Batch capture is deliberately fresh for every preflight/runtime validation, never part of the saved plan.
        internal void ValidateInstalledEndpoints(string source,string destination,CancellationToken token,DateTime deadline) {
            ValidateInstalledPaths(token,deadline,source,destination);
        }
        // Internal transaction artifacts use only this boundary, never public source-name eligibility.
        internal void ValidateInstalledPaths(CancellationToken token,DateTime deadline,params string[] paths) {
            MigrationInstalledRoots roots=MigrationInstalledRoots.Capture(installedRootInventory,token,deadline);
            foreach(string path in paths) roots.ValidatePath(path);
        }
        internal static bool Within(string path,string root) {return path.Equals(root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
        internal void ValidateSourcePath(string path) {
            string p=MigrationNativeFiles.Normalize(path);
            if(p[0]!='C' || !Within(p,profile) || p.Equals(profile,StringComparison.OrdinalIgnoreCase)) throw new IOException("Source must be an ordinary subtree of the current C profile.");
            foreach(string known in wholeKnownRoots) if(p.Equals(known,StringComparison.OrdinalIgnoreCase)) throw new IOException("Whole personal known folders cannot be migrated.");
            if(Within(p,Path.Combine(profile,"AppData")) || Within(p,self) || Within(self,p)) throw new IOException("Application and cleaner data are protected.");
            foreach(string cloud in cloudRoots) if(Within(p,cloud)) throw new IOException("Cloud data is protected.");
            ValidateNames(p);
        }
        internal void ValidateDestinationPath(string path) {
            string p=MigrationNativeFiles.Normalize(path); string parent=Path.GetDirectoryName(p);
            if(p.Length<=3 || parent==null || parent.Length<=3 || Within(p,self) || Within(self,p) || Within(p,profile)) throw new IOException("Destination must be a new directory under an ordinary target parent.");
            foreach(string cloud in cloudRoots) if(Within(p,cloud)) throw new IOException("Cloud destinations are protected.");
            if(p.Substring(3).Split('\\')[0].Equals("Users",StringComparison.OrdinalIgnoreCase) || p.Substring(3).Split('\\')[0].Equals("Documents and Settings",StringComparison.OrdinalIgnoreCase)) throw new IOException("Other profile trees cannot be migration destinations.");
            ValidateNames(p);
        }
        // Test volume simulation still uses ordinary native paths in its own profile fixture.
        internal void ValidateSimulatedDestinationPath(string path) {
            string p=MigrationNativeFiles.Normalize(path); if(p.Length<=3 || Path.GetDirectoryName(p).Length<=3 || Within(p,self) || Within(self,p)) throw new IOException("Unsafe simulated destination."); ValidateNames(p);
        }
        static void ValidateNames(string full) {
            string[] parts=full.Substring(3).Split('\\');
            foreach(string part in parts) {
                if(part.StartsWith(".cdm-",StringComparison.OrdinalIgnoreCase) || part.Equals(MigrationJournal.StorageLeafName,StringComparison.OrdinalIgnoreCase)) throw new IOException("Migration state, retained backups, staging and junction artifacts are protected.");
                foreach(string name in RuntimeNames) if(part.Equals(name,StringComparison.OrdinalIgnoreCase)) throw new IOException("Runtime, model, project or cleaner storage is protected.");
                if(part.StartsWith("OneDrive",StringComparison.OrdinalIgnoreCase) || part.Equals("Dropbox",StringComparison.OrdinalIgnoreCase) || part.Equals("Google Drive",StringComparison.OrdinalIgnoreCase) || part.Equals("iCloudDrive",StringComparison.OrdinalIgnoreCase)) throw new IOException("Cloud roots cannot be migrated.");
            }
            foreach(string name in new[]{"Windows","Program Files","Program Files (x86)","ProgramData","Recovery","System Volume Information","$Recycle.Bin","$WINDOWS.~BT","$Windows.~WS"})
                if(parts[0].Equals(name,StringComparison.OrdinalIgnoreCase)) throw new IOException("System destination or source is protected.");
        }
        internal void ValidateEntry(string path,bool directory) {
            ValidateSourcePath(path);
            if(directory) return;
            string name=Path.GetFileName(path),extension=Path.GetExtension(path).ToLowerInvariant();
            if(name.EndsWith("-wal",StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm",StringComparison.OrdinalIgnoreCase) || name.EndsWith("-journal",StringComparison.OrdinalIgnoreCase) ||
                Array.IndexOf(new[]{".db",".sqlite",".sqlite3",".db3",".mdb",".accdb",".gguf",".ggml",".safetensors",".ckpt",".pt",".pth",".onnx",".tflite",".h5"},extension)>=0 ||
                extension==".bin" && (name.IndexOf("model",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("weight",StringComparison.OrdinalIgnoreCase)>=0)) throw new IOException("Database, sidecar or model data requires an application-specific migration.");
        }
        internal void ValidateProjectAncestors(string source,CancellationToken token,DateTime deadline) {
            string current=MigrationNativeFiles.Normalize(source);
            while(Within(current,profile)) {
                using(var pins=new MigrationNativeFiles.ParentLocks(Path.Combine(current,".migration-marker-check"))) RequireNoProjectMarkers(current,token,deadline);
                if(current.Equals(profile,StringComparison.OrdinalIgnoreCase)) break; current=Path.GetDirectoryName(current);
            }
        }
        internal static void RequireNoProjectMarkers(string directory,CancellationToken token,DateTime deadline) {
            int entries=0;
            foreach(string path in Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Project marker work budget exhausted.");
                if(++entries>50000) throw new IOException("Project marker inspection budget exhausted.");
                string name=Path.GetFileName(path);
                foreach(string marker in ProjectNames) if(name.Equals(marker,StringComparison.OrdinalIgnoreCase)) throw new IOException("Project/environment marker rejects entire migration.");
                string ext=Path.GetExtension(name); foreach(string project in new[]{".csproj",".sln",".fsproj",".vbproj"}) if(ext.Equals(project,StringComparison.OrdinalIgnoreCase)) throw new IOException("Project source cannot be migrated.");
            }
        }
    }
}
