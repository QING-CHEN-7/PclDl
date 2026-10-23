using PclDl.Core.Java;

namespace PclDl.Tests
{
    internal sealed class JavaTests
    {
        [Test]
        public void 版本号解析_老版与新式()
        {
            Assert.Equal(8, JavaLocator.ParseMajor("1.8.0_392"), "1.8.0_392 应为 8");
            Assert.Equal(7, JavaLocator.ParseMajor("1.7.0_80"), "1.7.0_80 应为 7");
            Assert.Equal(17, JavaLocator.ParseMajor("17.0.14"), "17.0.14 应为 17");
            Assert.Equal(21, JavaLocator.ParseMajor("21"), "21 应为 21");
            Assert.Equal(21, JavaLocator.ParseMajor("21.0.1+12-LTS"), "带后缀应为 21");
            Assert.Equal(0, JavaLocator.ParseMajor(""), "空字符串应为 0");
        }

        [Test]
        public void 需要的Java版本_按MC版本推断()
        {
            Assert.Equal(8, JavaLocator.RequiredMajorFor("1.12.2"), "1.12.2 需要 Java 8");
            Assert.Equal(8, JavaLocator.RequiredMajorFor("1.16.5"), "1.16.5 需要 Java 8");
            Assert.Equal(16, JavaLocator.RequiredMajorFor("1.17.1"), "1.17.1 需要 Java 16");
            Assert.Equal(17, JavaLocator.RequiredMajorFor("1.18.2"), "1.18.2 需要 Java 17");
            Assert.Equal(17, JavaLocator.RequiredMajorFor("1.20.1"), "1.20.1 需要 Java 17");
            Assert.Equal(21, JavaLocator.RequiredMajorFor("1.21"), "1.21 需要 Java 21");
        }

        [Test]
        public void 需要的Java版本_JSON优先()
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse("{\"javaVersion\":{\"majorVersion\":21}}");
            Assert.Equal(21, JavaLocator.RequiredMajorFor("1.16.5", json), "JSON 里的 javaVersion 应优先");
        }

        [Test]
        public void 选择Java_精确优先()
        {
            var runtimes = new[]
            {
                new JavaRuntime { JavaPath = "a", MajorVersion = 21 },
                new JavaRuntime { JavaPath = "b", MajorVersion = 8 },
                new JavaRuntime { JavaPath = "c", MajorVersion = 17 }
            };
            Assert.Equal("b", JavaLocator.SelectBest(runtimes, 8).JavaPath, "需要 8 就选 8");
            Assert.Equal("c", JavaLocator.SelectBest(runtimes, 17).JavaPath, "需要 17 就选 17");
        }

        [Test]
        public void 选择Java_需要8时不用高版本兜底()
        {
            var onlyNew = new[] { new JavaRuntime { JavaPath = "a", MajorVersion = 17 } };
            Assert.True(JavaLocator.SelectBest(onlyNew, 8) == null, "只有 Java 17 时不能拿来跑需要 8 的安装器");
            Assert.Equal("a", JavaLocator.SelectBest(onlyNew, 17).JavaPath, "需要 17 时可用");
        }

        [Test]
        public void 选择Java_高新版本兜底()
        {
            var only21 = new[] { new JavaRuntime { JavaPath = "a", MajorVersion = 21 } };
            Assert.Equal("a", JavaLocator.SelectBest(only21, 17).JavaPath, "需要 17 时可以用 21 兜底");
        }

        [Test]
        public void 缺少Java时的提示包含版本号()
        {
            var message = JavaLocator.DescribeMissing(8, new[] { new JavaRuntime { MajorVersion = 17 } });
            Assert.True(message.Contains("Java 8"), "提示里应有需要的版本：" + message);
            Assert.True(message.Contains("Java 17"), "提示里应列出已有的版本：" + message);
        }
    }
}
