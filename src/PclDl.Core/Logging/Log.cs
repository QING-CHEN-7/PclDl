using System;
using System.IO;

namespace PclDl.Core.Logging
{
    public enum LogLevel { Debug, Info, Warn, Error }

    public interface ILog
    {
        void Write(LogLevel level, string message);
    }

    public sealed class ConsoleLog : ILog
    {
        private readonly object _gate = new object();
        private readonly bool _verbose;

        public ConsoleLog(bool verbose = false) { _verbose = verbose; }

        public void Write(LogLevel level, string message)
        {
            if (level == LogLevel.Debug && !_verbose) return;
            lock (_gate)
            {
                var old = Console.ForegroundColor;
                switch (level)
                {
                    case LogLevel.Warn: Console.ForegroundColor = ConsoleColor.Yellow; break;
                    case LogLevel.Error: Console.ForegroundColor = ConsoleColor.Red; break;
                    case LogLevel.Debug: Console.ForegroundColor = ConsoleColor.DarkGray; break;
                }
                Console.WriteLine("[" + level.ToString().ToLowerInvariant() + "] " + message);
                Console.ForegroundColor = old;
            }
        }
    }

    public sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message) { }
    }

    public static class LogExtensions
    {
        public static void Debug(this ILog log, string m) { log.Write(LogLevel.Debug, m); }
        public static void Info(this ILog log, string m) { log.Write(LogLevel.Info, m); }
        public static void Warn(this ILog log, string m) { log.Write(LogLevel.Warn, m); }
        public static void Error(this ILog log, string m) { log.Write(LogLevel.Error, m); }
    }
}
