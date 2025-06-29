using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace Gun.Services;

public interface IGraphService
{
    public bool IsInitialized { get; }

    public Task InitializeAsync();

    public Task CheckForUpdatesAsync();
}

public class GraphService : IGraphService
{
    private readonly ILogger<GraphService> _logger;
    private readonly ISoundService _soundService;
    private readonly GraphServiceClient _graphServiceClient;
    private DateTime? _lastMessageCheckTime = null;
    private DateTime? _lastEventCheckTime = null;
    private string? _currentUserId = null;
    private string? _currentUserPrincipalName = null;
    public bool IsInitialized { get; private set; } = false;

    public GraphService(ILogger<GraphService> logger, ISoundService soundService, GraphServiceClient graphServiceClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _soundService = soundService ?? throw new ArgumentNullException(nameof(soundService));
        _graphServiceClient = graphServiceClient ?? throw new ArgumentNullException(nameof(graphServiceClient));
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Get the current user's ID and Principal Name
            var user = await _graphServiceClient.Me.Request().GetAsync();
            _currentUserId = user.Id;
            _currentUserPrincipalName = user.UserPrincipalName; // Get the user's UPN
            Console.WriteLine($"\nMonitoring messages and events for user: {user.DisplayName} ({_currentUserPrincipalName})");
            Console.WriteLine($"User ID: {_currentUserId}");

            // Set initial check times to now minus a short buffer, or null if you want to check all from the beginning
            _lastMessageCheckTime = DateTime.UtcNow.AddMinutes(-5); // Check messages from the last 5 minutes initially
            _lastEventCheckTime = DateTime.UtcNow.AddMinutes(-5);   // Check events from the last 5 minutes initially
            IsInitialized = true;
        }
        catch (ServiceException ex)
        {
            Console.WriteLine($"Error calling Microsoft Graph: {ex.Message}");
            Console.WriteLine($"Error Code: {ex.StatusCode}");
            //Console.WriteLine($"Request ID: {ex.Error?.InnerError?.RequestId}");
            Console.WriteLine("Please ensure your access token is valid and has the necessary permissions.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
        }

        Console.WriteLine("Application stopped.");
    }

    public async Task CheckForUpdatesAsync()
    {
        _logger.LogInformation("Checking for updates at {DateTimeNow}...", TimeProvider.System.GetLocalNow());

        // Ensure initialization has occurred
        if (_currentUserId == null || _currentUserPrincipalName == null)
        {
            _logger.LogWarning("Service not fully initialized. Attempting initialization...");
            await InitializeAsync();
            if (_currentUserId == null || _currentUserPrincipalName == null)
            {
                _logger.LogError("Service failed to initialize. Skipping update check.");
                return;
            }
        }

        try
        {
            await CheckNewTeamsMessagesAsync(_currentUserId);
            await CheckNewCalendarEventsAsync(_currentUserPrincipalName);

            _logger.LogInformation("Update check complete.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error during update check: {ExceptionMessage}", exception.Message);
        }
    }

    private async Task CheckNewTeamsMessagesAsync(string? currentUserId)
    {
        if (currentUserId == null)
        {
            _logger.LogError("User ID not available. Cannot check messages.");
            return;
        }

        _logger.LogInformation("Checking for new Teams messages...");

        try
        {
            // Get all chats the user is a member of
            // We expand members to check if it's a 1-on-1 chat and if the other member exists.
            // We select a reasonable number of recent messages for each chat, filtering by timestamp.
            var chats = await _graphServiceClient.Me.Chats.Request()
                .Expand("members") // Expand members to identify 1:1 chats
                .GetAsync();

            var newMessagesCount = 0;

            foreach (var chat in chats.CurrentPage)
            {
                // Fetch a reasonable number of recent messages.
                // Client-side filtering by CreatedDateTime is used as Graph API doesn't fully support
                // robust $filter operations on message body or direct filtering by timestamp on chat messages.
                var messages = await _graphServiceClient.Chats[chat.Id].Messages.Request()
                    .Top(20) // Fetch a reasonable number of recent messages
                    .GetAsync();

                foreach (var message in messages.CurrentPage.OrderBy(m => m.CreatedDateTime)) // Process in chronological order
                {
                    if (message.CreatedDateTime > _lastMessageCheckTime)
                    {
                        bool isDirectMessage = chat.ChatType == ChatType.OneOnOne;
                        bool isMentioned = message.Mentions?.Any(m => m.Mentioned?.User?.Id == currentUserId) ?? false;

                        if (isDirectMessage)
                        {
                            Console.WriteLine($"New Direct Message in chat '{chat.Topic}' from {message.From?.User?.DisplayName}: {message.Body?.Content}");
                            newMessagesCount++;
                        }
                        else if (isMentioned)
                        {
                            Console.WriteLine($"New Chat Message with Mention in chat '{chat.Topic}' from {message.From?.User?.DisplayName}: {message.Body?.Content}");
                            newMessagesCount++;
                        }
                    }
                }
            }

            if (newMessagesCount > 0)
            {
                Console.WriteLine($"Detected {newMessagesCount} new relevant message(s). Playing notification sound.");
                _soundService.PlaySound();
            }
            else
            {
                Console.WriteLine("No new relevant Teams messages detected.");
            }

            _lastMessageCheckTime = TimeProvider.System.GetUtcNow().DateTime; // Update last checked time after processing
        }
        catch (ServiceException ex)
        {
            Console.WriteLine($"Error checking Teams messages: {ex.Message}");
        }
    }

    private async Task CheckNewCalendarEventsAsync(string? currentUserPrincipalName)
    {
        if (_currentUserPrincipalName == null)
        {
            Console.WriteLine("Current user's principal name not available. Cannot check event invitations accurately.");
            return;
        }

        Console.WriteLine("Checking for new calendar events...");
        try
        {
            if (currentUserPrincipalName == null)
            {
                Console.WriteLine("Current user's principal name not available. Cannot check event invitations accurately.");
                return;
            }

            // Get events for today. Time filter is crucial for efficiency.
            var startOfDay = DateTime.Today.ToUniversalTime();
            var endOfDay = DateTime.Today.AddDays(1).ToUniversalTime();

            var queryOptions = new List<QueryOption>()
        {
            new QueryOption("startDateTime", startOfDay.ToString("yyyy-MM-ddTHH:mm:ss")),
            new QueryOption("endDateTime", endOfDay.ToString("yyyy-MM-ddTHH:mm:ss"))
        };

            var events = await _graphServiceClient.Me.Calendar.Events.Request(queryOptions)
                .OrderBy("createdDateTime desc") // Order to find recent events
                .GetAsync();

            var newEventsCount = 0;

            foreach (var ev in events.CurrentPage)
            {
                // Only process events created or last modified after the last check time
                // And where the user's response is "none" (meaning they haven't responded to the invitation)
                if (ev.CreatedDateTime > _lastEventCheckTime || ev.LastModifiedDateTime > _lastEventCheckTime)
                {
                    // Find if the current user is an attendee and their response status is 'None'
                    var myAttendeeStatus = ev.Attendees?
                        .FirstOrDefault(a => a.EmailAddress.Address.Equals(currentUserPrincipalName, StringComparison.OrdinalIgnoreCase));

                    if (myAttendeeStatus != null && myAttendeeStatus.Status.Response == ResponseType.None)
                    {
                        Console.WriteLine($"New Event Invitation: '{ev.Subject}' from {ev.Organizer?.EmailAddress?.Name} at {ev.Start?.DateTime} (Status: {myAttendeeStatus.Status.Response})");
                        newEventsCount++;
                    }
                }
            }

            if (newEventsCount > 0)
            {
                Console.WriteLine($"Detected {newEventsCount} new event invitation(s). Playing notification sound.");
                _soundService.PlaySound();
            }
            else
            {
                Console.WriteLine("No new event invitations detected for today.");
            }

            _lastEventCheckTime = TimeProvider.System.GetUtcNow().DateTime; // Update last checked time
        }
        catch (ServiceException ex)
        {
            Console.WriteLine($"Error checking calendar events: {ex.Message}");
        }
    }
}
