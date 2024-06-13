namespace SysBot.Base;

public static class LogConfig
{
    public static bool LoggingEnabled { get; set; } = true;

    public static int MaxArchiveFiles { get; set; } = 14; // 2 weeks
}
