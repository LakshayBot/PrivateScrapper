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
                WHERE downloaded = FALSE AND video_source_url IS NOT NULL
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

        public async Task<List<ChannelData>> GetActiveChannelsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, url, check_interval_minutes FROM channels WHERE is_active = TRUE;";

            using var reader = await command.ExecuteReaderAsync();
            var channels = new List<ChannelData>();

            while (await reader.ReadAsync())
            {
                channels.Add(new ChannelData
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Url = reader.GetString(2),
                    CheckIntervalMinutes = reader.GetInt32(3)
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
    }
}