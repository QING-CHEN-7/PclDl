using Newtonsoft.Json.Linq;
using PclDl.Core.Resolve;

namespace PclDl.Tests
{
    internal sealed class MergeTests
    {
        [Test]
        public void 参数合并_加载器只写自己的那几条()
        {
            var vanilla = JObject.Parse("{\"id\":\"1.20.1\",\"arguments\":{\"game\":[\"--username\",\"${auth_player_name}\"],\"jvm\":[\"-Djava.library.path=${natives_directory}\"]}}");
            var loader = JObject.Parse("{\"id\":\"1.20.1-forge-47.1.106\",\"arguments\":{\"game\":[\"--launchTarget\",\"forgeclient\"],\"jvm\":[\"--add-opens\",\"java.base/java.lang.invoke=cpw.mods.securejarhandler\"]}}");

            var merged = VersionResolver.Merge(new[] { vanilla, loader }, new[] { "1.20.1", "1.20.1-forge-47.1.106" });
            var game = (JArray)merged.Arguments["game"];
            var jvm = (JArray)merged.Arguments["jvm"];

            Assert.Equal(4, game.Count, "原版 2 条 + 加载器 2 条都要保留");
            Assert.Equal("--username", (string)game[0], "原版参数在前");
            Assert.Equal("--launchTarget", (string)game[2], "加载器参数在后");
            Assert.Equal(3, jvm.Count, "原版 1 条 + 加载器 2 条");
            Assert.Equal("-Djava.library.path=${natives_directory}", (string)jvm[0], "原版 jvm 参数在前");
            Assert.Equal("--add-opens", (string)jvm[1], "加载器的 add-opens 必须在");
        }

        [Test]
        public void 参数合并_没有arguments时返回空()
        {
            var only = JObject.Parse("{\"id\":\"1.12.2\",\"minecraftArguments\":\"--username ${auth_player_name}\"}");
            var merged = VersionResolver.Merge(new[] { only }, new[] { "1.12.2" });
            Assert.True(merged.Arguments == null, "老格式没有 arguments 时应为 null");
            Assert.Equal("--username ${auth_player_name}", merged.LegacyArguments, "老格式走 minecraftArguments");
        }
    }
}
