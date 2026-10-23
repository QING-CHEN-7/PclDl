using System;
using System.IO;

namespace PclDl.Core.Transport
{
    /// <summary>
    /// v4 §2 L4 的 Verifier：size → sha1 → 原子落盘。
    /// 下载完成后先用它判定，再决定是否落到目标路径，避免半成品进正式目录。
    /// </summary>
    public static class Verifier
    {
        public const string Ok = null;
        public const string Missing = "MISSING";
        public const string SizeMismatch = "SIZE";
        public const string Sha1Mismatch = "SHA1";

        /// <summary>返回 null 表示完好，否则返回 MISSING / SIZE / SHA1。</summary>
        public static string Check(string path, long size, string sha1)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Missing;
            if (size > 0 && new FileInfo(path).Length != size) return SizeMismatch;
            if (!string.IsNullOrEmpty(sha1))
            {
                var actual = Hashing.Sha1File(path);
                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase)) return Sha1Mismatch;
            }
            return Ok;
        }

        /// <summary>
        /// 同盘原子落盘：先删目标再 Move（Move 在同一卷上是原子重命名）。
        /// 目标被占用时重试几次，仍失败则抛出让上层记失败。
        /// </summary>
        public static void MoveIntoPlace(string tempPath, string target, int retries = 3)
        {
            if (string.IsNullOrEmpty(tempPath) || string.IsNullOrEmpty(target))
                throw new ArgumentException("tempPath/target 不能为空");

            Exception last = null;
            for (int attempt = 0; attempt < Math.Max(1, retries); attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tempPath, target);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(120 * (attempt + 1));
                }
            }
            throw new IOException("落盘失败：" + target + " :: " + (last == null ? "" : last.Message), last);
        }
    }
}
