﻿namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Newtonsoft.Json;
    using Tools;

    public enum FailReason
    {
        NoResultNode,
        NoFolderNode,
        NoOverwriteNode,
        Conflict,
        Unexpected,
        Cancelled,
        FileNotFound,
        ContentIdMismatch
    }

    public enum UploadState
    {
        Waiting = 0,
        ContentId,
        Uploading,
        Finishing,
        Failed
    }

    public class UploadService : IDisposable
    {
        public const string UploadFolder = "Upload";

        private const int ReuploadDelay = 5000;
        private readonly ConcurrentDictionary<string, UploadInfo> allUploads = new ConcurrentDictionary<string, UploadInfo>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IHttpCloud cloud;
        private readonly BlockingCollection<UploadInfo> leftUploads = new BlockingCollection<UploadInfo>();
        private readonly int uploadLimit;
        private readonly SemaphoreSlim uploadLimitSemaphore;
        private string cachePath;
        private bool disposedValue; // To detect redundant calls
        private Task serviceTask;

        public UploadService(int limit, IHttpCloud cloud)
        {
            uploadLimit = limit;
            uploadLimitSemaphore = new SemaphoreSlim(limit);
            this.cloud = cloud;
        }

        public delegate Task OnUploadFailedDelegate(UploadInfo item, FailReason reason, string message);

        public delegate Task OnUploadFinishedDelegate(UploadInfo item, FSItem.Builder amazonNode);

        public delegate Task OnUploadProgressDelegate(UploadInfo item, long done);

        public delegate Task OnUploadStateDelegate(UploadInfo item, UploadState state);

        public string CachePath
        {
            get
            {
                return cachePath;
            }

            set
            {
                var newpath = Path.Combine(value, UploadFolder, cloud.Id);
                if (cachePath == newpath)
                {
                    return;
                }

                Log.Trace($"Cache path changed from {cachePath} to {newpath}");
                cachePath = newpath;
                Directory.CreateDirectory(cachePath);
                CheckOldUploads();
            }
        }

        public bool CheckFileHash { get; set; }

        public Func<UploadInfo, Task> OnUploadAdded { get; set; }

        public OnUploadFailedDelegate OnUploadFailed { get; set; }

        public OnUploadFinishedDelegate OnUploadFinished { get; set; }

        public OnUploadProgressDelegate OnUploadProgress { get; set; }

        public OnUploadStateDelegate OnUploadState { get; set; }

        public async Task AddOverwrite(FSItem item)
        {
            try
            {
                Directory.CreateDirectory(cachePath);

                // To copy a source file to file in cache folder
                var sourceFile = Path.Combine(cachePath, item.Id);
                var sourceFileInfoPath = sourceFile + ".info";
                var tempFile = sourceFile + ".temp";
                File.Copy(sourceFile, tempFile, true);
                BoschHelper.Encrypt(sourceFile, tempFile);
                var tempFileInfo = new FileInfo(tempFile);

                File.Delete(sourceFileInfoPath);
                var info = new UploadInfo(item)
                {
                    Length = tempFileInfo.Length,
                    Path = tempFile,
                    SourcePath = tempFile,
                    Overwrite = true
                };

                await WriteInfo(sourceFileInfoPath, info);
                leftUploads.Add(info);
                allUploads.TryAdd(info.Id, info);
                OnUploadAdded?.Invoke(info);
            }
            catch(Exception ex)
            {

            }
        }

        public async Task AddUpload(FSItem parent, string file)
        {
            Directory.CreateDirectory(cachePath);
            var fileinfo = new FileInfo(file);
            var infoId = Guid.NewGuid().ToString();

            // To copy a source file to file in cache folder
            var tempFile = Path.Combine(cachePath, infoId) + ".temp";
            var tempFileInfo = new FileInfo(tempFile);
            File.Copy(file, tempFile, true);
            BoschHelper.Encrypt(file, tempFile);

            var info = new UploadInfo
            {
                Id = infoId,
                Length = tempFileInfo.Length,
                Path = Path.Combine(parent.Path, Path.GetFileName(file)),
                ParentId = parent.Id,
                SourcePath = tempFile
            };

            var path = Path.Combine(cachePath, info.Id);
            await WriteInfo(path + ".info", info);
            leftUploads.Add(info);
            allUploads.TryAdd(info.Id, info);
            OnUploadAdded?.Invoke(info);
        }

        public void CancelUpload(string id)
        {
            if (!allUploads.TryGetValue(id, out UploadInfo outitem))
            {
                return;
            }

            outitem.Cancellation.Cancel();
            OnUploadFailed(outitem, FailReason.Cancelled, "Upload cancelled");
            allUploads.TryRemove(outitem.Id, out outitem);
            CleanUpload(outitem);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);

            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        public NewFileBlockWriter OpenNew(FSItem item)
        {
            Directory.CreateDirectory(cachePath);
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.OnClose = async () =>
            {
                if (!result.Cancelled)
                {
                    await AddUpload(item);
                }
            };

            return result;
        }

        public NewFileBlockWriter OpenTruncate(FSItem item)
        {
            Directory.CreateDirectory(cachePath);
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.SetLength(0);
            result.OnClose = async () =>
            {
                if (!result.Cancelled)
                {
                    await AddOverwrite(item);
                }
            };

            return result;
        }

        public void Start()
        {
            if (serviceTask != null)
            {
                return;
            }

            serviceTask = Task.Factory.StartNew(UploadTask, cancellation.Token, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        public void Stop()
        {
            if (serviceTask == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                serviceTask.Wait();
            }
            catch (AggregateException e)
            {
                e.Handle(ce => ce is TaskCanceledException);
            }

            serviceTask = null;
        }

        public async Task WaitForUploadsFinish()
        {
            while (leftUploads.Count > 0)
            {
                await Task.Delay(100);
            }

            for (var i = 0; i < uploadLimit; i++)
            {
                await uploadLimitSemaphore.WaitAsync();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    cancellation.Dispose();
                    uploadLimitSemaphore.Dispose();
                    leftUploads.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.
                disposedValue = true;
            }
        }

        private static async Task WriteInfo(string path, UploadInfo info)
        {
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true)))
            {
                await writer.WriteAsync(JsonConvert.SerializeObject(info));
            }
        }

        private async Task AddUpload(FSItem item)
        {
            var info = new UploadInfo(item);

            var path = Path.Combine(cachePath, item.Id);
            await WriteInfo(path + ".info", info);
            leftUploads.Add(info);
            allUploads.TryAdd(info.Id, info);
            await (OnUploadAdded?.Invoke(info) ?? Task.FromResult(0));
        }

        private async Task<string> CalcContentId(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                return await cloud.CalculateLocalStreamContentId(file);
            }
        }

        private void CheckOldUploads()
        {
            var files = Directory.GetFiles(cachePath, "*.info");
            if (files.Length == 0)
            {
                return;
            }

            Log.Warn($"{files.Length} not uploaded files found. Resuming.");
            foreach (var info in files.Select(f => new FileInfo(f)).OrderBy(f => f.CreationTime))
            {
                try
                {
                    var id = Path.GetFileNameWithoutExtension(info.Name);
                    var uploadinfo = JsonConvert.DeserializeObject<UploadInfo>(File.ReadAllText(info.FullName));

                    try
                    {
                        Contract.Assert(info.DirectoryName != null, "info.DirectoryName!=null");
                        leftUploads.Add(uploadinfo);
                        allUploads.TryAdd(id, uploadinfo);
                        OnUploadAdded?.Invoke(uploadinfo);
                    }
                    catch (FileNotFoundException)
                    {
                        Log.ErrorTrace("Cached upload file not found: " + uploadinfo.Path + " id:" + id);
                        CleanUpload(uploadinfo);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }

        private void CleanUpload(UploadInfo upload)
        {
            if (upload.SourcePath == null)
            {
                var sourcepath = Path.Combine(cachePath, upload.Id);
                try
                {
                    File.Delete(sourcepath);
                }
                catch (Exception)
                {
                    Log.Warn("CleanUpload did not find the file, probably successfully moved");
                }
            }

            try
            {
                var infopath = Path.Combine(cachePath, upload.Id + ".info");
                File.Delete(infopath);
                var tempPath = Path.Combine(cachePath, upload.Id + ".temp");
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                Log.Warn("CleanUpload did not find the info file, probably successfully moved");
            }

            upload.Dispose();
        }

        private async Task SetState(UploadInfo item, UploadState state)
        {
            if (OnUploadState != null)
            {
                await OnUploadState.Invoke(item, state);
            }
        }

        private async Task Upload(UploadInfo item)
        {
            var sourcepath = item.SourcePath ?? Path.Combine(cachePath, item.Id);
            var infopath = Path.Combine(cachePath, item.Id + ".info");
            try
            {
                var itemName = Path.GetFileName(item.Path);
                var parentId = item.ParentId;
                try
                {
                    Log.Trace("Started upload: " + item.Path);
                    FSItem.Builder node;

                    if (CheckFileHash && item.ContentId == null)
                    {
                        await SetState(item, UploadState.ContentId);
                        item.ContentId = await CalcContentId(sourcepath);
                    }

                    if (!item.Overwrite)
                    {
                        var checkparent = await cloud.Nodes.GetNode(parentId);
                        if (checkparent == null || !checkparent.IsDir)
                        {
                            Log.ErrorTrace("Folder does not exist to upload file: " + item.Path);
                            await OnUploadFailed(item, FailReason.NoFolderNode, "Parent folder is missing");
                            CleanUpload(item);
                            return;
                        }

                        var checknode = await cloud.Nodes.GetChild(parentId, itemName);
                        if (checknode != null)
                        {
                            Log.Warn("File with such name already exists and Upload is New: " + item.Path);
                            await OnUploadFailed(item, FailReason.Conflict, "File already exists");
                            CleanUpload(item);
                            return;
                        }

                        var lastPresenceCheck = DateTime.UtcNow;

                        await SetState(item, UploadState.Uploading);

                        node = await cloud.Files.UploadNew(
                            parentId,
                            itemName,
                            () => new FileStream(sourcepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                            async p =>
                            {
                                lastPresenceCheck = await CheckDuplicate(item, itemName, lastPresenceCheck);

                                await UploadProgress(item, p);
                            });
                    }
                    else
                    {
                        var checknode = await cloud.Nodes.GetNode(item.Id);
                        if (checknode == null)
                        {
                            Log.ErrorTrace("File does not exist to be overwritten: " + item.Path);
                            await OnUploadFailed(item, FailReason.NoOverwriteNode, "No file to overwrite");
                            CleanUpload(item);
                            return;
                        }

                        if (item.ContentId != null && item.ContentId == checknode.ContentId)
                        {
                            Log.Warn($"File content is the same. Skip overwrite: {item.Path}");
                            node = checknode;
                        }
                        else
                        {
                            await SetState(item, UploadState.Uploading);

                            node = await cloud.Files.Overwrite(
                                item.Id,
                                () => new FileStream(sourcepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                                async p => await UploadProgress(item, p));
                        }
                    }

                    if (node == null)
                    {
                        throw new NullReferenceException("File node is null: " + item.Path);
                    }

                    if (node.Length != item.Length)
                    {
                        item.Overwrite = true;
                        throw new Exception($"Uploaded file size not correct: {item.Path} Correct Size: {item.Length} Got: {node.Length}");
                    }

                    if (item.ContentId != null && item.ContentId != node.ContentId)
                    {
                        Log.ErrorTrace($"Upload finished with content id mismatch: {item.Path} local:{item.ContentId} remote:{node.ContentId}");
                        await OnUploadFailed(
                            item,
                            FailReason.ContentIdMismatch,
                            "Uploaded item content id does not match local file content id. Consider to check uploaded file and reupload.");
                        CleanUpload(item);
                        return;
                    }

                    node.ParentPath = Path.GetDirectoryName(item.Path);

                    Log.Trace("Finished upload: " + item.Path + " id:" + node.Id);
                    await OnUploadFinished(item, node);
                    CleanUpload(item);
                    return;
                }
                catch (FileNotFoundException ex)
                {
                    Log.Error($"Upload error upload file not found: {item.Path}", ex);
                    await OnUploadFailed(item, FailReason.FileNotFound, "Cached upload file is not found");

                    CleanUpload(item);
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (item.Cancellation.IsCancellationRequested)
                    {
                        Log.Info("Upload canceled");

                        await OnUploadFailed(item, FailReason.Cancelled, "Upload cancelled");
                        CleanUpload(item);
                    }

                    return;
                }
                catch (CloudException ex)
                {
                    if (ex.Error == System.Net.HttpStatusCode.Conflict)
                    {
                        var node = await cloud.Nodes.GetChild(parentId, itemName);
                        if (node != null)
                        {
                            Log.Warn($"Upload finished with conflict and file does exist: {item.Path}\r\n{ex}");
                            await OnUploadFinished(item, node);
                            CleanUpload(item);
                            return;
                        }

                        Log.Error($"Upload conflict but no file: {item.Path}", ex);
                        await OnUploadFailed(item, FailReason.Unexpected, "Upload conflict but there is no file in the same place");
                    }
                    else if (ex.Error == System.Net.HttpStatusCode.NotFound)
                    {
                        Log.Error($"Upload error Folder Not Found: {item.Path}", ex);
                        await OnUploadFailed(item, FailReason.NoFolderNode, "Folder node for new file is not found");

                        CleanUpload(item);
                        return;
                    }
                    else if (ex.Error == System.Net.HttpStatusCode.GatewayTimeout)
                    {
                        Log.Warn($"Gateway timeout happened: {item.Path}\r\nWait 30 seconds to check if file was really uploaded");

                        await Task.Delay(30000);
                        var node = await cloud.Nodes.GetChild(parentId, itemName);
                        if (node != null)
                        {
                            Log.Warn($"Gateway timeout happened: {item.Path}\r\nBut after 30 seconds file did appear");
                            File.Delete(infopath);

                            node.ParentPath = Path.GetDirectoryName(item.Path);

                            Log.Trace($"Finished upload: {item.Path} id:{node.Id}");
                            await OnUploadFinished(item, node);
                            item.Dispose();
                            return;
                        }

                        Log.ErrorTrace($"Gateway timeout happened: {item.Path}\r\nBut after 30 seconds file still did not appear.");
                        await OnUploadFailed(item, FailReason.Unexpected, "Gateway timeout happened but after 30 seconds file still did not appear");
                    }
                    else
                    {
                        Log.ErrorTrace($"Upload cloud exception: {item.Path} - {ex.Message}");
                        await OnUploadFailed(item, FailReason.Unexpected, $"Unexpected Error. Upload will retry.\r\n{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Upload failed: {item.Path}", ex);
                await OnUploadFailed(item, FailReason.Unexpected, $"Unexpected Error. Upload will retry.\r\n{ex.Message}");
            }
            finally
            {
                uploadLimitSemaphore.Release();
                allUploads.TryRemove(item.Id, out _);
            }

            await Task.Delay(ReuploadDelay);
            Log.Warn($"Repeat upload: {item.Path}");
            allUploads.TryAdd(item.Id, item);
            leftUploads.Add(item);
        }

        private async Task<DateTime> CheckDuplicate(UploadInfo item, string itemName, DateTime lastPresenceCheck)
        {
            if (DateTime.UtcNow - lastPresenceCheck > TimeSpan.FromMinutes(10))
            {
                lastPresenceCheck = DateTime.UtcNow;
                var checknode2 = await cloud.Nodes.GetChild(item.ParentId, itemName);
                if (checknode2 != null)
                {
                    if (item.ContentId == checknode2.ContentId)
                    {
                        Log.Warn($"Found already existing file. File content is the same. Cancel upload new: {item.Path}");
                        item.Cancellation.Cancel();
                    }
                    else
                    {
                        Log.Warn($"Found already existing file. File content is NOT the same. Conflict: {item.Path}");
                        throw new CloudException(System.Net.HttpStatusCode.Conflict, null);
                    }
                }
            }

            return lastPresenceCheck;
        }

        private async Task UploadProgress(UploadInfo item, long p)
        {
            if (p == 0)
            {
                await SetState(item, UploadState.Uploading);
            }

            if (OnUploadProgress != null)
            {
                await OnUploadProgress(item, p);
            }

            if (p == item.Length)
            {
                await SetState(item, UploadState.Finishing);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            item.Cancellation.Token.ThrowIfCancellationRequested();
        }

        private void UploadTask()
        {
            try
            {
                while (leftUploads.TryTake(out UploadInfo upload, -1, cancellation.Token))
                {
                    var uploadCopy = upload;
                    if (!uploadLimitSemaphore.Wait(-1, cancellation.Token))
                    {
                        return;
                    }

                    Task.Factory.StartNew(
                        async () =>
                        {
                            try
                            {
                                await Upload(uploadCopy);
                            }
                            catch (Exception e)
                            {
                                Log.Error(e);
                            }
                        },
                        TaskCreationOptions.LongRunning);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Upload service stopped");
            }
        }
    }
}