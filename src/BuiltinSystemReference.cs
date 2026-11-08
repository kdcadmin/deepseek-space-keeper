using System;
using System.Collections.Generic;

namespace ClearDisk {
 // Declarative path facts extracted from the supplied executable's System/Windows constants.
 // This is an independent metadata-only inventory, not an imported cleanup implementation.
 internal static class BuiltinSystemReference {
  internal const string TemporaryName="System / tmp（内置静态对照）",LogsName="System / logs（内置静态对照）",UpdatesName="System / updates（内置静态对照）",PrefetchName="System / prefetch（内置静态对照）";
  static CommunityEntry Entry(string name) {
   var entry=new CommunityEntry{Name=name,Warning="原版内置System静态路径对照，只读。仅还原已确认的文件路径，不执行服务、注册表或删除动作；未证明与原版逐文件运行结果完全一致。"};
   entry.Directives["Section"]=new List<string>{"System"};return entry;
  }
  static void Mask(CommunityEntry entry,string pattern) {int split=pattern.LastIndexOf('\\');if(split<0)throw new ArgumentException("File mask needs an explicit directory.");entry.FileKeys.Add(pattern.Substring(0,split)+"|"+pattern.Substring(split+1));}
  internal static List<CommunityEntry> Create() {
   var temp=Entry(TemporaryName);temp.FileKeys.Add(@"%Temp%|*|RECURSE");temp.FileKeys.Add(@"%WinDir%\Temp|*|RECURSE");
   var logs=Entry(LogsName);
   string[] logMasks={
    @"%ProgramData%\Application Data\Microsoft\Dr Watson\*.log",@"%ProgramData%\Application Data\Microsoft\Dr Watson\user.dmp",
    @"%LocalAppData%\Microsoft\Windows\WER\ReportArchive\*\*",@"%LocalAppData%\Microsoft\Windows\WER\ReportQueue\*\*",
    @"%ProgramData%\Microsoft\Windows\WER\ReportArchive\*\*",@"%ProgramData%\Microsoft\Windows\WER\ReportQueue\*\*",
    @"%LocalAppData%\Microsoft\Internet Explorer\brndlog.bak",@"%LocalAppData%\Microsoft\Internet Explorer\brndlog.txt",
    @"%WinDir%\*.log",@"%WinDir%\imsins.BAK",@"%WinDir%\OEWABLog.txt",@"%WinDir%\SchedLgU.txt",@"%WinDir%\ntbtlog.txt",@"%WinDir%\setuplog.txt",@"%WinDir%\REGLOCS.OLD",
    @"%WinDir%\Debug\*.log",@"%WinDir%\Debug\Setup\UpdSh.log",@"%WinDir%\Debug\UserMode\*.log",@"%WinDir%\Debug\UserMode\ChkAcc.bak",@"%WinDir%\Debug\UserMode\userenv.bak",
    @"%WinDir%\Microsoft.NET\Framework\*\*.log",@"%WinDir%\pchealth\helpctr\Logs\hcupdate.log",@"%WinDir%\security\logs\*.log",@"%WinDir%\security\logs\*.old",
    @"%WinDir%\SoftwareDistribution\*.log",@"%WinDir%\SoftwareDistribution\DataStore\Logs\*",@"%WinDir%\system32\TZLog.log",
    @"%WinDir%\system32\config\systemprofile\Application Data\Microsoft\Internet Explorer\brndlog.bak",@"%WinDir%\system32\config\systemprofile\Application Data\Microsoft\Internet Explorer\brndlog.txt",
    @"%WinDir%\system32\LogFiles\AIT\AitEventLog.etl.???",@"%WinDir%\system32\LogFiles\Firewall\pfirewall.log*",@"%WinDir%\system32\LogFiles\Scm\SCM.EVM*",@"%WinDir%\system32\LogFiles\WMI\Terminal*.etl",@"%WinDir%\system32\LogFiles\WMI\RTBackup\EtwRT.*etl",@"%WinDir%\system32\wbem\Logs\*.lo_",@"%WinDir%\system32\wbem\Logs\*.log"
   };
   foreach(string pattern in logMasks)Mask(logs,pattern);
   var updates=Entry(UpdatesName);
   foreach(string directory in new[]{@"%WinDir%\$NtUninstallKB*",@"%WinDir%\SoftwareDistribution",@"%WinDir%\SoftwareDistribution.old",@"%WinDir%\SoftwareDistribution.bak",@"%WinDir%\ie7updates",@"%WinDir%\ie8updates",@"%SystemDrive%\windows.old",@"%SystemDrive%\$windows.~bt",@"%SystemDrive%\$windows.~ws"})updates.FileKeys.Add(directory+"|*|RECURSE");
   var prefetch=Entry(PrefetchName);Mask(prefetch,@"%WinDir%\Prefetch\*.pf");
   return new List<CommunityEntry>{temp,logs,updates,prefetch};
  }
 }
}
