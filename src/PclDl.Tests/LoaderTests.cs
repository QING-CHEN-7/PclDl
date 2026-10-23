using PclDl.Core.Loaders;

namespace PclDl.Tests
{
    internal sealed class LoaderTests
    {
        [Test]
        public void NeoForge前缀映射_三段版本()
        {
            Assert.Equal("21.1", ForgeLoader.NeoForgePrefix("1.21.1"), "1.21.1 应映射到 21.1");
            Assert.Equal("20.4", ForgeLoader.NeoForgePrefix("1.20.4"), "1.20.4 应映射到 20.4");
        }

        [Test]
        public void NeoForge前缀映射_两段版本()
        {
            Assert.Equal("21.0", ForgeLoader.NeoForgePrefix("1.21"), "1.21 应映射到 21.0");
        }

        [Test]
        public void NeoForge前缀映射_1_20_1用47_1()
        {
            Assert.Equal("47.1", ForgeLoader.NeoForgePrefix("1.20.1"), "1.20.1 的 NeoForge 是 47.1.x");
        }

        [Test]
        public void NeoForge坐标_1_20_1用forge构件()
        {
            var a = ForgeLoader.NeoForgeTargetFor("1.20.1");
            Assert.Equal("net/neoforged/forge", a.ArtifactPath, "1.20.1 的构件是 net.neoforged:forge");
            Assert.Equal("1.20.1-47.1", a.VersionFilter, "版本号带 MC 前缀");
            Assert.Equal("forge", a.FileStem, "文件名是 forge-<ver>-installer.jar");

            var b = ForgeLoader.NeoForgeTargetFor("1.20.4");
            Assert.Equal("net/neoforged/neoforge", b.ArtifactPath, "1.20.4 起是 net.neoforged:neoforge");
            Assert.Equal("20.4", b.VersionFilter, "版本号就是 20.4.x");

            Assert.Equal("https://m/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
                ForgeLoader.NeoForgeInstallerUrl("https://m", a, "1.20.1-47.1.106"), "安装器地址拼接");
        }

        [Test]
        public void NeoForge版本选择_按数字段取最高()
        {
            const string xml = "<metadata><versioning><versions>" +
                "<version>47.1.99</version><version>47.1.106</version><version>47.1.3</version>" +
                "<version>20.4.190</version><version>21.1.9</version>" +
                "</versions></versioning></metadata>";
            Assert.Equal("47.1.106", ForgeLoader.SelectNeoForgeVersion(xml, "47.1"), "47.1 里 106 最大（不是字典序）");
            Assert.Equal("20.4.190", ForgeLoader.SelectNeoForgeVersion(xml, "20.4"), "20.4 只有 190");
            Assert.Equal("21.1.9", ForgeLoader.SelectNeoForgeVersion(xml, "21.1"), "21.1 只有 9");
            Assert.True(ForgeLoader.SelectNeoForgeVersion(xml, "99.9") == null, "没有匹配时返回 null");
        }

        [Test]
        public void 版本号按数字段比较()
        {
            Assert.True(ForgeLoader.CompareVersions("21.1.217", "21.1.99") > 0, "217 应大于 99");
            Assert.True(ForgeLoader.CompareVersions("21.1.99", "21.1.217") < 0, "99 应小于 217");
            Assert.Equal(0, ForgeLoader.CompareVersions("21.1.5", "21.1.5"), "相同版本应为 0");
        }

        [Test]
        public void 命令行引号_结尾反斜杠不吞引号()
        {
            var quoted = ForgeLoader.Quote("G:\\mc\\.minecraft\\");
            Assert.Equal("\"G:\\mc\\.minecraft\\\\\"", quoted, "结尾反斜杠应再补一个，避免 \" 转义");
            Assert.Equal("\"C:\\a b\"", ForgeLoader.Quote("C:\\a b"), "普通路径原样加引号");
        }

        [Test]
        public void 目录规范化_去掉结尾分隔符()
        {
            Assert.Equal("G:\\mc", PclDl.Core.Layout.DataLayout.NormalizeDir("G:\\mc\\"), "应去掉结尾反斜杠");
            Assert.Equal("G:\\mc", PclDl.Core.Layout.DataLayout.NormalizeDir("G:\\mc/"), "应去掉结尾斜杠");
            Assert.Equal("C:\\", PclDl.Core.Layout.DataLayout.NormalizeDir("C:\\"), "根目录保留");
        }

        [Test]
        public void NeoForge前缀映射_非法输入返回空()
        {
            Assert.True(ForgeLoader.NeoForgePrefix("") == null, "空字符串应返回 null");
            Assert.True(ForgeLoader.NeoForgePrefix("abc") == null, "非版本号应返回 null");
        }
    }
}
