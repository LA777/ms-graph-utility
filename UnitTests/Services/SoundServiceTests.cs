using Gun.Options;
using Gun.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.InMemory;
using Serilog.Sinks.InMemory.Assertions;

namespace UnitTests.Services;

public class SoundServiceTests
{
    [Fact]
    public void Constructor_NullSoundOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SoundService>>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new SoundService(null, mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var mockSoundOptions = new Mock<IOptionsMonitor<SoundOptions>>();
        mockSoundOptions.Setup(o => o.CurrentValue).Returns(new SoundOptions());

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new SoundService(mockSoundOptions.Object, null));
    }

    [Fact]
    public void PlaySound_FileExists_Windows_DoesNotLogError()
    {
        // Arrange
        var mockSoundOptions = new Mock<IOptionsMonitor<SoundOptions>>();
        mockSoundOptions.Setup(o => o.CurrentValue).Returns(new SoundOptions { NotificationSoundFileFullPath = "mixkit-happy-bells-notification-937.wav" });
        var logger = new LoggerConfiguration().WriteTo.InMemory(restrictedToMinimumLevel: LogEventLevel.Error).CreateLogger();
        ILogger<SoundService> msLogger = new SerilogLoggerFactory(logger).CreateLogger<SoundService>();
        var service = new SoundService(mockSoundOptions.Object, msLogger);

        // Act
        service.PlaySound();

        // Assert
        InMemorySink.Instance.Should().NotHaveMessage();
    }

    [Fact]
    public void PlaySound_FileDoesNotExist_LogsError()
    {
        // Arrange
        var mockSoundOptions = new Mock<IOptionsMonitor<SoundOptions>>();
        mockSoundOptions.Setup(o => o.CurrentValue).Returns(new SoundOptions { NotificationSoundFileFullPath = "non_existent_sound.wav" });
        var logger = new LoggerConfiguration().WriteTo.InMemory().CreateLogger();
        ILogger<SoundService> msLogger = new SerilogLoggerFactory(logger).CreateLogger<SoundService>();
        var service = new SoundService(mockSoundOptions.Object, msLogger);

        // Act
        service.PlaySound();

        // Assert
        InMemorySink.Instance.Should()
            .HaveMessage("Notification sound file not found at: {NotificationSoundFileFullPath}")
            .Appearing().Once();
    }

    [Fact]
    public void PlaySound_EmptyPath_LogsError()
    {
        // Arrange
        var mockSoundOptions = new Mock<IOptionsMonitor<SoundOptions>>();
        mockSoundOptions.Setup(o => o.CurrentValue)
            .Returns(new SoundOptions { NotificationSoundFileFullPath = "" })
            .Verifiable();
        var logger = new LoggerConfiguration().WriteTo.InMemory().CreateLogger();
        ILogger<SoundService> msLogger = new SerilogLoggerFactory(logger).CreateLogger<SoundService>();
        var service = new SoundService(mockSoundOptions.Object, msLogger);

        // Act
        service.PlaySound();

        // Assert
        InMemorySink.Instance.Should()
            .HaveMessage("Notification sound file not found at: {NotificationSoundFileFullPath}")
            .Appearing().Once();
    }
}
