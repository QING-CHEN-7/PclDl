using System;
using System.IO;

namespace PclDl.Core.Layout
{
    /// <summary>
    /// .minecraft 标准布局。目标是与 PCL 的期望完全一致：
    /// versions/&lt;id&gt;/&lt;id&gt;.json|.jar、libraries/、assets/indexes|objects|log_configs
    /// </summary>
    public sealed class DataLayout
    {
        public string GameDir { get; private set; }

        public DataLayout(string gameDir) { GameDir = NormalizeDir(Path.GetFullPath(gameDir)); }

        /// <summary>去掉结尾的路径分隔符（保留 "C:\" 这种根目录），避免拼出 G:\mc\\versions 或被当成转义引号。</summary>
        public static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            var trimmed = dir.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return dir.Substring(0, 1);
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }

        public string VersionsDir { get { return Path.Combine(GameDir, "versions"); } }
        public string LibrariesDir { get { return Path.Combine(GameDir, "libraries"); } }
        public string AssetsDir { get { return Path.Combine(GameDir, "assets"); } }
        public string IndexesDir { get { return Path.Combine(AssetsDir, "indexes"); } }
        public string ObjectsDir { get { return Path.Combine(AssetsDir, "objects"); } }
        public string LogConfigsDir { get { return Path.Combine(AssetsDir, "log_configs"); } }

        public string VersionDir(string id) { return Path.Combine(VersionsDir, id); }
        public string VersionJson(string id) { return Path.Combine(VersionDir(id), id + ".json"); }
        public string VersionJar(string id) { return Path.Combine(VersionDir(id), id + ".jar"); }
        /// <summary>PCL 的 natives 目录约定：versions\&lt;id&gt;\&lt;id&gt;-natives（ModLaunch.GetNativesFolder）。</summary>
        public string VersionNatives(string id) { return Path.Combine(VersionDir(id), id + "-natives"); }
        public string AssetIndex(string id) { return Path.Combine(IndexesDir, id + ".json"); }

        public string AssetObject(string hash)
        {
            var prefix = hash.Substring(0, 2);
            return Path.Combine(ObjectsDir, prefix, hash);
        }

        public string Library(string relativePath)
        {
            return Path.Combine(LibrariesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public void EnsureBaseDirectories()
        {
            Directory.CreateDirectory(VersionsDir);
            Directory.CreateDirectory(LibrariesDir);
            Directory.CreateDirectory(IndexesDir);
            Directory.CreateDirectory(ObjectsDir);
            Directory.CreateDirectory(LogConfigsDir);
        }
    }
}
