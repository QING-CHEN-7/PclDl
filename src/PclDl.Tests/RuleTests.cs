using Newtonsoft.Json.Linq;
using PclDl.Core.Resolve;

namespace PclDl.Tests
{
    internal sealed class RuleTests
    {
        [Test]
        public void 无规则默认允许()
        {
            Assert.True(RuleEvaluator.IsAllowed(null), "null rules 应允许");
            Assert.True(RuleEvaluator.IsAllowed(JArray.Parse("[]")), "空数组应允许");
        }

        [Test]
        public void 仅允许windows()
        {
            var rules = JArray.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"windows\"}}]");
            Assert.True(RuleEvaluator.IsAllowed(rules), "windows 应允许");
        }

        [Test]
        public void 仅允许osx则应拒绝()
        {
            var rules = JArray.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"osx\"}}]");
            Assert.True(!RuleEvaluator.IsAllowed(rules), "osx 规则在 windows 上应拒绝");
        }

        [Test]
        public void 先允许后拒绝()
        {
            var rules = JArray.Parse("[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"windows\"}}]");
            Assert.True(!RuleEvaluator.IsAllowed(rules), "最后一条匹配规则生效，应为拒绝");
        }

        [Test]
        public void 要求feature为真则拒绝()
        {
            var rules = JArray.Parse("[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}]");
            Assert.True(!RuleEvaluator.IsAllowed(rules), "演示用户功能默认关闭，应拒绝");
        }
    }
}
