using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PclDl.Core.Logging;

namespace PclDl.Core.Modpack
{
    /// <summary>
    /// CurseForge 整合包解析：manifest.json 里只有 projectID/fileID，
    /// 需要 CurseForge API（x-api-key）换出真实下载地址。
    /// Key 从环境变量 PCLDL_CURSEFORGE_KEY 读取；未配置时给出明确提示。
    /// </summary>
    public sealed class CurseForgeResolver
    {
        private const string DefaultApiBase = "https://api.curseforge.com/v1";

        private readonly HttpClient _http;
        private readonly ILog _log;
        private readonly string _apiKey;
        private readonly string _apiBase;

        public CurseForgeResolver(HttpClient http, ILog log, string apiKey)
        {
            _http = http;
            _log = log ?? new NullLog();
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("PCLDL_CURSEFORGE_KEY");
            _apiBase = Environment.GetEnvironmentVariable("PCLDL_CURSEFORGE_API");
            if (string.IsNullOrWhiteSpace(_apiBase)) _apiBase = DefaultApiBase;
            _apiBase = _apiBase.TrimEnd('/');
        }

        public bool HasKey { get { return !string.IsNullOrWhiteSpace(_apiKey); } }

        public sealed class ResolvedFile
        {
            public string Url;
            public string FileName;
            public long Size;
        }

        public async Task<ResolvedFile> ResolveFileAsync(long projectId, long fileId)
        {
            if (!HasKey)
                throw new InvalidOperationException(
                    "CurseForge 整合包需要 API Key：请设置环境变量 PCLDL_CURSEFORGE_KEY 后重试");

            var url = _apiBase + "/mods/" + projectId + "/files/" + fileId;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("CurseForge API 返回 " + (int)response.StatusCode +
                            "（" + Trim(body, 200) + "）");
                    }
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var data = json["data"] as JObject;
                    if (data == null) throw new InvalidOperationException("CurseForge 响应缺少 data");

                    var resolved = new ResolvedFile
                    {
                        FileName = (string)data["fileName"],
                        Size = data["fileLength"] == null ? 0 : (long)data["fileLength"],
                        Url = (string)data["downloadUrl"]
                    };

                    // 部分文件没有 downloadUrl，用 ForgeCDN 规则拼接
                    if (string.IsNullOrEmpty(resolved.Url) && fileId > 0)
                    {
                        var part1 = fileId / 1000;
                        var part2 = fileId % 1000;
                        resolved.Url = "https://edge.forgecdn.net/files/" + part1 + "/" + part2.ToString("000") + "/" +
                                       Uri.EscapeDataString(resolved.FileName ?? "file.jar");
                    }
                    if (string.IsNullOrEmpty(resolved.Url))
                        throw new InvalidOperationException("无法解析 CurseForge 文件地址：" + projectId + "/" + fileId);

                    _log.Debug("CurseForge 文件：" + resolved.Url);
                    return resolved;
                }
            }
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
