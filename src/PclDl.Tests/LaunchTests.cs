using Newtonsoft.Json.Linq;
using PclDl.Core.Java;
using PclDl.Core.Launch;

namespace PclDl.Tests
{
    internal sealed class LaunchTests
    {
        [Test]
        public void Java建议_接受javaVersion对象()
        {
            var info = JObject.Parse("{\"component\":\"java-runtime-gamma\",\"majorVersion\":21}");
            Assert.Equal(21, JavaLocator.RequiredMajorFor("1.16.5", info), "传 javaVersion 对象时应取 majorVersion，而不是退回按 MC 版本推断");
        }

        [Test]
        public void Java建议_也兼容整份JSON()
        {
            var whole = JObject.Parse("{\"javaVersion\":{\"majorVersion\":17}}");
            Assert.Equal(17, JavaLocator.RequiredMajorFor("1.16.5", whole), "传整份版本 JSON 时也应识别");
        }

        [Test]
        public void 库路径_优先用downloads里的path()
        {
            var lib = JObject.Parse("{\"name\":\"a.b:c:1.0\",\"downloads\":{\"artifact\":{\"path\":\"a/b/c/1.0/c-1.0.jar\"}}}");
            Assert.Equal("a/b/c/1.0/c-1.0.jar", LaunchPlanner.LibraryPath(lib), "应直接使用 artifact.path");
        }

        [Test]
        public void 库路径_按坐标推导含classifier()
        {
            var lib = JObject.Parse("{\"name\":\"org.lwjgl:lwjgl:3.3.1:natives-windows\"}");
            Assert.Equal("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar", LaunchPlanner.LibraryPath(lib), "应推出带 classifier 的路径");
        }

        [Test]
        public void 命令行引号_结尾反斜杠不吞引号()
        {
            Assert.Equal("\"G:\\mc\\.minecraft\\\\\"", LaunchPlanner.Quote("G:\\mc\\.minecraft\\"), "结尾反斜杠应再补一个");
        }
    }
}
