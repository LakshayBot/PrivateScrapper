#!/bin/bash

echo "Setting up FlareSolverr + HttpClient for your scraper..."
echo "=================================================="

# Check if Docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker Desktop first."
    echo "   You can download Docker Desktop from: https://www.docker.com/products/docker-desktop/"
    exit 1
fi

echo "✅ Docker is running"

# Check if FlareSolverr container already exists
if docker ps -a --format 'table {{.Names}}' | grep -q flaresolverr; then
    echo "📦 FlareSolverr container already exists"
    
    # Check if it's running
    if docker ps --format 'table {{.Names}}' | grep -q flaresolverr; then
        echo "✅ FlareSolverr is already running"
    else
        echo "🔄 Starting existing FlareSolverr container..."
        docker start flaresolverr
    fi
else
    echo "📦 Creating and starting FlareSolverr container..."
    docker run -d \
        --name=flaresolverr \
        -p 8191:8191 \
        -e LOG_LEVEL=info \
        --restart unless-stopped \
        ghcr.io/flaresolverr/flaresolverr:latest
fi

# Wait a moment for the service to start
echo "⏳ Waiting for FlareSolverr to be ready..."
sleep 5

# Test FlareSolverr connection
if curl -s http://localhost:8191 > /dev/null; then
    echo "✅ FlareSolverr is running and accessible at http://localhost:8191"
else
    echo "⚠️  FlareSolverr might still be starting up. Please wait a moment and try running your scraper."
fi

echo ""
echo "🎉 Setup complete!"
echo ""
echo "Key changes made to your scraper:"
echo "✅ Removed PuppeteerSharp dependency"
echo "✅ Added FlareSolverr client for Cloudflare bypass"
echo "✅ Simplified HTTP-only scraping approach"
echo "✅ Better reliability and performance"
echo ""
echo "To use your scraper:"
echo "1. Make sure FlareSolverr is running (this script started it)"
echo "2. Run your scraper: dotnet run"
echo "3. FlareSolverr will handle all Cloudflare challenges automatically"
echo ""
echo "FlareSolverr status:"
echo "🌐 Web interface: http://localhost:8191"
echo "📊 To stop: docker stop flaresolverr"
echo "🔄 To restart: docker restart flaresolverr"
