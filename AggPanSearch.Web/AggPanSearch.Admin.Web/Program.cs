using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NLog.Web;

namespace AggPanSearch.Admin.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder => webBuilder
                    .UseUrls("http://*:5000")
                    .UseStartup<Startup>())
                .ConfigureLogging(logging =>
                   {
                       logging.ClearProviders();
                       logging.SetMinimumLevel(LogLevel.Trace);
                   }).UseNLog();
    }
}
