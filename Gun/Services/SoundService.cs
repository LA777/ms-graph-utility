﻿using Gun.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Media;

namespace Gun.Services;

public interface ISoundService
{
    public void PlaySound();
}

public class SoundService : ISoundService
{
    private readonly IOptionsMonitor<SoundOptions> _soundOptionsDelegate;
    private readonly ILogger<SoundService> _logger;

    public SoundService(IOptionsMonitor<SoundOptions> soundOptionsDelegate, ILogger<SoundService> logger)
    {
        _soundOptionsDelegate = soundOptionsDelegate ?? throw new ArgumentNullException(nameof(soundOptionsDelegate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void PlaySound()
    {
        if (!File.Exists(_soundOptionsDelegate.CurrentValue.NotificationSoundFileFullPath))
        {
            _logger.LogError("Notification sound file not found at: {NotificationSoundFileFullPath}", _soundOptionsDelegate.CurrentValue.NotificationSoundFileFullPath);
            return;
        }

        try
        {
            // For cross-platform support or .mp3, you'd need libraries like NAudio or platform-specific commands.
            if (OperatingSystem.IsWindows())
            {
                using var player = new SoundPlayer(_soundOptionsDelegate.CurrentValue.NotificationSoundFileFullPath);
                _logger.LogInformation("Playing sound!");
                player.Play();
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
                    args = $"\"{_soundOptionsDelegate.CurrentValue.NotificationSoundFileFullPath}\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    command = "aplay";
                    args = $"\"{_soundOptionsDelegate.CurrentValue.NotificationSoundFileFullPath}\"";
                }
                else
                {
                    _logger.LogError("Sound playback is only supported on Windows, macOS, or Linux (with aplay) in this sample.");
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

                    using var process = Process.Start(processStartInfo);
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
                            _logger.LogError("Error playing sound: {Error}", error);
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to play sound: {ExceptionMessage}", exception.Message);
        }
    }
}
