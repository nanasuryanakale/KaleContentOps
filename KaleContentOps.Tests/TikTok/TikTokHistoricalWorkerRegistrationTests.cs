using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.Tests.TikTok
{
    public class TikTokHistoricalWorkerRegistrationTests
    {
        private ServiceProvider BuildProviderWithFlag(bool enabled)
        {
            var inMemory = new Dictionary<string, string?>
            {
                { "TikTok:EnableHistoricalDetailsP1Scheduler", enabled ? "true" : "false" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TikTokOptions>(config.GetSection("TikTok"));

            // replicate the conditional registration used in Program.cs
            if (config.GetValue<bool>("TikTok:EnableHistoricalDetailsP1Scheduler", false))
            {
                services.AddHostedService<TikTokHistoricalDetailsP1Worker>();
            }

            return services.BuildServiceProvider();
        }

        [Fact]
        public void Worker_IsRegistered_WhenFlagTrue()
        {
            using var provider = BuildProviderWithFlag(true);
            var hosted = provider.GetServices<IHostedService>().ToList();
            Assert.Contains(hosted, h => h.GetType() == typeof(TikTokHistoricalDetailsP1Worker));
        }

        [Fact]
        public void Worker_IsNotRegistered_WhenFlagFalse()
        {
            using var provider = BuildProviderWithFlag(false);
            var hosted = provider.GetServices<IHostedService>().ToList();
            Assert.DoesNotContain(hosted, h => h.GetType() == typeof(TikTokHistoricalDetailsP1Worker));
        }

        [Fact]
        public void Worker_CanBeConstructed_ByDI()
        {
            using var provider = BuildProviderWithFlag(true);
            // ensure we can construct the concrete worker type via service provider
            var hosted = provider.GetServices<IHostedService>().ToList();
            var instance = hosted.FirstOrDefault(h => h.GetType() == typeof(TikTokHistoricalDetailsP1Worker));
            Assert.NotNull(instance);
        }
    }
}
