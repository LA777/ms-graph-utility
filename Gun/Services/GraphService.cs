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
            _logger.LogInformation("Monitoring messages and events for user: {DisplayName} ({CurrentUserPrincipalName}).", user.DisplayName, _currentUserPrincipalName);
            _logger.LogInformation("User ID: {CurrentUserId}.", _currentUserId);

            // Set initial check times to now minus a short buffer, or null if you want to check all from the beginning
            _lastMessageCheckTime = DateTime.UtcNow.AddMinutes(-5); // Check messages from the last 5 minutes initially
            _lastEventCheckTime = DateTime.UtcNow.AddMinutes(-5);   // Check events from the last 5 minutes initially
            IsInitialized = true;
        }
        catch (ServiceException exception)
        {
            _logger.LogError(exception, "Error calling Microsoft Graph: {ExceptionMessage}. Error Code: {ExceptionStatusCode}", exception.Message, exception.StatusCode);
            _logger.LogError(exception, "Please ensure your access token is valid and has the necessary permissions.");
            //Console.WriteLine($"Request ID: {ex.Error?.InnerError?.RequestId}");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "An unexpected error occurred: {ExceptionMessage}.", exception.Message);
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
                            _logger.LogInformation("New Direct Message in chat '{ChatTopic}' from {MessageFromUserDisplayName}: {MessageBodyContent}",
                                chat.Topic, message.From?.User?.DisplayName, message.Body?.Content);
                            newMessagesCount++;
                        }
                        else if (isMentioned)
                        {
                            _logger.LogInformation("New Chat Message with Mention in chat '{ChatTopic}' from {MessageFromUserDisplayName}: {MessageBodyContent}",
                                chat.Topic, message.From?.User?.DisplayName, message.Body?.Content);
                            newMessagesCount++;
                        }
                    }
                }
            }

            if (newMessagesCount > 0)
            {
                _logger.LogInformation("Detected {NewMessagesCount} new relevant message(s). Playing notification sound.", newMessagesCount);
                _soundService.PlaySound();
            }
            else
            {
                _logger.LogInformation("No new relevant Teams messages detected.");
            }

            _lastMessageCheckTime = TimeProvider.System.GetUtcNow().DateTime; // Update last checked time after processing
        }
        catch (ServiceException exception)
        {
            _logger.LogError(exception, "Error checking Teams messages: {ExceptionMessage}", exception.Message);
        }
    }

    private async Task CheckNewCalendarEventsAsync(string? currentUserPrincipalName)
    {
        if (currentUserPrincipalName == null)
        {
            _logger.LogError("Current user's principal name not available. Cannot check event invitations accurately.");
            return;
        }

        _logger.LogInformation("Checking for new calendar events...");
        try
        {
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

            foreach (var calendarEvent in events.CurrentPage)
            {
                // Only process events created or last modified after the last check time
                // And where the user's response is "none" (meaning they haven't responded to the invitation)
                if (calendarEvent.CreatedDateTime > _lastEventCheckTime || calendarEvent.LastModifiedDateTime > _lastEventCheckTime)
                {
                    // Find if the current user is an attendee and their response status is 'None'
                    var myAttendeeStatus = calendarEvent.Attendees?
                        .FirstOrDefault(a => a.EmailAddress.Address.Equals(currentUserPrincipalName, StringComparison.OrdinalIgnoreCase));

                    if (myAttendeeStatus != null && myAttendeeStatus.Status.Response == ResponseType.None)
                    {
                        _logger.LogInformation("New Event Invitation: '{EventSubject}' from {EventOrganizerName} at {EventStartTime} (Status: {AttendeeStatus})",
                            calendarEvent.Subject, calendarEvent.Organizer?.EmailAddress?.Name, calendarEvent.Start?.DateTime, myAttendeeStatus.Status.Response);
                        newEventsCount++;
                    }
                }
            }

            if (newEventsCount > 0)
            {
                _logger.LogInformation("Detected {NewEventsCount} new event invitation(s). Playing notification sound.", newEventsCount);
                _soundService.PlaySound();
            }
            else
            {
                _logger.LogInformation("No new event invitations detected for today.");
            }

            _lastEventCheckTime = TimeProvider.System.GetUtcNow().DateTime; // Update last checked time
        }
        catch (ServiceException exception)
        {
            _logger.LogError(exception, "Error checking calendar events: {ExceptionMessage}", exception.Message);
        }
    }
}
