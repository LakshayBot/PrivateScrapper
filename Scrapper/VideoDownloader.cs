using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleScraper
{
    public class VideoDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly DatabaseService _dbService;
        private readonly string _downloadDirectory;

        public VideoDownloader(DatabaseService dbService, string downloadDirectory = null)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _dbService = dbService;
            
            // If no download directory specified, create a "downloads" folder in the current directory
            _downloadDirectory = downloadDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
            
            // Ensure the download directory exists
            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }
        }

        public async Task DownloadAllUndownloadedVideosAsync()
        {
            var videos = await _dbService.GetUndownloadedVideosAsync();
            
            if (videos.Count == 0)
            {
                Console.WriteLine("No videos to download.");
                return;
            }
            
            Console.WriteLine($"Found {videos.Count} videos to download.");
            
            for (int i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                Console.WriteLine($"Downloading video {i+1}/{videos.Count}: {video.Title}");
                
                await DownloadVideoAsync(video);
            }
        }

        public async Task DownloadVideoAsync(VideoData video)
        {
            if (string.IsNullOrEmpty(video.VideoSourceUrl))
            {
                Console.WriteLine($"No video URL available for {video.Title}");
                return;
            }
            
            try
            {
                // Create a valid filename from the video title
                string safeFileName = GetSafeFileName(video.Title);
                
                // Extract file extension from URL or default to .mp4
                string extension = Path.GetExtension(video.VideoSourceUrl);
                if (string.IsNullOrEmpty(extension) || extension == "." || extension.Length > 5)
                {
                    extension = ".mp4";
                }
                
                string fileName = $"{safeFileName}_{video.PostId}{extension}";
                string filePath = Path.Combine(_downloadDirectory, fileName);
                
                // Check if file already exists
                if (File.Exists(filePath))
                {
                    Console.WriteLine($"File already exists: {filePath}");
                    await _dbService.MarkVideoAsDownloadedAsync(video.Url, filePath);
                    return;
                }
                
                Console.WriteLine($"Downloading from {video.VideoSourceUrl}");
                Console.WriteLine($"Saving to: {filePath}");
                
                // Try to download the video, handle URL expiration if needed
                await DownloadWithRetryAsync(video, filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading video {video.Title}: {ex.Message}");
            }
        }

        private async Task DownloadWithRetryAsync(VideoData video, string filePath, int maxRetries = 2)
        {
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount <= maxRetries)
            {
                try
                {
                    // Stream the download to avoid loading the entire file in memory
                    using (var response = await _httpClient.GetAsync(video.VideoSourceUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // Check if the response was successful (200-299)
                        if (response.IsSuccessStatusCode)
                        {
                            long? totalBytes = response.Content.Headers.ContentLength;
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[8192]; // 8KB buffer
                                long totalBytesRead = 0;
                                int bytesRead;
                                
                                // Display progress while downloading
                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    
                                    // Update progress
                                    if (totalBytes.HasValue)
                                    {
                                        double progress = (double)totalBytesRead / totalBytes.Value;
                                        Console.Write($"\rDownload progress: {progress:P2} ({FormatFileSize(totalBytesRead)}/{FormatFileSize(totalBytes.Value)})");
                                    }
                                    else
                                    {
                                        Console.Write($"\rDownloaded: {FormatFileSize(totalBytesRead)}");
                                    }
                                }
                            }
                            
                            Console.WriteLine();  // Add new line after progress reporting
                            success = true;
                            
                            // Mark as downloaded in database
                            await _dbService.MarkVideoAsDownloadedAsync(video.Url, filePath);
                            Console.WriteLine($"Download complete: {filePath}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound) // 404 error
                        {
                            retryCount++;
                            if (retryCount <= maxRetries)
                            {
                                Console.WriteLine($"Video URL expired (404 Not Found). Attempting to refresh URL (Retry {retryCount}/{maxRetries})...");
                                
                                // Use the VideoScraper to get a fresh URL
                                bool urlRefreshed = await RefreshVideoSourceUrl(video);
                                
                                if (urlRefreshed)
                                {
                                    Console.WriteLine($"URL refreshed successfully. New URL: {video.VideoSourceUrl}");
                                    // We'll retry the download with the new URL in the next loop iteration
                                    await Task.Delay(1000); // Small delay before retry
                                }
                                else
                                {
                                    Console.WriteLine("Failed to refresh URL. Moving to next video.");
                                    break;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"Maximum retries reached. Unable to download {video.Title}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Failed to download video. Server returned {(int)response.StatusCode} {response.StatusCode}");
                            break;
                        }
                    }
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404"))
                {
                    // Handle 404 errors that might throw exceptions instead of returning a status code
                    retryCount++;
                    if (retryCount <= maxRetries)
                    {
                        Console.WriteLine($"Video URL expired (404 Not Found). Attempting to refresh URL (Retry {retryCount}/{maxRetries})...");
                        
                        // Use the VideoScraper to get a fresh URL
                        bool urlRefreshed = await RefreshVideoSourceUrl(video);
                        
                        if (urlRefreshed)
                        {
                            Console.WriteLine($"URL refreshed successfully. New URL: {video.VideoSourceUrl}");
                            // We'll retry the download with the new URL in the next loop iteration
                            await Task.Delay(1000); // Small delay before retry
                        }
                        else
                        {
                            Console.WriteLine("Failed to refresh URL. Moving to next video.");
                            break;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Maximum retries reached. Unable to download {video.Title}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during download: {ex.Message}");
                    break; // Don't retry on general errors
                }
            }
        }

        private async Task<bool> RefreshVideoSourceUrl(VideoData video)
        {
            try
            {
                // Create a temporary VideoScraper to fetch the updated URL
                using var tempScraper = new VideoScraper();
                await tempScraper.Initialize();
                
                // Get a fresh video source URL
                string newSourceUrl = await tempScraper.GetVideoSourceUrl(video.Url);
                
                if (!string.IsNullOrEmpty(newSourceUrl))
                {
                    // Update the video data object
                    video.VideoSourceUrl = newSourceUrl;
                    
                    // Update the database with the new URL
                    await _dbService.UpdateVideoSourceUrlAsync(video.Url, newSourceUrl);
                    
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing video URL: {ex.Message}");
                return false;
            }
        }

        private string GetSafeFileName(string fileName)
        {
            // Remove invalid file name characters
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            
            string safeFileName = Regex.Replace(fileName, invalidRegStr, "_");
            
            // Limit file name length
            if (safeFileName.Length > 100)
            {
                safeFileName = safeFileName.Substring(0, 100);
            }
            
            return safeFileName;
        }

        private string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal size = bytes;
            
            while (size >= 1024 && counter < suffixes.Length - 1)
            {
                size /= 1024;
                counter++;
            }
            
            return $"{size:n2} {suffixes[counter]}";
        }
    }
}