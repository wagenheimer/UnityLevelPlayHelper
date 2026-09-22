using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Thin client over the ironSource / LevelPlay publisher APIs:
    /// auth, applications, ad units (v1) and mediation instances (v4).
    ///
    /// Read + write. Every call returns a result object instead of throwing, so the UI can show
    /// the API error verbatim. The bearer token is cached for its 24h lifetime.
    /// </summary>
    internal static class LevelPlayApiClient
    {
        const string AuthUrl = "https://platform.ironsrc.com/partners/publisher/auth";
        const string AppsUrl = "https://platform.ironsrc.com/partners/publisher/applications/v6";
        const string AdUnitsUrl = "https://platform.ironsrc.com/levelPlay/adUnits/v1/";
        const string InstancesUrl = "https://platform.ironsrc.com/levelPlay/network/instances/v4/";

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        static string cachedToken;
        static DateTime cachedAtUtc = DateTime.MinValue;

        // ------------------------------------------------------------ result envelope

        internal sealed class ApiResult
        {
            public bool Ok;
            public string Error = "";
            public string Json = "";

            public static ApiResult Fail(string error) => new ApiResult { Ok = false, Error = error };
            public static ApiResult Success(string json) => new ApiResult { Ok = true, Json = json };
        }

        // ------------------------------------------------------------ DTOs

        [Serializable]
        internal sealed class AppDto
        {
            public string appKey;
            public string appName;
            public string appStatus;
            public string platform;
            public string bundleId;
            public AdUnitsSummary adUnits;
        }

        [Serializable]
        internal sealed class AdUnitsSummary
        {
            public NetworkSummary rewardedVideo;
            public NetworkSummary interstitial;
            public NetworkSummary banner;
            public NetworkSummary offerWall;
        }

        [Serializable]
        internal sealed class NetworkSummary
        {
            public string[] activeNetworks;
        }

        [Serializable]
        internal sealed class AdUnitDto
        {
            public string mediationAdUnitId;
            public string mediationAdUnitName;
            public string adFormat;
            public bool hasAbTest;
            public bool isPaused;
        }

        [Serializable]
        internal sealed class InstanceDto
        {
            public int instanceId;
            public string instanceName;
            public string adUnit;
            public string adFormat;
            public string networkName;
            public bool isBidder;
            public bool isLive;
        }

        [Serializable] sealed class AppList { public AppDto[] items; }
        [Serializable] sealed class AdUnitList { public AdUnitDto[] items; }
        [Serializable] sealed class InstanceList { public InstanceDto[] items; }

        // Request DTOs (write)
        [Serializable]
        internal sealed class AdUnitRequest
        {
            public string mediationAdUnitName;
            public string adFormat;
            public Reward reward;
            public AdUnitSettings[] settings;
        }

        [Serializable]
        internal sealed class Reward
        {
            public string rewardItemName;
            public int rewardAmount;
        }

        [Serializable]
        internal sealed class AdUnitSettings
        {
            public string testGroup;
            public bool? cappingEnabled;
            public int? cappingLimit;
            public string cappingInterval;
            public bool? pacingEnabled;
            public float? pacingMinutes;
            public int? bannerRefreshRate;
        }

        [Serializable]
        internal sealed class AppRequest
        {
            // Mode A: app not online yet
            public string appName;
            public string platform;
            // Mode B: live app (storeUrl + taxonomy)
            public string storeUrl;
            public string taxonomy;
            public int coppa;
        }

        [Serializable]
        internal sealed class InstanceRequest
        {
            public string instanceName;
            public string networkName;
            public string adFormat;
            public bool isBidder;
            public string appConfig1;
            public string instanceConfig1;
            public bool isLive;
        }

        // ------------------------------------------------------------ auth

        internal static bool HasToken => !string.IsNullOrEmpty(cachedToken) && DateTime.UtcNow - cachedAtUtc < TimeSpan.FromHours(23);

        internal static void InvalidateToken() => cachedToken = null;

        internal static async Task<ApiResult> AuthenticateAsync(string secretKey, string refreshToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, AuthUrl))
                {
                    request.Headers.Add("secretkey", secretKey);
                    request.Headers.Add("refreshToken", refreshToken);

                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            return ApiResult.Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

                        cachedToken = body.Trim().Trim('"');
                        cachedAtUtc = DateTime.UtcNow;
                        return ApiResult.Success(cachedToken);
                    }
                }
            }
            catch (Exception e)
            {
                return ApiResult.Fail(e.Message);
            }
        }

        internal static async Task<ApiResult> GetTokenAsync()
        {
            if (HasToken) return ApiResult.Success(cachedToken);
            if (!LevelPlayApiCredentials.HasCredentials)
                return ApiResult.Fail("No API credentials stored.");

            return await AuthenticateAsync(LevelPlayApiCredentials.SecretKey, LevelPlayApiCredentials.RefreshToken)
                .ConfigureAwait(false);
        }

        // ------------------------------------------------------------ apps

        internal static async Task<(ApiResult result, List<AppDto> apps)> GetApplicationsAsync(string platform = null)
        {
            var url = AppsUrl + "?appStatus=active";
            if (!string.IsNullOrEmpty(platform)) url += "&platform=" + platform;

            var result = await GetAsync(url).ConfigureAwait(false);
            if (!result.Ok) return (result, new List<AppDto>());

            var parsed = JsonUtility.FromJson<AppList>("{\"items\":" + result.Json + "}");
            return (result, parsed?.items?.ToList() ?? new List<AppDto>());
        }

        internal static Task<ApiResult> CreateApplicationAsync(AppRequest request)
        {
            var body = JsonUtility.ToJson(request);
            return SendJsonAsync(HttpMethod.Post, AppsUrl, body);
        }

        // ------------------------------------------------------------ ad units

        internal static async Task<(ApiResult result, List<AdUnitDto> units)> GetAdUnitsAsync(string appKey)
        {
            var result = await GetAsync(AdUnitsUrl + appKey).ConfigureAwait(false);
            if (!result.Ok) return (result, new List<AdUnitDto>());

            var parsed = JsonUtility.FromJson<AdUnitList>("{\"items\":" + result.Json + "}");
            return (result, parsed?.items?.ToList() ?? new List<AdUnitDto>());
        }

        internal static Task<ApiResult> CreateAdUnitsAsync(string appKey, IEnumerable<AdUnitRequest> units)
            => SendJsonAsync(HttpMethod.Post, AdUnitsUrl + appKey, ToJsonArray(units));

        internal static Task<ApiResult> UpdateAdUnitAsync(string appKey, object update)
            => SendJsonAsync(HttpMethod.Put, AdUnitsUrl + appKey, ToJsonArray(new[] { update }));

        // ------------------------------------------------------------ instances

        internal static async Task<(ApiResult result, List<InstanceDto> instances)> GetInstancesAsync(string appKey)
        {
            var result = await GetAsync(InstancesUrl + appKey).ConfigureAwait(false);
            if (!result.Ok) return (result, new List<InstanceDto>());

            var parsed = JsonUtility.FromJson<InstanceList>("{\"items\":" + result.Json + "}");
            return (result, parsed?.items?.ToList() ?? new List<InstanceDto>());
        }

        internal static Task<ApiResult> CreateInstancesAsync(string appKey, IEnumerable<InstanceRequest> instances)
            => SendJsonAsync(HttpMethod.Post, InstancesUrl + appKey, ToJsonArray(instances));

        // ------------------------------------------------------------ plumbing

        static async Task<ApiResult> GetAsync(string url)
        {
            var token = await GetTokenAsync().ConfigureAwait(false);
            if (!token.Ok) return token;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("Authorization", "Bearer " + token.Json);

                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return response.IsSuccessStatusCode
                            ? ApiResult.Success(body)
                            : ApiResult.Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");
                    }
                }
            }
            catch (Exception e)
            {
                return ApiResult.Fail(e.Message);
            }
        }

        static async Task<ApiResult> SendJsonAsync(HttpMethod method, string url, string json)
        {
            var token = await GetTokenAsync().ConfigureAwait(false);
            if (!token.Ok) return token;

            try
            {
                using (var request = new HttpRequestMessage(method, url))
                {
                    request.Headers.Add("Authorization", "Bearer " + token.Json);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return response.IsSuccessStatusCode
                            ? ApiResult.Success(body)
                            : ApiResult.Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");
                    }
                }
            }
            catch (Exception e)
            {
                return ApiResult.Fail(e.Message);
            }
        }

        static string ToJsonArray<T>(IEnumerable<T> items) =>
            "[" + string.Join(",", items.Select(JsonUtility.ToJson)) + "]";

        static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? "" : (value.Length > 400 ? value.Substring(0, 400) + "..." : value);
    }
}
