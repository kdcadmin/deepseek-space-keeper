using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
namespace ClearDisk {
 // Independent, compiled allowlist. Community definitions never contribute to this list.
 internal static class CuratedRoots {
  internal static readonly string[] Chromium={@"Google\Chrome\User Data",@"Google\Chrome Beta\User Data",@"Google\Chrome SxS\User Data",@"Microsoft\Edge\User Data",@"Microsoft\Edge Beta\User Data",@"Microsoft\Edge Dev\User Data",@"BraveSoftware\Brave-Browser\User Data",@"Chromium\User Data",@"Vivaldi\User Data",@"Tencent\QQBrowser\User Data",@"Quark\User Data",@"Quark\Browser\User Data",@"The Quark Authors\Quark\User Data"};
  internal static readonly string[] Apps={"Code","Cursor","discord","Slack","Teams","Notion","Typora","Obsidian","Antigravity","QwenWorkCN","TRAE SOLO","Comfy Desktop","Grok Bot","QuarkCloudDrive","Claude","ChatGPT","Windsurf","Zed","Figma","Postman","Insomnia","GitHub Desktop","Microsoft Teams"};
  internal static readonly string[] Render={"Cache","Code Cache","GPUCache"};
  internal static readonly string[] ProfileCaches={@"AppData\Roaming\NVIDIA\ComputeCache",@"AppData\LocalLow\NVIDIA\DXCache",@"AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache"};
  internal static readonly string[] Local={"Temp","CrashDumps",@"npm-cache\_cacache",@"pip\Cache","D3DSCache",@"NVIDIA\DXCache",@"NVIDIA\GLCache",@"AMD\DxCache",@"AMD\DxcCache",@"AMD\GLCache",@"Intel\ShaderCache","cursor-compile-cache",@"Microsoft\Windows\WER\ReportArchive",@"Microsoft\Windows\WER\ReportQueue"};
  internal static readonly string[] Opera={@"Opera Software\Opera Stable",@"Opera Software\Opera GX Stable",@"Opera Software\Opera Beta",@"Opera Software\Opera Developer"};
  internal static bool BrowserProfile(string name) { return name=="Default" || Regex.IsMatch(name,@"^Profile [0-9]+$"); }
  internal static bool FirefoxProfile(string name) { return Regex.IsMatch(name,@"^[a-zA-Z0-9]{8}\.[a-zA-Z0-9_-]+$"); }
  // Regenerable caches only. Sessions, login, settings and model files stay outside this list.
  internal static readonly string[] AiCacheRelative={
   @".codex\cache",@".codex\tmp",@".codex\.tmp",
   @".cursor\browser-logs",
   @"AppData\Roaming\Cursor\Cache",@"AppData\Roaming\Cursor\Code Cache",@"AppData\Roaming\Cursor\GPUCache",@"AppData\Roaming\Cursor\CachedData",@"AppData\Roaming\Cursor\CachedExtensionVSIXs",@"AppData\Roaming\Cursor\logs",@"AppData\Roaming\Cursor\DawnGraphiteCache",@"AppData\Roaming\Cursor\DawnWebGPUCache",
   @"AppData\Local\cursor-compile-cache",
   @"AppData\Roaming\@deepseek-ai\dsh-desktop\Cache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\Code Cache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\GPUCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\DawnGraphiteCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\DawnWebGPUCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\GPUPersistentCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\GrShaderCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\ShaderCache",@"AppData\Roaming\@deepseek-ai\dsh-desktop\logs"};
  internal static bool IsAiCache(string profile,string path) {
   if(String.IsNullOrEmpty(profile)||String.IsNullOrEmpty(path)) return false;
   foreach(string relative in AiCacheRelative) if(SafetyPolicy.IsWithin(path,Path.Combine(profile,relative))) return true;
   return false;
  }
  internal static bool IsTrusted(string profile,string root) {
   foreach(string relative in AiCacheRelative) if(Equal(root,Path.Combine(profile,relative))) return true;
   string local=Path.Combine(profile,"AppData","Local"), roaming=Path.Combine(profile,"AppData","Roaming");
   foreach(string relative in ProfileCaches)if(Equal(root,Path.Combine(profile,relative)))return true;
   foreach(string relative in Local) if(Equal(root,Path.Combine(local,relative))) return true;
   if(Equal(root,Path.Combine(roaming,"npm-cache","_cacache"))) return true;
   foreach(string app in Apps) foreach(string leaf in new[]{"Cache","Code Cache","GPUCache","CachedData"}) if(Equal(root,Path.Combine(roaming,app,leaf))) return true;
   foreach(string browser in Opera) foreach(string leaf in Render) if(Equal(root,Path.Combine(roaming,browser,leaf)) || Equal(root,Path.Combine(local,browser,leaf))) return true;
   string parent=Path.GetDirectoryName(root), leafName=Path.GetFileName(root);
   if(parent==null) return false;
   if(Array.IndexOf(Render,leafName)>=0 && BrowserProfile(Path.GetFileName(parent))) foreach(string browser in Chromium) if(Equal(Path.GetDirectoryName(parent),Path.Combine(local,browser))) return true;
      if(Array.IndexOf(Render,leafName)>=0 && BrowserProfile(Path.GetFileName(parent))) foreach(string browser in Opera)
    if(Equal(Path.GetDirectoryName(parent),Path.Combine(local,browser)) || Equal(Path.GetDirectoryName(parent),Path.Combine(roaming,browser))) return true;
   if((leafName=="cache2" || leafName=="startupCache" || leafName=="shader-cache") && FirefoxProfile(Path.GetFileName(parent)))
    if(Equal(Path.GetDirectoryName(parent),Path.Combine(local,"Mozilla","Firefox","Profiles"))) return true;
   return false;
  }
  static bool Equal(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
 }
}
