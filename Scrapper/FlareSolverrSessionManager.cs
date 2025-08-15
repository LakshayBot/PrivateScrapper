using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleScraper
{
    /// <summary>
    /// Singleton session manager for FlareSolverr to ensure only one session is created
    /// and reused across all operations. Handles session expiration and renewal automatically.
    /// </summary>
    public class FlareSolverrSessionManager : IDisposable
    {
        private static FlareSolverrSessionManager? _instance;
        private static readonly object _lock = new object();
        
        private FlareSolverrClient? _client;
        private DateTime _sessionCreatedAt;
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30); // Sessions expire after 30 minutes
        private readonly SemaphoreSlim _sessionSemaphore = new SemaphoreSlim(1, 1);
        private bool _disposed = false;

        private FlareSolverrSessionManager()
        {
            // Private constructor for singleton
        }

        public static FlareSolverrSessionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new FlareSolverrSessionManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public async Task<FlareSolverrClient> GetClientAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlareSolverrSessionManager));

            await _sessionSemaphore.WaitAsync();
            try
            {
                // Check if we need to create a new client or renew session
                if (_client == null || IsSessionExpired())
                {
                    Console.WriteLine("Creating new FlareSolverr session...");
                    
                    // Clean up old client if it exists
                    if (_client != null)
                    {
                        try
                        {
                            await _client.DestroySessionAsync();
                            _client.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Error cleaning up old FlareSolverr session: {ex.Message}");
                        }
                    }

                    // Create new client and session
                    _client = new FlareSolverrClient();
                    
                    // Test connection first
                    bool isConnected = await _client.TestConnectionAsync();
                    if (!isConnected)
                    {
                        _client.Dispose();
                        _client = null;
                        throw new Exception("Cannot connect to FlareSolverr. Please ensure FlareSolverr is running on http://192.168.1.3:8191/v1");
                    }

                    // Create session
                    await _client.CreateSessionAsync();
                    _sessionCreatedAt = DateTime.Now;
                    
                    Console.WriteLine($"✅ FlareSolverr session created successfully at {_sessionCreatedAt}");
                }

                return _client;
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        private bool IsSessionExpired()
        {
            return _client != null && DateTime.Now - _sessionCreatedAt > _sessionTimeout;
        }

        public async Task RenewSessionAsync()
        {
            if (_disposed)
                return;

            await _sessionSemaphore.WaitAsync();
            try
            {
                if (_client != null)
                {
                    Console.WriteLine("Renewing FlareSolverr session...");
                    
                    try
                    {
                        await _client.DestroySessionAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Error destroying old session during renewal: {ex.Message}");
                    }

                    try
                    {
                        await _client.CreateSessionAsync();
                        _sessionCreatedAt = DateTime.Now;
                        Console.WriteLine($"✅ FlareSolverr session renewed successfully at {_sessionCreatedAt}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error renewing session: {ex.Message}");
                        // If renewal fails, mark client as null so a new one will be created
                        _client.Dispose();
                        _client = null;
                        throw;
                    }
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _sessionSemaphore.Wait(5000); // Wait max 5 seconds
                
                if (_client != null)
                {
                    try
                    {
                        _client.DestroySessionAsync().Wait(5000);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                    
                    _client.Dispose();
                    _client = null;
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                _sessionSemaphore.Release();
            }

            _sessionSemaphore.Dispose();
            
            lock (_lock)
            {
                _instance = null;
            }
        }
    }
}
