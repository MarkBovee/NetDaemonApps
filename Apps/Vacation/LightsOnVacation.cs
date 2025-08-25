namespace NetDaemonApps.Apps.Vacation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Diagnostics;
    using HomeAssistantGenerated;
    using Microsoft.Extensions.Logging;
    using NetDaemon.Extensions.Scheduler;
    using NetDaemon.HassModel;

    /// <summary>
    /// Vacation lights automation: randomly turns on lights when it's dark to simulate presence.
    /// </summary>
    [NetDaemonApp]
    public class LightsOnVacation
    {
        /// <summary>
        /// The ha
        /// </summary>
        private readonly IHaContext _ha;

        /// <summary>
        /// The scheduler
        /// </summary>
        private readonly INetDaemonScheduler _scheduler;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<LightsOnVacation> _logger;

        /// <summary>
        /// The services
        /// </summary>
        private readonly Services _services;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        // Configurable: entity id for vacation mode (input_boolean)
        /// <summary>
        /// The vacation mode entity
        /// </summary>
        private const string VacationModeEntity = "input_boolean.vacation_mode";

        // Configurable: number of lights to turn on each day
        /// <summary>
        /// The lights per day
        /// </summary>
        private const int LightsPerDay = 3;

        // Configurable: time to turn off all lights (e.g., 00:30)
        /// <summary>
        /// The lights off time
        /// </summary>
        private readonly TimeSpan _lightsOffTime = new(0, 30, 0);

        /// <summary>
        /// The candidate light entity ids
        /// </summary>
        private List<string> _candidateLightEntityIds;

        /// <summary>
        /// Initializes a new instance of the <see cref="LightsOnVacation"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        public LightsOnVacation(IHaContext ha, INetDaemonScheduler scheduler, ILogger<LightsOnVacation> logger)
        {
            _ha = ha;
            _scheduler = scheduler;
            _logger = logger;
            _services = new Services(ha);
            _entities = new Entities(ha);

            _candidateLightEntityIds = DiscoverLightEntities();
            _logger.LogInformation($"Discovered candidate lights for vacation: {string.Join(", ", _candidateLightEntityIds)}");

            if (Debugger.IsAttached)
            {
                // Output the list of candidate lights for inspection in debug mode
                _logger.LogInformation("Debugger attached. Candidate light entities:");
                foreach (var entity in _candidateLightEntityIds)
                {
                    _logger.LogInformation($"  {entity}");
                }
                // Do not run automation logic in debug mode
                return;
            }

            _logger.LogInformation("Started LightsOnVacation program");

            // Schedule daily routine at sunset
            ScheduleDailyAtSunset();

            // Schedule lights off at configured time
            _scheduler.RunEvery(TimeSpan.FromDays(1), () =>
            {
                var offTime = DateTime.Today.Add(_lightsOffTime);
                var now = DateTime.Now;
                var delay = offTime > now ? offTime - now : TimeSpan.Zero;
                _scheduler.RunIn(delay, () =>
                {
                    if (!IsVacationModeActive())
                    {
                        return;
                    }

                    foreach (var light in _candidateLightEntityIds)
                    {
                        _services.Light.TurnOff(new NetDaemon.HassModel.Entities.ServiceTarget { EntityIds = new[] { light } });
                    }

                    _logger.LogInformation("Turned off all vacation lights.");
                });
            });
        }

        /// <summary>
        /// Schedules the daily at sunset
        /// </summary>
        private void ScheduleDailyAtSunset()
        {
            _scheduler.RunEvery(TimeSpan.FromDays(1), () =>
            {
                var sunset = GetSunsetTime();
                var now = DateTime.Now;
                var delay = sunset > now ? sunset - now : TimeSpan.Zero;
                _scheduler.RunIn(delay, () =>
                {
                    if (!IsVacationModeActive())
                    {
                        _logger.LogInformation("Vacation mode is not active. Skipping lights automation.");
                        return;
                    }

                    var selectedLights = PickRandomLights();
                    foreach (var light in selectedLights)
                    {
                        _services.Light.TurnOn(new NetDaemon.HassModel.Entities.ServiceTarget { EntityIds = new[] { light } });
                        _logger.LogInformation($"Turned on {light} for vacation simulation.");
                    }

                    // Optionally, schedule hourly changes for realism
                    ScheduleHourlyLightChanges(selectedLights);
                });
            });
        }

        /// <summary>
        /// Gets the sunset time
        /// </summary>
        /// <returns>The date time</returns>
        private DateTime GetSunsetTime()
        {
            var sun = _entities.Sun.Sun;
            var sunsetStr = sun.Attributes?.NextSetting;
            if (DateTime.TryParse(sunsetStr, out var sunset))
            {
                return sunset;
            }

            return DateTime.Today.AddHours(20); // fallback 20:00
        }

        /// <summary>
        /// Describes whether this instance is vacation mode active
        /// </summary>
        /// <returns>The bool</returns>
        private bool IsVacationModeActive()
        {
            var vacation = _ha.GetState(VacationModeEntity);
            return vacation?.State == "on";
        }

        /// <summary>
        /// Discovers the light entities
        /// </summary>
        /// <returns>A list of string</returns>
        private List<string> DiscoverLightEntities()
        {
            var allEntities = _ha.GetAllEntities();
            var lights = allEntities.Where(e => e.EntityId.StartsWith("light.")).Select(e => e.EntityId);
            var switches = allEntities
                .Where(e => e.EntityId.StartsWith("switch.") && IsLikelyLightSwitch(e))
                .Select(e => e.EntityId);
            return lights.Concat(switches).Distinct().ToList();
        }

        /// <summary>
        /// The light keywords
        /// </summary>
        private static readonly string[] LightKeywords =
        {
            "light", "lamp", "led", "spot", "moodlight", "verlichting"
        };

        /// <summary>
        /// The exclude keywords
        /// </summary>
        private static readonly string[] ExcludeKeywords =
        {
            "badkamer", "vaatwasser", "tv", "computer", "audio", "droger", "vriezer", "ems", "wasmachine", "auto_off", "auto_update", "shuffle", "repeat", "do_not_disturb"
        };

        /// <summary>
        /// Describes whether this instance is likely light switch
        /// </summary>
        /// <param name="e">The </param>
        /// <returns>The bool</returns>
        private bool IsLikelyLightSwitch(NetDaemon.HassModel.Entities.Entity e)
        {
            var name = e.EntityId.ToLowerInvariant();
            var friendly = string.Empty;
            if (e.Attributes != null && e.Attributes.ContainsKey("friendly_name") && e.Attributes["friendly_name"] != null)
            {
                friendly = e.Attributes["friendly_name"].ToString().ToLowerInvariant();
            }

            // Exclude if any exclusion keyword is present
            foreach (var exclude in ExcludeKeywords)
            {
                if (name.Contains(exclude) || friendly.Contains(exclude))
                {
                    return false;
                }
            }

            // Include if any light keyword is present
            foreach (var keyword in LightKeywords)
            {
                if (name.Contains(keyword) || friendly.Contains(keyword))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Picks the random lights
        /// </summary>
        /// <returns>A list of string</returns>
        private List<string> PickRandomLights()
        {
            var rnd = new Random(DateTime.Now.DayOfYear);
            return _candidateLightEntityIds.OrderBy(_ => rnd.Next()).Take(LightsPerDay).ToList();
        }

        /// <summary>
        /// Schedules the hourly light changes using the specified initial lights
        /// </summary>
        /// <param name="initialLights">The initial lights</param>
        private void ScheduleHourlyLightChanges(List<string> initialLights)
        {
            var now = DateTime.Now;
            var offTime = DateTime.Today.Add(_lightsOffTime);
            var hours = (int)(offTime - now).TotalHours;
            for (var i = 1; i < hours; i++)
            {
                _scheduler.RunIn(TimeSpan.FromHours(i), () =>
                {
                    if (!IsVacationModeActive())
                    {
                        return;
                    }

                    var newLights = PickRandomLights();
                    foreach (var light in _candidateLightEntityIds)
                    {
                        if (newLights.Contains(light))
                        {
                            _services.Light.TurnOn(new NetDaemon.HassModel.Entities.ServiceTarget { EntityIds = new[] { light } });
                        }
                        else
                        {
                            _services.Light.TurnOff(new NetDaemon.HassModel.Entities.ServiceTarget { EntityIds = new[] { light } });
                        }
                    }

                    _logger.LogInformation($"Changed vacation lights: {string.Join(", ", newLights)}");
                });
            }
        }
    }
}
