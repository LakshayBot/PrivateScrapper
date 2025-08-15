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
        private readonly VideoScraper? _videoScraper; // Optional shared scraper instance

        public VideoDownloader(DatabaseService dbService, string downloadDirectory = null, VideoScraper videoScraper = null)
        {
            _httpClient = new HttpClient();
            // Enhanced headers for better stealth to match browser behavior
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "video");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "no-cors");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
            _dbService = dbService;
            _videoScraper = videoScraper; // Use shared scraper if provided
            
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

        public async Task DownloadVideoAsync(VideoData video, bool showProgress = true, Action<double>? progressCallback = null, Action<long?>? fileSizeCallback = null)
        {
            if (string.IsNullOrEmpty(video.VideoSourceUrl))
            {
                if (showProgress) Console.WriteLine($"No video URL available for {video.Title}");
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
                
                // Check if file already exists and validate completeness
                if (File.Exists(filePath))
                {
                    // Validate file completeness by checking if it can be opened and has reasonable size
                    bool isFileComplete = await ValidateDownloadedFile(filePath, video);
                    
                    if (isFileComplete)
                    {
                        if (showProgress) Console.WriteLine($"✅ File already exists and is complete: {Path.GetFileName(filePath)}");
                        await _dbService.MarkVideoAsDownloadedAsync(video.Url, filePath);
                        video.DownloadPath = filePath; // Update the in-memory object
                        return;
                    }
                    else
                    {
                        if (showProgress) Console.WriteLine($"⚠️ Existing file appears incomplete, re-downloading: {Path.GetFileName(filePath)}");
                        // Delete the incomplete file
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch (Exception deleteEx)
                        {
                            Console.WriteLine($"Warning: Could not delete incomplete file: {deleteEx.Message}");
                        }
                    }
                }
                
                // Try to download the video, handle URL expiration if needed
                await DownloadWithRetryAsync(video, filePath, showProgress, progressCallback, fileSizeCallback);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading video {video.Title}: {ex.Message}");
            }
        }

        private async Task DownloadWithRetryAsync(VideoData video, string filePath, bool showProgress = true, Action<double>? progressCallback = null, Action<long?>? fileSizeCallback = null, int maxRetries = 2)
        {
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount <= maxRetries)
            {
                DownloadProgressDisplay? progressDisplay = null;
                string tempFilePath = filePath + ".tmp"; // Download to temp file first
                
                try
                {
                    // Stream the download to avoid loading the entire file in memory
                    using (var response = await _httpClient.GetAsync(video.VideoSourceUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // Check if the response was successful (200-299)
                        if (response.IsSuccessStatusCode)
                        {
                            long? totalBytes = response.Content.Headers.ContentLength;
                            
                            // Notify about file size
                            fileSizeCallback?.Invoke(totalBytes);
                            
                            // Initialize progress display if showing progress
                            if (showProgress && totalBytes.HasValue)
                            {
                                progressDisplay = new DownloadProgressDisplay(video.Title, totalBytes.Value);
                            }
                            
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[8192]; // 8KB buffer
                                long totalBytesRead = 0;
                                int bytesRead;
                                
                                // Download with progress updates
                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    
                                    // Update progress displays
                                    progressDisplay?.UpdateProgress(totalBytesRead);
                                    progressCallback?.Invoke(totalBytes.HasValue ? (double)totalBytesRead / totalBytes.Value * 100 : 0);
                                }
                            }
                            
                            // Verify download completeness
                            if (totalBytes.HasValue)
                            {
                                var tempFileInfo = new FileInfo(tempFilePath);
                                if (tempFileInfo.Length != totalBytes.Value)
                                {
                                    throw new Exception($"Download incomplete: {tempFileInfo.Length}/{totalBytes.Value} bytes");
                                }
                            }
                            
                            // Move temp file to final location (atomic operation)
                            File.Move(tempFilePath, filePath);
                            
                            // Mark as successful
                            progressDisplay?.Complete();
                            success = true;
                            
                            // Mark as downloaded in database and update video object
                            await _dbService.MarkVideoAsDownloadedAsync(video.Url, filePath);
                            video.DownloadPath = filePath; // Update the in-memory object
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound) // 404 error
                        {
                            progressDisplay?.Error("URL expired");
                            retryCount++;
                            if (retryCount <= maxRetries)
                            {
                                if (showProgress) Console.WriteLine($"🔄 URL expired. Refreshing... (Retry {retryCount}/{maxRetries})");
                                
                                // Use the VideoScraper to get a fresh URL
                                bool urlRefreshed = await RefreshVideoSourceUrl(video);
                                
                                if (urlRefreshed)
                                {
                                    if (showProgress) Console.WriteLine($"✅ URL refreshed successfully");
                                    await Task.Delay(1000); // Small delay before retry
                                }
                                else
                                {
                                    if (showProgress) Console.WriteLine($"❌ Failed to refresh URL");
                                    break;
                                }
                            }
                            else
                            {
                                if (showProgress) Console.WriteLine($"❌ Maximum retries reached for {video.Title}");
                            }
                        }
                        else
                        {
                            progressDisplay?.Error($"Server error {(int)response.StatusCode}");
                            if (showProgress) Console.WriteLine($"❌ Server returned {(int)response.StatusCode} {response.StatusCode}");
                            break;
                        }
                    }
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404"))
                {
                    progressDisplay?.Error("URL expired");
                    
                    // Clean up temp file if it exists
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"Warning: Could not clean up temp file: {cleanupEx.Message}");
                    }
                    
                    retryCount++;
                    if (retryCount <= maxRetries)
                    {
                        if (showProgress) Console.WriteLine($"🔄 URL expired. Refreshing... (Retry {retryCount}/{maxRetries})");
                        
                        bool urlRefreshed = await RefreshVideoSourceUrl(video);
                        
                        if (urlRefreshed)
                        {
                            if (showProgress) Console.WriteLine($"✅ URL refreshed successfully");
                            await Task.Delay(1000);
                        }
                        else
                        {
                            if (showProgress) Console.WriteLine($"❌ Failed to refresh URL");
                            break;
                        }
                    }
                    else
                    {
                        if (showProgress) Console.WriteLine($"❌ Maximum retries reached for {video.Title}");
                    }
                }
                catch (Exception ex)
                {
                    progressDisplay?.Error($"Download error: {ex.Message}");
                    if (showProgress) Console.WriteLine($"❌ Error during download: {ex.Message}");
                    
                    // Clean up temp file if it exists
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"Warning: Could not clean up temp file: {cleanupEx.Message}");
                    }
                    
                    break;
                }
            }
        }

        private async Task<bool> RefreshVideoSourceUrl(VideoData video)
        {
            try
            {
                VideoScraper scraperToUse;
                bool shouldDispose = false;

                // Use shared scraper if available, otherwise create a temporary one
                if (_videoScraper != null)
                {
                    scraperToUse = _videoScraper;
                }
                else
                {
                    scraperToUse = new VideoScraper();
                    await scraperToUse.Initialize();
                    shouldDispose = true;
                }

                try
                {
                    // Get a fresh video source URL
                    string newSourceUrl = await scraperToUse.GetVideoSourceUrl(video.Url);
                    
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
                finally
                {
                    // Only dispose if we created a temporary scraper
                    if (shouldDispose)
                    {
                        scraperToUse.Dispose();
                    }
                }
            }
            catch (Exception)
            {
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

        private async Task<bool> ValidateDownloadedFile(string filePath, VideoData video)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                
                // Check if file is very small (likely incomplete)
                if (fileInfo.Length < 1024) // Less than 1KB
                {
                    Console.WriteLine($"File too small ({fileInfo.Length} bytes), likely incomplete");
                    return false;
                }
                
                // Try to get expected file size from the server
                try
                {
                    using (var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, video.VideoSourceUrl)))
                    {
                        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                        {
                            long expectedSize = response.Content.Headers.ContentLength.Value;
                            long actualSize = fileInfo.Length;
                            
                            // Allow 1% tolerance for minor differences
                            double tolerance = expectedSize * 0.01;
                            
                            if (Math.Abs(expectedSize - actualSize) <= tolerance)
                            {
                                Console.WriteLine($"File size validation passed: {actualSize}/{expectedSize} bytes");
                                return true;
                            }
                            else
                            {
                                Console.WriteLine($"File size mismatch: {actualSize}/{expectedSize} bytes (difference: {Math.Abs(expectedSize - actualSize)})");
                                return false;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // If we can't check the server, use basic file validation
                    Console.WriteLine("Could not verify file size with server, using basic validation");
                }
                
                // Basic validation: check if file can be opened and isn't corrupted
                try
                {
                    using (var stream = File.OpenRead(filePath))
                    {
                        // Try to read first and last bytes to ensure file isn't corrupted
                        if (stream.Length > 0)
                        {
                            stream.ReadByte(); // Read first byte
                            if (stream.Length > 1)
                            {
                                stream.Seek(-1, SeekOrigin.End);
                                stream.ReadByte(); // Read last byte
                            }
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("File appears to be corrupted or inaccessible");
                    return false;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error validating file: {ex.Message}");
                return false; // Assume incomplete if we can't validate
            }
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