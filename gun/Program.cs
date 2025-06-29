using Microsoft.Graph;
using System.Diagnostics; // For Process.Start
using System.Media; // For playing WAV files (Windows only)
using System.Timers; // For polling
using Microsoft.Extensions.Configuration;

Console.WriteLine("GUN Application");
Console.WriteLine("--------------------------------------------------");

// Configuration: Replace with your Access Token
// IMPORTANT: You must obtain an access token with the necessary permissions
// (User.Read, Calendars.Read, Chat.Read, Chat.ReadBasic)
// This token needs to be refreshed periodically if it expires.
const string NotificationSoundFilePath = "notification.wav"; // Path to your WAV notification sound file
// Polling interval in minutes
const int PollingIntervalMinutes = 5;

// Configuration from appsettings.json
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: false, reloadOnChange: true)
    .Build();

// Retrieve AccessToken from configuration
const string AccessTokenKey = "AccessToken";
string? accessToken = config[AccessTokenKey];

if (string.IsNullOrEmpty(accessToken) || accessToken == "YOUR_ACCESS_TOKEN_HERE")
{
    Console.WriteLine($"Error: Access Token not found or not configured in appsettings.json under '{AccessTokenKey}'.");
    Console.WriteLine("Please ensure you have a valid access token and it's placed correctly in appsettings.json.");
    return; // Exit if no valid token
}

Console.WriteLine("Access Token loaded from appsettings.json.");

// Scopes required for the application (for reference, though not used in direct token flow)
// You must ensure your AccessToken has been granted these scopes.
string[] requiredScopes = new string[] {
    "User.Read",
    "Calendars.Read",
    "Chat.Read", // Read your 1-on-1 chats
    "Chat.ReadBasic", // Read basic info about chats, needed for mentions
};

// Variables to store last checked times to avoid duplicate notifications
DateTime? lastMessageCheckTime = null;
DateTime? lastEventCheckTime = null;

// Store the authenticated user's ID and Principal Name for detection
string? currentUserId = null;
string? currentUserPrincipalName = null; // Storing this for event attendee check

// Initialize GraphServiceClient
GraphServiceClient graphClient;

try
{
    // Initialize GraphServiceClient directly with the pre-existing access token
    graphClient = new GraphServiceClient(new DelegateAuthenticationProvider(async (requestMessage) =>
    {
        requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    }));

    // Get the current user's ID and Principal Name
    var user = await graphClient.Me.Request().GetAsync();
    currentUserId = user.Id;
    currentUserPrincipalName = user.UserPrincipalName; // Get the user's UPN
    Console.WriteLine($"\nMonitoring messages and events for user: {user.DisplayName} ({currentUserPrincipalName})");
    Console.WriteLine($"User ID: {currentUserId}");

    // Set initial check times to now minus a short buffer, or null if you want to check all from the beginning
    lastMessageCheckTime = DateTime.UtcNow.AddMinutes(-5); // Check messages from the last 5 minutes initially
    lastEventCheckTime = DateTime.UtcNow.AddMinutes(-5);   // Check events from the last 5 minutes initially

    // Set up a timer for periodic checking
    System.Timers.Timer timer = new System.Timers.Timer(PollingIntervalMinutes * 60 * 1000); // Convert minutes to milliseconds
    timer.Elapsed += async (sender, e) => await CheckForUpdates(sender!, e);
    timer.AutoReset = true;
    timer.Enabled = true;

    Console.WriteLine($"\nMonitoring started. Checking every {PollingIntervalMinutes} minutes...");
    Console.WriteLine("Press Enter to exit.");
    Console.ReadLine(); // Keep the console open until user presses Enter

    timer.Stop();
    timer.Dispose();
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

async Task CheckForUpdates(object sender, ElapsedEventArgs e)
{
    Console.WriteLine($"\nChecking for updates at {DateTime.Now}...");
    try
    {
        await CheckNewTeamsMessages();
        await CheckNewCalendarEvents();
        Console.WriteLine("Update check complete.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during update check: {ex.Message}");
    }
}

async Task CheckNewTeamsMessages()
{
    if (currentUserId == null)
    {
        Console.WriteLine("User ID not available. Cannot check messages.");
        return;
    }

    Console.WriteLine("Checking for new Teams messages...");
    try
    {
        // Get all chats the user is a member of
        // We expand members to check if it's a 1-on-1 chat and if the other member exists.
        // We select a reasonable number of recent messages for each chat, filtering by timestamp.
        var chats = await graphClient.Me.Chats.Request()
            .Expand("members") // Expand members to identify 1:1 chats
            .GetAsync();

        var newMessagesCount = 0;

        foreach (var chat in chats.CurrentPage)
        {
            // Fetch a reasonable number of recent messages.
            // Client-side filtering by CreatedDateTime is used as Graph API doesn't fully support
            // robust $filter operations on message body or direct filtering by timestamp on chat messages.
            var messages = await graphClient.Chats[chat.Id].Messages.Request()
                .Top(20) // Fetch a reasonable number of recent messages
                .GetAsync();

            foreach (var message in messages.CurrentPage.OrderBy(m => m.CreatedDateTime)) // Process in chronological order
            {
                if (message.CreatedDateTime > lastMessageCheckTime)
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
            PlayNotificationSound();
        }
        else
        {
            Console.WriteLine("No new relevant Teams messages detected.");
        }

        lastMessageCheckTime = DateTime.UtcNow; // Update last checked time after processing
    }
    catch (ServiceException ex)
    {
        Console.WriteLine($"Error checking Teams messages: {ex.Message}");
    }
}

async Task CheckNewCalendarEvents()
{
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

        var events = await graphClient.Me.Calendar.Events.Request(queryOptions)
            .OrderBy("createdDateTime desc") // Order to find recent events
            .GetAsync();

        var newEventsCount = 0;

        foreach (var ev in events.CurrentPage)
        {
            // Only process events created or last modified after the last check time
            // And where the user's response is "none" (meaning they haven't responded to the invitation)
            if (ev.CreatedDateTime > lastEventCheckTime || ev.LastModifiedDateTime > lastEventCheckTime)
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
            PlayNotificationSound();
        }
        else
        {
            Console.WriteLine("No new event invitations detected for today.");
        }

        lastEventCheckTime = DateTime.UtcNow; // Update last checked time
    }
    catch (ServiceException ex)
    {
        Console.WriteLine($"Error checking calendar events: {ex.Message}");
    }
}

void PlayNotificationSound()
{
    if (System.IO.File.Exists(NotificationSoundFilePath))
    {
        try
        {
            // For cross-platform support or .mp3, you'd need libraries like NAudio or platform-specific commands.
            if (OperatingSystem.IsWindows())
            {
                using (var player = new SoundPlayer(NotificationSoundFilePath))
                {
                    player.Play();
                }
            }
            else
            {
                // For Linux/macOS, you might use 'aplay' or 'afplay' command line tools
                // Make sure these tools are installed on the user's system.
                // Example for macOS: afplay
                // Example for Linux: aplay (requires alsa-utils)
                string command = "";
                string args = "";

                if (OperatingSystem.IsMacOS())
                {
                    command = "afplay";
                    args = $"\"{NotificationSoundFilePath}\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    command = "aplay";
                    args = $"\"{NotificationSoundFilePath}\"";
                }
                else
                {
                    Console.WriteLine("Sound playback is only supported on Windows, macOS, or Linux (with aplay) in this sample.");
                    return;
                }

                if (!string.IsNullOrEmpty(command))
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = System.Diagnostics.Process.Start(processStartInfo))
                    {
                        if (process != null)
                        {
                            process.WaitForExit(2000); // Wait up to 2 seconds for sound to play
                            if (!process.HasExited)
                            {
                                process.Kill(); // Kill if it hangs
                            }
                            string error = process.StandardError.ReadToEnd();
                            if (!string.IsNullOrEmpty(error))
                            {
                                Console.WriteLine($"Error playing sound: {error}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to play sound: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine($"Notification sound file not found at: {NotificationSoundFilePath}");
    }
}
