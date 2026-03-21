#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Denis.TradingEngine.Strategy.Pullback
{
    public static class PullbackConfigProvider
    {
        private static readonly object _lock = new();
        private static readonly ILogger _log = Log.ForContext(typeof(PullbackConfigProvider));

        private static PullbackConfigRoot? _cached;
        private static DateTime _cachedAtUtc;
        private static bool _pathLogged;

        // TTL cache (podesi kako ti odgovara)
        private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

        private static FileSystemWatcher? _watcher;
        private static bool _watcherInitialized;

        public static PullbackConfigRoot GetConfig()
        {
            EnsureWatcher();

            // Brz put: TTL nije istekao i imamo cache
            var now = DateTime.UtcNow;
            var cached = _cached;
            if (cached != null && (now - _cachedAtUtc) <= _ttl)
                return cached;

            lock (_lock)
            {
                // Double-check pod lock-om
                cached = _cached;
                if (cached != null && (now - _cachedAtUtc) <= _ttl)
                    return cached;

                var cfg = LoadFromDisk_NoThrow();
                _cached = cfg;
                _cachedAtUtc = now;
                return cfg;
            }
        }

        private static PullbackConfigRoot LoadFromDisk_NoThrow()
        {
            var path = GetConfigPath();
            try
            {
                if (!_pathLogged)
                {
                    _log.Information("[PULLBACK-CFG] Loading from {Path} exists={Exists}", path, File.Exists(path));
                    _pathLogged = true;
                }

                if (!File.Exists(path))
                    return new PullbackConfigRoot();

                var json = File.ReadAllText(path);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var cfg = JsonSerializer.Deserialize<PullbackConfigRoot>(json, options)
                          ?? new PullbackConfigRoot();

                _log.Information(
                    "[PULLBACK-CFG] Loaded OK defaultsDebug={Dbg} exchanges={ExCount}",
                    cfg.Defaults.DebugLogging,
                    cfg.Exchanges?.Count ?? 0);

                return cfg;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[PULLBACK-CFG] Failed to load {Path}. Falling back to defaults.", path);
                return new PullbackConfigRoot();
            }
        }

        private static string GetConfigPath()
        {
            // Ako ti je config u output folderu, ovo je OK.
            // Ako želiš root projekta, prebaci na Environment.CurrentDirectory.
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "pullback-config.json");
        }

        private static void EnsureWatcher()
        {
            if (_watcherInitialized)
                return;

            lock (_lock)
            {
                if (_watcherInitialized)
                    return;

                try
                {
                    var path = GetConfigPath();
                    var dir = Path.GetDirectoryName(path);
                    var file = Path.GetFileName(path);

                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file))
                    {
                        _watcherInitialized = true;
                        return;
                    }

                    _watcher = new FileSystemWatcher(dir, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite
                                     | NotifyFilters.FileName
                                     | NotifyFilters.Size
                                     | NotifyFilters.CreationTime
                    };

                    _watcher.Changed += (_, __) => InvalidateCache();
                    _watcher.Created += (_, __) => InvalidateCache();
                    _watcher.Renamed += (_, __) => InvalidateCache();
                    _watcher.Deleted += (_, __) => InvalidateCache();

                    _watcher.EnableRaisingEvents = true;
                }
                catch
                {
                    // ako watcher ne može (npr. permission), TTL i dalje radi
                }
                finally
                {
                    _watcherInitialized = true;
                }
            }
        }

        private static void InvalidateCache()
        {
            // Ne zaključavamo dugo; samo invalidate.
            lock (_lock)
            {
                _cached = null;
                _cachedAtUtc = default;
            }
        }
    }
}
