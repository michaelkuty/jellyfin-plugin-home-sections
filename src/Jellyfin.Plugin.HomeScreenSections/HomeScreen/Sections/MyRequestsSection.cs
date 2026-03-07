using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections
{
    public class MyRequestsSection : IHomeScreenSection
    {
        private readonly IUserManager m_userManager;
        private readonly ILibraryManager m_libraryManager;
        private readonly IDtoService m_dtoService;
        private static readonly ILogger _logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MyRequestsSection>();

        public string? Section => "MyJellyseerrRequests";

        public string? DisplayText { get; set; } = "My Requests";

        public int? Limit => 1;

        public string? Route => null;

        public string? AdditionalData { get; set; } = null;

        public object? OriginalPayload { get; } = null;

        public MyRequestsSection(IUserManager userManager, ILibraryManager libraryManager, IDtoService dtoService)
        {
            m_userManager = userManager;
            m_libraryManager = libraryManager;
            m_dtoService = dtoService;
        }

        public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            DtoOptions? dtoOptions = new DtoOptions
            {
                Fields = new[]
                {
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.MediaSourceCount
                }
            };

            string? jellyseerrUrl = HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrUrl;

            if (string.IsNullOrEmpty(jellyseerrUrl))
            {
                _logger.LogWarning("MyRequests: Jellyseerr URL not configured");
                return new QueryResult<BaseItemDto>();
            }

            User? user = m_userManager.GetUserById(payload.UserId);
            if (user == null)
            {
                _logger.LogWarning("MyRequests: User {UserId} not found", payload.UserId);
                return new QueryResult<BaseItemDto>();
            }

            _logger.LogInformation("MyRequests: Looking up Jellyfin user \"{Username}\" (ID: {UserId}) in Seerr at {Url}",
                user.Username, payload.UserId, jellyseerrUrl);

            try
            {
                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(jellyseerrUrl);
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("X-Api-Key", HomeScreenSectionsPlugin.Instance.Configuration.JellyseerrApiKey);

                string userSearchUrl = $"/api/v1/user?q={Uri.EscapeDataString(user.Username)}";
                _logger.LogDebug("MyRequests: Calling Seerr user search: {Url}", userSearchUrl);

                HttpResponseMessage usersResponse = client.GetAsync(userSearchUrl).GetAwaiter().GetResult();

                if (!usersResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MyRequests: Seerr user search returned {StatusCode}", usersResponse.StatusCode);
                    return new QueryResult<BaseItemDto>();
                }

                string userResponseRaw = usersResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.LogDebug("MyRequests: Seerr user search response: {Response}", userResponseRaw);

                JArray? userResults = JObject.Parse(userResponseRaw).Value<JArray>("results");
                if (userResults == null)
                {
                    _logger.LogWarning("MyRequests: Seerr returned null results for user search");
                    return new QueryResult<BaseItemDto>();
                }

                // Try matching by jellyfinUsername first, then fall back to email
                int? jellyseerrUserId = userResults.OfType<JObject>()
                    .FirstOrDefault(x => x.Value<string>("jellyfinUsername") == user.Username)
                    ?.Value<int>("id");

                if (jellyseerrUserId == null)
                {
                    // Fallback: match by email (handles OIDC users whose Jellyfin username is their email)
                    jellyseerrUserId = userResults.OfType<JObject>()
                        .FirstOrDefault(x => string.Equals(x.Value<string>("email"), user.Username, StringComparison.OrdinalIgnoreCase))
                        ?.Value<int>("id");
                }

                if (jellyseerrUserId == null)
                {
                    _logger.LogWarning("MyRequests: No Seerr user found matching \"{Username}\". Found {Count} users in results.",
                        user.Username, userResults.Count);
                    foreach (var u in userResults.OfType<JObject>())
                    {
                        _logger.LogDebug("MyRequests: Seerr user: jellyfinUsername=\"{JfName}\", email=\"{Email}\", displayName=\"{Name}\"",
                            u.Value<string>("jellyfinUsername"), u.Value<string>("email"), u.Value<string>("displayName"));
                    }
                    return new QueryResult<BaseItemDto>();
                }

                _logger.LogInformation("MyRequests: Matched Seerr user ID {SeerrUserId} for Jellyfin user \"{Username}\"",
                    jellyseerrUserId, user.Username);

                string requestsUrl = $"/api/v1/user/{jellyseerrUserId}/requests?take=100";
                _logger.LogDebug("MyRequests: Fetching requests: {Url}", requestsUrl);

                HttpResponseMessage requestsResponse = client.GetAsync(requestsUrl).GetAwaiter().GetResult();

                if (requestsResponse.IsSuccessStatusCode)
                {
                    string jsonRaw = requestsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    JObject? jsonResponse = JObject.Parse(jsonRaw);

                    JArray? allResults = jsonResponse?.Value<JArray>("results");
                    _logger.LogInformation("MyRequests: Got {Count} total requests from Seerr", allResults?.Count ?? 0);

                    IEnumerable<JObject>? presentRequestedMedia = allResults?.OfType<JObject>()
                        .Where(x => x.Value<JObject>("media")?.Value<string>("jellyfinMediaId") != null)
                        .Select(x => x.Value<JObject>("media")!);

                    int presentCount = presentRequestedMedia?.Count() ?? 0;
                    _logger.LogInformation("MyRequests: {PresentCount} requests have jellyfinMediaId (out of {TotalCount})",
                        presentCount, allResults?.Count ?? 0);

                    // Log requests that are missing jellyfinMediaId for debugging
                    if (allResults != null)
                    {
                        foreach (var req in allResults.OfType<JObject>())
                        {
                            var media = req.Value<JObject>("media");
                            string? mediaType = media?.Value<string>("mediaType");
                            string? jfMediaId = media?.Value<string>("jellyfinMediaId");
                            int? mediaStatus = media?.Value<int>("status");
                            string? reqStatus = req.Value<string>("status");
                            _logger.LogDebug("MyRequests: Request - type={MediaType}, jellyfinMediaId={JfId}, mediaStatus={MediaStatus}, requestStatus={ReqStatus}",
                                mediaType, jfMediaId ?? "(null)", mediaStatus, reqStatus);
                        }
                    }

                    // If no requests have jellyfinMediaId, return empty immediately.
                    // An empty ItemIds array causes Jellyfin to return ALL items (no filter).
                    if (presentCount == 0)
                    {
                        _logger.LogInformation("MyRequests: No requests with jellyfinMediaId, returning empty");
                        return new QueryResult<BaseItemDto>();
                    }

                    VirtualFolderInfo[] folders = m_libraryManager.GetVirtualFolders()
                        .FilterToUserPermitted(m_libraryManager, user);

                    _logger.LogDebug("MyRequests: User has access to {FolderCount} library folders", folders.Length);

                    IEnumerable<string?>? jellyfinItemIds = presentRequestedMedia?.Select(x => x.Value<string>("jellyfinMediaId"));

                    var config = HomeScreenSectionsPlugin.Instance?.Configuration;
                    var sectionSettings = config?.SectionSettings.FirstOrDefault(x => x.SectionId == Section);
                    bool hideWatchedItems = sectionSettings?.HideWatchedItems == true;

                    IEnumerable<BaseItem> items = folders.SelectMany(x =>
                    {
                        return m_libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            ItemIds = jellyfinItemIds?.Select(y => Guid.Parse(y ?? Guid.Empty.ToString()))?.ToArray() ?? Array.Empty<Guid>(),
                            Recursive = true,
                            EnableTotalRecordCount = false,
                            ParentId = Guid.Parse(x.ItemId ?? Guid.Empty.ToString())
                        });
                    }).OrderByDescending(item => item.DateCreated);

                    if (hideWatchedItems)
                    {
                        items = items.Where(item => !item.IsPlayedVersionSpecific(user));
                    }

                    var finalItems = items.Take(16).ToArray();
                    _logger.LogInformation("MyRequests: Returning {Count} items for user \"{Username}\"", finalItems.Length, user.Username);

                    return new QueryResult<BaseItemDto>(m_dtoService.GetBaseItemDtos(finalItems, dtoOptions, user));
                }
                else
                {
                    _logger.LogWarning("MyRequests: Requests endpoint returned {StatusCode}", requestsResponse.StatusCode);
                }

                return new QueryResult<BaseItemDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MyRequests: Error fetching requests from Seerr at {Url}", jellyseerrUrl);
                return new QueryResult<BaseItemDto>();
            }
        }

        public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
        {
            yield return this;
        }

        public HomeScreenSectionInfo GetInfo()
        {
            return new HomeScreenSectionInfo
            {
                Section = Section,
                DisplayText = DisplayText,
                AdditionalData = AdditionalData,
                Route = Route,
                Limit = Limit ?? 1,
                OriginalPayload = OriginalPayload,
                ViewMode = SectionViewMode.Landscape,
                AllowViewModeChange = true, // TODO: Change this to allowed view modes
                AllowHideWatched = true
            };
        }
    }
}