namespace SimpleScraper
{
    public class ChannelData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public int CheckIntervalMinutes { get; set; }
        public DateTime? LastChecked { get; set; }
    }

    public class ChannelStats
    {
        public int ChannelId { get; set; }
        public string ChannelName { get; set; }
        public int PendingDownloads { get; set; }
        public int PendingUploads { get; set; }
        public int Completed { get; set; }
        public int TotalVideos { get; set; }
    }
}