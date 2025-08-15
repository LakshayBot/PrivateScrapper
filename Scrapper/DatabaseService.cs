using Npgsql;
using System.Data;

namespace SimpleScraper
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Create video table if it doesn't exist with download status fields
            using var videoTableCommand = connection.CreateCommand();
            videoTableCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS videos (
                    id SERIAL PRIMARY KEY,
                    title TEXT NOT NULL,
                    url TEXT NOT NULL UNIQUE,
                    video_source_url TEXT,
                    post_id TEXT,
                    downloaded BOOLEAN DEFAULT FALSE,
                    download_path TEXT,
                    download_date TIMESTAMP,
                    is_uploaded_to_telegram BOOLEAN DEFAULT FALSE,
                    telegram_message_id TEXT,
                    telegram_upload_attempt_timestamp TIMESTAMP,
                    scraped_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            ";
            await videoTableCommand.ExecuteNonQueryAsync();

            // Create channels table if it doesn't exist
            using var channelsTableCommand = connection.CreateCommand();
            channelsTableCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS channels (
                    id SERIAL PRIMARY KEY,
                    name TEXT NOT NULL,
                    url TEXT NOT NULL,
                    last_checked TIMESTAMP DEFAULT NULL,
                    check_interval_minutes INT DEFAULT 60,
                    is_active BOOLEAN DEFAULT TRUE,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            ";
            await channelsTableCommand.ExecuteNonQueryAsync();
        }

        public async Task SaveVideosAsync(List<VideoData> videos)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            foreach (var video in videos)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO videos (title, url, video_source_url, post_id)
                    VALUES (@title, @url, @videoSourceUrl, @postId)
                    ON CONFLICT (url) DO UPDATE 
                    SET 
                        title = @title,
                        video_source_url = @videoSourceUrl,
                        scraped_at = CURRENT_TIMESTAMP
                    RETURNING id;
                ";
                command.Parameters.AddWithValue("title", video.Title);
                command.Parameters.AddWithValue("url", video.Url);
                command.Parameters.AddWithValue("videoSourceUrl", video.VideoSourceUrl ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("postId", video.PostId ?? (object)DBNull.Value);

                await command.ExecuteScalarAsync();
            }
        }

        public async Task<List<VideoData>> GetAllVideosAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title, url, video_source_url, post_id FROM videos ORDER BY scraped_at DESC;";

            using var reader = await command.ExecuteReaderAsync();
            var videos = new List<VideoData>();

            while (await reader.ReadAsync())
            {
                videos.Add(new VideoData
                {
                    Title = reader.GetString(0),
                    Url = reader.GetString(1),
                    VideoSourceUrl = !reader.IsDBNull(2) ? reader.GetString(2) : null,
                    PostId = !reader.IsDBNull(3) ? reader.GetString(3) : null
                });
            }

            return videos;
        }

        public async Task<List<VideoData>> GetUndownloadedVideosAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT title, url, video_source_url, post_id 
                FROM videos 
                WHERE (downloaded = FALSE OR downloaded IS NULL) 
                  AND video_source_url IS NOT NULL
                ORDER BY scraped_at DESC;
            ";

            using var reader = await command.ExecuteReaderAsync();
            var videos = new List<VideoData>();

            while (await reader.ReadAsync())
            {
                videos.Add(new VideoData
                {
                    Title = reader.GetString(0),
                    Url = reader.GetString(1),
                    VideoSourceUrl = !reader.IsDBNull(2) ? reader.GetString(2) : null,
                    PostId = !reader.IsDBNull(3) ? reader.GetString(3) : null
                });
            }

            return videos;
        }

        public async Task<int> GetUndownloadedVideosCountAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM videos 
                WHERE (downloaded = FALSE OR downloaded IS NULL) 
                  AND video_source_url IS NOT NULL;
            ";

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<int> GetPendingUploadsCountAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM videos 
                WHERE downloaded = TRUE 
                  AND (is_uploaded_to_telegram = FALSE OR is_uploaded_to_telegram IS NULL);
            ";

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<int> GetCompletedDownloadsCountAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM videos 
                WHERE downloaded = TRUE;
            ";

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<int> GetCompletedUploadsCountAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM videos 
                WHERE is_uploaded_to_telegram = TRUE;
            ";

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<List<ChannelData>> GetActiveChannelsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, url, check_interval_minutes, last_checked FROM channels WHERE is_active = TRUE;";

            using var reader = await command.ExecuteReaderAsync();
            var channels = new List<ChannelData>();

            while (await reader.ReadAsync())
            {
                channels.Add(new ChannelData
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Url = reader.GetString(2),
                    CheckIntervalMinutes = reader.GetInt32(3),
                    LastChecked = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
                });
            }

            return channels;
        }

        public async Task UpdateChannelLastCheckedAsync(int channelId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE channels SET last_checked = CURRENT_TIMESTAMP WHERE id = @channelId";
            command.Parameters.AddWithValue("channelId", channelId);

            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveChannelAsync(string name, string url, int checkIntervalMinutes = 60)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO channels (name, url, check_interval_minutes) 
                VALUES (@name, @url, @checkInterval)
                ON CONFLICT (url) DO UPDATE 
                SET name = @name, check_interval_minutes = @checkInterval, is_active = TRUE;
            ";
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("url", url);
            command.Parameters.AddWithValue("checkInterval", checkIntervalMinutes);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<Dictionary<int, ChannelStats>> GetChannelStatsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT 
                    c.id,
                    c.name,
                    COUNT(CASE WHEN v.downloaded = FALSE OR v.downloaded IS NULL THEN 1 END) as pending_downloads,
                    COUNT(CASE WHEN v.downloaded = TRUE AND (v.is_uploaded_to_telegram = FALSE OR v.is_uploaded_to_telegram IS NULL) THEN 1 END) as pending_uploads,
                    COUNT(CASE WHEN v.is_uploaded_to_telegram = TRUE THEN 1 END) as completed,
                    COUNT(v.id) as total_videos
                FROM channels c
                LEFT JOIN videos v ON v.url LIKE '%' || REPLACE(REPLACE(c.url, 'https://sxyprn.com/', ''), '.html', '') || '%'
                WHERE c.is_active = TRUE
                GROUP BY c.id, c.name
            ";

            using var reader = await command.ExecuteReaderAsync();
            var stats = new Dictionary<int, ChannelStats>();

            while (await reader.ReadAsync())
            {
                var channelId = reader.GetInt32(0);
                stats[channelId] = new ChannelStats
                {
                    ChannelId = channelId,
                    ChannelName = reader.GetString(1),
                    PendingDownloads = reader.GetInt32(2),
                    PendingUploads = reader.GetInt32(3),
                    Completed = reader.GetInt32(4),
                    TotalVideos = reader.GetInt32(5)
                };
            }

            return stats;
        }

        public async Task MarkVideoAsDownloadedAsync(string url, string downloadPath)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE videos 
                SET downloaded = TRUE, download_path = @downloadPath, download_date = CURRENT_TIMESTAMP 
                WHERE url = @url;
            ";
            command.Parameters.AddWithValue("url", url);
            command.Parameters.AddWithValue("downloadPath", downloadPath);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<bool> VideoExistsAsync(string url)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM videos WHERE url = @url;";
            command.Parameters.AddWithValue("url", url);

            int count = Convert.ToInt32(await command.ExecuteScalarAsync());
            return count > 0;
        }

        public async Task UpdateVideoSourceUrlAsync(string url, string videoSourceUrl)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE videos 
                SET video_source_url = @videoSourceUrl
                WHERE url = @url;
            ";
            command.Parameters.AddWithValue("url", url);
            command.Parameters.AddWithValue("videoSourceUrl", videoSourceUrl);

            await command.ExecuteNonQueryAsync();
        }

        // Telegram-related methods
        public async Task MarkVideoAsTelegramUploadedAsync(string url, string messageId = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE videos 
                SET is_uploaded_to_telegram = TRUE,
                    telegram_message_id = @messageId
                WHERE url = @url;
            ";
            command.Parameters.AddWithValue("url", url);
            command.Parameters.AddWithValue("messageId", messageId ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task UpdateTelegramUploadAttemptTimestampAsync(string url)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE videos 
                SET telegram_upload_attempt_timestamp = CURRENT_TIMESTAMP
                WHERE url = @url;
            ";
            command.Parameters.AddWithValue("url", url);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<VideoData>> GetDownloadedButNotUploadedVideosAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT title, url, video_source_url, post_id, download_path
                FROM videos 
                WHERE downloaded = TRUE 
                  AND (is_uploaded_to_telegram = FALSE OR is_uploaded_to_telegram IS NULL)
                  AND download_path IS NOT NULL
                ORDER BY download_date ASC;
            ";

            using var reader = await command.ExecuteReaderAsync();
            var videos = new List<VideoData>();

            while (await reader.ReadAsync())
            {
                videos.Add(new VideoData
                {
                    Title = reader.GetString(0),
                    Url = reader.GetString(1),
                    VideoSourceUrl = !reader.IsDBNull(2) ? reader.GetString(2) : null,
                    PostId = !reader.IsDBNull(3) ? reader.GetString(3) : null,
                    DownloadPath = !reader.IsDBNull(4) ? reader.GetString(4) : null
                });
            }

            return videos;
        }

        public async Task<List<VideoData>> GetVideosMissingSourceUrlAsync(int limit = 20)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT title, url, post_id 
                FROM videos 
                WHERE (downloaded = FALSE OR downloaded IS NULL)
                  AND video_source_url IS NULL
                ORDER BY scraped_at DESC
                LIMIT @limit;
            ";
            command.Parameters.AddWithValue("limit", limit);

            using var reader = await command.ExecuteReaderAsync();
            var videos = new List<VideoData>();
            while (await reader.ReadAsync())
            {
                videos.Add(new VideoData
                {
                    Title = reader.GetString(0),
                    Url = reader.GetString(1),
                    PostId = !reader.IsDBNull(2) ? reader.GetString(2) : null
                });
            }
            return videos;
        }
    }
}