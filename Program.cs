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
using System.Diagnostics;
using System.Threading.Tasks;

using NetDaemonApps.Models.EnergyPrices;
using NetDaemonApps.Models.Battery;
using Microsoft.Extensions.Options;

try
{
    // Run self-tests for password hashing and signature generation
    //SAJPowerBatteryApi.RunElekeeperSelfTests();

    // Create and configure the host builder
    var host = Host.CreateDefaultBuilder(args)
        .UseNetDaemonAppSettings()                                      // Load NetDaemon app settings
        .UseNetDaemonDefaultLogging()                                   // Configure logging
        .UseNetDaemonRuntime()                                          // Add NetDaemon runtime
        .UseNetDaemonTextToSpeech()                                     // Add TTS support
        .ConfigureServices((context, services) =>
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())   // Register app assemblies
                .AddNetDaemonStateManager()                             // Add state manager
                .AddNetDaemonScheduler()                                // Add scheduler
                .Configure<BatteryOptions>(context.Configuration.GetSection("Battery"))
                .AddSingleton(sp =>
                {
                    var opt = sp.GetRequiredService<IOptions<BatteryOptions>>().Value;
                    return new SAJPowerBatteryApi(
                        username: opt.Username ?? string.Empty,
                        password: opt.Password ?? string.Empty,
                        deviceSerialNumber: opt.DeviceSerialNumber ?? string.Empty,
                        plantUid: opt.PlantUid ?? string.Empty,
                        baseUrl: opt.BaseUrl ?? string.Empty
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
        .Build();

    // If in development environment, allow apps to initialize then terminate
    var environment = host.Services.GetRequiredService<IHostEnvironment>();
    if (environment.IsDevelopment())
    {
        Console.WriteLine("Development mode: Starting host for app initialization...");
        await host.StartAsync();
        
        // Give apps time to initialize and run their startup logic
        await Task.Delay(TimeSpan.FromSeconds(10));
        
        Console.WriteLine("Development mode: Stopping host after initialization...");
        await host.StopAsync();
        return;
    }
    
    // Normal production run
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception e)
{
    // Log and rethrow any startup exceptions
    Console.WriteLine($"Failed to start host... {e}");
    throw;
}
