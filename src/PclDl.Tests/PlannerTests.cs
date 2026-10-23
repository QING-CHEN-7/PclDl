using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PclDl.Core.Layout;
using PclDl.Core.Logging;
using PclDl.Core.Plan;
using PclDl.Core.Resolve;
using PclDl.Core.Transport;

namespace PclDl.Tests
{
    internal sealed class PlannerTests
    {
        private const string Sample = @"{
          'id': '1.20.1-test',
          'mainClass': 'net.minecraft.client.main.Main',
          'assets': '5',
          'assetIndex': { 'id': '5', 'sha1': 'aaaa', 'size': 10, 'totalSize': 1000, 'url': 'https://piston-meta.mojang.com/v1/packages/aaaa/5.json' },
          'downloads': { 'client': { 'sha1': 'bbbb', 'size': 100, 'url': 'https://piston-data.mojang.com/v1/objects/bbbb/client.jar' } },
          'logging': { 'client': { 'argument': '-Dlog4j.configurationFile=log4j2.xml', 'file': { 'id': 'client-1.12.xml', 'sha1': 'cccc', 'size': 20, 'url': 'https://piston-data.mojang.com/v1/objects/cccc/client-1.12.xml' } } },
          'libraries': [
            { 'name': 'common:lib:1.0', 'downloads': { 'artifact': { 'path': 'common/lib/1.0/lib-1.0.jar', 'sha1': 'dddd', 'size': 30, 'url': 'https://libraries.minecraft.net/common/lib/1.0/lib-1.0.jar' } } },
            { 'name': 'osx:only:1.0', 'rules': [ { 'action': 'allow', 'os': { 'name': 'osx' } } ], 'downloads': { 'artifact': { 'path': 'osx/only/1.0/only-1.0.jar', 'sha1': 'eeee', 'size': 40, 'url': 'https://libraries.minecraft.net/osx/only/1.0/only-1.0.jar' } } },
            { 'name': 'native:lwjgl:3', 'downloads': { 'classifiers': { 'natives-windows': { 'path': 'native/lwjgl/3/lwjgl-3-natives-windows.jar', 'sha1': 'ffff', 'size': 50, 'url': 'https://libraries.minecraft.net/native/lwjgl/3/lwjgl-3-natives-windows.jar' } } }, 'natives': { 'windows': 'natives-windows' }, 'extract': { 'exclude': [ 'META-INF/' ] } }
          ]
        }";

        [Test]
        public void 计划包含客户端与资源索引()
        {
            var version = BuildVersion();
            var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "mc-test"));
            var planner = new DownloadPlanner(MirrorMap.CreateBmclapi(), true, layout, new NullLog());
            var plan = planner.PlanCore(version);

            Assert.True(plan.Files.Any(f => f.Kind == PlannedKind.Client), "缺少客户端文件");
            Assert.True(plan.Files.Any(f => f.Kind == PlannedKind.AssetIndex), "缺少资源索引");
            Assert.True(plan.Files.Any(f => f.Kind == PlannedKind.LogConfig), "缺少日志配置");
            Assert.True(plan.Files.Any(f => f.Kind == PlannedKind.Native), "缺少 natives");
            Assert.Equal("5", plan.AssetIndexId, "资源索引 ID 错误");
        }

        [Test]
        public void 排除非windows库()
        {
            var version = BuildVersion();
            var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "mc-test"));
            var planner = new DownloadPlanner(MirrorMap.CreateBmclapi(), true, layout, new NullLog());
            var plan = planner.PlanCore(version);

            Assert.True(!plan.Files.Any(f => f.Target.Contains("osx")), "osx 库不应被计划");
            Assert.True(plan.Files.Any(f => f.Target.Contains("lib-1.0.jar")), "windows 通用库应被计划");
        }

        [Test]
        public void 资源对象路径分片正确()
        {
            var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "mc-test"));
            var path = layout.AssetObject("abcdef0123");
            Assert.True(path.EndsWith(Path.Combine("objects", "ab", "abcdef0123")), "资源分片路径错误：" + path);
        }

        [Test]
        public void 候选地址镜像优先()
        {
            var version = BuildVersion();
            var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "mc-test"));
            var planner = new DownloadPlanner(MirrorMap.CreateBmclapi(), true, layout, new NullLog());
            var plan = planner.PlanCore(version);
            var client = plan.Files.First(f => f.Kind == PlannedKind.Client);
            Assert.True(client.Urls[0].StartsWith("https://bmclapi2"), "客户端应镜像优先");
        }

        private static PclDl.Core.Models.ResolvedVersion BuildVersion()
        {
            var json = JObject.Parse(Sample.Replace("'", "\""));
            return VersionResolver.Merge(new List<JObject> { json }, new List<string> { "1.20.1-test" });
        }
    }
}
