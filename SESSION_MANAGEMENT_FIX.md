# FlareSolverr Session Management Fix

## Problem
The original code had a session management issue where:
1. **AutomatedScraperService** used a single VideoScraper instance with one FlareSolverr session for monitoring channels
2. **VideoDownloader** created NEW VideoScraper instances (and thus new FlareSolverr sessions) every time it needed to refresh a video URL when downloads failed
3. This resulted in multiple sessions being created unnecessarily, causing performance issues and potential rate limiting

## Solution Implemented

### 1. FlareSolverrSessionManager (New Singleton Class)
Created a singleton session manager (`FlareSolverrSessionManager.cs`) that:
- **Manages a single FlareSolverr session** across the entire application
- **Handles session expiration** automatically (30-minute timeout)
- **Provides thread-safe access** to the shared session
- **Automatically renews sessions** when they expire
- **Ensures only one session exists** at any given time

Key features:
```csharp
// Get shared client (creates session if needed)
var client = await FlareSolverrSessionManager.Instance.GetClientAsync();

// Automatic session renewal when expired
await FlareSolverrSessionManager.Instance.RenewSessionAsync();
```

### 2. VideoScraper Modifications
Updated `VideoScraper` class to:
- **Use the session manager** instead of creating its own FlareSolverrClient
- **Added retry logic** with automatic session renewal on failures
- **Improved error handling** for session-related issues

Key changes:
```csharp
// Before: Created new client every time
_flareSolverr = new FlareSolverrClient();

// After: Uses shared session manager
_flareSolverr = await FlareSolverrSessionManager.Instance.GetClientAsync();
```

### 3. VideoDownloader Modifications
Modified `VideoDownloader` to:
- **Accept a shared VideoScraper instance** in constructor (optional parameter)
- **Reuse the shared scraper** when available instead of creating new ones
- **Fall back to temporary scraper** only when no shared instance is provided

Key changes:
```csharp
// Before: Always created new VideoScraper
using var tempScraper = new VideoScraper();
await tempScraper.Initialize();

// After: Use shared scraper if available
if (_videoScraper != null) {
    scraperToUse = _videoScraper; // Reuse shared instance
} else {
    scraperToUse = new VideoScraper(); // Temporary only if needed
}
```

### 4. Service Integration
Updated all services to use shared instances:
- **AutomatedScraperService**: Passes its scraper instance to VideoDownloader
- **Program.cs**: Updated all VideoDownloader instantiations to use shared scrapers where possible
- **Proper cleanup**: Added session manager disposal in cleanup methods

## Benefits

### ✅ Session Efficiency
- **One session per application** instead of multiple sessions
- **Automatic session renewal** instead of creating new sessions
- **Reduced FlareSolverr load** and better performance

### ✅ Resource Management
- **Shared browser resources** across all operations
- **Reduced memory usage** from multiple browser instances
- **Faster operation** due to session reuse

### ✅ Reliability
- **Automatic retry logic** with session renewal
- **Better error handling** for session expiration
- **Thread-safe session management**

### ✅ Maintainability
- **Centralized session management** in one class
- **Clear separation of concerns**
- **Easy to monitor and debug** session issues

## Usage Examples

### Automated Mode (Recommended)
```bash
# Uses shared session throughout the entire process
./Scrapper --auto
```

### Manual Mode
All manual operations now also benefit from shared session management:
- Channel monitoring reuses the same session
- Video downloads use the shared scraper when possible
- Session automatically renews when expired

## Configuration
- **Session timeout**: 30 minutes (configurable in FlareSolverrSessionManager)
- **FlareSolverr URL**: `http://192.168.1.4:8191/v1` (matches your setup)
- **Automatic retry**: Up to 2 retries with session renewal on failure

## Files Modified
1. `FlareSolverrSessionManager.cs` - New singleton session manager
2. `Program.cs` - Updated VideoScraper to use session manager + retry logic
3. `VideoDownloader.cs` - Added shared scraper support
4. `AutomatedScraperService.cs` - Passes shared scraper to downloader
5. `FlareSolverrClient.cs` - Updated URL to match your configuration

The solution ensures that your application now uses exactly **one FlareSolverr session** for all operations, automatically handling session expiration and renewal as needed. This should significantly reduce the session management issues you were experiencing during video downloads.
