using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleScraper
{
    public class ChannelMonitor : IDisposable
    {
        private readonly VideoScraper _scraper;
        private readonly DatabaseService _dbService;
        private readonly string _channelUrl;
        private readonly TimeSpan _checkInterval;
        private readonly int _channelId;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;
        private HashSet<string> _knownVideoIds = new HashSet<string>();
        private int _videosAdded = 0;

        public ChannelMonitor(VideoScraper scraper, DatabaseService dbService, string channelUrl, TimeSpan checkInterval, int channelId = 0)
        {
            _scraper = scraper;
            _dbService = dbService;
            _channelUrl = channelUrl;
            _checkInterval = checkInterval;
            _channelId = channelId;
        }

        public async Task StartMonitoring()
        {
            await InitializeKnownVideos();
            _cancellationTokenSource = new CancellationTokenSource();
            _monitoringTask = MonitorChannelAsync(_cancellationTokenSource.Token);
            
            Console.WriteLine($"Started monitoring channel: {_channelUrl}");
            Console.WriteLine($"Checking for new videos every {_checkInterval.TotalMinutes} minute(s)");
            Console.WriteLine("Press 'Q' to stop monitoring and exit");
        }

        public void StopMonitoring()
        {
            _cancellationTokenSource?.Cancel();
            _monitoringTask?.Wait();
            
            Console.WriteLine($"\nMonitoring stopped. Found {_videosAdded} new videos.");
        }

        private async Task InitializeKnownVideos()
        {
            // Get all existing video URLs from the database to avoid duplicates
            var existingVideos = await _dbService.GetAllVideosAsync();
            foreach (var video in existingVideos)
            {
                if (!string.IsNullOrEmpty(video.PostId))
                {
                    _knownVideoIds.Add(video.PostId);
                }
            }
            
            Console.WriteLine($"Loaded {_knownVideoIds.Count} known videos from database");
        }

        private async Task MonitorChannelAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine($"\n[{DateTime.Now}] Checking channel {_channelUrl} for new videos...");
                    
                    // First, get the basic info about the latest videos
                    var latestVideosInfo = await _scraper.MonitorChannel(_channelUrl, 300);
                    
                    // Update the last checked timestamp in the database if we have a valid channel ID
                    if (_channelId > 0)
                    {
                        await _dbService.UpdateChannelLastCheckedAsync(_channelId);
                    }
                    
                    // Filter out videos we've already seen
                    var newVideosInfo = latestVideosInfo
                        .Where(v => !string.IsNullOrEmpty(v.PostId) && !_knownVideoIds.Contains(v.PostId))
                        .ToList();
                    
                    int newVideosCount = 0;
                    
                    if (newVideosInfo.Count > 0)
                    {
                        Console.WriteLine($"Found {newVideosInfo.Count} potential new videos - fetching details...");
                        
                        var newVideos = new List<VideoData>();
                        
                        // Now fetch the full details only for the new videos
                        foreach (var videoInfo in newVideosInfo)
                        {
                            try
                            {
                                // Get the video source URL
                                string vidUrl = await _scraper.GetVideoSourceUrl(videoInfo.Url);
                                if (!string.IsNullOrEmpty(vidUrl))
                                {
                                    videoInfo.VideoSourceUrl = vidUrl;
                                    newVideos.Add(videoInfo);
                                    newVideosCount++;
                                    
                                    // Add to known videos immediately to avoid duplicates
                                    _knownVideoIds.Add(videoInfo.PostId);
                                    
                                    Console.WriteLine($"New video: {videoInfo.Title}");
                                    Console.WriteLine($"URL: {videoInfo.Url}");
                                    Console.WriteLine($"Source: {videoInfo.VideoSourceUrl}");
                                    Console.WriteLine(new string('-', 40));
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error fetching video details: {ex.Message}");
                            }
                            
                            // Small delay to avoid hammering the server
                            await Task.Delay(1000, cancellationToken);
                        }
                        
                        if (newVideos.Count > 0)
                        {
                            // Save new videos to database
                            await _dbService.SaveVideosAsync(newVideos);
                            _videosAdded += newVideosCount;
                            Console.WriteLine($"Saved {newVideosCount} new videos to database");
                        }
                        else
                        {
                            Console.WriteLine("No valid new videos found");
                        }
                    }
                    else
                    {
                        Console.WriteLine("No new videos found");
                    }
                    
                    // Wait for the next check interval
                    Console.WriteLine($"Next check in {_checkInterval.TotalMinutes} minute(s)...");
                    await Task.Delay(_checkInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Monitoring was canceled, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during monitoring: {ex.Message}");
                    // Wait a bit before retrying to avoid hammering the server on errors
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }
}