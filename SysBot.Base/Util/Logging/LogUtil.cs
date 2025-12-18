using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SysBot.Base;

/// <summary>
/// Logic wrapper to handle logging (via NLog).
/// </summary>
public static class LogUtil
{
    // hook in here if you want to forward the message elsewhere???
    public static readonly List<ILogForwarder> Forwarders = [];

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static LogUtil()
    {
        if (!LogConfig.LoggingEnabled)
            return;

        var config = new LoggingConfiguration();
        Directory.CreateDirectory("logs");
        var WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var logfile = new FileTarget("logfile")
        {
            FileName = Path.Combine(WorkingDirectory, "logs", "SysBotLog.txt"),
            ConcurrentWrites = true,

            ArchiveEvery = FileArchivePeriod.Day,
            ArchiveNumbering = ArchiveNumberingMode.Date,
            ArchiveFileName = Path.Combine(WorkingDirectory, "logs", "SysBotLog.{#}.txt"),
            ArchiveDateFormat = "yyyy-MM-dd",
            ArchiveAboveSize = 104857600, // 100MB (never)
            MaxArchiveFiles = LogConfig.MaxArchiveFiles,
            Encoding = Encoding.Unicode,
            WriteBom = true,
        };
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);
        LogManager.Configuration = config;
    }

    public static DateTime LastLogged { get; private set; } = DateTime.Now;

    public static void LogError(string message, string identity)
    {
        Logger.Log(LogLevel.Error, $"{identity} {message}");
        Log(message, identity);
    }

    public static void LogInfo(string message, string identity)
    {
        if (string.IsNullOrWhiteSpace(botName))
            return "UnknownBot";

        // Check if this is a system component and should be consolidated
        if (LogConfig.ConsolidateSystemLogs)
        {
            foreach (var systemIdentity in LogConfig.SystemIdentities)
            {
                if (botName.Equals(systemIdentity, StringComparison.OrdinalIgnoreCase) ||
                    botName.StartsWith(systemIdentity + " ", StringComparison.OrdinalIgnoreCase) ||
                    botName.StartsWith(systemIdentity + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return "System";
                }
            }
        }

        // Keep the full identifier (e.g., "HeXbyt3-483256", "USB-1")
        // Just sanitize invalid file system characters
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", botName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

        // Remove any trailing/leading whitespace or underscores
        sanitized = sanitized.Trim('_', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "UnknownBot" : sanitized;
    }

    /// <summary>
    /// Checks if an identity is a trainer identifier (Name-XXXXXX format)
    /// </summary>
    private static bool IsTrainerIdentifier(string identity)
    {
        return identity.Contains('-') && System.Text.RegularExpressions.Regex.IsMatch(identity, @"-\d{6}$");
    }

    /// <summary>
    /// Checks if identity should skip per-bot logging (system-wide services)
    /// </summary>
    private static bool IsGlobalIdentity(string identity)
    {
        return LogConfig.SystemIdentities.Any(prefix => identity.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                                                         identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Flushes buffered logs from early identifier (IP/USB) to trainer folder
    /// </summary>
    public static void FlushBufferedLogs(string earlyIdentifier, string trainerIdentifier)
    {
        if (LogBuffer.TryRemove(earlyIdentifier, out var bufferedLogs))
        {
            var botLogger = GetOrCreateBotLogger(trainerIdentifier);
            foreach (var entry in bufferedLogs)
            {
                botLogger.Log(entry.Level, entry.Message);
            }
        }
    }

    public static void LogError(string message, string identity)
    {
        // Log to master log
        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Error, $"{identity} {message}");

        // Handle per-bot logging
        if (LogConfig.EnablePerBotLogging && !IsGlobalIdentity(identity))
        {
            if (IsTrainerIdentifier(identity))
            {
                // Identified bot - log directly to trainer folder
                var botLogger = GetOrCreateBotLogger(identity);
                botLogger.Log(LogLevel.Error, message);
            }
            else
            {
                // Early bot identifier (IP/USB) - buffer for later
                LogBuffer.GetOrAdd(identity, _ => new List<BufferedLogEntry>())
                    .Add(new BufferedLogEntry(LogLevel.Error, message, DateTime.Now));
            }
        }

        // Forward to external listeners (Discord, etc.)
        foreach (var fwd in Forwarders)
        {
            try
            {
                fwd.Forward(message, identity);
            }
            catch { }
        }
    }

    public static void LogInfo(string message, string identity)
    {
        // Log to master log
        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Info, $"{identity} {message}");

        // Handle per-bot logging
        if (LogConfig.EnablePerBotLogging && !IsGlobalIdentity(identity))
        {
            if (IsTrainerIdentifier(identity))
            {
                // Identified bot - log directly to trainer folder
                var botLogger = GetOrCreateBotLogger(identity);
                botLogger.Log(LogLevel.Info, message);
            }
            else
            {
                // Early bot identifier (IP/USB) - buffer for later
                LogBuffer.GetOrAdd(identity, _ => new List<BufferedLogEntry>())
                    .Add(new BufferedLogEntry(LogLevel.Info, message, DateTime.Now));
            }
        }

        // Forward to external listeners (Discord, etc.)
        foreach (var fwd in Forwarders)
        {
            try
            {
                fwd.Forward(message, identity);
            }
            catch { }
        }
    }

    public static void LogSuspicious(string message, string identity)
    {
        // Log to master log
        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Warn, $"[SECURITY] {identity} {message}");

        // Log to per-bot log
        if (LogConfig.EnablePerBotLogging)
        {
            var botLogger = GetOrCreateBotLogger(identity);
            botLogger.Log(LogLevel.Warn, $"[SECURITY] {message}");
        }

        // Forward to external listeners (Discord, etc.)
        foreach (var fwd in Forwarders)
        {
            try
            {
                fwd.Forward($"[SECURITY] {message}", identity);
            }
            catch { }
        }
    }

    public static void LogSafe(Exception exception, string identity)
    {
        Logger.Log(LogLevel.Error, $"Exception from {identity}:");
        Logger.Log(LogLevel.Error, exception);

        var err = exception.InnerException;
        while (err is not null)
        {
            Logger.Log(LogLevel.Error, err);
            err = err.InnerException;
        }
    }

    public static void LogText(string message) => Logger.Log(LogLevel.Info, message);

    private static void Log(string message, string identity)
    {
        foreach (var fwd in Forwarders)
        {
            try
            {
                fwd.Forward(message, identity);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, $"Failed to forward log from {identity} - {message}");
                Logger.Log(LogLevel.Error, ex);
            }
        }

        LastLogged = DateTime.Now;
    }
}
