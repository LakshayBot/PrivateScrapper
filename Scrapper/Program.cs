using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Text.Json;
using Npgsql;
using PuppeteerSharp;

namespace SimpleScraper
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                // Check if automated mode is requested via command line
                bool automatedMode = args.Length > 0 && (args[0] == "--auto" || args[0] == "-a");

                if (automatedMode)
                {
                    await RunAutomatedMode();
                    return;
                }

                Console.WriteLine("Simple Video URL Scraper");
                Console.WriteLine("------------------------");

                // PostgreSQL connection string
                string connectionString = GetConnectionString();
                
                // Initialize database
                var dbService = new DatabaseService(connectionString);
                Console.WriteLine("Initializing database...");
                await dbService.InitializeDatabaseAsync();

                Console.WriteLine("\nSelect an operation mode:");
                Console.WriteLine("1. One-time scraping");
                Console.WriteLine("2. Monitor channels from database");
                Console.WriteLine("3. Add new channel to database");
                Console.WriteLine("4. Download all undownloaded videos");
                Console.WriteLine("5. Start automated processing");
                Console.Write("Enter option (default: 1): ");
                
                string option = Console.ReadLine();
                
                switch (option)
                {
                    case "2": // Monitor channels from database
                        var scraper = new VideoScraper();
                        Console.WriteLine("Initializing browser...");
                        await scraper.Initialize();
                        await MonitorChannelsFromDatabase(scraper, dbService);
                        scraper.Dispose();
                        break;
                        
                    case "3": // Add new channel
                        await AddChannelToDatabase(dbService);
                        break;
                        
                    case "4": // Download all undownloaded videos
                        await DownloadAllUndownloadedVideos(dbService);
                        break;
                        
                    case "5": // Start automated processing
                        await RunAutomatedMode();
                        break;
                        
                    default: // One-time scraping (option 1 or invalid)
                        var oneTimeScraper = new VideoScraper();
                        Console.WriteLine("Initializing browser...");
                        await oneTimeScraper.Initialize();
                        await PerformOneTimeScraping(oneTimeScraper, dbService);
                        oneTimeScraper.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static async Task RunAutomatedMode()
        {
            Console.WriteLine("Starting in automated mode...");
            
            // Hardcoded connection string for automated mode
            string connectionString = "Host=192.168.1.3;Database=scraper;Username=postgres;Password=postgres";
            
            // Default download directory
            string downloadDir = Path.Combine(Directory.GetCurrentDirectory(), "downloads");
            
            // Telegram configuration (you can set these as environment variables or config file)
            string telegramBotToken = "7033069921:AAHKwPLvbSsSpkncIv-eEgs_jt56fZLTF9g";
            string telegramChatId = "-1002522142205";
            string telegramServerUrl =  "http://192.168.1.3:8081";
            
            // Initialize logger
            Logger.Initialize(Path.Combine(downloadDir, "logs"));
            Logger.Log($"Automated mode starting at {DateTime.Now}");
            Logger.Log($"Download directory: {downloadDir}");
            
            if (!string.IsNullOrEmpty(telegramBotToken) && !string.IsNullOrEmpty(telegramChatId))
            {
                Logger.Log($"Telegram upload enabled - Server: {telegramServerUrl}, Chat ID: {telegramChatId}");
            }
            else
            {
                Logger.Log("Telegram upload disabled - missing bot token or chat ID");
                Console.WriteLine("⚠️  Telegram upload disabled. Set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID environment variables to enable.");
            }
            
            // Create and start the automated service
            using var automatedService = new AutomatedScraperService(connectionString, downloadDir, 
                telegramBotToken, telegramChatId, telegramServerUrl);
            
            await automatedService.Initialize();
            
            Logger.Log("\nAutomated processing started");
            Logger.Log("Press Ctrl+C to stop.");
            
            // Set up console cancellation
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true;
                exitEvent.Set();
                Logger.Log("Shutdown requested by user. Stopping services...");
                automatedService.StopAutomatedProcessing();
            };

            // Start the automated processing
            var processingTask = automatedService.StartAutomatedProcessing();
            
            // Wait for Ctrl+C
            exitEvent.WaitOne();
            
            // Wait for processing to stop gracefully
            await processingTask;
            Logger.Log("Automated processing stopped. Cleaning up FlareSolverr session...");
            
            // Clean up the session manager
            FlareSolverrSessionManager.Instance.Dispose();
            
            Logger.Log("Cleanup complete. Exiting...");
            Logger.Close();
        }

        static async Task MonitorChannelsFromDatabase(VideoScraper scraper, DatabaseService dbService)
        {
            var activeChannels = await dbService.GetActiveChannelsAsync();
            
            if (activeChannels.Count == 0)
            {
                Console.WriteLine("No active channels found in the database. Please add channels first.");
                return;
            }
            
            Console.WriteLine($"Found {activeChannels.Count} active channels to monitor:");
            foreach (var channel in activeChannels)
            {
                Console.WriteLine($"- {channel.Name} (Check every {channel.CheckIntervalMinutes} minutes)");
            }
            
            // Create a channel monitor for each channel
            var monitors = new List<ChannelMonitor>();
            
            foreach (var channel in activeChannels)
            {
                var monitor = new ChannelMonitor(
                    scraper, 
                    dbService, 
                    channel.Url, 
                    TimeSpan.FromMinutes(channel.CheckIntervalMinutes),
                    channel.Id
                );
                
                await monitor.StartMonitoring();
                monitors.Add(monitor);
            }
            
            Console.WriteLine("\nMonitoring all channels. Press 'Q' to stop and exit.");
            
            // Keep monitoring until user presses Q
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        foreach (var monitor in monitors)
                        {
                            monitor.StopMonitoring();
                        }
                        break;
                    }
                }
                
                await Task.Delay(100);
            }
            
            foreach (var monitor in monitors)
            {
                monitor.Dispose();
            }
        }

        static async Task AddChannelToDatabase(DatabaseService dbService)
        {
            Console.Write("\nEnter the channel name: ");
            string channelName = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(channelName))
            {
                Console.WriteLine("Error: Channel name cannot be empty.");
                return;
            }
            
            // Construct the URL from the channel name
            string channelUrl = NormalizeChannelInput(channelName);
            
            Console.Write("Check interval in minutes (default: 60): ");
            string intervalInput = Console.ReadLine();
            int intervalMinutes = 60;
            
            if (!string.IsNullOrWhiteSpace(intervalInput) && int.TryParse(intervalInput, out int parsedInterval))
            {
                intervalMinutes = Math.Max(1, parsedInterval); // Minimum 1 minute
            }
            
            await dbService.SaveChannelAsync(channelName, channelUrl, intervalMinutes);
            Console.WriteLine($"Channel '{channelName}' added successfully with URL: {channelUrl}");
        }

        static async Task DownloadAllUndownloadedVideos(DatabaseService dbService)
        {
            Console.Write("\nEnter download directory (leave empty for default): ");
            string downloadDir = Console.ReadLine();
            
            Console.Write("Number of concurrent downloads (default: 3): ");
            string concurrentInput = Console.ReadLine();
            int maxConcurrentDownloads = 3;
            
            if (!string.IsNullOrWhiteSpace(concurrentInput) && int.TryParse(concurrentInput, out int parsedConcurrent))
            {
                maxConcurrentDownloads = Math.Max(1, Math.Min(parsedConcurrent, 6)); // Limit to 1-6 concurrent downloads
            }
            
            // Get all undownloaded videos
            var videos = await dbService.GetUndownloadedVideosAsync();
            
            if (videos.Count == 0)
            {
                Console.WriteLine("No videos to download.");
                return;
            }
            
            Console.WriteLine($"Found {videos.Count} videos to download using {maxConcurrentDownloads} concurrent workers.");
            
            // Create pipeline service without Telegram upload (download only)
            var pipelineService = new DownloadUploadPipelineService(
                dbService, 
                string.IsNullOrWhiteSpace(downloadDir) ? null : downloadDir,
                null, // No bot token
                null, // No chat ID  
                null, // No telegram server URL
                maxConcurrentDownloads,
                0  // No upload workers
            );
            
            try
            {
                // Initialize the shared browser
                await pipelineService.InitializeAsync();
                
                // Start the workers
                pipelineService.StartWorkers();
                
                // Update status to show we're processing videos
                pipelineService.UpdateScrapeStatus($"📥 Processing {videos.Count} videos for download...");
                
                // Check and update video source URLs for any videos that don't have them
                var videoScraper = new VideoScraper();
                await videoScraper.Initialize();
                
                int processedCount = 0;
                foreach (var video in videos)
                {
                    processedCount++;
                    
                    if (string.IsNullOrEmpty(video.VideoSourceUrl))
                    {
                        pipelineService.UpdateScrapeStatus($"🔗 Getting download URL for video {processedCount}/{videos.Count}: {video.Title}");
                        try
                        {
                            string vidUrl = await videoScraper.GetVideoSourceUrl(video.Url);
                            if (!string.IsNullOrEmpty(vidUrl))
                            {
                                video.VideoSourceUrl = vidUrl;
                                await dbService.UpdateVideoSourceUrlAsync(video.Url, vidUrl);
                                Console.WriteLine($"✅ Got source URL for: {video.Title}");
                            }
                            else
                            {
                                Console.WriteLine($"⚠️ Could not get source URL for: {video.Title}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error getting source URL for {video.Title}: {ex.Message}");
                        }
                    }
                }
                
                videoScraper.Dispose();
                
                // Now start the downloads
                pipelineService.UpdateScrapeStatus($"📥 Starting downloads for {videos.Count} videos...");
                
                // Process all videos
                await pipelineService.ProcessVideosAsync(videos);
                
                pipelineService.UpdateScrapeStatus("✅ All downloads completed!");
                Console.WriteLine($"\n✅ Completed downloading {videos.Count} videos.");
            }
            finally
            {
                pipelineService.Dispose();
            }
        }

        // Extracted method to normalize channel input to a URL
        static string NormalizeChannelInput(string channelInput)
        {
            const string BaseUrl = "https://sxyprn.com";
            
            // Handle spaces in channel names - the site uses hyphens for spaces
            channelInput = channelInput.Trim();

            // If user just entered a channel name without the full URL
            if (!channelInput.Contains("/") && !channelInput.Contains("."))
            {
                // Replace any spaces with hyphens
                channelInput = channelInput.Replace(" ", "-");
                // Add the .html extension
                if (!channelInput.EndsWith(".html"))
                {
                    channelInput = channelInput + ".html";
                }
            }

            // Ensure URL is absolute
            if (!channelInput.StartsWith("http"))
            {
                channelInput = BaseUrl + (channelInput.StartsWith("/") ? channelInput : "/" + channelInput);
            }

            return channelInput;
        }

        static async Task PerformOneTimeScraping(VideoScraper scraper, DatabaseService dbService)
        {
            Console.Write("\nEnter the channel name or URL: ");
            string channelInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(channelInput))
            {
                Console.WriteLine("Error: Channel name cannot be empty.");
                return;
            }

            Console.Write("How many videos would you like to scrape? (default: 10): ");
            string maxVideosInput = Console.ReadLine();
            int maxVideos = 10;
            
            if (!string.IsNullOrWhiteSpace(maxVideosInput) && int.TryParse(maxVideosInput, out int parsedMaxVideos))
            {
                maxVideos = parsedMaxVideos;
            }
            
            Console.WriteLine($"Scraping up to {maxVideos} videos from channel: {channelInput}");
            var videos = await scraper.ScrapeChannel(channelInput, maxVideos);

            Console.WriteLine($"\nFound {videos.Count} videos with .vid URLs:");

            // Save to JSON file
            string outputFile = "scraped_videos.json";
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonOutput = JsonSerializer.Serialize(videos, options);
            await File.WriteAllTextAsync(outputFile, jsonOutput);
            Console.WriteLine($"\nVideo data has been saved in JSON format to {Path.GetFullPath(outputFile)}");

            // Save to PostgreSQL database
            Console.WriteLine("\nSaving videos to PostgreSQL database...");
            await dbService.SaveVideosAsync(videos);
            Console.WriteLine($"Successfully saved {videos.Count} videos to database.");
            
            // Ask user if they want to download the videos now
            Console.Write("\nDo you want to download these videos now? (Y/N): ");
            string downloadNow = Console.ReadLine();
            
            if (downloadNow?.Trim().ToUpper() == "Y")
            {
                Console.Write("Enter download directory (leave empty for default): ");
                string downloadDir = Console.ReadLine();
                
                var downloader = new VideoDownloader(dbService, 
                    string.IsNullOrWhiteSpace(downloadDir) ? null : downloadDir, scraper);
                
                foreach (var video in videos)
                {
                    await downloader.DownloadVideoAsync(video);
                }
            }
        }

        static string GetConnectionString()
        {
            string defaultConnectionString = "Host=192.168.1.3;Database=scraper;Username=postgres;Password=postgres";

            Console.WriteLine("\nPostgreSQL Database Configuration");
            Console.WriteLine("--------------------------------");
            Console.Write($"Connection String (default: {defaultConnectionString}): ");

            string input = defaultConnectionString;
            return string.IsNullOrWhiteSpace(input) ? defaultConnectionString : input;
        }
    }

    public class VideoScraper : IDisposable
    {
        private FlareSolverrClient? _flareSolverr;
        private const string BaseUrl = "https://sxyprn.com";
        
        public VideoScraper()
        {
            // FlareSolverr client will be obtained from session manager when needed
        }
        
        public async Task Initialize()
        {
            Console.WriteLine("Initializing FlareSolverr connection...");
            
            // Get shared client from session manager
            _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
            
            // Test with a simple request to verify everything works
            try
            {
                await _flareSolverr.GetPageContentAsync(BaseUrl);
                Console.WriteLine("✅ FlareSolverr initialized and tested successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ FlareSolverr test failed: {ex.Message}");
                throw;
            }
        }

        public async Task<List<VideoData>> ScrapeChannel(string channelInput, int maxVideos = 10)
        {
            string baseChannelUrl = NormalizeChannelUrl(channelInput);
            var allVideos = new List<VideoData>();
            int currentPage = 1;
            int totalPages = 1;
            
            Console.WriteLine($"Starting to scrape channel: {baseChannelUrl}");

            try
            {
                // Ensure we have a valid FlareSolverr client
                if (_flareSolverr == null)
                {
                    _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                }

                while (allVideos.Count < maxVideos && currentPage <= totalPages)
                {
                    int offset = (currentPage - 1) * 30;
                    string channelUrl = currentPage == 1 
                        ? baseChannelUrl 
                        : $"{GetBaseUrlWithoutPage(baseChannelUrl)}?page={offset}";
                        
                    Console.WriteLine($"Fetching page {currentPage} (target: {totalPages}) with FlareSolverr: {channelUrl}");
                    
                    string html = await GetPageContentWithRetry(channelUrl);

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    
                    if (currentPage == 1)
                    {
                        totalPages = ExtractTotalPages(doc, baseChannelUrl);
                        Console.WriteLine($"Found a total of {totalPages} pages for channel {baseChannelUrl}");
                    }

                    var videoNodes = FindVideoNodes(doc);
                    if (videoNodes == null || videoNodes.Count == 0)
                    {
                        Console.WriteLine($"No videos found on page {currentPage} of {channelUrl}. Moving to next page or finishing.");
                        currentPage++;
                        if (currentPage > totalPages && totalPages > 0)
                             break;
                        continue;
                    }

                    Console.WriteLine($"Found {videoNodes.Count} videos on page {currentPage}. Processing...");
                    int processedCountOnPage = 0;

                    foreach (var node in videoNodes)
                    {
                        if (allVideos.Count >= maxVideos)
                            break;

                        try
                        {
                            var title = ExtractTitle(node);
                            var postUrl = ExtractUrl(node);

                            if (string.IsNullOrEmpty(postUrl) || !postUrl.Contains("/post/"))
                            {
                                continue;
                            }

                            if (!postUrl.StartsWith("http"))
                            {
                                postUrl = postUrl.StartsWith("/") ? BaseUrl + postUrl : BaseUrl + "/" + postUrl;
                            }

                            var postId = ExtractPostIdFromUrl(postUrl);
                            processedCountOnPage++;
                            Console.WriteLine($"Processing video {processedCountOnPage}/{videoNodes.Count} on page {currentPage}: {title.Substring(0, Math.Min(title.Length, 50))}");

                            var video = new VideoData
                            {
                                Title = title,
                                Url = postUrl,
                                PostId = postId
                            };

                            string vidUrl = await GetVideoSourceUrl(postUrl);
                            if (!string.IsNullOrEmpty(vidUrl))
                            {
                                video.VideoSourceUrl = vidUrl;
                                allVideos.Add(video);
                                Console.WriteLine($"✅ Found .vid URL for: {title.Substring(0, Math.Min(title.Length, 50))}");
                            }
                            else
                            {
                                Console.WriteLine($"❌ No .vid URL found for: {title.Substring(0, Math.Min(title.Length, 50))}");
                            }
                            await Task.Delay(1000); // Delay between processing individual videos
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing a video item: {ex.Message}");
                        }
                    }
                    currentPage++;
                    if (currentPage <= totalPages && allVideos.Count < maxVideos)
                    {
                        Console.WriteLine($"Waiting 2 seconds before fetching next page...");
                        await Task.Delay(2000); 
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scraping channel {baseChannelUrl}: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

            Console.WriteLine($"Channel scraping complete for {baseChannelUrl}. Found {allVideos.Count} videos.");
            return allVideos;
        }

        public async Task<List<VideoData>> MonitorChannel(string channelInput, int maxVideosToDiscover = 50)
        {
            string baseChannelUrl = NormalizeChannelUrl(channelInput);
            var discoveredVideos = new List<VideoData>();
            int currentPage = 1;
            int totalPages = 1; 
            int maxPagesToCheck = 10; // Limit how many pages to check during monitoring
            
            Console.WriteLine($"Monitoring channel: {baseChannelUrl} (checking up to {maxPagesToCheck} pages or {maxVideosToDiscover} videos)");

            try
            {
                // Ensure we have a valid FlareSolverr client
                if (_flareSolverr == null)
                {
                    _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                }

                while (discoveredVideos.Count < maxVideosToDiscover && currentPage <= totalPages && currentPage <= maxPagesToCheck)
                {
                    int offset = (currentPage - 1) * 30;
                    string channelUrl = currentPage == 1 
                        ? baseChannelUrl 
                        : $"{GetBaseUrlWithoutPage(baseChannelUrl)}?page={offset}";
                        
                    Console.WriteLine($"Monitoring page {currentPage} (target: {Math.Min(totalPages, maxPagesToCheck)}) with FlareSolverr: {channelUrl}");
                    
                    string html = await GetPageContentWithRetry(channelUrl);

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    
                    if (currentPage == 1)
                    {
                        totalPages = ExtractTotalPages(doc, baseChannelUrl);
                        Console.WriteLine($"Channel {baseChannelUrl} has {totalPages} total pages. Will check up to {Math.Min(totalPages, maxPagesToCheck)}.");
                    }

                    var videoNodes = FindVideoNodes(doc);
                    if (videoNodes == null || videoNodes.Count == 0)
                    {
                        Console.WriteLine($"No videos found on monitored page {currentPage} of {channelUrl}.");
                        currentPage++;
                         if (currentPage > totalPages && totalPages > 0) break;
                        continue;
                    }

                    Console.WriteLine($"Found {videoNodes.Count} video items on monitored page {currentPage}.");

                    foreach (var node in videoNodes)
                    {
                        if (discoveredVideos.Count >= maxVideosToDiscover)
                            break;

                        try
                        {
                            var title = ExtractTitle(node);
                            var postUrl = ExtractUrl(node);

                            if (string.IsNullOrEmpty(postUrl) || !postUrl.Contains("/post/"))
                            {
                                continue;
                            }

                            if (!postUrl.StartsWith("http"))
                            {
                                postUrl = postUrl.StartsWith("/") ? BaseUrl + postUrl : BaseUrl + "/" + postUrl;
                            }
                            var postId = ExtractPostIdFromUrl(postUrl);

                            // For monitoring, we just collect basic info. The .vid URL will be fetched later if it's new.
                            discoveredVideos.Add(new VideoData
                            {
                                Title = title,
                                Url = postUrl,
                                PostId = postId
                            });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing a video node during monitoring: {ex.Message}");
                        }
                    }
                    currentPage++;
                    if (currentPage <= Math.Min(totalPages, maxPagesToCheck) && discoveredVideos.Count < maxVideosToDiscover)
                    {
                        await Task.Delay(1500); // Delay before fetching next page in monitoring
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error monitoring channel {baseChannelUrl}: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

            Console.WriteLine($"Monitoring scan complete for {baseChannelUrl}. Discovered {discoveredVideos.Count} potential videos.");
            return discoveredVideos;
        }

        private string NormalizeChannelUrl(string channelInput)
        {
            // Handle spaces in channel names - the site uses hyphens for spaces
            channelInput = channelInput.Trim();

            // If user just entered a channel name without the full URL
            if (!channelInput.Contains("/") && !channelInput.Contains("."))
            {
                // Replace any spaces with hyphens
                channelInput = channelInput.Replace(" ", "-");
                // Add the .html extension
                if (!channelInput.EndsWith(".html"))
                {
                    channelInput = channelInput + ".html";
                }
            }

            // Ensure URL is absolute
            if (!channelInput.StartsWith("http"))
            {
                channelInput = BaseUrl + (channelInput.StartsWith("/") ? channelInput : "/" + channelInput);
            }

            return channelInput;
        }

        private HtmlNodeCollection FindVideoNodes(HtmlDocument doc)
        {
            var possibleSelectors = new[]
            {
                "//div[contains(@class, 'post')]",
                "//div[contains(@class, 'video')]",
                "//div[contains(@class, 'thumb')]",
                "//div[contains(@class, 'item')]",
                "//div[@class='post-container']//div",
                "//div[contains(@class, 'postitem')]",
                "//div[contains(@class, 'content')]//div[contains(@class, 'post')]",
                "//a[contains(@href, '/post/')]/..", // Look for links to posts
                "//div[contains(@class, 'channel-video')]", // Channel-specific selector
                "//div[contains(@class, 'channel-post')]"   // Channel-specific selector
            };

            foreach (var selector in possibleSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null && nodes.Count > 0)
                {
                    Console.WriteLine($"Found {nodes.Count} video nodes with selector: {selector}");
                    return nodes;
                }
            }
            
            return null;
        }

        public async Task<string> GetVideoSourceUrl(string postUrl)
        {
            try
            {
                Console.WriteLine($"Extracting .vid URL from: {postUrl}");
                
                string postId = ExtractPostIdFromUrl(postUrl);
                
                // Ensure we have a valid FlareSolverr client
                if (_flareSolverr == null)
                {
                    _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                }
                
                // Use FlareSolverr to get the video URL with retry logic
                var videoUrl = await GetVideoUrlWithRetry(postUrl, postId);
                
                if (!string.IsNullOrEmpty(videoUrl))
                {
                    Console.WriteLine($"✅ Successfully found video URL: {videoUrl}");
                    return videoUrl;
                }
                
                Console.WriteLine($"❌ No video URL found for {postUrl}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting video URL from {postUrl}: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetVidUrlViaPuppeteer(string postUrl, string postId)
        {
            try
            {
                Console.WriteLine($"🎬 Using Puppeteer to click video and capture network requests for {postId}");
                
                // Import PuppeteerSharp for this specific task
                using var browser = await PuppeteerSharp.Puppeteer.LaunchAsync(new PuppeteerSharp.LaunchOptions
                {
                    Headless = false,
                    Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
                });
                
                using var page = await browser.NewPageAsync();
                
                // Set up network request interception to capture .vid URLs
                var vidUrl = "";
                page.Response += (sender, e) =>
                {
                    if (e.Response.Url.Contains(postId) && e.Response.Url.EndsWith(".vid"))
                    {
                        Console.WriteLine($"🎯 Captured .vid URL from network: {e.Response.Url}");
                        vidUrl = e.Response.Url;
                    }
                };
                
                // Navigate to the post page
                Console.WriteLine($"🌐 Navigating to post page: {postUrl}");
                await page.GoToAsync(postUrl, new PuppeteerSharp.NavigationOptions
                {
                    WaitUntil = new[] { PuppeteerSharp.WaitUntilNavigation.Networkidle2 },
                    Timeout = 30000
                });
                
                // Wait for the video element to be present
                await page.WaitForSelectorAsync("video#player_el, video.player_el", new PuppeteerSharp.WaitForSelectorOptions
                {
                    Timeout = 10000
                });
                
                Console.WriteLine("📹 Found video player element, clicking to trigger network requests...");
                
                // Click on the video to trigger the .vid URL request
                await page.ClickAsync("video#player_el, video.player_el");
                
                // Wait a bit for the network request to complete
                await Task.Delay(3000);
                
                // Try to play the video if clicking didn't work
                if (string.IsNullOrEmpty(vidUrl))
                {
                    Console.WriteLine("� Trying to play video via JavaScript...");
                    await page.EvaluateExpressionAsync(@"
                        const video = document.querySelector('video#player_el, video.player_el');
                        if (video) {
                            video.play();
                        }
                    ");
                    
                    // Wait for network requests
                    await Task.Delay(3000);
                }
                
                // If still no URL, try triggering load event
                if (string.IsNullOrEmpty(vidUrl))
                {
                    Console.WriteLine("🔄 Trying to trigger video load event...");
                    await page.EvaluateExpressionAsync(@"
                        const video = document.querySelector('video#player_el, video.player_el');
                        if (video) {
                            video.load();
                            video.currentTime = 0.1;
                        }
                    ");
                    
                    // Wait for network requests
                    await Task.Delay(2000);
                }
                
                if (!string.IsNullOrEmpty(vidUrl))
                {
                    Console.WriteLine($"✅ Successfully captured .vid URL: {vidUrl}");
                    return vidUrl;
                }
                
                Console.WriteLine("❌ No .vid URL captured from network requests");
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error using Puppeteer to get .vid URL: {ex.Message}");
                return "";
            }
        }

        private async Task<string> TryGetActualVidUrl(string postUrl, string postId, string directVidUrl)
        {
            try
            {
                Console.WriteLine($"Attempting to get actual .vid URL via: {directVidUrl}");
                
                // Try to follow the redirect chain to get the final trafficdeposit.com URL
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                httpClient.DefaultRequestHeaders.Add("Referer", postUrl);
                httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
                
                var response = await httpClient.GetAsync(directVidUrl);
                
                // Check if we got a redirect response
                if (response.StatusCode == System.Net.HttpStatusCode.Found || 
                    response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                    response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect)
                {
                    var location = response.Headers.Location?.ToString();
                    if (!string.IsNullOrEmpty(location) && location.Contains("trafficdeposit.com") && location.EndsWith(".vid"))
                    {
                        Console.WriteLine($"✅ Found actual .vid URL via redirect: {location}");
                        return location;
                    }
                }
                
                // If direct response contains the URL
                var responseContent = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(responseContent))
                {
                    // Check if the response itself is the video URL
                    if (responseContent.StartsWith("http") && responseContent.Contains("trafficdeposit.com") && responseContent.EndsWith(".vid"))
                    {
                        Console.WriteLine($"✅ Found actual .vid URL in response: {responseContent.Trim()}");
                        return responseContent.Trim();
                    }
                    
                    // If we got a successful response, the direct URL might work
                    if (directVidUrl.Contains("trafficdeposit.com") || response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"✅ Direct .vid URL appears to be valid: {directVidUrl}");
                        return directVidUrl;
                    }
                }
                
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error trying to get actual .vid URL: {ex.Message}");
                return "";
            }
        }

        private string ExtractTitle(HtmlNode node)
        {
            var titleSelectors = new[]
            {
                ".//span[contains(@class, 'post-title')]",
                ".//span[contains(@class, 'title')]",
                ".//div[contains(@class, 'title')]",
                ".//h3",
                ".//h2",
                ".//a[@title]",
                ".//img[@alt]"
            };

            foreach (var selector in titleSelectors)
            {
                var titleNode = node.SelectSingleNode(selector);
                if (titleNode != null)
                {
                    if (selector.Contains("@title"))
                        return titleNode.GetAttributeValue("title", "").Trim();
                    else if (selector.Contains("@alt"))
                        return titleNode.GetAttributeValue("alt", "").Trim();
                    else
                        return titleNode.InnerText.Trim();
                }
            }

            return "Untitled";
        }

        private string ExtractUrl(HtmlNode node)
        {
            var linkNode = node.SelectSingleNode(".//a");
            if (linkNode != null)
            {
                return linkNode.GetAttributeValue("href", "");
            }
            return "";
        }

        private string ExtractPostIdFromUrl(string url)
        {
            var match = Regex.Match(url, @"/post/([^/\.]+)");
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }
            return string.Empty;
        }

        private int ExtractTotalPages(HtmlDocument doc, string baseUrl)
        {
            try
            {
                // Look for pagination controls
                var paginationDiv = doc.DocumentNode.SelectSingleNode("//div[@id='center_control']");
                if (paginationDiv == null)
                {
                    Console.WriteLine("No pagination controls found. Assuming only 1 page.");
                    return 1;
                }

                // Find all pagination links
                var paginationLinks = paginationDiv.SelectNodes(".//a[@href]");
                if (paginationLinks == null || paginationLinks.Count == 0)
                {
                    return 1;
                }

                // Extract page offsets and find the total number of pages
                int maxOffset = 0;
                int itemsPerPage = 30; // Default items per page is 30

                foreach (var link in paginationLinks)
                {
                    string href = link.GetAttributeValue("href", "");
                    var match = Regex.Match(href, @"page=(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int offset))
                    {
                        maxOffset = Math.Max(maxOffset, offset);
                    }
                }

                // If we found the maximum offset, calculate the total pages
                // Adding 1 to include the first page (which has offset 0)
                int totalPages = (maxOffset / itemsPerPage) + 1;
                Console.WriteLine($"Found pagination with max offset {maxOffset}, calculating {totalPages} total pages");
                return totalPages;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting total pages: {ex.Message}");
                return 1; // Default to 1 page on error
            }
        }

        private string GetBaseUrlWithoutPage(string url)
        {
            // Remove any existing ?page= parameter
            int queryIndex = url.IndexOf("?page=");
            if (queryIndex > 0)
            {
                return url.Substring(0, queryIndex);
            }
            return url;
        }

        private async Task<string> GetPageContentWithRetry(string url, int maxRetries = 2)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (_flareSolverr == null)
                    {
                        _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                    }
                    
                    return await _flareSolverr.GetPageContentAsync(url);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Attempt {attempt + 1} failed for {url}: {ex.Message}");
                    
                    if (attempt < maxRetries)
                    {
                        Console.WriteLine("Renewing FlareSolverr session and retrying...");
                        await FlareSolverrSessionManager.Instance.RenewSessionAsync();
                        _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                        await Task.Delay(2000); // Wait before retry
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            
            throw new Exception($"Failed to get page content after {maxRetries + 1} attempts");
        }

        private async Task<string> GetVideoUrlWithRetry(string postUrl, string postId, int maxRetries = 2)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (_flareSolverr == null)
                    {
                        _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                    }
                    
                    return await _flareSolverr.GetVideoUrlFromPage(postUrl, postId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Video URL extraction attempt {attempt + 1} failed for {postUrl}: {ex.Message}");
                    
                    if (attempt < maxRetries)
                    {
                        Console.WriteLine("Renewing FlareSolverr session and retrying...");
                        await FlareSolverrSessionManager.Instance.RenewSessionAsync();
                        _flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
                        await Task.Delay(2000); // Wait before retry
                    }
                    else
                    {
                        Console.WriteLine($"Failed to get video URL after {maxRetries + 1} attempts");
                        return null;
                    }
                }
            }
            
            return null;
        }

        public void Dispose()
        {
            // Don't dispose the FlareSolverr client directly since it's managed by the session manager
            // The session manager will handle cleanup when the application shuts down
        }
    }
}