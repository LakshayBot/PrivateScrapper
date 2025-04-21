using HtmlAgilityPack;
using PuppeteerSharp;
using System.Text.RegularExpressions;
using System.Text.Json;
using Npgsql;

namespace SimpleScraper
{
    public class VideoData
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string VideoSourceUrl { get; set; }
        public string PostId { get; set; }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Simple Video URL Scraper");
                Console.WriteLine("------------------------");

                // PostgreSQL connection string - Update with your database details
                string connectionString = GetConnectionString();

                // Initialize database
                var dbService = new DatabaseService(connectionString);
                Console.WriteLine("Initializing database...");
                //await dbService.InitializeDatabaseAsync();

                Console.Write("Enter the channel name or URL: ");
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

                var scraper = new VideoScraper();
                Console.WriteLine("Initializing browser...");
                await scraper.Initialize();

                Console.WriteLine($"Scraping up to {maxVideos} videos from channel: {channelInput}");
                var videos = await scraper.ScrapeChannel(channelInput, maxVideos);

                Console.WriteLine($"\nFound {videos.Count} videos with .vid URLs:");

                // Save to JSON file (keep existing functionality)
                string outputFile = "scraped_videos.json";
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonOutput = JsonSerializer.Serialize(videos, options);
                await File.WriteAllTextAsync(outputFile, jsonOutput);
                Console.WriteLine($"\nVideo data has been saved in JSON format to {Path.GetFullPath(outputFile)}");

                // Save to PostgreSQL database
                Console.WriteLine("\nSaving videos to PostgreSQL database...");
                await dbService.SaveVideosAsync(videos);
                Console.WriteLine($"Successfully saved {videos.Count} videos to database.");

                scraper.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static string GetConnectionString()
        {
            string defaultConnectionString = "Host=192.168.1.3;Database=scraper;Username=postgres;Password=postgres";

            Console.WriteLine("\nPostgreSQL Database Configuration");
            Console.WriteLine("--------------------------------");
            Console.Write($"Connection String (default: {defaultConnectionString}): ");

            string input = Console.ReadLine();
            return string.IsNullOrWhiteSpace(input) ? defaultConnectionString : input;
        }
    }

    public class VideoScraper : IDisposable
    {
        private readonly HttpClient _httpClient;
        private IBrowser _browser;
        private const string BaseUrl = "https://sxyprn.com";

        public VideoScraper()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        }

        public async Task Initialize()
        {
            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync();
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
            });
        }

        public async Task<List<VideoData>> ScrapeChannel(string channelInput, int maxVideos = 10)
        {
            string channelUrl = NormalizeChannelUrl(channelInput);

            Console.WriteLine($"Fetching channel page: {channelUrl}");
            var html = await _httpClient.GetStringAsync(channelUrl);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var videos = new List<VideoData>();
            var videoNodes = FindVideoNodes(doc);

            if (videoNodes == null || videoNodes.Count == 0)
            {
                Console.WriteLine("No videos found on this channel page.");
                return videos;
            }

            Console.WriteLine($"Found {videoNodes.Count} videos on channel page. Processing...");
            int processedCount = 0;

            foreach (var node in videoNodes)
            {
                if (videos.Count >= maxVideos)
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

                    processedCount++;
                    Console.WriteLine($"Processing video {processedCount}/{videoNodes.Count}: {title}");

                    var video = new VideoData
                    {
                        Title = title,
                        Url = postUrl,
                        PostId = postId
                    };

                    // Get .vid URL from network traffic
                    string vidUrl = await GetVideoSourceUrl(postUrl);
                    if (!string.IsNullOrEmpty(vidUrl))
                    {
                        video.VideoSourceUrl = vidUrl;
                        videos.Add(video);
                        Console.WriteLine($"✅ Found .vid URL: {vidUrl}");
                    }
                    else
                    {
                        Console.WriteLine("❌ No .vid URL found for this video");
                    }

                    // Add a small delay between requests
                    await Task.Delay(1500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing a video: {ex.Message}");
                }
            }

            return videos;
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

        private async Task<string> GetVideoSourceUrl(string postUrl)
        {
            try
            {
                Console.WriteLine($"Extracting .vid URL from: {postUrl}");
                using var page = await _browser.NewPageAsync();
                await page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });
                await page.SetRequestInterceptionAsync(true);

                string videoUrl = null;
                string postId = ExtractPostIdFromUrl(postUrl);

                page.Request += async (sender, e) =>
                {
                    var url = e.Request.Url;
                    if (url.Contains(".vid") && url.Contains(postId))
                    {
                        videoUrl = url;
                    }
                    await e.Request.ContinueAsync();
                };

                await page.GoToAsync(postUrl, new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle2 }
                });

                // Wait for the .vid request to appear (up to 10 seconds)
                int waitTime = 0;
                int maxWaitTime = 10000; // 10 seconds

                while (string.IsNullOrEmpty(videoUrl) && waitTime < maxWaitTime)
                {
                    await Task.Delay(500);
                    waitTime += 500;
                }

                return videoUrl;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting video source: {ex.Message}");
                return null;
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

        public void Dispose()
        {
            try
            {
                _browser?.CloseAsync().Wait();
                _browser?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing browser: {ex.Message}");
            }
            _httpClient?.Dispose();
        }
    }
}