using Gun.Services;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Gun.Jobs;

[DisallowConcurrentExecution] // Prevents multiple instances of the job from running simultaneously
public class GraphMonitorJob : IJob
{
    private readonly IGraphService _graphService;
    private readonly ILogger<GraphMonitorJob> _logger;

    public GraphMonitorJob(IGraphService graphService, ILogger<GraphMonitorJob> logger)
    {
        _graphService = graphService ?? throw new ArgumentNullException(nameof(graphService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Running job.");

        try
        {
            if (!_graphService.IsInitialized)
            {
                _logger.LogInformation("Graph service is not initilized. Running InitializeAsync.");
                await _graphService.InitializeAsync();
            }

            _logger.LogInformation("Graph service is initilized. Running CheckForUpdatesAsync.");
            await _graphService.CheckForUpdatesAsync();

            _logger.LogInformation("Job completed.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error: {ExceptionMessage}", exception.Message);
        }
    }
}
