using System.Reflection;

namespace MonitorBridge.Shared;

internal static class BuildInfo
{
    /// <summary>Version plus build time, e.g. 0.1.0+20261114.0030. Two PCs compare this on connect.</summary>
    public static readonly string Build =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "알 수 없음";
}
