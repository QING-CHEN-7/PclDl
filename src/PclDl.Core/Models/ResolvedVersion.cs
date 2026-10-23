using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PclDl.Core.Models
{
    public sealed class ManifestEntry
    {
        public string Id;
        public string Type;
        public string Url;
        public string Sha1;
        public long Size;
        public string ReleaseTime;
    }

    /// <summary>
    /// 合并 inheritsFrom 链之后的版本描述。字段保持原始 JObject 形态，避免有损转换。
    /// </summary>
    public sealed class ResolvedVersion
    {
        public string Id;
        public string Type;
        public string MainClass;
        public string Assets;
        public JObject AssetIndex;
        public JObject Client;
        public JObject Logging;
        public JObject JavaVersion;
        public JObject Arguments;
        public string LegacyArguments;
        /// <summary>JSON 里的 jar 字段：实例用它指向真正的客户端 jar（如整合包指向原版）。</summary>
        public string Jar;
        public readonly List<JObject> Libraries = new List<JObject>();
        /// <summary>downloads.client 实际所属的版本 ID（加载器版本的 jar 来自父版本）。</summary>
        public string ClientVersionId;
        public string RawJson;
        public readonly List<string> InheritanceChain = new List<string>();

        public string AssetIndexId
        {
            get { return AssetIndex == null ? null : (string)AssetIndex["id"]; }
        }
    }
}
