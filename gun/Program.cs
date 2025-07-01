using Gun.Clients;
using Gun.Jobs;
using Gun.Options;
using Gun.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Polly;
using Polly.Extensions.Http;
using Quartz;
using Serilog;
using Serilog.Events;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace Gun;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("GUN Application");
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"version {version}");

        // Configuration: Replace with your Access Token
        // IMPORTANT: You must obtain an access token with the necessary permissions
        // (User.Read, Calendars.Read, Chat.Read, Chat.ReadBasic)
        // This token needs to be refreshed periodically if it expires.
        // const string NotificationSoundFilePath = "notification.wav"; // Path to your WAV notification sound file
        // Polling interval in minutes

        // Scopes required for the application (for reference, though not used in direct token flow)
        // You must ensure your AccessToken has been granted these scopes.
        string[] requiredScopes = new string[] {
            "User.Read",
            "Calendars.Read",
            "Chat.Read", // Read your 1-on-1 chats
            "Chat.ReadBasic", // Read basic info about chats, needed for mentions
        };

        var hostEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{hostEnvironment}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

        ConfigureServices(builder);
        IHost host = builder.Build();
        await host.RunAsync();
    }

    private static void ConfigureServices(HostApplicationBuilder builder)
    {
        builder.Services.AddOptions<SoundOptions>().Bind(builder.Configuration.GetSection(nameof(SoundOptions)));

        var graphOptions = builder.Configuration.GetSection(nameof(GraphOptions)).Get<GraphOptions>();
        ArgumentNullException.ThrowIfNull(graphOptions);

        builder.Services.AddOptions<LoginClientOptions>().Bind(builder.Configuration.GetSection(nameof(LoginClientOptions)));
        var loginClientOptions = builder.Configuration.GetSection(nameof(LoginClientOptions)).Get<LoginClientOptions>();
        ArgumentNullException.ThrowIfNull(loginClientOptions);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.AppSettings()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Override default log levels for Microsoft namespaces
            .Enrich.FromLogContext() // Enables enriching logs with contextual information
            .MinimumLevel.Verbose()
            .Enrich.WithProperty("Version", version)
            .WriteTo.Console(LogEventLevel.Information, "{Timestamp:yyyy-MM-dd HH:mm:ss} | {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs/gun-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Configure Logging with Serilog:
        // This integrates Serilog with the Microsoft.Extensions.Logging abstraction.
        // All ILogger instances will now route through Serilog.
        builder.Services.AddLogging(configure =>
        {
            configure.ClearProviders(); // Clear existing providers (like the default Console logger)
            configure.AddSerilog(Log.Logger); // Add Serilog as the logging provider
        });

        // Configure Quartz.NET
        builder.Services.AddQuartz(q =>
        {
            // Use a custom JobFactory to allow dependency injection into jobs
            //q.UseMicrosoftDependencyInjectionJobFactory();

            // Register the job and bind it to a trigger
            var jobKey = new JobKey("graphMonitorJob");
            q.AddJob<GraphMonitorJob>(opts => opts.WithIdentity(jobKey));

            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("graphMonitorJobTrigger")
                .WithSimpleSchedule(x => x
                    .WithIntervalInMinutes(graphOptions.PollingIntervalInMinutes)
                    .RepeatForever())
                .StartNow());
        });

        // Add the Quartz.NET hosted service
        builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        builder.Services.AddSingleton<ISoundService, SoundService>();
        builder.Services.AddSingleton<IGraphService, GraphService>();

        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.NotFound)
            .WaitAndRetryAsync(loginClientOptions.RetryAttempts, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                builder.Services.BuildServiceProvider().GetRequiredService<ILogger<LoginClient>>()
                .LogWarning("Delaying for {TotalMilliseconds}ms, then making retry {RetryAttempt}.", timespan.TotalMilliseconds, retryAttempt);
            });


        builder.Services.AddHttpClient<ILoginClient, LoginClient>("LoginClient", client =>
        {
            //client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(loginClientOptions.LoginBaseUrl);
                c.DefaultRequestHeaders.Add("Origin", loginClientOptions.Origin);
            })
            .AddPolicyHandler(retryPolicy);

        //builder.Services.AddScoped<LoginClient>(sp =>
        //{
        //    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        //    var httpClient = httpClientFactory.CreateClient("LoginClient");
        //    return new LoginClient(httpClient);
        //});
        // LoginClient

        builder.Services.AddSingleton<GraphServiceClient>(sp =>
        {
            var graphClient = new GraphServiceClient(new DelegateAuthenticationProvider(async (requestMessage) =>
            {
                //requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphOptions.AccessToken);
            }));

            return graphClient;
        });
    }
}
