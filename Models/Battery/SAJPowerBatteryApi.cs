// -----------------------------------------------------------------------------
// Helper class for interacting with the SAJ Power Battery API
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Models.Battery
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides methods to interact with the SAJ Power Battery API, including authentication and schedule management.
    /// </summary>
    public class SAJPowerBatteryApi
    {
        /// <summary>
        /// The http client
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// The username
        /// </summary>
        private readonly string _username;

        /// <summary>
        /// The base url
        /// </summary>
        private readonly string _baseUrl;

        /// <summary>
        /// The SAJ-token
        /// </summary>
        private string? _token;

        /// <summary>
        /// The SAJ-token expiration
        /// </summary>
        private DateTime? _tokenExpiration;

        /// <summary>
        /// The password
        /// </summary>
        private readonly string _password;

        /// <summary>
        /// The device serial number
        /// </summary>
        private readonly string _deviceSerialNumber;

        /// <summary>
        /// The plant UID for the device
        /// </summary>
        private readonly string _plantUid;

        #region HAR default values and static API parameters
        /// <summary>
        /// Elekeeper AES-ECB password encryption key
        /// </summary>
        private const string PasswordEncryptionKey = "ec1840a7c53cf0709eb784be480379b6";

        /// <summary>
        /// Elekeeper query signing key
        /// </summary>
        private const string QuerySignKey = "ktoKRLgQPjvNyUZO8lVc9kU1Bsip6XIe";

        /// <summary>
        /// The default oper type
        /// </summary>
        private const int DefaultOperType = 15;

        /// <summary>
        /// The default is parallel batch setting
        /// </summary>
        private const int DefaultIsParallelBatchSetting = 0;

        /// <summary>
        /// The default app project name
        /// </summary>
        private const string DefaultAppProjectName = "elekeeper";

        /// <summary>
        /// The default lang
        /// </summary>
        private const string DefaultLang = "en";

        /// <summary>
        /// The client id
        /// </summary>
        private const string ClientId = "esolar-monitor-admin";

        #endregion

        /// <summary>
        /// Indicates whether the API has valid configuration (username, password, device SN, plant UID, base URL).
        /// </summary>
        public bool IsConfigured { get; }

        /// <summary>
        /// If configuration is invalid, contains the human-readable reason.
        /// </summary>
        public string? ConfigurationError { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SAJPowerBatteryApi"/> class
        /// </summary>
        /// <param name="username">The username</param>
        /// <param name="password">The password</param>
        /// <param name="deviceSerialNumber">The device serial number</param>
        /// <param name="plantUid">The plant UID for the device</param>
        /// <param name="baseUrl">The base url</param>
        public SAJPowerBatteryApi(string username, string password, string deviceSerialNumber, string plantUid, string baseUrl = "https://eop.saj-electric.com")
        {
            _username = username;
            _password = password;
            _deviceSerialNumber = deviceSerialNumber;
            _plantUid = plantUid;
            _baseUrl = baseUrl;
            _httpClient = new HttpClient();

            // Validate configuration
            string? error = null;
            if (string.IsNullOrWhiteSpace(_username)) error = (error == null ? "Missing Username" : error + ", Username");
            if (string.IsNullOrWhiteSpace(_password)) error = (error == null ? "Missing Password" : error + ", Password");
            if (string.IsNullOrWhiteSpace(_deviceSerialNumber)) error = (error == null ? "Missing DeviceSerialNumber" : error + ", DeviceSerialNumber");
            if (string.IsNullOrWhiteSpace(_plantUid)) error = (error == null ? "Missing PlantUid" : error + ", PlantUid");
            if (string.IsNullOrWhiteSpace(_baseUrl) || !Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
                error = (error == null ? "Invalid BaseUrl" : error + ", BaseUrl");

            IsConfigured = string.IsNullOrEmpty(error);
            ConfigurationError = error;

            if (!IsConfigured)
            {
                Console.Error.WriteLine($"SAJ API not configured: {ConfigurationError}");
            }
        }

        /// <summary>
        /// Checks if the current SAJ-token is valid (not expired).
        /// </summary>
        /// <returns>True if valid, false otherwise.</returns>
        public bool IsTokenValid(bool printStatus = false)
        {
            var isValid = false;

            if (ReadTokenFromFile())
            {
                isValid = !string.IsNullOrEmpty(_token) && _tokenExpiration.HasValue && _tokenExpiration > DateTime.UtcNow;
            }

            if (printStatus)
            {
                Console.WriteLine(isValid ? $"SAJ token is valid until {_tokenExpiration!.Value.ToLocalTime()}" : "SAJ token is invalid or expired.");
            }

            return isValid;
        }

        /// <summary>
        /// Ensures authentication: checks for a valid SAJ-token, authenticates if needed.
        /// </summary>
        /// <returns>The authentication SAJ-token.</returns>
        private async Task<string> EnsureAuthenticatedAsync()
        {
            // Try to read token from file
            if (IsTokenValid())
            {
                SetToken(_token);
                return _token!;
            }

            var token = await Authenticate();

            return token;
        }

        /// <summary>
        /// Reads the token and expiration from the SAJ-token file.
        /// </summary>
        private bool ReadTokenFromFile()
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "SAJ-token");

            if (!File.Exists(filePath))
            {
                return false;
            }
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("token", out var tokenElem) && root.TryGetProperty("expiresIn", out var expiresElem))
                {
                    _token = tokenElem.GetString();
                    var expiresIn = expiresElem.GetInt32();

                    // Calculate expiration from file's last write time
                    var fileTime = File.GetLastWriteTimeUtc(filePath);

                    _tokenExpiration = fileTime.AddSeconds(expiresIn);

                    return true;
                }
            }
            catch (Exception ex)
            {
                // Log the error for diagnostics, but do not throw
                Console.Error.WriteLine($"Error reading SAJ-token file: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Writes the token and expiration to the SAJ-token file.
        /// </summary>
        private static void WriteTokenToFile(string token, int expiresIn)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "SAJ-token");
            var obj = new { token, expiresIn };
            var json = JsonSerializer.Serialize(obj);

            File.WriteAllText(filePath, json);

            Console.WriteLine("New SAJ-token saved successfully.");
        }

        /// <summary>
        /// Builds the complete BatteryScheduleParameters object for the battery charge/discharge schedule.
        /// Analyzes the periods to determine the correct communication patterns based on charge/discharge counts.
        /// </summary>
        /// <param name="chargingPeriods">List of charging/discharging periods</param>
        /// <returns>The complete BatteryScheduleParameters object.</returns>
        public static BatteryScheduleParameters BuildBatteryScheduleParameters(List<ChargingPeriod> chargingPeriods)
        {
            if (chargingPeriods == null || !chargingPeriods.Any())
            {
                throw new ArgumentException("At least one charging period is required", nameof(chargingPeriods));
            }

            // Count charge and discharge periods
            var chargePeriods = chargingPeriods.Where(p => p.ChargeType == BatteryChargeType.Charge).ToList();
            var dischargePeriods = chargingPeriods.Where(p => p.ChargeType == BatteryChargeType.Discharge).ToList();

            Console.WriteLine($"Schedule analysis: {chargePeriods.Count} charge periods, {dischargePeriods.Count} discharge periods");

            // Validate minimum requirements
            if (chargePeriods.Count == 0 && dischargePeriods.Count == 0)
            {
                throw new ArgumentException("At least one charge or discharge period is required", nameof(chargingPeriods));
            }

            // Build the value string: mode|period1|period2|...
            var valueBuilder = new StringBuilder("1"); // Mode 1 = enabled

            foreach (var period in chargingPeriods)
            {
                valueBuilder.Append("|").Append(period.ToApiFormat());
            }

            var value = valueBuilder.ToString();

            // Generate address patterns based on charge/discharge pattern
            var (commAddress, componentId, transferId) = GenerateAddressPatterns(chargingPeriods);

            return new BatteryScheduleParameters
            {
                CommAddress = commAddress,
                ComponentId = componentId,
                TransferId = transferId,
                Value = value
            };
        }

        /// <summary>
        /// Generates the communication address patterns based on the charge/discharge pattern.
        /// These patterns are required by the SAJ API for proper device communication.
        /// </summary>
        /// <param name="chargingPeriods">List of charging/discharging periods</param>
        /// <returns>Tuple of (commAddress, componentId, transferId)</returns>
        private static (string commAddress, string componentId, string transferId) GenerateAddressPatterns(List<ChargingPeriod> chargingPeriods)
        {
            var chargePeriods = chargingPeriods.Where(p => p.ChargeType == BatteryChargeType.Charge).Count();
            var dischargePeriods = chargingPeriods.Where(p => p.ChargeType == BatteryChargeType.Discharge).Count();

            // Pattern selection based on charge/discharge combination
            return (chargePeriods, dischargePeriods) switch
            {
                // 1 charge + 1 discharge
                (1, 1) => (
                    commAddress: "3647|3606|3607|3608_3608|361B|361C|361D_361D",
                    componentId: "|30|30|30_30|30|30|30_30",
                    transferId: "|5|5|2_1|5|5|2_1"
                ),

                // 1 charge + 2 discharge (from HAR file)
                (1, 2) => (
                    commAddress: "3647|3606|3607|3608_3608|361B|361C|361D_361D|361E|361F|3620_3620",
                    componentId: "|30|30|30_30|30|30|30_30|30|30|30_30",
                    transferId: "|5|5|2_1|5|5|2_1|5|5|2_1"
                ),

                // 3 charge + 1 discharge (from HAR file)
                (3, 1) => (
                    commAddress: "3647|3606|3607|3608_3608|3609|360A|360B_360B|360C|360D|360E_360E|361B|361C|361D_361D",
                    componentId: "|30|30|30_30|30|30|30_30|30|30|30_30|30|30|30_30",
                    transferId: "|5|5|2_1|5|5|2_1|5|5|2_1|5|5|2_1"
                ),

                // 2 charge + 1 discharge (future pattern)
                (2, 1) => (
                    commAddress: "3647|3606|3607|3608_3608|3609|360A|360B_360B|361B|361C|361D_361D",
                    componentId: "|30|30|30_30|30|30|30_30|30|30|30_30",
                    transferId: "|5|5|2_1|5|5|2_1|5|5|2_1"
                ),

                // For other patterns, extrapolate
                _ => GenerateExtendedAddressPattern(chargePeriods + dischargePeriods)
            };
        }

        /// <summary>
        /// Generates address patterns for custom period counts by extrapolating the known patterns.
        /// </summary>
        /// <param name="periodCount">Number of periods</param>
        /// <returns>Tuple of (commAddress, componentId, transferId)</returns>
        private static (string commAddress, string componentId, string transferId) GenerateExtendedAddressPattern(int periodCount)
        {
            // Base patterns for single period
            var baseCommPattern = new[] { "3647", "3606", "3607", "3608" };
            var baseComponentPattern = new[] { "", "30", "30", "30" };
            var baseTransferPattern = new[] { "", "5", "5", "2" };

            var commParts = new List<string>();
            var componentParts = new List<string>();
            var transferParts = new List<string>();

            for (int i = 0; i < periodCount; i++)
            {
                var basePart = string.Join("|", baseCommPattern);
                var componentPart = string.Join("|", baseComponentPattern);
                var transferPart = string.Join("|", baseTransferPattern);

                // For periods beyond the first, modify the pattern (add offset)
                if (i > 0)
                {
                    // Add hex offset to addresses for additional periods
                    var offset = i * 4;
                    var modifiedComm = baseCommPattern.Skip(1).Select(addr =>
                        (Convert.ToInt32(addr, 16) + offset).ToString("X")).ToArray();
                    basePart = baseCommPattern[0] + "|" + string.Join("|", modifiedComm);
                    transferPart += "_1"; // Add weekday suffix
                }

                commParts.Add(basePart);
                componentParts.Add(componentPart);
                transferParts.Add(transferPart);
            }

            return (
                commAddress: string.Join("_", commParts),
                componentId: string.Join("_", componentParts),
                transferId: string.Join("_", transferParts)
            );
        }

        public async Task<bool> ClearBatteryScheduleAsync()
        {
            if (!IsConfigured)
            {
                Console.Error.WriteLine($"Cannot clear schedule: SAJ API not configured ({ConfigurationError})");
                return false;
            }
            await EnsureAuthenticatedAsync();
            try
            {
                var url = $"{_baseUrl}/dev-api/api/v1/remote/client/saveCommonParaRemoteSetting";
                var clientDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                var random = Guid.NewGuid().ToString("N");

                // Prepare parameters for signing
                var signParamsDict = new Dictionary<string, string>
                {
                    { "appProjectName", DefaultAppProjectName },
                    { "clientDate", clientDate },
                    { "lang", DefaultLang },
                    { "timeStamp", timeStamp },
                    { "random", random },
                    { "clientId", ClientId }
                };
                var signedParams = CalcSignatureElekeeper(signParamsDict);

                // Add clear-schedule-specific parameters
                signedParams["deviceSn"] = _deviceSerialNumber;
                signedParams["isParallelBatchSetting"] = DefaultIsParallelBatchSetting.ToString();
                signedParams["commAddress"] = "3647|3647";
                signedParams["componentId"] = "|0";
                signedParams["operType"] = DefaultOperType.ToString();
                signedParams["transferId"] = "|";
                signedParams["value"] = "0|0";

                var content = new FormUrlEncodedContent(signedParams);
                var response = await _httpClient.PostAsync(url, content);
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var errCode = doc.RootElement.GetProperty("errCode").GetInt32();

                var success = errCode == 0;

                Console.WriteLine(success ? "Battery schedule cleared successfully." : $"Failed to clear battery schedule. Error code: {errCode}, Message: {doc.RootElement.GetProperty("errMsg").GetString()}");

                return success;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        /// <summary>
        /// Saves the battery charge/discharge schedule to the remote API, using dynamic parameters.
        /// </summary>
        /// <param name="batterySchedule">Dynamic parameters for the schedule.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public async Task<bool> SaveBatteryScheduleAsync(BatteryScheduleParameters batterySchedule)
        {
            if (!IsConfigured)
            {
                Console.Error.WriteLine($"Cannot save schedule: SAJ API not configured ({ConfigurationError})");
                return false;
            }

            await EnsureAuthenticatedAsync();

            try
            {
                // Prepare schedule save request
                var url = $"{_baseUrl}/dev-api/api/v1/remote/client/saveCommonParaRemoteSetting";
                var clientDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                var random = Guid.NewGuid().ToString("N");

                // Prepare parameters for signing
                var signParamsDict = new Dictionary<string, string>
                {
                    { "appProjectName", DefaultAppProjectName },
                    { "clientDate", clientDate },
                    { "lang", DefaultLang },
                    { "timeStamp", timeStamp },
                    { "random", random },
                    { "clientId", ClientId }
                };
                var signedParams = CalcSignatureElekeeper(signParamsDict);

                // Add schedule-specific parameters
                signedParams["deviceSn"] = _deviceSerialNumber;
                signedParams["isParallelBatchSetting"] = DefaultIsParallelBatchSetting.ToString();
                signedParams["commAddress"] = batterySchedule.CommAddress;
                signedParams["componentId"] = batterySchedule.ComponentId;
                signedParams["operType"] = DefaultOperType.ToString();
                signedParams["transferId"] = batterySchedule.TransferId;
                signedParams["value"] = batterySchedule.Value;

                var content = new FormUrlEncodedContent(signedParams);
                var response = await _httpClient.PostAsync(url, content);
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var errCode = doc.RootElement.GetProperty("errCode").GetInt32();

                var success = errCode == 0;

                Console.WriteLine(success ? "Battery schedule saved successfully." : $"Failed to save battery schedule. Error code: {errCode}, Message: {doc.RootElement.GetProperty("errMsg").GetString()}");

                return success;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        /// <summary>
        /// Authenticates the user using Elekeeper API (AES password, signed params).
        /// </summary>
        private async Task<string> Authenticate()
        {
            try
            {
                var url = $"{_baseUrl}/dev-api/api/v1/sys/login";

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("lang", "en");
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:142.0) Gecko/20100101 Firefox/142.0");
                _httpClient.DefaultRequestHeaders.Add("Origin", "https://eop.saj-electric.com");
                _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
                _httpClient.DefaultRequestHeaders.Add("enableSign", "false");
                _httpClient.DefaultRequestHeaders.Add("DNT", "1");
                _httpClient.DefaultRequestHeaders.Add("Sec-GPC", "1");

                var clientDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                var random = GenerateRandomAlphanumeric(32);
                var dataToSign = new Dictionary<string, string> {
                    { "appProjectName", DefaultAppProjectName },
                    { "clientDate", clientDate },
                    { "lang", DefaultLang },
                    { "timeStamp", timeStamp },
                    { "random", random },
                    { "clientId", ClientId }
                };
                var signed = CalcSignatureElekeeper(dataToSign);

                var formData = new List<KeyValuePair<string, string>> {
                    new("lang", DefaultLang),
                    new("password", EncryptPasswordElekeeper(_password)),
                    new("rememberMe", "true"),
                    new("username", _username),
                    new("loginType", "1"),
                    new("appProjectName", DefaultAppProjectName),
                    new("clientDate", clientDate),
                    new("timeStamp", timeStamp),
                    new("random", random),
                    new("clientId", ClientId),
                    new("signParams", signed["signParams"]),
                    new("signature", signed["signature"])
                };

                var content = new FormUrlEncodedContent(formData);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
                {
                    CharSet = "UTF-8"
                };

                var response = await _httpClient.PostAsync(url, content);

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var tokenElem))
                {
                    var tokenHead = data.TryGetProperty("tokenHead", out var headElem) ? headElem.GetString() : "";
                    var tokenValue = tokenElem.GetString();

                    // Remove Bearer prefix if present
                    if (!string.IsNullOrEmpty(tokenHead) && tokenHead.Trim().StartsWith("bearer", StringComparison.CurrentCultureIgnoreCase))
                    {
                        tokenHead = "";
                    }

                    _token = (tokenHead ?? "") + (tokenValue ?? "");
                    var expiresIn = data.GetProperty("expiresIn").GetInt32();
                    _tokenExpiration = DateTime.UtcNow.AddSeconds(expiresIn);

                    SetToken(_token);
                    WriteTokenToFile(_token!, expiresIn);
                }
                else
                {
                    throw new Exception("Token not found in response: " + json);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

            return _token!;
        }

        /// <summary>
        /// Sets the authorization SAJ-token for further requests.
        /// </summary>
        /// <param name="token">The JWT SAJ-token.</param>
        private void SetToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return;

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _httpClient.DefaultRequestHeaders.Remove("Cookie");
            _httpClient.DefaultRequestHeaders.Add("Cookie", $"SAJ-token={token}");
        }

        #region Helpers
        // Helper to generate 32-char alphanumeric string
        private static string GenerateRandomAlphanumeric(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        /// <summary>
        /// Encrypts the password using AES-ECB with PKCS7 padding and hex output (matches Elekeeper Python/JS).
        /// </summary>
        private static string EncryptPasswordElekeeper(string plainPassword)
        {
            // Use ISO-8859-1 encoding for compatibility with Python/JS
            var keyBytes = StringToByteArray(PasswordEncryptionKey);
            var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
            var padded = PadPkcs7(plainBytes, 16);
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = keyBytes;
            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(padded, 0, padded.Length);
            return BitConverter.ToString(encrypted).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Strings the to byte array using the specified hex
        /// </summary>
        /// <param name="hex">The hex</param>
        /// <returns>The bytes</returns>
        private static byte[] StringToByteArray(string hex)
        {
            var len = hex.Length;
            var bytes = new byte[len / 2];

            for (var i = 0; i < len; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// Pads the pkcs 7 using the specified data
        /// </summary>
        /// <param name="data">The data</param>
        /// <param name="blockSize">The block size</param>
        /// <returns>The padded</returns>
        private static byte[] PadPkcs7(byte[] data, int blockSize)
        {
            var padLen = blockSize - (data.Length % blockSize);
            var padded = new byte[data.Length + padLen];

            Buffer.BlockCopy(data, 0, padded, 0, data.Length);

            for (var i = data.Length; i < padded.Length; i++)
            {
                padded[i] = (byte)padLen;
            }

            return padded;
        }

        /// <summary>
        /// Signs a dictionary of parameters for Elekeeper API (MD5, then custom SHA1-to-hex, uppercase).
        /// </summary>
        private static Dictionary<string, string> CalcSignatureElekeeper(Dictionary<string, string> dict)
        {
            // Sort keys alphabetically as in Python
            var sortedKeys = dict.Keys.OrderBy(k => k).ToList();
            var keysStr = string.Join(",", sortedKeys);
            var sortedString = string.Join("&", sortedKeys.Select(k => $"{k}={dict[k]}"));
            var signString = sortedString + "&key=" + QuerySignKey;
            // Use ISO-8859-1 encoding for MD5
            var md5Bytes = MD5.Create().ComputeHash(Encoding.GetEncoding("ISO-8859-1").GetBytes(signString));
            var md5Hex = BitConverter.ToString(md5Bytes).Replace("-", "").ToLowerInvariant();
            var signature = Sha1HexCustom(md5Hex).ToUpperInvariant();
            dict["signature"] = signature;
            dict["signParams"] = keysStr;
            return dict;
        }

        // Custom SHA1-to-hex logic (matches Elekeeper JS/Python, no zero-padding)
        private static string Sha1HexCustom(string input)
        {
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in hash)
            {
                sb.Append((b >> 4).ToString("x")); // high nibble, no padding
                sb.Append((b & 0xF).ToString("x")); // low nibble, no padding
            }
            return sb.ToString();
        }
        #endregion

        /// <summary>
        /// Retrieves the current user mode from the SAJ Power Battery API.
        /// </summary>
        /// <returns>The user mode as a BatteryUserMode enum value, or Unknown if failed to retrieve.</returns>
        public async Task<BatteryUserMode> GetUserModeAsync()
        {
            if (!IsConfigured)
            {
                Console.Error.WriteLine($"Cannot get user mode: SAJ API not configured ({ConfigurationError})");
                return BatteryUserMode.Unknown;
            }

            await EnsureAuthenticatedAsync();

            try
            {
                var url = $"{_baseUrl}/dev-api/api/v1/monitor/home/getDeviceEneryFlowData";
                var clientDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                var random = GenerateRandomAlphanumeric(32);

                // Prepare parameters for signing
                var signParamsDict = new Dictionary<string, string>
                {
                    { "plantUid", _plantUid },
                    { "deviceSn", _deviceSerialNumber },
                    { "appProjectName", DefaultAppProjectName },
                    { "clientDate", clientDate },
                    { "lang", DefaultLang },
                    { "timeStamp", timeStamp },
                    { "random", random },
                    { "clientId", ClientId }
                };

                var signedParams = CalcSignatureElekeeper(signParamsDict);

                // Build query string
                var queryParams = string.Join("&", signedParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                var fullUrl = $"{url}?{queryParams}";

                var response = await _httpClient.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("errCode", out var errCodeElem) && errCodeElem.GetInt32() == 0)
                {
                    if (root.TryGetProperty("data", out var data) && data.TryGetProperty("userModeName", out var userModeNameElem))
                    {
                        var userModeNameString = userModeNameElem.GetString();
                        var userMode = BatteryUserModeExtensions.FromApiString(userModeNameString);

                        Console.WriteLine($"Retrieved user mode: {userMode} ({userModeNameString})");
                        return userMode;
                    }
                    else
                    {
                        Console.WriteLine("userModeName field not found in API response");
                        return BatteryUserMode.Unknown;
                    }
                }
                else
                {
                    var errMsg = root.TryGetProperty("errMsg", out var errMsgElem) ? errMsgElem.GetString() : "Unknown error";
                    Console.WriteLine($"Failed to retrieve user mode. Error: {errMsg}");
                    return BatteryUserMode.Unknown;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while retrieving user mode: {ex.Message}");
                return BatteryUserMode.Unknown;
            }
        }
    }
}
