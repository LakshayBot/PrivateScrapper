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

            // Create table if it doesn't exist
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS videos (
                    id SERIAL PRIMARY KEY,
                    title TEXT NOT NULL,
                    url TEXT NOT NULL,
                    video_source_url TEXT,
                    post_id TEXT,
                    scraped_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            ";
            await command.ExecuteNonQueryAsync();
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
    }
}