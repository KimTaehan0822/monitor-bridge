namespace MonitorBridge.Core.Updates;

/// <summary>Compares a GitHub release tag (v1.2.3) with this build's version: the first three numbers only.</summary>
public static class ReleaseVersion
{
    public static bool IsNewer(string latestTag, string current)
    {
        var a = Parts(latestTag);
        var b = Parts(current);
        if (a is null || b is null) return false;
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    /// <summary>"v1.2.3.4+build" → [1,2,3]; missing numbers are 0; null when it does not start with a number.</summary>
    static int[]? Parts(string version)
    {
        var core = version.TrimStart('v', 'V').Split('+')[0].Split('.');
        if (!int.TryParse(core[0], out _)) return null;
        var parts = new int[3];
        for (var i = 0; i < 3 && i < core.Length; i++) int.TryParse(core[i], out parts[i]);
        return parts;
    }
}
