using System;
namespace ClearDisk {
 internal static class CleanupCapacity {
  internal static string Gigabytes(long bytes){return (bytes/1073741824d).ToString("0.00")+" GB";}
  internal static string Describe(long bytes){return Gigabytes(bytes)+(Math.Abs((double)bytes)<1073741824d?"（"+Display.Size(bytes)+"）":"");}
  internal static string Files(long bytes,long count){return Describe(bytes)+" · "+count.ToString("N0")+" 个文件";}
 }
}
