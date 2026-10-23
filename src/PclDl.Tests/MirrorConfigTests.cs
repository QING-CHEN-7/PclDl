using System;
using System.IO;
using PclDl.Core.Layout;
using PclDl.Core.Transport;

namespace PclDl.Tests
{
    internal sealed class MirrorConfigTests
    {
        [Test]
        public void 外置镜像配置_解析规则()
        {
            var json = "{\"rules\":[{\"name\":\"x\",\"from\":\"https://a.example\",\"to\":\"https://mirror.example\"}]}";
            var map = MirrorMap.FromJson(json);
            Assert.Equal(1, map.Rules.Count, "应解析出 1 条规则");
            Assert.Equal("https://mirror.example/b/c.jar", map.ToMirror("https://a.example/b/c.jar"), "应命中前缀替换");
        }

        [Test]
        public void 外置镜像配置_文件不存在时生成默认并生效()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pcldl-mirror-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var map = MirrorMap.LoadOrCreate(dir);
                var path = Path.Combine(dir, "PCL2Plugins", "mirror.json");
                Assert.True(File.Exists(path), "应生成默认 mirror.json");
                Assert.True(map.Rules.Count > 0, "应有内置规则");
                Assert.True(map.ToMirror("https://launchermeta.mojang.com/x") != null, "默认规则应映射 launchermeta");

                // 改文件后应重新加载（热更新）
                File.WriteAllText(path, "{\"rules\":[{\"name\":\"only\",\"from\":\"https://x\",\"to\":\"https://y\"}]}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
                var updated = MirrorMap.LoadOrCreate(dir);
                Assert.Equal(1, updated.Rules.Count, "文件变了应重新读取");
                Assert.Equal("https://y/z", updated.ToMirror("https://x/z"), "新规则生效");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void 实例注册_写入PCL的Setup格式()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pcldl-reg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var layout = new DataLayout(dir);
                Directory.CreateDirectory(layout.VersionDir("测试版本"));
                InstanceRegistrar.Register(layout, "测试版本");

                var ini = Path.Combine(layout.VersionDir("测试版本"), "PCL", "Setup.ini");
                Assert.True(File.Exists(ini), "应生成 PCL\\Setup.ini");
                var text = File.ReadAllText(ini);
                foreach (var key in new[] { "Logo:", "LogoCustom:", "DisplayType:", "IsStar:" })
                    Assert.True(text.Contains(key), "缺少键 " + key + "：" + text);

                // 已有用户设置不能被覆盖
                File.WriteAllText(ini, "Logo:custom.png\r\nIsStar:True\r\n");
                InstanceRegistrar.Register(layout, "测试版本", isStar: false);
                text = File.ReadAllText(ini);
                Assert.True(text.Contains("Logo:custom.png"), "用户 Logo 不应被覆盖：" + text);
                Assert.True(text.Contains("IsStar:True"), "用户 IsStar 不应被覆盖：" + text);
                Assert.True(text.Contains("DisplayType:"), "缺失的键应补上：" + text);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
