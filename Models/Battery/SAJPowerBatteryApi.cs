// -----------------------------------------------------------------------------
// Helper class for interacting with the SAJ Power Battery API
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Models.Battery
{
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
        /// Initializes a new instance of the <see cref="SAJPowerBatteryApi"/> class
        /// </summary>
        /// <param name="username">The username</param>
        /// <param name="password">The password</param>
        /// <param name="deviceSerialNumber">The device serial number</param>
        /// <param name="baseUrl">The base url</param>
        public SAJPowerBatteryApi(string username, string password, string deviceSerialNumber, string baseUrl = "https://eop.saj-electric.com")
        {
            _username = username;
            _password = password;
            _deviceSerialNumber = deviceSerialNumber;
            _baseUrl = baseUrl;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Ensures authentication: checks for a valid SAJ-token, authenticates if needed.
        /// </summary>
        /// <returns>The authentication SAJ-token.</returns>
        public async Task<string> EnsureAuthenticatedAsync()
        {
            // Try to read token from file
            if (ReadTokenFromFile())
            {
                if (IsTokenValid())
                {
                    SetToken(_token);
                    return _token!;
                }
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
                if (root.TryGetProperty("token", out var tokenElem) &&
                    root.TryGetProperty("expiresIn", out var expiresElem))
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
        private void WriteTokenToFile(string token, int expiresIn)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "SAJ-token");
            var obj = new { token, expiresIn };
            var json = JsonSerializer.Serialize(obj);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Builds the complete BatteryScheduleParameters object for the battery charge/discharge schedule.
        /// </summary>
        /// <param name="chargeStart">Charge start time (HH:mm)</param>
        /// <param name="chargeEnd">Charge end time (HH:mm)</param>
        /// <param name="chargePower">Charge power (W)</param>
        /// <param name="dischargeStart">Discharge start time (HH:mm)</param>
        /// <param name="dischargeEnd">Discharge end time (HH:mm)</param>
        /// <param name="dischargePower">Discharge power (W)</param>
        /// <returns>The complete BatteryScheduleParameters object.</returns>
        public BatteryScheduleParameters BuildBatteryScheduleParameters(string chargeStart, string chargeEnd, int chargePower, string dischargeStart, string dischargeEnd, int dischargePower)
        {
            // All weekdays enabled
            var weekdays = "1,1,1,1,1,1,1";

            // Value string: mode|chargeStart|chargeEnd|chargePower_weekdays|dischargeStart|dischargeEnd|dischargePower_weekdays
            var value = $"1|{chargeStart}|{chargeEnd}|{chargePower}_{weekdays}|{dischargeStart}|{dischargeEnd}|{dischargePower}_{weekdays}";

            // HAR pattern for 2 entries
            var commAddress = "3647|3606|3607|3608_3608|361B|361C|361D_361D";
            var componentId = "|30|30|30_30|30|30|30_30";
            var transferId = "|5|5|2_1|5|5|2_1";

            return new BatteryScheduleParameters
            {
                CommAddress = commAddress,
                ComponentId = componentId,
                TransferId = transferId,
                Value = value
            };
        }

        /// <summary>
        /// Saves the battery charge/discharge schedule to the remote API, using dynamic parameters.
        /// </summary>
        /// <param name="batterySchedule">Dynamic parameters for the schedule.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public async Task<bool> SaveBatteryScheduleAsync(BatteryScheduleParameters batterySchedule)
        {
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

                return errCode == 0;
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

                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("SAJ-token", out var tokenElem))
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
        /// Sets the authorization SAJ-token for subsequent requests.
        /// </summary>
        /// <param name="token">The JWT SAJ-token.</param>
        private void SetToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return;

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _httpClient.DefaultRequestHeaders.Remove("Cookie");
            _httpClient.DefaultRequestHeaders.Add("Cookie", $"SAJ-token={token}");
        }

        /// <summary>
        /// Checks if the current SAJ-token is valid (not expired).
        /// </summary>
        /// <returns>True if valid, false otherwise.</returns>
        private bool IsTokenValid()
        {
            return !string.IsNullOrEmpty(_token) && _tokenExpiration.HasValue && _tokenExpiration > DateTime.UtcNow;
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
    }
}
