using System.Net.Http.Json;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections
{
    public class DiscoverSection : IHomeScreenSection
    {
        private readonly IUserManager m_userManager;
        private readonly ImageCacheService m_imageCacheService;
        private static readonly ILogger _logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DiscoverSection>();

        public virtual string? Section => "Discover";

        public virtual string? DisplayText { get; set; } = "Discover";
        public int? Limit => 1;
        public string? Route => null;
        public string? AdditionalData { get; set; }
        public object? OriginalPayload { get; } = null;

        protected virtual string JellyseerEndpoint => "/api/v1/discover/trending";

        private const int MaxPages = 10;

        public DiscoverSection(IUserManager userManager, ImageCacheService imageCacheService)
        {
            m_userManager = userManager;
            m_imageCacheService = imageCacheService;
        }

        public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            List<BaseItemDto> returnItems = new List<BaseItemDto>();

            string? jellyseerrUrl = HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrUrl;
            string? jellyseerrExternalUrl = HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrExternalUrl;

            // Use external URL for frontend links if configured, otherwise fall back to internal URL
            string? jellyseerrDisplayUrl = !string.IsNullOrEmpty(jellyseerrExternalUrl) ? jellyseerrExternalUrl : jellyseerrUrl;

            if (string.IsNullOrEmpty(jellyseerrUrl))
            {
                _logger.LogWarning("Jellyseerr URL not configured, skipping \"{Section}\"", Section);
                return new QueryResult<BaseItemDto>();
            }

            User? user = m_userManager.GetUserById(payload.UserId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found in Jellyfin", payload.UserId);
                return new QueryResult<BaseItemDto>();
            }

            _logger.LogInformation("DiscoverSection: Looking up Jellyfin user \"{Username}\" (ID: {UserId}) in Seerr at {Url}",
                user.Username, payload.UserId, jellyseerrUrl);

            try
            {
                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(jellyseerrUrl);
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("X-Api-Key", HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrApiKey);

                string userSearchUrl = $"/api/v1/user?q={Uri.EscapeDataString(user.Username)}";
                _logger.LogDebug("DiscoverSection: Calling Seerr user search: {Url}", userSearchUrl);

                HttpResponseMessage usersResponse = client.GetAsync(userSearchUrl).GetAwaiter().GetResult();

                if (!usersResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("DiscoverSection: Seerr user search returned {StatusCode}", usersResponse.StatusCode);
                    return new QueryResult<BaseItemDto>();
                }

                string userResponseRaw = usersResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.LogDebug("DiscoverSection: Seerr user search response: {Response}", userResponseRaw);

                JArray? userResults = JObject.Parse(userResponseRaw).Value<JArray>("results");
                if (userResults == null)
                {
                    _logger.LogWarning("DiscoverSection: Seerr returned null results for user search");
                    return new QueryResult<BaseItemDto>();
                }

                int? jellyseerrUserId = userResults.OfType<JObject>().FirstOrDefault(x => x.Value<string>("jellyfinUsername") == user.Username)?.Value<int>("id");

                if (jellyseerrUserId == null)
                {
                    _logger.LogWarning("DiscoverSection: No Seerr user found with jellyfinUsername=\"{Username}\". Found {Count} users in results.",
                        user.Username, userResults.Count);
                    // Log what usernames were found for debugging
                    foreach (var u in userResults.OfType<JObject>())
                    {
                        _logger.LogDebug("DiscoverSection: Seerr user: jellyfinUsername=\"{JfName}\", email=\"{Email}\"",
                            u.Value<string>("jellyfinUsername"), u.Value<string>("email"));
                    }
                    return new QueryResult<BaseItemDto>();
                }

                _logger.LogInformation("DiscoverSection: Matched Seerr user ID {SeerrUserId} for Jellyfin user \"{Username}\"",
                    jellyseerrUserId, user.Username);

                client.DefaultRequestHeaders.Add("X-Api-User", jellyseerrUserId.ToString());

                // Make the API call to discover and get the 20 results
                int page = 1;
                do
                {
                    _logger.LogDebug("DiscoverSection: Fetching {Endpoint}?page={Page}", JellyseerEndpoint, page);
                    HttpResponseMessage discoverResponse = client.GetAsync($"{JellyseerEndpoint}?page={page}").GetAwaiter().GetResult();

                    if (!discoverResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("DiscoverSection: Discover endpoint returned {StatusCode} on page {Page}",
                            discoverResponse.StatusCode, page);
                        break;
                    }

                    string jsonRaw = discoverResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    JObject? jsonResponse = JObject.Parse(jsonRaw);

                    if (jsonResponse != null)
                    {
                        JArray? results = jsonResponse.Value<JArray>("results");
                        if (results == null || results.Count == 0)
                        {
                            _logger.LogDebug("DiscoverSection: No more results on page {Page}", page);
                            break;
                        }

                        foreach (JObject item in results.OfType<JObject>().Where(x => !x.Value<bool>("adult")))
                        {
                            if (!string.IsNullOrEmpty(HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrPreferredLanguages) &&
                                !HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrPreferredLanguages.Split(',')
                                    .Select(x => x.Trim()).Contains(item.Value<string>("originalLanguage")))
                            {
                                continue;
                            }

                            if (item.Value<JObject>("mediaInfo") == null)
                            {
                                string dateTimeString = item.Value<string>("firstAirDate") ??
                                                        item.Value<string>("releaseDate") ?? "1970-01-01";

                                if (string.IsNullOrWhiteSpace(dateTimeString))
                                {
                                    dateTimeString = "1970-01-01";
                                }

                                string posterPath = item.Value<string>("posterPath") ?? "404";
                                string cachedImageUrl = GetCachedImageUrl($"https://image.tmdb.org/t/p/w600_and_h900_bestv2{posterPath}");
                                float rating = item.Value<float?>("vote_average") ?? item.Value<float?>("voteAverage") ?? 0f;

                                returnItems.Add(new BaseItemDto()
                                {
                                    Name = item.Value<string>("title") ?? item.Value<string>("name"),
                                    OriginalTitle = item.Value<string>("originalTitle") ?? item.Value<string>("originalName"),
                                    SourceType = item.Value<string>("mediaType"),
                                    CommunityRating = rating > 0 ? rating : null,
                                    ProviderIds = new Dictionary<string, string>()
                                    {
                                        { "JellyseerrRoot", jellyseerrDisplayUrl },
                                        { "Jellyseerr", item.Value<int>("id").ToString() },
                                        { "JellyseerrPoster", cachedImageUrl }
                                    },
                                    PremiereDate = DateTime.Parse(dateTimeString)
                                });
                            }
                        }
                    }

                    page++;
                } while (returnItems.Count < 20 && page <= MaxPages);

                _logger.LogInformation("DiscoverSection: Returning {Count} items for \"{Section}\"", returnItems.Count, Section);

                return new QueryResult<BaseItemDto>()
                {
                    Items = returnItems,
                    StartIndex = 0,
                    TotalRecordCount = returnItems.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DiscoverSection: Error fetching discover results from Seerr at {Url}", jellyseerrUrl);
                return new QueryResult<BaseItemDto>();
            }
        }

        protected string GetCachedImageUrl(string sourceUrl)
        {
            return ImageCacheHelper.GetCachedImageUrl(m_imageCacheService, sourceUrl);
        }

        public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
        {
            yield return this;
        }

        public HomeScreenSectionInfo GetInfo()
        {
            return new HomeScreenSectionInfo()
            {
                Section = Section,
                DisplayText = DisplayText,
                AdditionalData = AdditionalData,
                Route = Route,
                Limit = Limit ?? 1,
                OriginalPayload = OriginalPayload,
                ViewMode = SectionViewMode.Portrait,
                AllowViewModeChange = false
            };
        }
    }
}
