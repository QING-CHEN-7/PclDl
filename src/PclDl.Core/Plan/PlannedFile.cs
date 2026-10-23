using System.Collections.Generic;

namespace PclDl.Core.Plan
{
    public enum PlannedKind { Client, Library, Native, AssetIndex, Asset, LogConfig }

    public sealed class PlannedFile
    {
        public string Target;
        public List<string> Urls = new List<string>();
        public long Size;
        public string Sha1;
        public PlannedKind Kind;

        // Native 专用：下载后需要解压到哪里、排除哪些前缀
        public string ExtractTo;
        public List<string> Exclude = new List<string>();
    }

    public sealed class PlanResult
    {
        public readonly List<PlannedFile> Files = new List<PlannedFile>();
        public readonly List<PlannedFile> Natives = new List<PlannedFile>();
        public string AssetIndexId;
        public string AssetIndexTarget;
    }
}
