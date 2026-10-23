using System;
using System.IO;
using System.Security.Cryptography;

namespace PclDl.Core.Transport
{
    public static class Hashing
    {
        public static string Sha1File(string path)
        {
            using (var sha = SHA1.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var hash = sha.ComputeHash(fs);
                return ToHex(hash);
            }
        }

        public static string Sha1(byte[] bytes)
        {
            using (var sha = SHA1.Create()) return ToHex(sha.ComputeHash(bytes));
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
