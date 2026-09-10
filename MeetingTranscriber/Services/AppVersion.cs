using System.Reflection;

namespace MeetingTranscriber.Services;

/// <summary>The running build's version, as set by the release workflow's -p:Version (a plain reflection read, so it works the same whether Velopack-installed, portable, or a raw dev build).</summary>
public static class AppVersion
{
    public static string Current { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "dev" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
