using System;
using System.Net.Http;
using System.Threading.Tasks;

class TestFlareSolverr
{
    static async Task Main()
    {
        using var client = new HttpClient();
        try
        {
            var response = await client.GetAsync("http://localhost:8191/v1");
            Console.WriteLine($"Status: {response.StatusCode}");
            Console.WriteLine($"Is running: {response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
