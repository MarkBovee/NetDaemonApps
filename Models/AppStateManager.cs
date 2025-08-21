using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NetDaemonApps.Models
{
    /// <summary>
    /// The app state manager class
    /// </summary>
    public static class AppStateManager
    {
        /// <summary>
        /// The base directory
        /// </summary>
        private static readonly string StateFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "State");

        /// <summary>
        /// The state cache
        /// </summary>
        private static Dictionary<string, Dictionary<string, object>> _stateCache = new();

        /// <summary>
        /// The lock
        /// </summary>
        private static readonly object _lock = new();

        /// <summary>
        /// The logger
        /// </summary>
        private static ILogger? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AppStateManager"/> class
        /// </summary>
        static AppStateManager()
        {
            LoadState();
        }

        /// <summary>
        /// Sets the logger using the specified logger
        /// </summary>
        /// <param name="logger">The logger</param>
        public static void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads the state
        /// </summary>
        private static void LoadState()
        {
            lock (_lock)
            {
                if (!File.Exists(StateFilePath))
                {
                    _stateCache = new Dictionary<string, Dictionary<string, object>>();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(StateFilePath);
                    _stateCache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json) ?? new Dictionary<string, Dictionary<string, object>>();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load state from file: {FilePath}", StateFilePath);
                    _stateCache = new Dictionary<string, Dictionary<string, object>>();
                }
            }
        }

        /// <summary>
        /// Saves the state
        /// </summary>
        private static void SaveState()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_stateCache, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(StateFilePath, json);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to save state to file: {FilePath}", StateFilePath);
                }
            }
        }

        /// <summary>
        /// Gets the state using the specified app name
        /// </summary>
        /// <typeparam name="T">The </typeparam>
        /// <param name="appName">The app name</param>
        /// <param name="key">The key</param>
        /// <returns>The</returns>
        public static T? GetState<T>(string appName, string key)
        {
            lock (_lock)
            {
                if (_stateCache.TryGetValue(appName, out var appState) && appState != null && appState.TryGetValue(key, out var value))
                {
                    if (value is JsonElement elem)
                    {
                        return elem.Deserialize<T>();
                    }
                    return (T?)Convert.ChangeType(value, typeof(T));
                }
                return default;
            }
        }

        /// <summary>
        /// Sets the state using the specified app name
        /// </summary>
        /// <typeparam name="T">The </typeparam>
        /// <param name="appName">The app name</param>
        /// <param name="key">The key</param>
        /// <param name="value">The value</param>
        /// <exception cref="ArgumentNullException"></exception>
        public static void SetState<T>(string appName, string key, T value)
        {
            lock (_lock)
            {
                if (!_stateCache.ContainsKey(appName) || _stateCache[appName] == null)
                {
                    _stateCache[appName] = new Dictionary<string, object>();
                }
                _stateCache[appName][key] = value ?? throw new ArgumentNullException(nameof(value));
                SaveState();
            }
        }
    }
}
