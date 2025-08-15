using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Linq;
using PuppeteerSharp;

namespace SimpleScraper
{
    public class FlareSolverrClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _flareSolverrUrl = "http://localhost:8191/v1";
        private string _sessionId = "";
        private readonly List<string> _userAgents;
        private readonly Random _random;
        private int _currentUserAgentIndex = 0;

        public FlareSolverrClient()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2) // 2-minute timeout for Cloudflare challenges
            };
            
            // Initialize user agent rotation
            _userAgents = new List<string>
            {
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:120.0) Gecko/20100101 Firefox/120.0",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:120.0) Gecko/20100101 Firefox/120.0",
                "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15"
            };
            _random = new Random();
        }

        private string GetNextUserAgent(Action<string>? statusCallback = null)
        {
            // Use round-robin with some randomness
            _currentUserAgentIndex = (_currentUserAgentIndex + 1) % _userAgents.Count;
            
            // Sometimes pick a completely random one for extra variety
            if (_random.Next(100) < 20) // 20% chance
            {
                _currentUserAgentIndex = _random.Next(_userAgents.Count);
            }
            
            var userAgent = _userAgents[_currentUserAgentIndex];
            statusCallback?.Invoke($"🔄 Using User Agent: {userAgent.Substring(0, Math.Min(50, userAgent.Length))}...");
            return userAgent;
        }

        private async Task RecreateSessionWithNewUserAgent(Action<string>? statusCallback = null)
        {
            statusCallback?.Invoke("🚫 Possible ban detected - recreating session with new user agent...");
            
            // Destroy current session
            await DestroySessionAsync(statusCallback);
            
            // Wait a bit before creating new session
            await Task.Delay(2000);
            
            // Create new session (will use new user agent)
            await CreateSessionAsync(statusCallback);
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                // FlareSolverr returns 405 for GET requests, but that means it's running
                var response = await _httpClient.GetAsync($"{_flareSolverrUrl}");
                
                // 405 Method Not Allowed means FlareSolverr is running but doesn't accept GET
                // This is the expected response, so we consider it a successful connection
                return response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ||
                       response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task CreateSessionAsync(Action<string>? statusCallback = null)
        {
            var userAgent = GetNextUserAgent(statusCallback);
            
            var requestData = new
            {
                cmd = "sessions.create",
                userAgent = userAgent
            };

            var response = await SendRequestAsync(requestData);
            if (response.Status == "ok")
            {
                _sessionId = response.Session;
                statusCallback?.Invoke($"✅ FlareSolverr session created: {_sessionId}");
            }
            else
            {
                throw new Exception($"Failed to create FlareSolverr session: {response.Message}");
            }
        }

        public async Task<string> GetPageContentAsync(string url, Action<string>? statusCallback = null)
        {
            if (string.IsNullOrEmpty(_sessionId))
            {
                await CreateSessionAsync(statusCallback);
            }

            var currentUserAgent = GetNextUserAgent(statusCallback);
            var requestData = new
            {
                cmd = "request.get",
                url = url,
                session = _sessionId,
                userAgent = currentUserAgent,
                maxTimeout = 120000 // 2 minutes in milliseconds
            };

            statusCallback?.Invoke($"📡 Requesting page via FlareSolverr: {url}");
            var response = await SendRequestAsync(requestData);

            if (response.Status == "ok")
            {
                statusCallback?.Invoke($"✅ Successfully retrieved page content (length: {response.Solution?.Response?.Length ?? 0})");
                return response.Solution?.Response ?? "";
            }

            // Check for various ban/block indicators
            var message = response.Message?.ToLower() ?? "";
            if (message.Contains("session") || message.Contains("ban") || message.Contains("block") || 
                message.Contains("403") || message.Contains("captcha") || message.Contains("challenge"))
            {
                statusCallback?.Invoke($"🚫 Possible ban/block detected: {response.Message}");
                
                // Try recreating session with new user agent
                await RecreateSessionWithNewUserAgent(statusCallback);
                
                // Retry with new session and user agent
                currentUserAgent = GetNextUserAgent(statusCallback);
                requestData = new
                {
                    cmd = "request.get",
                    url = url,
                    session = _sessionId,
                    userAgent = currentUserAgent,
                    maxTimeout = 120000
                };

                response = await SendRequestAsync(requestData);
                if (response.Status == "ok")
                {
                    statusCallback?.Invoke($"✅ Retry successful with new session");
                    return response.Solution?.Response ?? "";
                }
            }

            throw new Exception($"FlareSolverr request failed: {response.Message}");
        }

        public async Task<string?> GetVideoUrlFromPage(string url, string postId, Action<string>? statusCallback = null)
        {
            if (string.IsNullOrEmpty(_sessionId))
            {
                await CreateSessionAsync(statusCallback);
            }

            statusCallback?.Invoke($"🔍 Getting video URL for: {postId}");
            
            try
            {
                // Step 1: Get session cookies from FlareSolverr (to bypass Cloudflare)
                statusCallback?.Invoke("🍪 Getting session cookies from FlareSolverr...");
                var sessionInfo = await GetSessionCookies(url, statusCallback);
                
                if (sessionInfo == null || sessionInfo.Cookies == null || sessionInfo.Cookies.Count == 0)
                {
                    statusCallback?.Invoke("⚠️ Failed to get session cookies, trying with new session...");
                    
                    // Try recreating session
                    await RecreateSessionWithNewUserAgent(statusCallback);
                    sessionInfo = await GetSessionCookies(url, statusCallback);
                    
                    if (sessionInfo == null || sessionInfo.Cookies == null || sessionInfo.Cookies.Count == 0)
                    {
                        statusCallback?.Invoke("❌ Still failed to get session cookies after retry");
                        return null;
                    }
                }
                
                statusCallback?.Invoke($"✅ Got {sessionInfo.Cookies.Count} cookies from FlareSolverr");
                
                // Step 2: Use Puppeteer with FlareSolverr cookies to interact with the page
                statusCallback?.Invoke("🎭 Using Puppeteer to click video element and capture network requests...");
                var vidUrl = await CaptureVideoUrlWithPuppeteer(url, postId, sessionInfo.Cookies, sessionInfo.UserAgent, statusCallback);
                
                if (!string.IsNullOrEmpty(vidUrl))
                {
                    statusCallback?.Invoke($"✅ Found .vid URL!");
                    
                    // Step 3: Follow redirects to get final trafficdeposit.com URL
                    statusCallback?.Invoke("🔗 Following redirects to get final URL...");
                    var finalUrl = await FollowRedirectsToFinalUrl(vidUrl, statusCallback);
                    
                    if (!string.IsNullOrEmpty(finalUrl))
                    {
                        statusCallback?.Invoke($"✅ Final video URL obtained!");
                        return finalUrl;
                    }
                }
                
                statusCallback?.Invoke($"❌ No .vid URL found for post {postId}");
                return null;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"❌ Error getting video URL: {ex.Message}");
                return null;
            }
        }
        
        private async Task<string?> FollowRedirectsToFinalUrl(string url, Action<string>? statusCallback = null)
        {
            if (string.IsNullOrEmpty(url))
                return null;
                
            try
            {
                using var httpClient = new HttpClient();
                var userAgent = GetNextUserAgent();
                httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                // Make a HEAD request to follow redirects without downloading content
                var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                
                if (response.IsSuccessStatusCode)
                {
                    var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                    if (finalUrl.Contains("trafficdeposit.com"))
                    {
                        statusCallback?.Invoke($"✅ Successfully followed redirects!");
                        return finalUrl;
                    }
                }
                
                return url; // Return original if redirect doesn't lead to trafficdeposit
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"⚠️ Error following redirects: {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}...");
                return url; // Return original URL if redirect fails
            }
        }

        private async Task<FlareSolverrResponse> SendRequestAsync(object requestData)
        {
            var json = JsonSerializer.Serialize(requestData);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(_flareSolverrUrl, content);
            var responseJson = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new Exception($"FlareSolverr HTTP error: {httpResponse.StatusCode} - {responseJson}");
            }

            var response = JsonSerializer.Deserialize<FlareSolverrResponse>(responseJson);
            return response ?? new FlareSolverrResponse { Status = "error", Message = "Failed to parse response" };
        }

        public async Task DestroySessionAsync(Action<string>? statusCallback = null)
        {
            if (string.IsNullOrEmpty(_sessionId)) return;

            try
            {
                var requestData = new
                {
                    cmd = "sessions.destroy",
                    session = _sessionId
                };

                await SendRequestAsync(requestData);
                statusCallback?.Invoke($"🗑️ FlareSolverr session destroyed: {_sessionId}");
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"❌ Error destroying session: {ex.Message}");
            }
            finally
            {
                _sessionId = "";
            }
        }

        private async Task<SessionInfo> GetSessionCookies(string url, Action<string>? statusCallback = null)
        {
            try
            {
                var currentUserAgent = GetNextUserAgent(statusCallback);
                
                // Make a request to get the page and cookies
                var requestData = new
                {
                    cmd = "request.get",
                    url = url,
                    session = _sessionId,
                    userAgent = currentUserAgent,
                    maxTimeout = 120000
                };

                var response = await SendRequestAsync(requestData);
                if (response.Status == "ok" && response.Solution != null)
                {
                    var sessionInfo = new SessionInfo
                    {
                        UserAgent = response.Solution.UserAgent ?? currentUserAgent
                    };

                    if (response.Solution.Cookies != null)
                    {
                        sessionInfo.Cookies = response.Solution.Cookies;
                    }

                    return sessionInfo;
                }

                return null;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"❌ Error getting session cookies: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> CaptureVideoUrlWithPuppeteer(string url, string postId, List<FlareSolverrCookie> cookies, string userAgent, Action<string>? statusCallback = null)
        {
            try
            {
                statusCallback?.Invoke("🚀 Launching Puppeteer with FlareSolverr session cookies...");

                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();

                var browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-accelerated-2d-canvas",
                        "--no-first-run",
                        "--no-zygote",
                        "--disable-gpu"
                    }
                });

                var page = await browser.NewPageAsync();

                // Set user agent from FlareSolverr session
                if (!string.IsNullOrEmpty(userAgent))
                {
                    await page.SetUserAgentAsync(userAgent);
                }

                // Set cookies from FlareSolverr session
                var puppeteerCookies = cookies.Select(c => new CookieParam
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires > 0 ? c.Expires : null,
                    HttpOnly = c.HttpOnly,
                    Secure = c.Secure,
                    SameSite = c.SameSite switch
                    {
                        "Strict" => SameSite.Strict,
                        "Lax" => SameSite.Lax,
                        "None" => SameSite.None,
                        _ => SameSite.Lax
                    }
                }).ToArray();

                await page.SetCookieAsync(puppeteerCookies);

                // Setup network request monitoring
                var vidUrl = "";
                var videoUrlTaskCompletion = new TaskCompletionSource<string>();

                page.Request += async (sender, e) =>
                {
                    var requestUrl = e.Request.Url;
                    
                    // Look for .vid URLs
                    if (requestUrl.Contains(".vid") && requestUrl.Contains(postId))
                    {
                        statusCallback?.Invoke($"🎯 Found .vid URL in network requests: {requestUrl}");
                        vidUrl = requestUrl;
                        videoUrlTaskCompletion.TrySetResult(requestUrl);
                    }
                    else if (requestUrl.Contains(".vid") && requestUrl.Contains("trafficdeposit"))
                    {
                        statusCallback?.Invoke($"🎯 Found trafficdeposit .vid URL: {requestUrl}");
                        vidUrl = requestUrl;
                        videoUrlTaskCompletion.TrySetResult(requestUrl);
                    }
                };

                statusCallback?.Invoke($"🌐 Navigating to: {postId}");
                await page.GoToAsync(url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle2 } });

                // Wait for page to load completely
                await Task.Delay(3000);

                // Look for the video player element and click it
                statusCallback?.Invoke("🎬 Looking for video player element...");
                
                try
                {
                    // Try to find and click the video player element
                    await page.WaitForSelectorAsync("#player_el", new WaitForSelectorOptions { Timeout = 5000 });
                    statusCallback?.Invoke("🎬 Found #player_el, clicking...");
                    await page.ClickAsync("#player_el");
                }
                catch
                {
                    // Try alternative selectors
                    var videoSelectors = new[] { "video", ".video-player", "#video", ".player" };
                    
                    foreach (var selector in videoSelectors)
                    {
                        try
                        {
                            await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Timeout = 2000 });
                            statusCallback?.Invoke($"🎬 Found {selector}, clicking...");
                            await page.ClickAsync(selector);
                            break;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                // Wait for network requests to be captured
                var timeout = Task.Delay(15000);
                var completedTask = await Task.WhenAny(videoUrlTaskCompletion.Task, timeout);

                if (completedTask == videoUrlTaskCompletion.Task)
                {
                    vidUrl = await videoUrlTaskCompletion.Task;
                }

                await browser.CloseAsync();

                return vidUrl;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"❌ Error in Puppeteer video capture: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                DestroySessionAsync().Wait(5000); // Wait max 5 seconds for cleanup
            }
            catch
            {
                // Ignore cleanup errors
            }
            _httpClient?.Dispose();
        }
    }

    public class FlareSolverrResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("session")]
        public string Session { get; set; } = "";

        [JsonPropertyName("solution")]
        public FlareSolverrSolution? Solution { get; set; }
    }

    public class FlareSolverrSolution
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("response")]
        public string Response { get; set; } = "";

        [JsonPropertyName("cookies")]
        public List<FlareSolverrCookie> Cookies { get; set; } = new List<FlareSolverrCookie>();

        [JsonPropertyName("userAgent")]
        public string UserAgent { get; set; } = "";
    }

    public class FlareSolverrCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("value")]
        public string Value { get; set; } = "";

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = "";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("expires")]
        public double Expires { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("httpOnly")]
        public bool HttpOnly { get; set; }

        [JsonPropertyName("secure")]
        public bool Secure { get; set; }

        [JsonPropertyName("session")]
        public bool Session { get; set; }

        [JsonPropertyName("sameSite")]
        public string SameSite { get; set; } = "";
    }

    // Session info class to hold cookies and user agent
    public class SessionInfo
    {
        public List<FlareSolverrCookie> Cookies { get; set; } = new List<FlareSolverrCookie>();
        public string UserAgent { get; set; } = "";
    }
}
