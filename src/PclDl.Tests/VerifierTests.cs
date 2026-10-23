using System;
using System.IO;
using PclDl.Core.Transport;

namespace PclDl.Tests
{
    internal sealed class VerifierTests
    {
        [Test]
        public void 校验_缺失尺寸sha1三种情况()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pcldl-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "a.bin");
                Assert.Equal(Verifier.Missing, Verifier.Check(path, 3, null), "不存在应返回 MISSING");

                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                Assert.Equal(Verifier.SizeMismatch, Verifier.Check(path, 5, null), "长度不符应返回 SIZE");

                var sha = Hashing.Sha1File(path);
                Assert.True(Verifier.Check(path, 3, sha) == null, "尺寸+sha1 都对应返回 null");
                Assert.Equal(Verifier.Sha1Mismatch, Verifier.Check(path, 3, new string('0', 40)), "sha1 不符应返回 SHA1");
                Assert.True(Verifier.Check(path, 0, null) == null, "不校验尺寸时应直接通过");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void 落盘_覆盖已有文件()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pcldl-move-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var temp = Path.Combine(dir, "x.part");
                var target = Path.Combine(dir, "x.jar");
                File.WriteAllText(temp, "new");
                File.WriteAllText(target, "old");

                Verifier.MoveIntoPlace(temp, target);

                Assert.Equal("new", File.ReadAllText(target), "应覆盖为目标内容");
                Assert.True(!File.Exists(temp), "临时文件应已不存在");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
