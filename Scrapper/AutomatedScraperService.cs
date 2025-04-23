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
        private readonly VideoDownloader _downloader;
        private readonly CancellationTokenSource _cancellationSource;
        private bool _isRunning;
        private readonly string _downloadDirectory;
        
        public AutomatedScraperService(string connectionString, string downloadDirectory = null)
        {
            _dbService = new DatabaseService(connectionString);
            _scraper = new VideoScraper();
            _downloadDirectory = downloadDirectory;
            _downloader = new VideoDownloader(_dbService, downloadDirectory);
            _cancellationSource = new CancellationTokenSource();
            
            // Initialize the logger
            Logger.Initialize(Path.Combine(_downloadDirectory, "logs"));
        }
        
        public async Task Initialize()
        {
            Console.WriteLine("Initializing automated scraper service...");
            await _dbService.InitializeDatabaseAsync();
            await _scraper.Initialize();
            Console.WriteLine("Automated scraper service initialized successfully.");
        }
        
        public async Task StartAutomatedProcessing()
        {
            if (_isRunning)
            {
                Console.WriteLine("Automated processing is already running.");
                return;
            }
            
            _isRunning = true;
            Console.WriteLine("Starting automated processing...");
            
            // Track the last check time for each channel
            var lastCheckTimes = new Dictionary<int, DateTime>();
            
            try
            {
                while (!_cancellationSource.Token.IsCancellationRequested)
                {
                    // 1. Get all active channels from the database
                    var activeChannels = await _dbService.GetActiveChannelsAsync();
                    
                    if (activeChannels.Count == 0)
                    {
                        Console.WriteLine("No active channels found. Waiting 5 minutes before checking again...");
                        await Task.Delay(TimeSpan.FromMinutes(5), _cancellationSource.Token);
                        continue;
                    }
                    
                    // 2. Check which channels need processing based on their intervals
                    var channelsToProcess = new List<ChannelData>();
                    var now = DateTime.Now;
                    
                    foreach (var channel in activeChannels)
                    {
                        // Get last check time, default to min value if never checked
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
                    
                    // 3. Process channels that are due for checking
                    if (channelsToProcess.Count > 0)
                    {
                        Console.WriteLine($"\nFound {channelsToProcess.Count} channels due for checking...");
                        
                        foreach (var channel in channelsToProcess)
                        {
                            if (_cancellationSource.Token.IsCancellationRequested)
                                break;
                                
                            await ProcessChannel(channel);
                            
                            // Update last check time after processing
                            lastCheckTimes[channel.Id] = DateTime.Now;
                            
                            // Add a small delay between processing channels
                            await Task.Delay(TimeSpan.FromSeconds(5), _cancellationSource.Token);
                        }
                        
                        // 4. Download any undownloaded videos after processing channels
                        await DownloadNewVideos();
                    }
                    else
                    {
                        Console.WriteLine("No channels due for checking at this time.");
                    }
                    
                    // 5. Wait a minute before the next check cycle
                    Console.WriteLine($"Waiting 1 minute before checking channel schedules again...");
                    await Task.Delay(TimeSpan.FromMinutes(1), _cancellationSource.Token);
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
                Console.WriteLine($"\n[{DateTime.Now}] Processing channel: {channel.Name} (ID: {channel.Id})");
                Console.WriteLine($"Check interval: {channel.CheckIntervalMinutes} minutes");
                
                // 1. Monitor the channel for potential videos
                var potentialVideos = await _scraper.MonitorChannel(channel.Url, 500);
                Console.WriteLine($"Found {potentialVideos.Count} potential videos on {channel.Name}");
                
                if (potentialVideos.Count == 0)
                {
                    // No videos found but still update the last checked timestamp
                    await _dbService.UpdateChannelLastCheckedAsync(channel.Id);
                    return;
                }
                
                // 2. Filter to get only videos that don't exist in our database
                int checkedCount = 0;
                int newVideoCount = 0;
                var newVideos = new List<VideoData>();
                
                foreach (var video in potentialVideos)
                {
                    checkedCount++;
                    
                    // Check if this video might cause a cancellation token exception
                    if (_cancellationSource.Token.IsCancellationRequested)
                        break;
                        
                    // Progress report
                    if (checkedCount % 10 == 0)
                    {
                        Console.WriteLine($"Checked {checkedCount}/{potentialVideos.Count} videos...");
                    }
                    
                    try
                    {
                        // Check if video exists in database
                        bool exists = await _dbService.VideoExistsAsync(video.Url);
                        
                        if (!exists)
                        {
                            // Get the .vid URL
                            string vidUrl = await _scraper.GetVideoSourceUrl(video.Url);
                            
                            if (!string.IsNullOrEmpty(vidUrl))
                            {
                                video.VideoSourceUrl = vidUrl;
                                newVideos.Add(video);
                                newVideoCount++;
                                Console.WriteLine($"New video found ({newVideoCount}): {video.Title}");
                            }
                            else
                            {
                                Console.WriteLine($"Could not get video URL for: {video.Title}");
                            }
                            
                            // Add a delay between requests to avoid overloading the server
                            await Task.Delay(1500);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue processing other videos
                        Console.WriteLine($"Error processing video {video.Url}: {ex.Message}");
                    }
                }
                
                // 3. Save new videos to database
                if (newVideos.Count > 0)
                {
                    Console.WriteLine($"Saving {newVideos.Count} new videos to database from {channel.Name}...");
                    await _dbService.SaveVideosAsync(newVideos);
                    Console.WriteLine($"Videos saved successfully for {channel.Name}");
                }
                else
                {
                    Console.WriteLine($"No new videos found for {channel.Name}");
                }
                
                // 4. Update last checked timestamp for this channel
                await _dbService.UpdateChannelLastCheckedAsync(channel.Id);
                Console.WriteLine($"Updated last checked time for {channel.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing channel {channel.Name}: {ex.Message}");
            }
        }
        
        private async Task DownloadNewVideos()
        {
            try
            {
                Console.WriteLine("\n[DOWNLOADING] Checking for new videos to download...");
                var undownloadedVideos = await _dbService.GetUndownloadedVideosAsync();
                
                if (undownloadedVideos.Count == 0)
                {
                    Console.WriteLine("No new videos to download.");
                    return;
                }
                
                Console.WriteLine($"Found {undownloadedVideos.Count} videos awaiting download.");
                Console.WriteLine($"Download location: {_downloadDirectory}");
                
                for (int i = 0; i < undownloadedVideos.Count; i++)
                {
                    // Check for cancellation
                    if (_cancellationSource.Token.IsCancellationRequested)
                    {
                        Console.WriteLine("Download process interrupted by user.");
                        break;
                    }
                    
                    var video = undownloadedVideos[i];
                    Console.WriteLine($"Downloading video {i+1}/{undownloadedVideos.Count}: {video.Title}");
                    
                    await _downloader.DownloadVideoAsync(video);
                    
                    // Add a small delay between downloads to prevent overwhelming the network
                    if (i < undownloadedVideos.Count - 1)
                    {
                        await Task.Delay(1000);
                    }
                }
                
                Console.WriteLine($"Download session complete. Processed {undownloadedVideos.Count} videos.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during download: {ex.Message}");
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
            _scraper?.Dispose();
            _cancellationSource?.Dispose();
        }
    }
}