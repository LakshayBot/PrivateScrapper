using System;
using System.Diagnostics;

namespace SimpleScraper
{
    /// <summary>
    /// Simple console progress display for video downloads.
    /// Shows video name, progress bar, speed, and time in a clean format.
    /// </summary>
    public class DownloadProgressDisplay
    {
        private readonly Stopwatch _stopwatch;
        private readonly string _videoTitle;
        private long _totalBytes;
        private long _downloadedBytes;
        private long _lastBytesRead;
        private DateTime _lastUpdate;
        private readonly int _maxTitleLength = 50;
        private bool _isServerEnvironment = false; // Track if we're in a server environment

        public DownloadProgressDisplay(string videoTitle, long totalBytes)
        {
            _videoTitle = TruncateTitle(videoTitle);
            _totalBytes = totalBytes;
            _stopwatch = Stopwatch.StartNew();
            _lastUpdate = DateTime.Now;
            
            // Detect server environment
            try
            {
                // Better detection: Check if we're in a proper interactive terminal
                var hasValidWindow = Console.WindowWidth > 0 && Console.WindowHeight > 0;
                var termVar = Environment.GetEnvironmentVariable("TERM");
                var hasTerminal = !string.IsNullOrEmpty(termVar);
                var isInteractive = Environment.UserInteractive;
                
                // Mac Terminal.app typically has TERM=xterm-256color and proper window dimensions
                // Ubuntu server typically has no TERM or TERM=dumb and WindowWidth=0
                var isMacTerminal = hasValidWindow && hasTerminal && termVar.Contains("xterm");
                var isLinuxServer = !hasValidWindow || string.IsNullOrEmpty(termVar) || termVar == "dumb";
                
                // Only consider it a server environment if we clearly can't do cursor positioning
                _isServerEnvironment = Console.IsOutputRedirected;
                
                // If we think it's a desktop environment, test cursor positioning
                if (!_isServerEnvironment)
                {
                    var originalTop = Console.CursorTop;
                    var originalLeft = Console.CursorLeft;
                    Console.SetCursorPosition(originalLeft, originalTop);
                    // If we got here, cursor positioning works
                }
            }
            catch
            {
                _isServerEnvironment = true;
            }
            
            // Show initial info (these stay on screen)
            Console.WriteLine($"\n📥 {_videoTitle}");
            Console.WriteLine($"Size: {FormatFileSize(totalBytes)}");
            
            // Debug info (remove this later if needed)
            var debugTermVar = Environment.GetEnvironmentVariable("TERM") ?? "null";
            Console.WriteLine($"Debug: Environment detected as {(_isServerEnvironment ? "Server" : "Desktop")}, WindowWidth: {Console.WindowWidth}, TERM: '{debugTermVar}', Interactive: {Environment.UserInteractive}");
            
            if (!_isServerEnvironment)
            {
                // For desktop: Start with initial progress line (will be updated in-place with \r)
                Console.Write($"[░░░░░░░░░░░░░░░░░░░░] 0.0% | 0 B/{FormatFileSize(totalBytes)} | 0 B/s | ETA: --:--");
                // No newline - next update will use \r to overwrite this line
            }
            else
            {
                Console.WriteLine("Starting download...");
            }
        }

        public void UpdateProgress(long bytesRead)
        {
            _downloadedBytes = bytesRead;
            
            // Update every 500ms to avoid too much console output
            var now = DateTime.Now;
            if ((now - _lastUpdate).TotalMilliseconds < 500 && bytesRead < _totalBytes)
                return;

            var progressPercent = _totalBytes > 0 ? (double)_downloadedBytes / _totalBytes * 100 : 0;
            var speed = CalculateSpeed(bytesRead, now);
            var eta = CalculateETA(speed);
            
            // Create progress bar
            var progressBar = CreateProgressBar(progressPercent);
            var progressLine = $"{progressBar} {progressPercent:F1}% | {FormatFileSize(_downloadedBytes)}/{FormatFileSize(_totalBytes)} | {FormatSpeed(speed)} | ETA: {eta}";
            
            if (_isServerEnvironment)
            {
                // Server environment: Use simple periodic updates with timestamp (every 2 seconds only)
                if ((now - _lastUpdate).TotalMilliseconds >= 2000 || bytesRead >= _totalBytes)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {progressLine}");
                }
                else
                {
                    return; // Skip update for server mode if not enough time passed
                }
            }
            else
            {
                // Desktop environment: TRUE single line updates - no fallbacks to new lines!
                Console.Write($"\r{progressLine}".PadRight(120)); // Carriage return + overwrite with padding
                // Never use Console.WriteLine or create new lines in desktop mode
            }
            
            _lastBytesRead = bytesRead;
            _lastUpdate = now;
        }

        public void Complete()
        {
            _stopwatch.Stop();
            var finalSpeed = _totalBytes / _stopwatch.Elapsed.TotalSeconds;
            
            var completeLine = $"✅ [████████████████████] 100.0% | {FormatFileSize(_totalBytes)}/{FormatFileSize(_totalBytes)} | {FormatSpeed(finalSpeed)} | {_stopwatch.Elapsed:mm\\:ss}";
            
            if (_isServerEnvironment)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {completeLine}");
            }
            else
            {
                // Desktop: Overwrite the progress line and then move to next line
                Console.Write($"\r{completeLine}".PadRight(120));
                Console.WriteLine(); // Move to next line after completion
            }
        }

        public void Error(string error)
        {
            var errorLine = $"❌ Error: {error}";
            
            if (_isServerEnvironment)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {errorLine}");
            }
            else
            {
                // Desktop: Overwrite the progress line with error and move to next line
                Console.Write($"\r{errorLine}".PadRight(120));
                Console.WriteLine(); // Move to next line after error
            }
        }

        private string CreateProgressBar(double percent)
        {
            const int barLength = 20;
            var filled = (int)(percent / 100 * barLength);
            var empty = barLength - filled;
            
            return $"[{new string('█', filled)}{new string('░', empty)}]";
        }

        private double CalculateSpeed(long currentBytes, DateTime now)
        {
            var timeDiff = (now - _lastUpdate).TotalSeconds;
            if (timeDiff <= 0) return 0;
            
            var bytesDiff = currentBytes - _lastBytesRead;
            return bytesDiff / timeDiff;
        }

        private string CalculateETA(double speed)
        {
            if (speed <= 0) return "--:--";
            
            var remainingBytes = _totalBytes - _downloadedBytes;
            var remainingSeconds = remainingBytes / speed;
            
            if (remainingSeconds > 3600) return ">1h";
            
            var time = TimeSpan.FromSeconds(remainingSeconds);
            return $"{time:mm\\:ss}";
        }

        private string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/s";
            if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:F1} KB/s";
            if (bytesPerSecond < 1024 * 1024 * 1024) return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
            return $"{bytesPerSecond / (1024 * 1024 * 1024):F1} GB/s";
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private string TruncateTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "Unknown Video";
            
            if (title.Length <= _maxTitleLength) return title;
            
            return title.Substring(0, _maxTitleLength - 3) + "...";
        }
    }
}
