using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PclDl.Core.Logging;
using PclDl.Core.Plan;

namespace PclDl.Core.Transport
{
    public sealed class FetchOptions
    {
        public int Concurrency = 16;
        public int Retries = 3;
        public TimeSpan Timeout = TimeSpan.FromSeconds(20);
        /// <summary>多源竞速的错峰间隔：第一个源超过这么久还没拿到响应头，就同时开下一个源。</summary>
        public TimeSpan RaceStagger = TimeSpan.FromMilliseconds(900);
        public string UserAgent = "PclDl/0.1 (+https://github.com/)";
    }

    public sealed class FetchProgress
    {
        public long BytesDone;
        public long BytesTotal;
        public int FilesDone;
        public int FilesTotal;
        /// <summary>当前正在下载的文件（相对路径更易读）。</summary>
        public string CurrentFile;
        /// <summary>平均速度（字节/秒）。</summary>
        public double BytesPerSecond;
        /// <summary>预计剩余时间；无法估算时为 null。</summary>
        public TimeSpan? Eta;
    }

    public sealed class FetchResult
    {
        public int Downloaded;
        public int Skipped;
        public int Failed;
        public readonly List<string> Errors = new List<string>();

        public bool Ok { get { return Failed == 0; } }
    }

    /// <summary>
    /// 多源回退、断点续传、sha1 校验、原子落盘。
    /// 只处理远程文件下载；解压与写入由上层负责。
    /// </summary>
    public sealed class FileFetcher : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ILog _log;

        public FileFetcher(ILog log, FetchOptions options = null)
        {
            _log = log ?? new NullLog();
            Options = options ?? new FetchOptions();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = 16
            };
            _http = new HttpClient(handler);
            _http.Timeout = Options.Timeout;
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Options.UserAgent);
        }

        public FetchOptions Options { get; private set; }

        public async Task<FetchResult> FetchAllAsync(
            IReadOnlyList<PlannedFile> files,
            IProgress<FetchProgress> progress,
            CancellationToken ct)
        {
            var result = new FetchResult();
            CleanupStaleParts(files);
            var totalBytes = files.Sum(f => Math.Max(0, f.Size));
            var doneBytes = 0L;
            var doneFiles = 0;
            var gate = new SemaphoreSlim(Options.Concurrency);
            var tasks = new List<Task>();

            var state = new FetchProgress { BytesTotal = totalBytes, FilesTotal = files.Count };
            var watch = Stopwatch.StartNew();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        state.CurrentFile = Path.GetFileName(file.Target);
                        var outcome = await FetchOneAsync(file, (delta) =>
                        {
                            var d = Interlocked.Add(ref doneBytes, delta);
                            state.BytesDone = d;
                            state.FilesDone = Volatile.Read(ref doneFiles);
                            if (progress != null) progress.Report(Snapshot(state, watch));
                        }, ct).ConfigureAwait(false);

                        if (outcome == FetchOutcome.Downloaded) Interlocked.Increment(ref result.Downloaded);
                        else if (outcome == FetchOutcome.Skipped) Interlocked.Increment(ref result.Skipped);
                        else
                        {
                            Interlocked.Increment(ref result.Failed);
                            lock (result.Errors) result.Errors.Add(file.Target);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref result.Failed);
                        lock (result.Errors) result.Errors.Add(file.Target + " :: " + ex.Message);
                    }
                    finally
                    {
                        Interlocked.Increment(ref doneFiles);
                        state.FilesDone = Volatile.Read(ref doneFiles);
                        if (progress != null) progress.Report(Snapshot(state, watch));
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return result;
        }

        private static FetchProgress Snapshot(FetchProgress s, Stopwatch watch)
        {
            var done = Interlocked.Read(ref s.BytesDone);
            var speed = watch != null && watch.ElapsedMilliseconds > 300
                ? done / Math.Max(0.001, watch.Elapsed.TotalSeconds)
                : 0;

            TimeSpan? eta = null;
            if (speed > 1024 && s.BytesTotal > done)
                eta = TimeSpan.FromSeconds((s.BytesTotal - done) / speed);

            return new FetchProgress
            {
                BytesDone = done,
                BytesTotal = s.BytesTotal,
                FilesDone = Volatile.Read(ref s.FilesDone),
                FilesTotal = s.FilesTotal,
                CurrentFile = s.CurrentFile,
                BytesPerSecond = speed,
                Eta = eta
            };
        }

        private enum FetchOutcome { Downloaded, Skipped, Failed }

        private async Task<FetchOutcome> FetchOneAsync(PlannedFile file, Action<long> onDelta, CancellationToken ct)
        {
            if (File.Exists(file.Target) && VerifyLocal(file))
            {
                onDelta(file.Size);
                return FetchOutcome.Skipped;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Target));

            var urls = file.Urls.Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (urls.Count == 0) { _log.Error("没有可用的下载地址：" + file.Target); return FetchOutcome.Failed; }

            for (int attempt = 0; attempt < Options.Retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // 与 PCL 一致：多源错峰竞速，谁先出响应头就用谁，避免单源卡死拖慢整体
                    await DownloadRacingAsync(urls, file, onDelta, ct).ConfigureAwait(false);
                    if (!VerifyPart(file))
                    {
                        _log.Warn("校验失败，重下：" + file.Target);
                        TryDelete(file.Target + ".part");
                        continue;
                    }
                    if (File.Exists(file.Target)) File.Delete(file.Target);
                    Verifier.MoveIntoPlace(file.Target + ".part", file.Target);
                    return FetchOutcome.Downloaded;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Debug("下载失败 " + file.Target + " :: " + ex.Message);
                }
                // 指数退避：0.4s、0.8s、1.6s…（上限 5s）
                var backoff = Math.Min(5000, 400 * Math.Pow(2, attempt));
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), ct).ConfigureAwait(false);
            }

            _log.Error("多次重试仍失败：" + file.Target);
            return FetchOutcome.Failed;
        }

        /// <summary>
        /// 清理上次被中断（崩溃、被杀进程、断电）留下的 *.race / *.part 临时文件，
        /// 否则它们会一直躺在 assets/objects 与 libraries 里。
        /// </summary>
        private void CleanupStaleParts(IReadOnlyList<PlannedFile> files)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    var dir = Path.GetDirectoryName(file.Target);
                    if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
                }
                catch { }
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var path in Directory.EnumerateFiles(dir, "*.race").Concat(Directory.EnumerateFiles(dir, "*.part")))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 多源错峰竞速：第 i 个源延迟 i*RaceStagger 后启动；任一下载完成即宣布胜出并取消其余。
        /// 竞速期间不逐块回报进度（避免重复计数），胜出后一次性按文件大小回报。
        /// </summary>
        private async Task DownloadRacingAsync(List<string> urls, PlannedFile file, Action<long> onDelta, CancellationToken ct)
        {
            if (urls.Count == 1)
            {
                await DownloadToPartAsync(urls[0], file, onDelta, ct).ConfigureAwait(false);
                return;
            }

            var winnerPart = file.Target + ".part";
            var winner = 0;
            var tempParts = new ConcurrentBag<string>();

            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var tasks = new List<Task>();
                for (int i = 0; i < urls.Count; i++)
                {
                    var index = i;
                    var url = urls[i];
                    tasks.Add(Task.Run(async () =>
                    {
                        var tempPart = file.Target + "." + index + ".race";
                        tempParts.Add(tempPart);
                        ActiveParts[tempPart] = 1;
                        try
                        {
                            if (index > 0) await Task.Delay(TimeSpan.FromMilliseconds(Options.RaceStagger.TotalMilliseconds * index), raceCts.Token).ConfigureAwait(false);
                            await DownloadToRacePartAsync(url, file, tempPart, raceCts.Token).ConfigureAwait(false);

                            if (Interlocked.CompareExchange(ref winner, 1, 0) == 0)
                            {
                                TryDelete(winnerPart);
                                Verifier.MoveIntoPlace(tempPart, winnerPart);
                                raceCts.Cancel();
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { _log.Debug("竞速源失败 " + url + " :: " + ex.Message); }
                    }, raceCts.Token));
                }

                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { }

                foreach (var temp in tempParts) { TryDelete(temp); ActiveParts.TryRemove(temp, out _); }

                if (Volatile.Read(ref winner) == 0) throw new IOException("所有下载源都失败：" + file.Target);
                if (onDelta != null && file.Size > 0) onDelta(file.Size);
            }
        }

        private async Task DownloadToRacePartAsync(string url, PlannedFile file, string partPath, CancellationToken ct)
        {
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(Options.Timeout);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    attempt.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                    using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while ((read = await ReadWithIdleTimeoutAsync(src, buffer, attempt.Token, Options.Timeout).ConfigureAwait(false)) > 0)
                            await dst.WriteAsync(buffer, 0, read, attempt.Token).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task DownloadToPartAsync(string url, PlannedFile file, Action<long> onDelta, CancellationToken ct)
        {
            // 单次尝试的硬超时：ResponseHeadersRead 之后的读流不受 HttpClient.Timeout 约束
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // 只限制"连接 + 等响应头"这一段；响应体改成逐块空闲超时（见 ReadWithIdleTimeoutAsync）
                attempt.CancelAfter(Options.Timeout);
                try
                {
                    await DownloadToPartCoreAsync(url, file, onDelta, attempt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("下载超时（" + Options.Timeout.TotalSeconds.ToString("0") + "s）：" + url);
                }
            }
        }

        private async Task DownloadToPartCoreAsync(string url, PlannedFile file, Action<long> onDelta, CancellationTokenSource attempt)
        {
            var ct = attempt.Token;
            var part = file.Target + ".part";
            ActiveParts[part] = 1;
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        TryDelete(part);
                        throw new IOException("range not satisfiable, restart");
                    }
                    response.EnsureSuccessStatusCode();
                    attempt.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);   // 响应头到手，改由逐块空闲超时兜底

                    var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (!append) { TryDelete(part); existing = 0; onDelta(0); }

                    using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buffer = new byte[1 << 16];
                        long chunk = 0;
                        int read;
                        while ((read = await ReadWithIdleTimeoutAsync(src, buffer, ct, Options.Timeout).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            chunk += read;
                            if (chunk >= 262144) { onDelta(chunk); chunk = 0; }
                        }
                        if (chunk > 0) onDelta(chunk);
                    }
                }
            }
            ActiveParts.TryRemove(part, out _);
        }

        /// <summary>
        /// 逐块读：只要还有数据就一直读，多久都行；但只要连续 Options.Timeout 收不到新数据就放弃这一路。
        /// 为什么不能只靠 CancellationToken：.NET Framework 的 socket 读不一定会及时响应取消，
        /// 镜像挂住时会把整个下载卡死（实测卡过 3 分钟以上没有任何进展）。
        /// </summary>
        private static async Task<int> ReadWithIdleTimeoutAsync(Stream src, byte[] buffer, CancellationToken ct, TimeSpan idle)
        {
            var readTask = src.ReadAsync(buffer, 0, buffer.Length, ct);
            if (readTask.IsCompleted) return await readTask.ConfigureAwait(false);

            var delay = Task.Delay(idle, ct);
            var finished = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
            if (finished != readTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("连续 " + idle.TotalSeconds.ToString("0") + " 秒没有收到数据");
            }
            return await readTask.ConfigureAwait(false);
        }

        private bool VerifyLocal(PlannedFile file)
        {
            return Verifier.Check(file.Target, file.Size, file.Sha1) == Verifier.Ok;
        }

        private bool VerifyPart(PlannedFile file)
        {
            var part = file.Target + ".part";
            if (!File.Exists(part)) return false;
            var info = new FileInfo(part);
            if (file.Size > 0 && info.Length != file.Size) return false;
            if (!string.IsNullOrEmpty(file.Sha1))
            {
                var actual = Hashing.Sha1File(part);
                if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>当前正在写的临时文件，插件退出时用它做清理（v4 §6）。</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveParts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public static void CleanupActiveParts(ILog log = null)
        {
            foreach (var path in ActiveParts.Keys)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                ActiveParts.TryRemove(path, out _);
            }
            if (log != null) log.Info("已清理活动的临时文件");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose() { _http.Dispose(); }
    }
}
