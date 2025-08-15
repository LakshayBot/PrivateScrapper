using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleScraper
{
    public class AutomatedScraperService : IDisposable
    {
        private readonly DatabaseService _dbService;
        private readonly VideoScraper _scraper;
        private readonly DownloadUploadPipelineService _pipelineService;
        private readonly CancellationTokenSource _cancellationSource;
        private bool _isRunning;
        private readonly string _downloadDirectory;
        
        public AutomatedScraperService(string connectionString, string downloadDirectory = null, 
            string telegramBotToken = null, string telegramChatId = null, string telegramServerUrl = null)
        {
            _dbService = new DatabaseService(connectionString);
            _scraper = new VideoScraper();
            _downloadDirectory = downloadDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
            _cancellationSource = new CancellationTokenSource();
            
            // Create pipeline service with parallel downloads and optional Telegram uploads
            _pipelineService = new DownloadUploadPipelineService(
                _dbService,
                _downloadDirectory,
                telegramBotToken,
                telegramChatId,
                telegramServerUrl,
                maxConcurrentDownloads: 3, // 3 concurrent downloads for automated service
                maxConcurrentUploads: 2    // 2 concurrent uploads if Telegram is enabled
            );
            
            // Initialize the logger
            Logger.Initialize(Path.Combine(_downloadDirectory, "logs"));
            
            _pipelineService.UpdateScrapeStatus($"🏗️ AutomatedScraperService initialized with download directory: {_downloadDirectory}");
            _pipelineService.UpdateScrapeStatus("⚡ Parallel downloads enabled: 3 concurrent workers");
            
            if (!string.IsNullOrEmpty(telegramBotToken) && !string.IsNullOrEmpty(telegramChatId))
            {
                _pipelineService.UpdateScrapeStatus("📤 Telegram upload service enabled: 2 concurrent workers");
            }
            else
            {
                _pipelineService.UpdateScrapeStatus("📤 Telegram upload disabled - missing bot token or chat ID.");
            }
        }
        
        public async Task Initialize()
        {
            _pipelineService.UpdateScrapeStatus("🚀 Initializing automated scraper service...");
            await _dbService.InitializeDatabaseAsync();
            
            // Initialize the pipeline service (this will set up the shared browser)
            await _pipelineService.InitializeAsync();
            
            // Try to initialize scraper, but don't fail if it doesn't work
            try 
            {
                await _scraper.Initialize();
                _pipelineService.UpdateScrapeStatus("✅ Video scraper initialized successfully.");
            }
            catch (Exception ex)
            {
                _pipelineService.UpdateScrapeStatus($"⚠️ WARNING: Could not initialize video scraper: {ex.Message}");
                _pipelineService.UpdateScrapeStatus("⚠️ Some functionality may be limited, but downloads can still work.");
            }

            // Auto-configure a default channel if none are present
            try
            {
                var existingChannels = await _dbService.GetActiveChannelsAsync();
                if (existingChannels.Count == 0)
                {
                    _pipelineService.UpdateScrapeStatus("📺 No channels found in database. Adding default 'SXYPRN Main' channel...");
                    await _dbService.SaveChannelAsync("SXYPRN Main", "https://sxyprn.com", checkIntervalMinutes: 30);
                    _pipelineService.UpdateScrapeStatus("✅ Default channel added successfully.");
                }
                else
                {
                    _pipelineService.UpdateScrapeStatus($"📺 Found {existingChannels.Count} active channels in database.");
                }
            }
            catch (Exception ex)
            {
                _pipelineService.UpdateScrapeStatus($"⚠️ Warning: Failed to auto-configure default channel: {ex.Message}");
            }

            _pipelineService.UpdateScrapeStatus("✅ Automated scraper service initialized successfully.");
        }
        
        public async Task StartAutomatedProcessing()
        {
            if (_isRunning)
            {
                _pipelineService.UpdateScrapeStatus("⚠️ Automated processing is already running.");
                return;
            }
            
            _isRunning = true;
            _pipelineService.UpdateScrapeStatus("🚀 Starting automated processing...");
            _pipelineService.UpdateScrapeStatus($"📁 Download directory: {_downloadDirectory}");
            
            // Start the pipeline workers (parallel downloads and uploads)
            _pipelineService.StartWorkers();
            
            // Queue any existing undownloaded videos (non-blocking)
            await DownloadAllPendingVideos();
            
            // Track the last check time for each channel
            var lastCheckTimes = new Dictionary<int, DateTime>();
            
            try
            {
                while (!_cancellationSource.Token.IsCancellationRequested)
                {
                    _pipelineService.UpdateScrapeStatus("🔄 Starting automated monitoring cycle...");
                    
                    // 1. Get all active channels from the database
                    var activeChannels = await _dbService.GetActiveChannelsAsync();
                    _pipelineService.UpdateScrapeStatus($"📺 Active channels: {activeChannels.Count}");
                    
                    if (activeChannels.Count == 0)
                    {
                        _pipelineService.UpdateScrapeStatus("⏳ No active channels found. Waiting 30 seconds before checking again...");
                        await Task.Delay(TimeSpan.FromSeconds(30), _cancellationSource.Token);
                        continue;
                    }
                    
                    // 2. Check which channels need processing based on their intervals
                    var channelsToProcess = new List<ChannelData>();
                    var now = DateTime.Now;
                    
                    foreach (var channel in activeChannels)
                    {
                        if (!lastCheckTimes.TryGetValue(channel.Id, out DateTime lastCheckTime))
                        {
                            lastCheckTime = DateTime.MinValue;
                        }
                        
                        var timeSinceLastCheck = now - lastCheckTime;
                        var checkInterval = TimeSpan.FromMinutes(channel.CheckIntervalMinutes);
                        
                        if (timeSinceLastCheck >= checkInterval)
                        {
                            channelsToProcess.Add(channel);
                        }
                    }
                    
                    _pipelineService.UpdateScrapeStatus($"📊 Channels due for checking: {channelsToProcess.Count}/{activeChannels.Count}");
                    
                    // 3. Process channels that are due for checking
                    if (channelsToProcess.Count > 0)
                    {
                        foreach (var channel in channelsToProcess)
                        {
                            if (_cancellationSource.Token.IsCancellationRequested)
                                break;
                            
                            await ProcessChannel(channel);
                            lastCheckTimes[channel.Id] = DateTime.Now;
                            await Task.Delay(TimeSpan.FromSeconds(2), _cancellationSource.Token);
                        }
                        
                        // Download any new videos that were found (non-blocking)
                        await DownloadAllPendingVideos();
                    }
                    else
                    {
                        _pipelineService.UpdateScrapeStatus("⏳ No channels due for checking at this time.");
                    }
                    
                    // 4. Wait before the next check cycle
                    var nextCheckTime = DateTime.Now.AddSeconds(60);
                    _pipelineService.UpdateScrapeStatus($"⏰ Monitoring active - Next channel check at {nextCheckTime:HH:mm:ss}");
                    await Task.Delay(TimeSpan.FromSeconds(60), _cancellationSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Automated processing was canceled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in automated processing: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                _isRunning = false;
            }
        }
        
        private async Task ProcessChannel(ChannelData channel)
        {
            try
            {
                _pipelineService.UpdateScrapeStatus($"📡 Processing channel: {channel.Name}");
                _pipelineService.UpdateScrapeStatus($"🔗 Channel URL: {channel.Url}");
                _pipelineService.UpdateScrapeStatus($"⏱️ Check interval: {channel.CheckIntervalMinutes} minutes");
                
                // 1. Monitor the channel for potential videos
                _pipelineService.UpdateScrapeStatus($"📡 Scanning {channel.Name} for videos...");
                var potentialVideos = await _scraper.MonitorChannel(channel.Url, 20, _pipelineService.UpdateScrapeStatus);
                _pipelineService.UpdateScrapeStatus($"📺 Found {potentialVideos.Count} potential videos on {channel.Name}");
                
                if (potentialVideos.Count == 0)
                {
                    _pipelineService.UpdateScrapeStatus($"✅ No videos found on {channel.Name}");
                    await _dbService.UpdateChannelLastCheckedAsync(channel.Id);
                    return;
                }
                
                _pipelineService.UpdateScrapeStatus($"🔍 Checking {potentialVideos.Count} videos from {channel.Name}...");
                
                // 2. Filter to get only videos that don't exist in our database
                int checkedCount = 0;
                int newVideoCount = 0;
                var newVideos = new List<VideoData>();
                
                foreach (var video in potentialVideos)
                {
                    checkedCount++;
                    if (_cancellationSource.Token.IsCancellationRequested) break;
                    
                    if (checkedCount % 5 == 0)
                    {
                        _pipelineService.UpdateScrapeStatus($"🔍 Checked {checkedCount}/{potentialVideos.Count} videos from {channel.Name}... (Found {newVideoCount} new)");
                    }
                    
                    try
                    {
                        bool exists = await _dbService.VideoExistsAsync(video.Url);
                        if (!exists)
                        {
                            _pipelineService.UpdateScrapeStatus($"🆕 Found new video: {video.Title} (Total new: {newVideoCount + 1})");
                            
                            // Save video to database immediately
                            try
                            {
                                await _dbService.SaveVideosAsync(new List<VideoData> { video });
                                _pipelineService.UpdateScrapeStatus($"💾 Saved video to database: {video.Title}");
                            }
                            catch (Exception saveEx)
                            {
                                _pipelineService.UpdateScrapeStatus($"❌ Error saving video to database: {saveEx.Message}");
                                continue; // Skip this video if we can't save it
                            }
                            
                            // Try to get video source URL but don't fail if it doesn't work
                            try
                            {
                                string? vidUrl = await _scraper.GetVideoSourceUrl(video.Url, _pipelineService.UpdateScrapeStatus);
                                if (!string.IsNullOrEmpty(vidUrl))
                                {
                                    video.VideoSourceUrl = vidUrl;
                                    // Update the video in database with source URL
                                    await _dbService.UpdateVideoSourceUrlAsync(video.Url, vidUrl);
                                    _pipelineService.UpdateScrapeStatus($"🔗 Updated source URL for: {video.Title}");
                                }
                                else
                                {
                                    _pipelineService.UpdateScrapeStatus($"⚠️ Could not get source URL for: {video.Title} (will try later)");
                                }
                            }
                            catch (Exception ex)
                            {
                                _pipelineService.UpdateScrapeStatus($"❌ Error getting source URL for {video.Title}: {ex.Message}");
                            }
                            
                            newVideos.Add(video);
                            newVideoCount++;
                            
                            await Task.Delay(500); // Brief delay between processing videos
                        }
                    }
                    catch (Exception ex)
                    {
                        _pipelineService.UpdateScrapeStatus($"❌ Error processing video {video.Url}: {ex.Message}");
                    }
                }
                
                if (newVideos.Count > 0)
                {
                    _pipelineService.UpdateScrapeStatus($"� Queuing {newVideos.Count} videos from {channel.Name} for download...");
                    
                    // Queue all videos for download in parallel (non-blocking)
                    // Videos are already saved to database individually above
                    _pipelineService.QueueVideosForProcessing(newVideos);
                    
                    _pipelineService.UpdateScrapeStatus($"✅ Queued {newVideos.Count} videos - downloading in parallel!");
                }
                else
                {
                    _pipelineService.UpdateScrapeStatus($"✅ No new videos found for {channel.Name}");
                }
                
                _pipelineService.UpdateScrapeStatus($"✅ Completed processing {channel.Name} - found {newVideoCount} new videos");
                await _dbService.UpdateChannelLastCheckedAsync(channel.Id);
                _pipelineService.UpdateScrapeStatus($"✅ Updated last checked time for {channel.Name}");
            }
            catch (Exception ex)
            {
                _pipelineService.UpdateScrapeStatus($"❌ Error processing {channel.Name}: {ex.Message}");
            }
        }
        
        private async Task DownloadAllPendingVideos()
        {
            try
            {
                _pipelineService.UpdateScrapeStatus("🔍 Checking for pending downloads...");
                var undownloadedVideos = await _dbService.GetUndownloadedVideosAsync();
                
                if (undownloadedVideos.Count == 0)
                {
                    _pipelineService.UpdateScrapeStatus("✅ No pending videos to download.");
                    return;
                }
                
                _pipelineService.UpdateScrapeStatus($"📥 Found {undownloadedVideos.Count} videos to queue for parallel workers.");
                
                // Queue videos for download but DON'T wait for completion
                // This allows scanning to continue while downloads are running
                _pipelineService.QueueVideosForProcessing(undownloadedVideos);
                
                _pipelineService.UpdateScrapeStatus($"✅ Queued {undownloadedVideos.Count} videos for download. Processing continues in background.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in DownloadAllPendingVideos: {ex.Message}");
                _pipelineService.UpdateScrapeStatus($"❌ Error queuing videos: {ex.Message}");
            }
        }
        
        public void StopAutomatedProcessing()
        {
            if (_isRunning)
            {
                Console.WriteLine("Stopping automated processing...");
                _cancellationSource.Cancel();
            }
        }
        
        public void Dispose()
        {
            StopAutomatedProcessing();
            _scraper?.Dispose();
            _pipelineService?.Dispose();
            _cancellationSource?.Dispose();
        }
    }
}