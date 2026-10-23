using PclDl.Core.Transport;

namespace PclDl.Tests
{
    internal sealed class MirrorTests
    {
        [Test]
        public void 清单地址映射()
        {
            var map = MirrorMap.CreateBmclapi();
            Assert.Equal("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                map.ToMirror("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"), "清单映射错误");
        }

        [Test]
        public void 资源对象映射到assets路径()
        {
            var map = MirrorMap.CreateBmclapi();
            Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef",
                map.ToMirror("https://resources.download.minecraft.net/ab/abcdef"), "资源映射错误");
        }

        [Test]
        public void 库地址映射到maven路径()
        {
            var map = MirrorMap.CreateBmclapi();
            Assert.Equal("https://bmclapi2.bangbang93.com/maven/a/b/1.0/b-1.0.jar",
                map.ToMirror("https://libraries.minecraft.net/a/b/1.0/b-1.0.jar"), "库映射错误");
        }

        [Test]
        public void 候选顺序镜像优先()
        {
            var map = MirrorMap.CreateBmclapi();
            var c = map.Candidates("https://libraries.minecraft.net/a/b.jar", true);
            Assert.Equal(2, c.Count, "候选数量应为 2");
            Assert.True(c[0].StartsWith("https://bmclapi2"), "镜像应排第一");
            Assert.True(c[1].StartsWith("https://libraries.minecraft.net"), "官方应排第二");
        }

        [Test]
        public void 版本JSON走version端点()
        {
            var map = MirrorMap.CreateBmclapi();
            var c = map.VersionJsonCandidates("1.20.1", "https://piston-meta.mojang.com/v1/packages/x/1.20.1.json", true);
            Assert.Equal("https://bmclapi2.bangbang93.com/version/1.20.1/json", c[0], "版本 JSON 镜像地址错误");
            Assert.Equal(2, c.Count, "版本 JSON 候选数量应为 2");
        }

        [Test]
        public void 客户端走version端点()
        {
            var map = MirrorMap.CreateBmclapi();
            var c = map.VersionClientCandidates("1.20.1", "https://piston-data.mojang.com/v1/objects/x/client.jar", true);
            Assert.Equal("https://bmclapi2.bangbang93.com/version/1.20.1/client", c[0], "客户端镜像地址错误");
        }

        [Test]
        public void 未知域名无镜像()
        {
            var map = MirrorMap.CreateBmclapi();
            Assert.True(map.ToMirror("https://example.com/a") == null, "未知域名应无镜像");
            var c = map.Candidates("https://example.com/a", true);
            Assert.Equal(1, c.Count, "无镜像时候选只有一个");
        }
    }
}
