using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace SimpleScraper
{
    public class TelegramUploadService : IDisposable
    {
        private readonly DatabaseService _dbService;
        private readonly HttpClient _httpClient;
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly string _serverUrl;
        private readonly string _tempDir;

        public TelegramUploadService(DatabaseService dbService, string botToken, string chatId, string serverUrl, string tempDirectory = null)
        {
            _dbService = dbService;
            _botToken = botToken;
            _chatId = chatId;
            _serverUrl = serverUrl; // e.g., http://192.168.1.3:8081
            _httpClient = new HttpClient();

            _tempDir = tempDirectory ?? Path.Combine(Path.GetTempPath(), "SimpleScraperThumbs");
            if (!Directory.Exists(_tempDir))
            {
                Directory.CreateDirectory(_tempDir);
            }
        }

        public async Task UploadVideoAsync(VideoData video)
        {
            // Validate video data
            if (video == null)
            {
                Logger.Log("Video data is null. Skipping Telegram upload.");
                return;
            }

            Logger.Log($"Attempting Telegram upload for: {video.Title}");
            Logger.Log($"Expected file path: {video.DownloadPath}");

            // Check if download path exists and is valid
            if (string.IsNullOrEmpty(video.DownloadPath))
            {
                Logger.Log($"Video file path is null or empty for {video.Title}. Skipping Telegram upload.");
                return;
            }

            // Normalize the path and check if file exists
            string fullPath = Path.GetFullPath(video.DownloadPath);
            if (!File.Exists(fullPath))
            {
                Logger.Log($"Video file not found for {video.Title}");
                Logger.Log($"  Expected path: {video.DownloadPath}");
                Logger.Log($"  Full path: {fullPath}");
                Logger.Log($"  File exists: {File.Exists(fullPath)}");
                
                // Try to find the file in the download directory with a similar name
                string fileName = Path.GetFileName(video.DownloadPath);
                string directory = Path.GetDirectoryName(video.DownloadPath) ?? _tempDir;
                
                if (Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => Path.GetFileName(f).Contains(video.PostId ?? ""))
                        .ToList();
                    
                    if (files.Any())
                    {
                        fullPath = files.First();
                        Logger.Log($"  Found alternative file: {fullPath}");
                    }
                    else
                    {
                        Logger.Log($"  No alternative files found in directory: {directory}");
                        Logger.Log($"  Available files: {string.Join(", ", Directory.GetFiles(directory).Take(5).Select(Path.GetFileName))}");
                        return;
                    }
                }
                else
                {
                    Logger.Log($"  Directory does not exist: {directory}");
                    return;
                }
            }

            // Update the video object with the correct path
            video.DownloadPath = fullPath;

            // Update attempt timestamp regardless of success/failure for this attempt
            await _dbService.UpdateTelegramUploadAttemptTimestampAsync(video.Url);

            string videoFileName = Path.GetFileName(video.DownloadPath);
            string sanitizedFileName = SanitizeFileName(videoFileName);
            string videoNameWithoutExt = Path.GetFileNameWithoutExtension(sanitizedFileName);
            string thumbnailPath = Path.Combine(_tempDir, $"{videoNameWithoutExt}_thumb.jpg");

            try
            {
                Logger.Log($"Processing for Telegram: {video.Title}");

                // 1. Get Metadata
                var metadata = await GetVideoMetadataAsync(video.DownloadPath);
                if (metadata == null)
                {
                    Logger.Log($"Failed to get metadata for {video.Title}. Skipping Telegram upload.");
                    return;
                }

                // 2. Generate Thumbnail
                bool thumbGenerated = await GenerateThumbnailAsync(video.DownloadPath, thumbnailPath);
                if (!thumbGenerated)
                {
                    Logger.Log($"Failed to generate thumbnail for {video.Title}. Skipping Telegram upload.");
                    return;
                }

                // 3. Construct Caption
                string caption = $"*{EscapeMarkdown(videoNameWithoutExt)}*\n"
                                 + $"📺 Resolution: {metadata.Width}x{metadata.Height}\n"
                                 + $"⏱ Duration: {metadata.DurationSeconds}s\n"
                                 + $"💾 Size: {FormatFileSize(metadata.SizeBytes)}";

                // 4. Send to Telegram Bot Server
                Logger.Log($"Uploading {video.Title} to Telegram...");

                // Try to read the file with retries
                byte[] videoBytes = null;
                int maxRetries = 5; // Increased retries
                int retryCount = 0;
                int retryDelay = 1000; // Start with 1 second delay

                while (retryCount < maxRetries)
                {
                    try
                    {
                        // Try to read the file with FileShare.ReadWrite to allow other processes to read/write
                        using (var fileStream = new FileStream(video.DownloadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            // Verify file is not empty
                            if (fileStream.Length == 0)
                            {
                                throw new Exception("Video file is empty");
                            }

                            videoBytes = new byte[fileStream.Length];
                            int bytesRead = 0;
                            int totalBytesRead = 0;

                            // Read the file in chunks to handle large files
                            while (totalBytesRead < fileStream.Length)
                            {
                                bytesRead = await fileStream.ReadAsync(videoBytes, totalBytesRead, (int)Math.Min(8192, fileStream.Length - totalBytesRead));
                                if (bytesRead == 0) break;
                                totalBytesRead += bytesRead;
                            }

                            if (totalBytesRead != fileStream.Length)
                            {
                                throw new Exception($"Failed to read complete file. Read {totalBytesRead} of {fileStream.Length} bytes");
                            }
                        }
                        break; // If successful, break the retry loop
                    }
                    catch (IOException ex)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            throw new Exception($"Failed to read video file after {maxRetries} attempts: {ex.Message}");
                        }
                        Logger.Log($"Attempt {retryCount} failed to read video file. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay);
                        retryDelay *= 2; // Exponential backoff
                    }
                }

                if (videoBytes == null || videoBytes.Length == 0)
                {
                    throw new Exception("Failed to read video file or file is empty");
                }

                // Verify the file size matches what we read
                var fileInfo = new FileInfo(video.DownloadPath);
                if (fileInfo.Length != videoBytes.Length)
                {
                    throw new Exception($"File size mismatch. Expected {fileInfo.Length} bytes but read {videoBytes.Length} bytes");
                }

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_chatId), "chat_id");
                form.Add(new ByteArrayContent(videoBytes), "video", sanitizedFileName);
                form.Add(new StringContent(caption), "caption");
                form.Add(new StringContent("Markdown"), "parse_mode");
                form.Add(new StringContent(metadata.DurationSeconds.ToString()), "duration");
                form.Add(new StringContent(metadata.Width.ToString()), "width");
                form.Add(new StringContent(metadata.Height.ToString()), "height");
                form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(thumbnailPath)), "thumb", Path.GetFileName(thumbnailPath));
                form.Add(new StringContent("true"), "supports_streaming");

                string requestUrl = $"{_serverUrl}/bot{_botToken}/sendVideo";
                HttpResponseMessage response = await _httpClient.PostAsync(requestUrl, form);

                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log($"Successfully uploaded {video.Title} to Telegram. Response: {responseContent}");
                    string messageId = ExtractMessageId(responseContent); 
                    await _dbService.MarkVideoAsTelegramUploadedAsync(video.Url, messageId);
                }
                else
                {
                    Logger.Log($"Failed to upload {video.Title} to Telegram. Status: {response.StatusCode}. Response: {responseContent}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error uploading {video.Title} to Telegram: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (File.Exists(thumbnailPath))
                {
                    try { File.Delete(thumbnailPath); }
                    catch (Exception ex) { Logger.Log($"Error deleting thumbnail {thumbnailPath}: {ex.Message}"); }
                }
            }
        }

        private string ExtractMessageId(string jsonResponse)
        {
            try
            {
                // Using verbatim string for regex to handle quotes and backslashes more easily.
                var match = Regex.Match(jsonResponse, @"""message_id"""":(\d+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not parse message_id from Telegram response: {ex.Message}");
            }
            return null;
        }

        private async Task<VideoFileMetadata> GetVideoMetadataAsync(string videoPath)
        {
            try
            {
                // Correctly escape quotes for command line arguments
                string durationCmd = $"-v error -select_streams v:0 -show_entries stream=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
                string widthCmd = $"-v error -select_streams v:0 -show_entries stream=width -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
                string heightCmd = $"-v error -select_streams v:0 -show_entries stream=height -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";

                string durationStr = await RunProcessAsync("ffprobe", durationCmd);
                string widthStr = await RunProcessAsync("ffprobe", widthCmd);
                string heightStr = await RunProcessAsync("ffprobe", heightCmd);

                if (string.IsNullOrWhiteSpace(durationStr) || string.IsNullOrWhiteSpace(widthStr) || string.IsNullOrWhiteSpace(heightStr))
                    return null;

                var metadata = new VideoFileMetadata
                {
                    DurationSeconds = (int)Math.Floor(decimal.Parse(durationStr.Trim(), CultureInfo.InvariantCulture)),
                    Width = int.Parse(widthStr.Trim()),
                    Height = int.Parse(heightStr.Trim()),
                    SizeBytes = new FileInfo(videoPath).Length
                };
                return metadata;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting metadata with ffprobe for {videoPath}: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> GenerateThumbnailAsync(string videoPath, string thumbnailPath)
        {
            try
            {
                // First, get video duration to calculate random timestamps
                string durationCmd = $"-v error -select_streams v:0 -show_entries stream=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
                string durationStr = await RunProcessAsync("ffprobe", durationCmd);
                if (string.IsNullOrWhiteSpace(durationStr))
                {
                    Logger.Log($"Could not get video duration for {videoPath}");
                    return false;
                }

                double duration = double.Parse(durationStr.Trim(), CultureInfo.InvariantCulture);
                int numFrames = 10; // Number of frames to extract
                var random = new Random();
                var timestamps = new List<double>();

                // Generate random timestamps, avoiding the first and last 5 seconds
                for (int i = 0; i < numFrames; i++)
                {
                    double timestamp = 5 + (random.NextDouble() * (duration - 10));
                    timestamps.Add(timestamp);
                }

                // Create a temporary directory for frames
                string tempDir = Path.Combine(Path.GetTempPath(), "SimpleScraperThumbs", Path.GetRandomFileName());
                Directory.CreateDirectory(tempDir);

                try
                {
                    // Extract frames at random timestamps
                    var framePaths = new List<string>();
                    foreach (var timestamp in timestamps)
                    {
                        string framePath = Path.Combine(tempDir, $"frame_{timestamp:F2}.jpg");
                        string frameCmd = $"-y -ss {timestamp:F2} -i \"{videoPath}\" -frames:v 1 -vf \"scale=160:-1\" \"{framePath}\"";
                        await RunProcessAsync("ffmpeg", frameCmd);
                        if (File.Exists(framePath))
                        {
                            framePaths.Add(framePath);
                        }
                    }

                    if (framePaths.Count == 0)
                    {
                        Logger.Log("No frames were successfully extracted.");
                        return false;
                    }

                    // Create a 2x5 grid of frames
                    string filterComplex = string.Join(";", Enumerable.Range(0, framePaths.Count)
                        .Select(i => $"[{i}:v]scale=160:-1[v{i}]"))
                        + ";"
                        + string.Join("", Enumerable.Range(0, framePaths.Count)
                        .Select(i => $"[v{i}]"))
                        + $"xstack=inputs={framePaths.Count}:layout=0_0|w0_0|w0+w1_0|w0+w1+w2_0|w0+w1+w2+w3_0|0_h0|w0_h0|w0+w1_h0|w0+w1+w2_h0|w0+w1+w2+w3_h0[v]";

                    // Build the input arguments for all frames
                    string inputArgs = string.Join(" ", framePaths.Select(p => $"-i \"{p}\""));

                    string collageCmd = $"-y {inputArgs} -filter_complex \"{filterComplex}\" -map \"[v]\" \"{thumbnailPath}\"";
                    await RunProcessAsync("ffmpeg", collageCmd);

                    return File.Exists(thumbnailPath);
                }
                finally
                {
                    // Clean up temporary directory
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error cleaning up temp directory: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error generating thumbnail collage with ffmpeg for {videoPath}: {ex.Message}");
                return false;
            }
        }

        private async Task<string> RunProcessAsync(string fileName, string arguments)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = processStartInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, args) => { if (args.Data != null) outputBuilder.AppendLine(args.Data); };
            process.ErrorDataReceived += (sender, args) => { if (args.Data != null) errorBuilder.AppendLine(args.Data); }; 

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(); // Use new WaitForExitAsync() for non-blocking wait

            if (process.ExitCode != 0)
            {
                string errorOutput = errorBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(errorOutput)) errorOutput = outputBuilder.ToString().Trim(); // Sometimes ffmpeg/ffprobe sends info to stdout on error
                throw new Exception($"`{fileName} {arguments}` exited with code {process.ExitCode}: {errorOutput}");
            }
            return outputBuilder.ToString().Trim();
        }

        private string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal size = bytes;
            while (Math.Round(size / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                size /= 1024;
                counter++;
            }
            return $"{size:n2} {suffixes[counter]}";
        }
        
        private string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            // Characters to escape in MarkdownV2: _ * [ ] ( ) ~ ` > # + - = | { } . !
            // Your script uses simpler caption, so I'll escape for basic Markdown (not V2)
            // Focusing on *, _, `, [, ], (, )
            return text
                .Replace("_", "\\_")
                .Replace("*", "\\*")
                .Replace("[", "\\[")
                .Replace("]", "\\]")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("`", "\\`");
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "video";

            // Remove invalid characters and replace spaces with underscores
            string sanitized = Regex.Replace(fileName, @"[^\w\-\.]", "_");
            
            // Replace multiple consecutive underscores with a single one
            sanitized = Regex.Replace(sanitized, @"_+", "_");
            
            // Remove leading and trailing underscores
            sanitized = sanitized.Trim('_');
            
            // Ensure the filename isn't too long (Telegram has limits)
            if (sanitized.Length > 64)
            {
                // Keep the extension
                string extension = Path.GetExtension(sanitized);
                sanitized = sanitized.Substring(0, 64 - extension.Length) + extension;
            }
            
            return sanitized;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            // Clean up temp directory if it's empty or if you want to force clean
            try
            {
                if (Directory.Exists(_tempDir) && !Directory.EnumerateFileSystemEntries(_tempDir).Any())
                {
                    Directory.Delete(_tempDir);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not clean up temp directory {_tempDir}: {ex.Message}");
            }
        }
    }

    public class VideoFileMetadata
    {
        public int DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long SizeBytes { get; set; }
    }
} 