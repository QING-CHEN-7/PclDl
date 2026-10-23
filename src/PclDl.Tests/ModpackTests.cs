using PclDl.Core.Modpack;

namespace PclDl.Tests
{
    internal sealed class ModpackTests
    {
        [Test]
        public void CurseForge加载器_带MC版本前缀()
        {
            var r = ModpackInstaller.ParseCurseForgeLoader("forge-1.20.1-47.2.0", "1.20.1");
            Assert.Equal("forge", r.Item1, "加载器应为 forge");
            Assert.Equal("47.2.0", r.Item2, "版本号应为 47.2.0");
        }

        [Test]
        public void CurseForge加载器_不带MC版本前缀()
        {
            var r = ModpackInstaller.ParseCurseForgeLoader("forge-47.2.0", "1.20.1");
            Assert.Equal("forge", r.Item1, "加载器应为 forge");
            Assert.Equal("47.2.0", r.Item2, "版本号应为 47.2.0");
        }

        [Test]
        public void CurseForge加载器_Fabric与NeoForge()
        {
            var fabric = ModpackInstaller.ParseCurseForgeLoader("fabric-0.15.11", "1.20.1");
            Assert.Equal("fabric", fabric.Item1, "加载器应为 fabric");
            Assert.Equal("0.15.11", fabric.Item2, "Fabric 版本应为 0.15.11");

            var neo = ModpackInstaller.ParseCurseForgeLoader("neoforge-20.4.190", "1.20.4");
            Assert.Equal("neoforge", neo.Item1, "加载器应为 neoforge");
            Assert.Equal("20.4.190", neo.Item2, "NeoForge 版本应为 20.4.190");
        }

        [Test]
        public void CurseForge加载器_纯原版或其他返回none()
        {
            Assert.Equal("none", ModpackInstaller.ParseCurseForgeLoader("", "1.20.1").Item1, "应返回 none");
            Assert.Equal("none", ModpackInstaller.ParseCurseForgeLoader(null, "1.20.1").Item1, "应返回 none");
            Assert.Equal("none", ModpackInstaller.ParseCurseForgeLoader("unknown-1.0", "1.20.1").Item1, "应返回 none");
        }
    }
}
