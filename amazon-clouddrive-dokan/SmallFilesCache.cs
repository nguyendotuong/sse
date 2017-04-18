﻿namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Common;
    using Tools;

    public partial class SmallFilesCache
    {
        private static readonly Dictionary<string, Downloader> Downloaders = new Dictionary<string, Downloader>(10);
        private static readonly object DownloadersLock = new object();

        private static string cachePath;

        private readonly UniqueBackgroundWorker cleanAllWorker;
        private readonly UniqueBackgroundWorker<long> cleanSizeWorker;
        private readonly IHttpCloud cloud;
        private readonly object totalSizeLock = new object();

        private ConcurrentDictionary<string, CacheEntry> access = new ConcurrentDictionary<string, CacheEntry>(10, 1000);
        private long totalSize;

        public SmallFilesCache(IHttpCloud a)
        {
            cleanAllWorker = new UniqueBackgroundWorker(ClearAll);
            cleanSizeWorker = new UniqueBackgroundWorker<long>(ClearSize);
            cloud = a;
        }

        public string CachePath
        {
            get
            {
                return cachePath;
            }

            set
            {
                if (cachePath == value)
                {
                    return;
                }

                var wasNull = cachePath == null;
                try
                {
                    if (cachePath != null)
                    {
                        Directory.Delete(cachePath, true);
                    }
                }
                catch (Exception)
                {
                    Log.Warn("Can not delete old cache: " + cachePath);
                }

                cachePath = Path.Combine(value, "SmallFiles");

                if (wasNull)
                {
                    Task.Run(() => RecalculateTotalSize());
                }
            }
        }

        public long CacheSize { get; internal set; }

        public Action<FSItem> OnDownloaded { get; set; }

        public Action<FSItem> OnDownloadFailed { get; set; }

        public Action<FSItem> OnDownloadStarted { get; set; }

        public long TotalSize
        {
            get
            {
                lock (totalSizeLock)
                {
                    return totalSize;
                }
            }

            set
            {
                lock (totalSizeLock)
                {
                    totalSize = value;
                }
            }
        }

        public void AddAsLink(FSItem item, string path)
        {
            Directory.CreateDirectory(cachePath);
            AddExisting(item, path);
        }

        public CacheEntry AddExisting(FSItem item, string sourcePath = null)
        {
            var newentry = new CacheEntry { Id = item.Id, AccessTime = DateTime.UtcNow, LinkPath = sourcePath };
            if (access.TryAdd(item.Id, newentry))
            {
                TotalSizeIncrease(item.Length);
                return newentry;
            }

            return null;
        }

        public Task ClearAllInBackground()
        {
            return cleanAllWorker.Run();
        }

        public void Delete(FSItem item)
        {
            try
            {
                var path = Path.Combine(cachePath, item.Id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                CacheEntry outitem;
                access.TryRemove(item.Id, out outitem);
            }
            catch (Exception)
            {
                Log.Warn($"Could not delete small file in cache {item.Name}");
            }
        }

        public FileInfo GetItemInfo(FSItem item)
        {
            var path = GetFilePath(item);
            return (path != null) ? new FileInfo(path) : null;
        }

        public void MoveToCache(string olditemPath, FSItem newitem)
        {
            var newitemPath = Path.Combine(CachePath, newitem.Id);

            try
            {
                if (!File.Exists(newitemPath))
                {
                    File.Move(olditemPath, newitemPath);
                    AddExisting(newitem);
                }
                else
                {
                    File.Delete(olditemPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public SmallFileBlockStream OpenReadCachedOnly(FSItem item, Downloader downloader = null)
        {
            var path = GetFilePath(item);
            if (path == null)
            {
                return null;
            }

            Log.Trace("Opened cached: " + item.Id);
            return SmallFileBlockStream.OpenReadonly(item, path, downloader);
        }

        public IBlockStream OpenReadWithDownload(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var downloader = StartDownload(item, path);
            CacheEntry entry;

            if (access.TryGetValue(item.Id, out entry))
            {
                entry.AccessTime = DateTime.UtcNow;
            }

            return OpenReadCachedOnly(item, downloader);
        }

        public SmallFileBlockStream OpenReadWrite(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var downloader = StartDownload(item, path);
            CacheEntry entry;
            if (access.TryGetValue(item.Id, out entry))
            {
                entry.AccessTime = DateTime.UtcNow;
            }

            Log.Trace("Opened ReadWrite cached: " + item.Id);
            return SmallFileBlockStream.OpenWriteable(item, path, downloader);
        }

        public void TotalSizeDecrease(long val)
        {
            lock (totalSizeLock)
            {
                totalSize -= val;
            }
        }

        public void TotalSizeIncrease(long val)
        {
            lock (totalSizeLock)
            {
                totalSize += val;
            }
        }

        private void ClearAll()
        {
            var oldcachePath = cachePath;
            var failed = 0;
            try
            {
                if (Directory.Exists(oldcachePath))
                {
                    foreach (var file in Directory.GetFiles(oldcachePath, "*", SearchOption.AllDirectories).ToList())
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (IOException)
                        {
                            failed++;
                        }
                    }
                }
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                Log.Warn("Old cache folder was empty");
            }
            catch (UnauthorizedAccessException)
            {
                Log.ErrorTrace("Cannot access folder: " + oldcachePath);
                return;
            }

            RecalculateTotalSize();

            if (failed > 0)
            {
                Log.Warn("Can not delete all cached files. Some files are still in use.");
            }
        }

        private void ClearSize(long size)
        {
            try
            {
                long deleted = 0;
                var cacheEntries = access.Values.Where(i => i.LinkPath == null).OrderBy(f => f.AccessTime).TakeWhile(f =>
                    {
                        size -= f.Length;
                        return size > 0;
                    }).ToList();
                foreach (var file in cacheEntries)
                {
                    try
                    {
                        var path = Path.Combine(cachePath, file.Id);
                        var info = new FileInfo(path);
                        File.Delete(path);
                        deleted += info.Length;
                        CacheEntry remove;
                        access.TryRemove(file.Id, out remove);
                    }
                    catch (IOException)
                    {
                        // Skip if failed
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                    }
                }

                TotalSizeDecrease(deleted);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private async Task Download(FSItem item, Stream result, Downloader downloader)
        {
            try
            {
                Log.Trace($"Started download: {item.Name} - {item.Id}");
                var start = Stopwatch.StartNew();
                var buf = new byte[64 << 10];
                var uncommitedSize = 0;
                const int commitSize = 512 << 10;

                using (result)
                using (var writer = new BufferedStream(result))
                {
                    OnDownloadStarted?.Invoke(item);
                    while (writer.Length < item.Length)
                    {
                        var stream = await cloud.Files.Download(item.Id);
                        stream.Position = writer.Length;
                        int red;
                        do
                        {
                            red = await stream.ReadAsync(buf, 0, buf.Length);
                            if (writer.Length == 0)
                            {
                                Log.Trace($"Got first part: {item.Name} - {item.Id} in {start.ElapsedMilliseconds}");
                            }

                            await writer.WriteAsync(buf, 0, red);
                            uncommitedSize += red;
                            if (uncommitedSize <= commitSize)
                            {
                                continue;
                            }

                            uncommitedSize = 0;
                            await writer.FlushAsync();
                            downloader.Downloaded = writer.Length;
                        }
                        while (red > 0);
                    }

                    await writer.FlushAsync();
                    downloader.Downloaded = writer.Length;
                }

                Log.Trace($"Finished download: {item.Name} - {item.Id}");
                OnDownloaded?.Invoke(item);
                BoschHelper.Decrypt(downloader.Path);

                if (access.TryAdd(
                        item.Id,
                        new CacheEntry { Id = item.Id, AccessTime = DateTime.UtcNow, Length = item.Length }))
                {
                    TotalSizeIncrease(item.Length);
                }

                if (TotalSize > CacheSize)
                {
                    var task = cleanSizeWorker.Run(TotalSize - CacheSize);
                }

                if (start.ElapsedMilliseconds > 29000)
                {
                    Log.Warn($"Downloading {item.Path} took: {start.ElapsedMilliseconds}");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorTrace($"Download failed: {item.Name} - {item.Id}\r\n{ex}");
                await downloader.Failed();
                OnDownloadFailed?.Invoke(item);
            }
            finally
            {
                lock (DownloadersLock)
                {
                    Downloaders.Remove(item.Path);
                }
            }
        }

        private string GetFilePath(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            CacheEntry entry;
            if (!access.TryGetValue(item.Id, out entry))
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                entry = AddExisting(item);
            }

            entry.AccessTime = DateTime.UtcNow;

            if (entry.LinkPath != null && File.Exists(entry.LinkPath))
            {
                path = entry.LinkPath;
            }

            if (!File.Exists(path))
            {
                CacheEntry remove;
                access.TryRemove(item.Id, out remove);
                return null;
            }

            return path;
        }

        private void RecalculateTotalSize()
        {
            if (cachePath == null)
            {
                return;
            }

            long t = 0;
            var newaccess = new ConcurrentDictionary<string, CacheEntry>(10, 1000);
            try
            {
                foreach (var file in Directory.GetFiles(cachePath))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        t += fi.Length;
                        var id = Path.GetFileName(file);
                        newaccess.TryAdd(id, new CacheEntry { Id = id, AccessTime = fi.LastAccessTimeUtc });
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Cannot access folder: " + cachePath, ex);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            access = newaccess;
            TotalSize = t;
        }

        private Downloader StartDownload(FSItem item, string path)
        {
            var downloader = new Downloader(item, path);

            lock (DownloadersLock)
            {
                Downloader result;
                if (Downloaders.TryGetValue(item.Path, out result))
                {
                    if (result.Task == null)
                    {
                        throw new Exception("Downloader Task is Null");
                    }

                    return result;
                }

                Directory.CreateDirectory(cachePath);

                var fileinfo = new FileInfo(path);

                if (fileinfo.Exists && fileinfo.Length == item.Length)
                {
                    return Downloader.CreateCompleted(item, path, item.Length);
                }

                Stream writer;

                if (!fileinfo.Exists || fileinfo.Length < item.Length)
                {
                    writer = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (writer.Length > 0)
                    {
                        Log.Warn(
                            $"File was not totally downloaded before. Should be {item.Length} but was {writer.Length}: {item.Path} - {item.Id}");
                        downloader.Downloaded = writer.Length;
                    }
                }
                else
                {
                    writer = new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }

                downloader.Task = Task.Factory.StartNew(async () => await Download(item, writer, downloader), TaskCreationOptions.LongRunning);
                Downloaders.Add(item.Path, downloader);

                return downloader;
            }
        }
    }
}