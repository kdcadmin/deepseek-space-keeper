using System;
using System.Collections.Generic;
using System.IO;

namespace ClearDisk {
    public static class Rules {
        public static List<CacheRule> Discover(string profile) {
            var rules = new List<CacheRule>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string local = Path.Combine(profile, "AppData", "Local");
            string roaming = Path.Combine(profile, "AppData", "Roaming");
            foreach(string relative in CuratedRoots.ProfileCaches)
                Add(rules,roots,"nvidia-user-"+relative,"NVIDIA 用户编译 / 着色器缓存",Path.Combine(profile,relative),RiskLevel.Safe,0,"用户目录中的可重建图形或编译缓存。清理后首次编译或启动可能变慢，使用中的文件会跳过。",null);
            Add(rules, roots, "temp", "用户临时文件", Path.Combine(local, "Temp"), RiskLevel.Safe, 7,
                "仅清理根目录或已知临时子目录中超过 7 天的 .tmp/.temp 文件，使用中的文件会跳过。", new[] { ".tmp", ".temp" });
            Add(rules, roots, "crashdumps", "程序崩溃转储", Path.Combine(local, "CrashDumps"), RiskLevel.Safe, 0, "程序崩溃转储，可清理后重新生成。", new[] { ".dmp" });
            Add(rules, roots, "npm", "npm 包下载缓存", Path.Combine(local, "npm-cache", "_cacache"), RiskLevel.Safe, 0, "仅处理下载缓存，不包含 npx 安装环境。", null);
            Add(rules, roots, "npm-roaming", "npm 包下载缓存", Path.Combine(roaming, "npm-cache", "_cacache"), RiskLevel.Safe, 0, "仅处理下载缓存，不包含 npx 安装环境。", null);
            Add(rules, roots, "pip", "pip 下载缓存", Path.Combine(local, "pip", "Cache"), RiskLevel.Safe, 0, "仅处理包下载缓存，不包含已安装环境。", null);
            foreach(string browser in CuratedRoots.Chromium) Browser(rules,roots,Path.Combine(local,browser),browser);
            foreach(string app in CuratedRoots.Apps) foreach(string leaf in new[]{"Cache","Code Cache","GPUCache","CachedData"})
                Add(rules,roots,"app-"+app+"-"+leaf,app+" "+leaf,Path.Combine(roaming,app,leaf),RiskLevel.Safe,0,"仅清理应用渲染缓存，不包含登录、站点或业务数据。",null);
            foreach(string browser in CuratedRoots.Opera) foreach(string basis in new[]{local,roaming}) foreach(string leaf in CuratedRoots.Render)
                Add(rules,roots,"opera-"+browser+"-"+leaf,browser+" "+leaf,Path.Combine(basis,browser,leaf),RiskLevel.Safe,0,"仅处理浏览器渲染缓存。",null);
            foreach(string browser in CuratedRoots.Opera) foreach(string basis in new[]{local,roaming}) Browser(rules,roots,Path.Combine(basis,browser),browser);
            string firefox=Path.Combine(local,"Mozilla","Firefox","Profiles");
            try {
                using(var pins=new NativeFiles.ParentLocks(Path.Combine(firefox,".discover")))
                foreach(string dir in Directory.EnumerateDirectories(firefox)) if(CuratedRoots.FirefoxProfile(Path.GetFileName(dir)) && (File.GetAttributes(dir)&FileAttributes.ReparsePoint)==0)
                    foreach(string leaf in new[]{"cache2","startupCache","shader-cache"}) Add(rules,roots,"firefox-"+Path.GetFileName(dir)+"-"+leaf,"Firefox "+leaf,Path.Combine(dir,leaf),RiskLevel.Safe,0,"仅处理 Firefox 可重建渲染缓存。",null);
            } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; }
            foreach(string relative in CuratedRoots.Local) if(relative!="Temp" && relative!="CrashDumps" && relative!=@"npm-cache\_cacache" && relative!=@"pip\Cache")
                Add(rules,roots,"curated-"+relative,"用户缓存 / "+relative,Path.Combine(local,relative),RiskLevel.Safe,0,"编译期确认的用户缓存或错误报告，可由应用重新生成。",null);
            Review(rules, roots, "uv", "uv 缓存与环境", Path.Combine(local, "uv", "cache"));
            Review(rules, roots, "pnpm", "pnpm 共享包仓库", Path.Combine(local, "pnpm", "store"));
            Review(rules, roots, "pnpm-v3", "pnpm 共享包仓库", Path.Combine(local, "pnpm-cache"));
            Review(rules, roots, "gradle", "Gradle 缓存", Path.Combine(profile, ".gradle", "caches"));
            Review(rules, roots, "conda", "Conda 包与环境", Path.Combine(profile, ".conda"));
            Review(rules, roots, "huggingface", "Hugging Face 模型", Path.Combine(profile, ".cache", "huggingface"));
            Review(rules, roots, "torch", "PyTorch 模型", Path.Combine(profile, ".cache", "torch"));
            Review(rules, roots, "ollama", "Ollama 模型", Path.Combine(profile, ".ollama", "models"));
            return rules;
        }
        public static List<CacheRule> AiCaches(string profile) {
            var rules=new List<CacheRule>();
            var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string relative in CuratedRoots.AiCacheRelative) {
                string kind=relative.IndexOf(".codex",StringComparison.OrdinalIgnoreCase)>=0?"Codex":(relative.IndexOf("deepseek",StringComparison.OrdinalIgnoreCase)>=0?"DeepSeek Harness":"Cursor");
                Add(rules,roots,"ai-"+relative.Replace('\\','-'),kind+" 缓存",Path.Combine(profile,relative),RiskLevel.Safe,0,"只清理 "+kind+" 可重新生成的缓存、日志或临时文件。对话、登录、设置和模型不在范围内。",null);
            }
            return rules;
        }
        static void Review(List<CacheRule> list, HashSet<string> roots, string id, string name, string path) {
            Add(list, roots, id, name, path, RiskLevel.Review, 0, "仅查看占用：可能包含正在使用的模型、环境或共享包。请使用对应软件的管理工具处理，本工具不删除运行数据。", null);
        }
        static void Browser(List<CacheRule> list, HashSet<string> roots, string path, string id) {
            var profiles = new List<string> { Path.Combine(path, "Default") };
            try {
                using(var pins=new NativeFiles.ParentLocks(Path.Combine(path,".discover"))) {
                    if (Directory.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Offline)) == 0)
                        foreach (string dir in Directory.GetDirectories(path, "Profile *"))
                            if (CuratedRoots.BrowserProfile(Path.GetFileName(dir)) && (File.GetAttributes(dir) & (FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Offline)) == 0) profiles.Add(dir);
                }
            } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; }
            foreach (string p in profiles) foreach (string leaf in new[] { "Cache", "Code Cache", "GPUCache" })
                Add(list, roots, id + "-" + Path.GetFileName(p) + "-" + leaf, id + " " + Path.GetFileName(p) + " " + leaf,
                    Path.Combine(p, leaf), RiskLevel.Safe, 0, "仅处理浏览器渲染缓存，不包含密码、历史记录、扩展与站点存储。", null);
        }
        static void Add(List<CacheRule> list, HashSet<string> roots, string id, string name, string path, RiskLevel risk, int age, string reason, string[] extensions) {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (roots.Add(full)) list.Add(new CacheRule { Id = id, Name = name, Root = full, Risk = risk, MinAgeDays = age, Reason = reason, Extensions = extensions });
        }
        internal static bool Matches(CacheRule rule, string path, DateTime lastWriteUtc) {
            if (rule.Risk != RiskLevel.Safe || lastWriteUtc > DateTime.UtcNow.AddDays(-Math.Max(0, rule.MinAgeDays))) return false;
            if (rule.Extensions != null && rule.Extensions.Length > 0) {
                bool match = false;
                foreach (string ext in rule.Extensions) if (String.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase)) match = true;
                if (!match) return false;
            }
            if (!String.Equals(rule.Id, "temp", StringComparison.OrdinalIgnoreCase)) return true;
            if (lastWriteUtc > DateTime.UtcNow.AddDays(-7)) return false;
            string relative = path.Substring(rule.Root.TrimEnd('\\').Length).TrimStart('\\');
            string extn = Path.GetExtension(path);
            if (!String.Equals(extn, ".tmp", StringComparison.OrdinalIgnoreCase) && !String.Equals(extn, ".temp", StringComparison.OrdinalIgnoreCase)) return false;
            if (relative.IndexOf('\\') < 0)
                return String.Equals(extn, ".tmp", StringComparison.OrdinalIgnoreCase) || String.Equals(extn, ".temp", StringComparison.OrdinalIgnoreCase);
            string first = relative.Split('\\')[0];
            // Never recursively clean arbitrary Temp folders; only disposable Chromium scratch directories.
            return first.StartsWith("scoped_dir", StringComparison.OrdinalIgnoreCase) || first.StartsWith("chrome_url_fetcher_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
