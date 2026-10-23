using System;
using System.IO;
using PclDl.Core.Install;
using PclDl.Core.Plan;
using PclDl.Core.Transport;

namespace PclDl.Tests
{
    internal sealed class RepairTests
    {
        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pcldl-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void 校验_缺失返回MISSING()
        {
            var dir = TempDir();
            try
            {
                var file = new PlannedFile { Target = Path.Combine(dir, "a.jar"), Size = 10 };
                Assert.Equal("MISSING", RepairService.WhyBroken(file), "文件不存在应报 MISSING");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void 校验_尺寸不符返回SIZE()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "a.jar");
                File.WriteAllBytes(path, new byte[5]);
                var file = new PlannedFile { Target = path, Size = 10 };
                Assert.Equal("SIZE", RepairService.WhyBroken(file), "长度不一致应报 SIZE");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void 校验_sha1不符返回SHA1_完好返回空()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "a.jar");
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                var good = new PlannedFile { Target = path, Size = 3, Sha1 = Hashing.Sha1File(path) };
                Assert.True(RepairService.WhyBroken(good) == null, "完好的文件应返回 null");

                var bad = new PlannedFile { Target = path, Size = 3, Sha1 = "0000000000000000000000000000000000000000" };
                Assert.Equal("SHA1", RepairService.WhyBroken(bad), "sha1 不符应报 SHA1");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
