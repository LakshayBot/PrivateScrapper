using System;
using System.IO;

namespace SimpleScraper
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static StreamWriter _logWriter;
        private static string _logFilePath;

        public static void Initialize(string logDirectory = null)
        {
            try
            {
                // Create logs directory if it doesn't exist
                string directory = logDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Create log file with date only (one file per day)
                string dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
                _logFilePath = Path.Combine(directory, $"scraper_{dateStamp}.log");
                
                // Open log file in append mode so multiple runs on same day go to same file
                _logWriter = new StreamWriter(_logFilePath, true);
                _logWriter.AutoFlush = true;
                
                Log($"Logger initialized. Log file: {_logFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing logger: {ex.Message}");
            }
        }

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logMessage = $"[{timestamp}] {message}";
            
            // Write to console
            Console.WriteLine(message);
            
            // Write to log file
            lock (_lock)
            {
                try
                {
                    _logWriter?.WriteLine(logMessage);
                }
                catch
                {
                    // Ignore write errors
                }
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                _logWriter?.Flush();
                _logWriter?.Close();
                _logWriter?.Dispose();
                _logWriter = null;
            }
        }
    }
}