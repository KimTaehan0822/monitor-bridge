using System.Runtime.InteropServices;
using System.Text;

namespace MonitorBridge.Core.Config;

/// <summary>
/// Windows DPAPI: text sealed here opens only on this PC under the same Windows account.
/// Called through crypt32 directly so the app needs no extra package.
/// </summary>
public static class PasswordVault
{
    public static string Protect(string text) =>
        Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(text), protect: true)
            ?? throw new InvalidOperationException($"비밀번호를 암호화하지 못했어요 ({Marshal.GetLastWin32Error()})"));

    /// <summary>Null when the text was sealed on another PC or account, or is not sealed text at all.</summary>
    public static string? TryUnprotect(string sealedText)
    {
        byte[] data;
        try { data = Convert.FromBase64String(sealedText); }
        catch (FormatException) { return null; }
        var plain = Run(data, protect: false);
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    static byte[]? Run(byte[] input, bool protect)
    {
        var inBlob = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(Math.Max(input.Length, 1)) };
        try
        {
            Marshal.Copy(input, 0, inBlob.Data, input.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Monitor Bridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok) return null;
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(inBlob.Data); }
    }

    const int UiForbidden = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob dataIn, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);
}
