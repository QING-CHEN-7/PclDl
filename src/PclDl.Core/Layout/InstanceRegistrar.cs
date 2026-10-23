using System;
using System.Collections.Generic;
using System.IO;

namespace PclDl.Core.Layout
{
    /// <summary>
    /// 实例注册（v4 §4.5）：除了 versions&lt;id&gt;&lt;id&gt;.json，还要写 PCL 自己的
    /// PCL\Setup.ini（Logo / LogoCustom / DisplayType / IsStar），
    /// 让 PCL 第一次扫到实例时就有稳定的展示信息。
    /// 格式与 PCL 一致：单行 "Key:Value"，不是 INI 段格式。
    /// </summary>
    public static class InstanceRegistrar
    {
        public static void Register(DataLayout layout, string versionId,
            bool isStar = false, int displayType = 0, string logo = null, bool logoCustom = false)
        {
            if (layout == null || string.IsNullOrWhiteSpace(versionId)) return;
            try
            {
                var dir = Path.Combine(layout.VersionDir(versionId), "PCL");
                var path = Path.Combine(dir, "Setup.ini");
                Directory.CreateDirectory(dir);

                // 已有文件就保留用户设置，只补缺失的键
                var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var index = line.IndexOf(':');
                        if (index <= 0) continue;
                        var key = line.Substring(0, index).Trim();
                        var value = line.Substring(index + 1).Trim();
                        if (key.Length > 0) existing[key] = value;
                    }
                }

                if (!existing.ContainsKey("Logo")) existing["Logo"] = logo ?? "";
                if (!existing.ContainsKey("LogoCustom")) existing["LogoCustom"] = (logoCustom ? "True" : "False");
                if (!existing.ContainsKey("DisplayType")) existing["DisplayType"] = displayType.ToString();
                if (!existing.ContainsKey("IsStar")) existing["IsStar"] = (isStar ? "True" : "False");

                var lines = new List<string>();
                foreach (var pair in existing) lines.Add(pair.Key + ":" + pair.Value);
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 注册失败不影响安装结果，PCL 自己会在加载时补写
            }
        }
    }
}
