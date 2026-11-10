using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace AmmoStacks
{
    // AmmoStacks' log. Every line goes to BepInEx\LogOutput.log and to its own file,
    // BepInEx\AmmoStacks.log (rewritten each launch; last launch kept as AmmoStacks.prev.log),
    // with a timestamp and a category:
    //   [start]  loading, config, environment, each hook      [items]  ammo/arrow items changed or reset
    //   [loot]   loot hooks firing, vanilla-stack checks       [watch]  something changed our values
    //   [status] summary (logged when it changes, max 1/min)   [error]  anything that failed
    internal static class ALog
    {
        static StreamWriter _file;
        static readonly object _lock = new object();
        static readonly Dictionary<string, string> _last = new Dictionary<string, string>();
        static readonly Dictionary<string, DateTime> _next = new Dictionary<string, DateTime>();
        static readonly HashSet<string> _once = new HashSet<string>();
        internal static bool Verbose;
        internal static int Errors, Warnings;
        internal static string FilePath;

        internal static void Open(string version)
        {
            try
            {
                FilePath = Path.Combine(Paths.BepInExRootPath, "AmmoStacks.log");
                var prev = Path.Combine(Paths.BepInExRootPath, "AmmoStacks.prev.log");
                try { if (File.Exists(FilePath)) File.Copy(FilePath, prev, true); } catch { }
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _file.WriteLine($"AmmoStacks {version} log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception e) { Plugin.L?.LogWarning("Couldn't open AmmoStacks.log: " + e.Message); }
        }

        static void Write(string level, string cat, string msg)
        {
            string line = $"[{cat}] {msg}";
            try
            {
                if (level == "ERROR") Plugin.L?.LogError(line);
                else if (level == "WARN") Plugin.L?.LogWarning(line);
                else if (level == "DEBUG") Plugin.L?.LogDebug(line);
                else Plugin.L?.LogInfo(line);
            }
            catch { }
            try
            {
                lock (_lock)   // loot can be generated off the main thread
                    _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-5} {line}");
            }
            catch { }
        }

        internal static void Info(string cat, string msg) => Write("INFO", cat, msg);
        internal static void Warn(string cat, string msg) { Warnings++; Write("WARN", cat, msg); }
        internal static void Error(string cat, string msg, Exception e = null)
        {
            Errors++;
            Write("ERROR", cat, e == null ? msg : $"{msg}: {Describe(e)}");
        }
        internal static void Debug(string cat, string msg) { if (Verbose) Write("DEBUG", cat, msg); }

        // Only when the message for this key changes.
        internal static void Change(string key, string cat, string msg)
        {
            lock (_lock)
            {
                if (_last.TryGetValue(key, out var old) && old == msg) return;
                _last[key] = msg;
            }
            Info(cat, msg);
        }

        internal static bool Once(string key)
        {
            lock (_lock) return _once.Add(key);
        }
        internal static void Once(string key, string cat, string msg) { if (Once(key)) Info(cat, msg); }
        internal static void WarnOnce(string key, string cat, string msg) { if (Once("W|" + key)) Warn(cat, msg); }

        // Error, but at most once per `seconds` for this key (for things that run every frame / every loot roll).
        internal static void ErrorThrottled(string key, double seconds, string cat, string msg, Exception e = null)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_next.TryGetValue("E|" + key, out var t) && now < t) return;
                _next["E|" + key] = now.AddSeconds(seconds);
            }
            Error(cat, msg + $" (repeats suppressed for {seconds:0}s)", e);
        }

        internal static void WarnThrottled(string key, double seconds, string cat, string msg)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_next.TryGetValue("W|" + key, out var t) && now < t) return;
                _next["W|" + key] = now.AddSeconds(seconds);
            }
            Warn(cat, msg);
        }

        // Exception with its inner exceptions and stack trace (IL2CPP exceptions hide the real error in InnerException).
        internal static string Describe(Exception e)
        {
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            for (var x = e; x != null && depth < 4; x = x.InnerException, depth++)
            {
                if (depth > 0) sb.Append("\n  caused by: ");
                sb.Append(x.GetType().Name).Append(": ").Append(x.Message);
            }
            sb.Append('\n').Append(e.StackTrace);
            return sb.ToString();
        }
    }
}
