using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;

namespace SimpleScraper
{
    public class DownloadUploadPipelineService : IDisposable
    {
        private readonly DatabaseService _dbService;
        private readonly VideoDownloader _downloader;
        private readonly TelegramUploadService _telegramUploader;
        private readonly ConcurrentQueue<VideoData> _downloadQueue;
        private readonly ConcurrentQueue<VideoData> _uploadQueue;
        private readonly CancellationTokenSource _cancellationSource;
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly SemaphoreSlim _uploadSemaphore;
        private readonly int _maxConcurrentDownloads;
        private readonly int _maxConcurrentUploads;
        private readonly VideoScraper _sharedScraper; // Shared scraper for all workers
        
        private Task[] _downloadWorkers;
        private Task[] _uploadWorkers;
        private Task _statusDisplayTask;
        
        // Progress tracking
        private readonly ConcurrentDictionary<string, DownloadProgress> _downloadProgress;
        private readonly ConcurrentDictionary<string, UploadProgress> _uploadProgress;
        private readonly ConcurrentBag<string> _completedDownloads;
        private readonly ConcurrentBag<string> _completedUploads;
        private readonly object _statusLock = new object();
        
        // Dashboard print throttling to preserve console history
        private string _lastDashboardSnapshot = "";
        private DateTime _lastDashboardPrintedAt = DateTime.MinValue;
        
        // Current scraping status (channel/video/step)
        private string _scrapeStatusLine = string.Empty;
        
        public DownloadUploadPipelineService(
            DatabaseService dbService, 
            string downloadDirectory,
            string botToken, 
            string chatId, 
            string telegramServerUrl,
            int maxConcurrentDownloads = 3,
            int maxConcurrentUploads = 2)
        {
            _dbService = dbService;
            
            // Create shared scraper for all download workers
            _sharedScraper = new VideoScraper();
            
            _downloader = new VideoDownloader(dbService, downloadDirectory, _sharedScraper);
            
            // Only create Telegram uploader if all required parameters are provided
            if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(chatId) && !string.IsNullOrEmpty(telegramServerUrl))
            {
                _telegramUploader = new TelegramUploadService(dbService, botToken, chatId, telegramServerUrl);
            }
            
            _downloadQueue = new ConcurrentQueue<VideoData>();
            _uploadQueue = new ConcurrentQueue<VideoData>();
            _cancellationSource = new CancellationTokenSource();
            _maxConcurrentDownloads = maxConcurrentDownloads;
            _maxConcurrentUploads = maxConcurrentUploads;
            _downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads);
            _uploadSemaphore = new SemaphoreSlim(maxConcurrentUploads);
            
            _downloadProgress = new ConcurrentDictionary<string, DownloadProgress>();
            _uploadProgress = new ConcurrentDictionary<string, UploadProgress>();
            _completedDownloads = new ConcurrentBag<string>();
            _completedUploads = new ConcurrentBag<string>();
        }

        public async Task InitializeAsync()
        {
            Console.WriteLine("Initializing browser for parallel downloads...");
            await _sharedScraper.Initialize();
        }

        public void StartWorkers()
        {
            // Start download workers
            _downloadWorkers = Enumerable.Range(0, _maxConcurrentDownloads)
                .Select(i => Task.Run(() => DownloadWorker(i, _cancellationSource.Token)))
                .ToArray();

            // Start upload workers only if Telegram uploader is available
            if (_telegramUploader != null && _maxConcurrentUploads > 0)
            {
                _uploadWorkers = Enumerable.Range(0, _maxConcurrentUploads)
                    .Select(i => Task.Run(() => UploadWorker(i, _cancellationSource.Token)))
                    .ToArray();
                    
                Logger.Log($"Started {_maxConcurrentDownloads} download workers and {_maxConcurrentUploads} upload workers");
            }
            else
            {
                _uploadWorkers = new Task[0]; // Empty array
                Logger.Log($"Started {_maxConcurrentDownloads} download workers (no upload workers - Telegram not configured)");
            }

            // Start status display
            _statusDisplayTask = Task.Run(() => StatusDisplayWorker(_cancellationSource.Token));
        }

        public async Task ProcessVideosAsync(List<VideoData> videos)
        {
            foreach (var video in videos)
            {
                _downloadQueue.Enqueue(video);
            }

            Logger.Log($"Enqueued {videos.Count} videos for processing");

            // Wait for all downloads to complete
            while (!_downloadQueue.IsEmpty || _downloadProgress.Any())
            {
                await Task.Delay(1000);
            }

            // Wait for all uploads to complete
            while (!_uploadQueue.IsEmpty || _uploadProgress.Any())
            {
                await Task.Delay(1000);
            }
        }

        public void QueueVideosForProcessing(List<VideoData> videos)
        {
            foreach (var video in videos)
            {
                _downloadQueue.Enqueue(video);
            }

            Logger.Log($"Queued {videos.Count} videos for background processing (non-blocking)");
        }

        public async Task ProcessExistingDownloadsAsync()
        {
            if (_telegramUploader == null)
            {
                Logger.Log("Telegram upload not enabled. Skipping existing downloads processing.");
                return;
            }

            // Get videos that are downloaded but not uploaded to Telegram
            var downloadedVideos = await _dbService.GetDownloadedButNotUploadedVideosAsync();
            
            if (downloadedVideos.Any())
            {
                Logger.Log($"Found {downloadedVideos.Count} previously downloaded videos to upload to Telegram");
                
                foreach (var video in downloadedVideos)
                {
                    _uploadQueue.Enqueue(video);
                }
            }
        }

        private async Task DownloadWorker(int workerId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_downloadQueue.TryDequeue(out VideoData video))
                {
                    await _downloadSemaphore.WaitAsync(cancellationToken);
                    
                    try
                    {
                        var progress = new DownloadProgress
                        {
                            VideoTitle = video.Title,
                            WorkerId = workerId,
                            Status = "Starting...",
                            StartTime = DateTime.Now
                        };
                        
                        _downloadProgress[video.Url] = progress;

                        // Update progress callback
                        progress.Status = "Downloading...";
                        
                        await _downloader.DownloadVideoAsync(video, false, (downloadProgress) => {
                            progress.Status = $"Downloading... {downloadProgress:F1}%";
                        }, (fileSize) => {
                            progress.TotalBytes = fileSize;
                        }); // Silent download for pipeline with progress callback
                        
                        progress.Status = "Completed";
                        progress.EndTime = DateTime.Now;
                        
                        // Remove from active progress and add to completed
                        _downloadProgress.TryRemove(video.Url, out _);
                        _completedDownloads.Add($"{video.Title} (Worker {workerId})");
                        
                        // Add to upload queue only if Telegram uploader is available
                        if (_telegramUploader != null)
                        {
                            _uploadQueue.Enqueue(video);
                        }
                    }
                    catch (Exception ex)
                    {
                        _downloadProgress.TryRemove(video.Url, out _);
                        Logger.Log($"Download error for {video.Title}: {ex.Message}");
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }
                }
                else
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        private async Task UploadWorker(int workerId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_uploadQueue.TryDequeue(out VideoData video))
                {
                    await _uploadSemaphore.WaitAsync(cancellationToken);
                    
                    try
                    {
                        var progress = new UploadProgress
                        {
                            VideoTitle = video.Title,
                            WorkerId = workerId,
                            Status = "Starting...",
                            StartTime = DateTime.Now
                        };
                        
                        _uploadProgress[video.Url] = progress;

                        progress.Status = "Uploading to Telegram...";
                        
                        await _telegramUploader.UploadVideoAsync(video);
                        
                        progress.Status = "Completed";
                        progress.EndTime = DateTime.Now;
                        
                        // Remove from active progress and add to completed
                        _uploadProgress.TryRemove(video.Url, out _);
                        _completedUploads.Add($"{video.Title} (Worker {workerId})");
                    }
                    catch (Exception ex)
                    {
                        _uploadProgress.TryRemove(video.Url, out _);
                        Logger.Log($"Upload error for {video.Title}: {ex.Message}");
                    }
                    finally
                    {
                        _uploadSemaphore.Release();
                    }
                }
                else
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        private async Task StatusDisplayWorker(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    lock (_statusLock)
                    {
                        // Display organized status with smart console clearing
                        DisplayPipelineStatus();
                    }
                    
                    await Task.Delay(2000, cancellationToken); // Update every 2 seconds
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Don't log display errors to avoid cluttering
                    // Logger.Log($"Status display error: {ex.Message}");
                }
            }
        }

        public void UpdateScrapeStatus(string status)
        {
            lock (_statusLock)
            {
                _scrapeStatusLine = status ?? string.Empty;
            }
        }

        private void DisplayPipelineStatus()
        {
            // Build the dashboard content without clearing the console
            var sb = new StringBuilder();
            
            // Calculate overall progress
            var totalVideos = _completedDownloads.Count + _completedUploads.Count + _downloadQueue.Count + _uploadQueue.Count + _downloadProgress.Count + _uploadProgress.Count;
            var completedVideos = _completedUploads.Count;
            var overallProgress = totalVideos > 0 ? (double)completedVideos / totalVideos * 100 : 0;
            
            // Calculate time estimates
            var firstStartTime = DateTime.Now;
            var allActiveTimes = new List<DateTime>();
            
            // Get start times from active operations
            allActiveTimes.AddRange(_downloadProgress.Values.Select(p => p.StartTime));
            allActiveTimes.AddRange(_uploadProgress.Values.Select(p => p.StartTime));
            
            if (allActiveTimes.Any())
            {
                firstStartTime = allActiveTimes.Min();
            }
            else if (_completedDownloads.Any() || _completedUploads.Any())
            {
                // If no active operations, estimate based on when the first might have started
                // Use current time minus a reasonable estimate for how long completed items took
                var avgTimePerVideo = completedVideos > 0 ? TimeSpan.FromMinutes(2) : TimeSpan.Zero; // Assume 2 minutes per video
                firstStartTime = DateTime.Now - TimeSpan.FromMinutes(completedVideos * 2);
            }
            
            var elapsed = DateTime.Now - firstStartTime;
            var estimatedTotal = completedVideos > 0 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / completedVideos * totalVideos) : TimeSpan.Zero;
            var remaining = estimatedTotal - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            
            sb.AppendLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║                          VIDEO PIPELINE DASHBOARD                            ║");
            sb.AppendLine("╠═══════════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║");
            
            // Overall Progress Bar
            sb.AppendLine("║ � OVERALL PROGRESS:");
            sb.AppendLine($"║   {CreateProgressBar(overallProgress, 60)} {overallProgress:F1}%");
            sb.AppendLine($"║   Completed: {completedVideos}/{totalVideos} videos | Elapsed: {elapsed:hh\\:mm\\:ss} | ETA: {remaining:hh\\:mm\\:ss}");
            
            // One-line scrape status (channel/video/step)
            var scrapeStatus = string.IsNullOrWhiteSpace(_scrapeStatusLine) ? "Idle" : _scrapeStatusLine;
            sb.AppendLine($"║   Status: {TruncateString(scrapeStatus, 100)}");
            sb.AppendLine("║");
            
            // Current Activity Summary
            var activeDownloads = _downloadProgress.ToList();
            var activeUploads = _uploadProgress.ToList();
            
            if (activeDownloads.Any() || activeUploads.Any())
            {
                sb.AppendLine("║ 🔄 CURRENT ACTIVITY:");
                
                // Show all active downloads (increased from 2 to show all 3)
                foreach (var kvp in activeDownloads.Take(5)) // Show up to 5 to accommodate 3 download workers
                {
                    var progress = kvp.Value;
                    var title = TruncateString(progress.VideoTitle, 35);
                    var elapsed_item = DateTime.Now - progress.StartTime;
                    var workerInfo = $"Worker {progress.WorkerId}";
                    var sizeInfo = progress.TotalBytes.HasValue ? $" | {progress.FileSizeText}" : "";
                    sb.AppendLine($"║   📥 {title}");
                    sb.AppendLine($"║      {progress.Status} | {workerInfo} | {elapsed_item:mm\\:ss}{sizeInfo}");
                }
                
                // Show active uploads
                foreach (var kvp in activeUploads.Take(3)) // Show up to 3 upload workers
                {
                    var progress = kvp.Value;
                    var title = TruncateString(progress.VideoTitle, 40);
                    var elapsed_item = DateTime.Now - progress.StartTime;
                    var workerInfo = $"Worker {progress.WorkerId}";
                    sb.AppendLine($"║   📤 {title}");
                    sb.AppendLine($"║      {progress.Status} | {workerInfo} | {elapsed_item:mm\\:ss}");
                }
                
                if (activeDownloads.Count + activeUploads.Count > 8)
                {
                    var additional = activeDownloads.Count + activeUploads.Count - 8;
                    sb.AppendLine($"║   ... and {additional} more active operations");
                }
                sb.AppendLine("║");
            }
            
            // Pipeline Status Bar
            sb.AppendLine("║ � PIPELINE STATUS:");
            sb.AppendLine("║   ┌─────────────────┬─────────┬─────────┬───────────┬─────────┐");
            sb.AppendLine("║   │ Stage           │ Active  │ Queued  │ Completed │ Workers │");
            sb.AppendLine("║   ├─────────────────┼─────────┼─────────┼───────────┼─────────┤");
            sb.AppendLine($"║   │ Downloads       │ {activeDownloads.Count,7} │ {_downloadQueue.Count,7} │ {_completedDownloads.Count,9} │ {activeDownloads.Count}/{_maxConcurrentDownloads,7} │");
            sb.AppendLine($"║   │ Uploads         │ {activeUploads.Count,7} │ {_uploadQueue.Count,7} │ {_completedUploads.Count,9} │ {activeUploads.Count}/{_maxConcurrentUploads,7} │");
            sb.AppendLine("║   └─────────────────┴─────────┴─────────┴───────────┴─────────┘");
            
            sb.AppendLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
            
            // Only print when content changes, or every 30s as a heartbeat
            var snapshot = sb.ToString();
            if (snapshot == _lastDashboardSnapshot && (DateTime.Now - _lastDashboardPrintedAt) < TimeSpan.FromSeconds(30))
            {
                return;
            }
            _lastDashboardSnapshot = snapshot;
            _lastDashboardPrintedAt = DateTime.Now;
            Console.WriteLine(); // spacer to avoid overwriting previous output
            Console.Write(snapshot);
        }
        
        private string CreateProgressBar(double percentage, int width)
        {
            percentage = Math.Max(0, Math.Min(100, percentage));
            int filled = (int)(percentage / 100.0 * width);
            int empty = width - filled;

            return "│" + new string('█', filled) + new string('░', empty) + "│";
        }
        
        private string TruncateString(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
                return input ?? "";
                
            return input.Substring(0, maxLength - 3) + "...";
        }

        public void StopWorkers()
        {
            _cancellationSource.Cancel();
            
            Task.WaitAll(_downloadWorkers?.Concat(_uploadWorkers).Concat(new[] { _statusDisplayTask }).ToArray() ?? new Task[0], 
                        TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            StopWorkers();
            _sharedScraper?.Dispose();
            _telegramUploader?.Dispose();
            _downloadSemaphore?.Dispose();
            _uploadSemaphore?.Dispose();
            _cancellationSource?.Dispose();
        }
    }

    public class DownloadProgress
    {
        public string VideoTitle { get; set; } = "";
        public int WorkerId { get; set; }
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long? TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public string FileSizeText => TotalBytes.HasValue ? FormatFileSize(TotalBytes.Value) : "Unknown size";
        
        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }

    public class UploadProgress
    {
        public string VideoTitle { get; set; } = "";
        public int WorkerId { get; set; }
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}