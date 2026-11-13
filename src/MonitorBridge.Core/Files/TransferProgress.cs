using System.Globalization;

namespace MonitorBridge.Core.Files;

public enum TransferState { Running, Done, Failed, Cancelled }

/// <summary>A snapshot of one file transfer, and the Korean text people see for it.</summary>
public sealed record TransferProgress(bool Sending, string PeerName, string Name, long Total, long Done, long ElapsedMs, TransferState State)
{
    const long MiB = 1L << 20, GiB = 1L << 30;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Describe() => State switch
    {
        TransferState.Done => (Sending ? "다 보냈어요: " : "다 받았어요: ") + Name,
        TransferState.Failed => "전송 실패: " + Name,
        TransferState.Cancelled => "전송 취소됨: " + Name,
        _ => $"{(Sending ? "파일 보내는 중" : "파일 받는 중")} {Percent}% · {Amount()} · {Rate()}",
    };

    int Percent => Total <= 0 ? 0 : (int)(Done * 100 / Total);

    string Amount() => Total >= GiB
        ? string.Create(Inv, $"{Done / (double)GiB:0.0}/{Total / (double)GiB:0.0}GB")
        : $"{Done / MiB}/{Total / MiB}MB";

    string Rate()
    {
        if (ElapsedMs <= 0 || Done <= 0) return "계산 중";
        double bytesPerSec = Done * 1000.0 / ElapsedMs;
        int minutes = Math.Max(1, (int)Math.Ceiling((Total - Done) / bytesPerSec / 60));
        return $"{(long)(bytesPerSec / MiB)}MB/s · 약 {minutes}분 남음";
    }

    public static string Size(long bytes) =>
        bytes >= GiB ? string.Create(Inv, $"{bytes / (double)GiB:0.0}GB") : $"{bytes / MiB}MB";

    /// <summary>Both PCs check their own limit, so the smaller one decides. Unknown peer (old build, not yet told): ours.</summary>
    public static long EffectiveLimit(long own, long? peer) => peer is { } p ? Math.Min(own, p) : own;

    /// <summary>Windows rejects tray tooltips longer than 127 characters.</summary>
    public static string Tooltip(string status, string? progress)
    {
        var s = progress is null ? status : status + "\n" + progress;
        return s.Length <= 127 ? s : s[..127];
    }
}
