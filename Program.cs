// -----------------------------------------------------------------------------
// Program entry point for NetDaemonApps
// This file configures and starts the NetDaemon host
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Extensions.Tts;
using NetDaemon.Runtime;
using System.Reflection;

using NetDaemonApps.Models.EnergyPrices;
using NetDaemonApps.Models.Battery;
using Microsoft.Extensions.Options;

try
{
    // Run self-tests for password hashing and signature generation
    //SAJPowerBatteryApi.RunElekeeperSelfTests();

    // Create and configure the host builder
    await Host.CreateDefaultBuilder(args)
        .UseNetDaemonAppSettings()                                      // Load NetDaemon app settings
        .UseNetDaemonDefaultLogging()                                   // Configure logging
        .UseNetDaemonRuntime()                                          // Add NetDaemon runtime
        .UseNetDaemonTextToSpeech()                                     // Add TTS support
        .ConfigureServices((context, services) =>
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())   // Register app assemblies
                .AddNetDaemonStateManager()                             // Add state manager
                .AddNetDaemonScheduler()                                // Add scheduler
                // Battery options and SAJ API
                .Configure<BatteryOptions>(context.Configuration.GetSection("Battery"))
                .AddSingleton(sp =>
                {
                    var opt = sp.GetRequiredService<IOptions<BatteryOptions>>().Value;
                    return new SAJPowerBatteryApi(
                        username: opt.Username ?? string.Empty,
                        password: opt.Password ?? string.Empty,
                        deviceSerialNumber: opt.DeviceSerialNumber ?? string.Empty,
                        baseUrl: opt.BaseUrl ?? "https://eop.saj-electric.com"
                    );
                })

        // Add next line if using code generator
        // .AddHomeAssistantGenerated()

        .AddScoped<IPriceHelper, PriceHelper>(sp =>
                new PriceHelper(
                    ha: sp.GetRequiredService<IHaContext>(),
                    logger: sp.GetRequiredService<ILogger<PriceHelper>>(),
                    scheduler: sp.GetRequiredService<INetDaemonScheduler>()
                )
            )
        )
        .Build()
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception e)
{
    // Log and rethrow any startup exceptions
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}
