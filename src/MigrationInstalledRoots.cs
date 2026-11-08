using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace ClearDisk {
    // A bounded, read-only policy observation. This never grants execution authority.
    internal sealed class MigrationInstalledRoots {
        internal const int MaximumRecords=20000;
        const string UninstallPath=@"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        readonly string[] roots;
        MigrationInstalledRoots(string[] roots) {this.roots=roots;}
        internal static IEnumerable<string> EmptyFixtureInventory(CancellationToken token,DateTime deadline) {return new string[0];}
        internal static MigrationInstalledRoots Capture(Func<CancellationToken,DateTime,IEnumerable<string>> inventory,CancellationToken token,DateTime deadline) {
            if(inventory==null) throw new IOException("Missing installation-root inventory; migration remains disabled.");
            DateTime inventoryDeadline=DateTime.UtcNow.AddSeconds(30); if(deadline<inventoryDeadline) inventoryDeadline=deadline;
            var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase); int inspected=0;
            try {
                Check(token,inventoryDeadline);
                IEnumerable<string> supplied=inventory(token,inventoryDeadline);
                if(supplied==null) throw new IOException("Installation-root inventory was unresolved; migration remains disabled.");
                foreach(string root in supplied) {
                    Check(token,inventoryDeadline);
                    if(++inspected>MaximumRecords) throw new IOException("Installation-root inventory record budget exhausted.");
                    result.Add(Resolve(root));
                }
                Check(token,inventoryDeadline);
                var captured=new string[result.Count]; result.CopyTo(captured); return new MigrationInstalledRoots(captured);
            } catch(UnauthorizedAccessException ex) {throw new IOException("Cannot read the complete installation-root inventory; migration remains disabled.",ex);}
            catch(SecurityException ex) {throw new IOException("Cannot read the complete installation-root inventory; migration remains disabled.",ex);}
        }
        internal void ValidatePath(string path) {
            string normalized=MigrationNativeFiles.Normalize(path);
            foreach(string root in roots) if(MigrationPolicy.Within(normalized,root) || MigrationPolicy.Within(root,normalized)) throw new IOException("Migration endpoint overlaps a registered installation directory.");
        }
        static string Resolve(string supplied) {
            if(String.IsNullOrWhiteSpace(supplied)) throw new IOException("A supplied installation root was unresolved; migration remains disabled.");
            string value=supplied.Trim();
            if(value.Length>=2 && value[0]=='"' && value[value.Length-1]=='"') value=value.Substring(1,value.Length-2);
            value=Environment.ExpandEnvironmentVariables(value);
            if(value.IndexOf('%')>=0) throw new IOException("An installation-root environment variable was unresolved; migration remains disabled.");
            // InstallLocation accepts either Windows separator; migration endpoints remain strictly backslash-only.
            value=value.Replace('/','\\');
            string root=MigrationNativeFiles.Normalize(value);
            if(root.Length<=3) throw new IOException("An installation root is an ambiguous whole drive; migration remains disabled.");
            return root;
        }
        static void Check(CancellationToken token,DateTime deadline) {
            token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Installation-root inventory work budget exhausted.");
        }
        // Independent of the display/measurement inventory: hidden and nameless registrations still protect paths.
        internal static IEnumerable<string> ReadRegistry(CancellationToken token,DateTime deadline) {
            var result=new List<string>(); int inspected=0;
            foreach(RegistryHive hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
            foreach(RegistryView view in Environment.Is64BitOperatingSystem?new[]{RegistryView.Registry32,RegistryView.Registry64}:new[]{RegistryView.Registry32}) {
                Check(token,deadline);
                using(var root=RegistryKey.OpenBaseKey(hive,view))
                using(var uninstall=root.OpenSubKey(UninstallPath,false)) {
                    // A scope with no Uninstall key has no supplied registrations.
                    if(uninstall==null) continue;
                    Check(token,deadline);
                    if(uninstall.SubKeyCount>MaximumRecords-inspected) throw new IOException("Installation-root registry record budget exhausted.");
                    string[] names=uninstall.GetSubKeyNames();
                    if(names.Length>MaximumRecords-inspected) throw new IOException("Installation-root registry record budget exhausted.");
                    foreach(string name in names) {
                        Check(token,deadline); if(++inspected>MaximumRecords) throw new IOException("Installation-root registry record budget exhausted.");
                        using(var registration=uninstall.OpenSubKey(name,false)) {
                            if(registration==null) throw new IOException("An installation registration disappeared during inspection; inspect again.");
                            object location=registration.GetValue("InstallLocation",null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if(location==null) continue;
                            string path=location as string;
                            if(path==null) throw new IOException("A registered installation location has an unsupported value type; migration remains disabled.");
                            // Empty/missing values provide no known root; they do not prove a program is portable.
                            if(String.IsNullOrWhiteSpace(path)) continue;
                            result.Add(path);
                        }
                    }
                }
            }
            Check(token,deadline); return result;
        }
    }
}
