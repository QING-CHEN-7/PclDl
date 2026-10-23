using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PclDl.Core.Logging;

namespace PclDl.Core.Layout
{
    public static class NativeExtractor
    {
        /// <summary>把 natives jar 解压到目标目录，跳过 exclude 前缀。</summary>
        public static int Extract(string jarPath, string targetDir, IList<string> excludes, ILog log)
        {
            if (!File.Exists(jarPath)) return 0;
            Directory.CreateDirectory(targetDir);
            var count = 0;

            using (var zip = ZipFile.OpenRead(jarPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项
                    if (ShouldExclude(entry.FullName, excludes)) continue;

                    var destination = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, true);
                    count++;
                }
            }

            if (log != null) log.Debug("解压 natives：" + jarPath + " -> " + count + " 个文件");
            return count;
        }

        private static bool ShouldExclude(string entryName, IList<string> excludes)
        {
            if (excludes == null) return false;
            foreach (var ex in excludes)
            {
                if (string.IsNullOrEmpty(ex)) continue;
                if (entryName.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
