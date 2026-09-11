#nullable disable

using System;

// Bundled with the optional screen-filter module.
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TarkovAutoShade
{
    internal sealed class ScreenshotWatcher : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, DateTime> emitted =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private Timer debounceTimer;
        private string pendingPath;
        private bool enabled;

        public event Action<string> ScreenshotReady;
        public event Action<string> WatcherFaulted;

        public bool IsActive
        {
            get
            {
                lock (sync)
                {
                    try
                    {
                        return watcher != null && watcher.EnableRaisingEvents;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public bool Enabled
        {
            get { lock (sync) return enabled; }
            set
            {
                lock (sync)
                {
                    enabled = value;
                    if (!enabled)
                    {
                        pendingPath = null;
                        if (debounceTimer != null)
                            debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    if (watcher != null) watcher.EnableRaisingEvents = enabled;
                }
            }
        }

        public string Folder { get; private set; }

        public void SetFolder(string folder)
        {
            lock (sync)
            {
                StopWatcher();
                pendingPath = null;
                if (debounceTimer != null)
                    debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                emitted.Clear();
                Folder = folder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return;

                watcher = new FileSystemWatcher(folder, "*.png");
                watcher.NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite | NotifyFilters.Size |
                    NotifyFilters.CreationTime;
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = enabled;
            }
        }

        public string FindLatest()
        {
            string folder;
            lock (sync) folder = Folder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return null;

            string latest = null;
            DateTime latestTime = DateTime.MinValue;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.png"); }
            catch (Exception exception)
            {
                Action<string> handler = WatcherFaulted;
                if (handler != null) handler(exception.Message);
                return null;
            }
            foreach (string file in files)
            {
                DateTime time;
                try { time = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                if (time > latestTime)
                {
                    latest = file;
                    latestTime = time;
                }
            }
            return latest;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Queue(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Queue(e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Action<string> handler = WatcherFaulted;
            if (handler == null) return;
            Exception error = e.GetException();
            handler(error == null ? "未知监听错误" : error.Message);
        }

        private void Queue(string filePath)
        {
            if (!enabled || !string.Equals(
                Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase))
                return;

            lock (sync)
            {
                if (!enabled) return;
                pendingPath = filePath;
                if (debounceTimer == null)
                {
                    debounceTimer = new Timer(delegate {
                        EmitPending();
                    }, null, 750, Timeout.Infinite);
                }
                else
                {
                    debounceTimer.Change(750, Timeout.Infinite);
                }
            }
        }

        private void EmitPending()
        {
            string path;
            lock (sync)
            {
                path = pendingPath;
                pendingPath = null;
            }
            if (string.IsNullOrWhiteSpace(path)) return;

            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                DateTime previous;
                if (emitted.TryGetValue(path, out previous) &&
                    (now - previous).TotalSeconds < 3.0)
                    return;
                emitted[path] = now;

                if (emitted.Count > 80)
                {
                    var stale = new List<string>();
                    foreach (KeyValuePair<string, DateTime> item in emitted)
                        if ((now - item.Value).TotalMinutes > 5.0) stale.Add(item.Key);
                    foreach (string item in stale) emitted.Remove(item);
                }
            }

            Action<string> handler = ScreenshotReady;
            if (handler != null) handler(path);
        }

        private void StopWatcher()
        {
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnChanged;
                watcher.Changed -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Error -= OnError;
                watcher.Dispose();
                watcher = null;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                StopWatcher();
                if (debounceTimer != null)
                {
                    debounceTimer.Dispose();
                    debounceTimer = null;
                }
            }
        }
    }
}
