using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>AI settings (part of <see cref="AppSettings"/>). Off by default: nothing leaves the machine until the user turns it on.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    /// <summary><see cref="AiConfig.Anthropic"/> or <see cref="AiConfig.OpenAi"/>.</summary>
    public string Provider { get; set; } = AiConfig.Anthropic;
    public string AnthropicModel { get; set; } = AiConfig.DefaultAnthropicModel;
    public string OpenAiBaseUrl { get; set; } = AiConfig.DefaultOpenAiUrl;
    public string OpenAiModel { get; set; } = "llama3.1";
}

/// <summary>
/// The API keys, one file per provider next to the settings file but never inside it (so they never end up in
/// settings or workspace files). On Windows the file is encrypted for the current user with DPAPI; elsewhere it is a
/// plain file that only the owner can read (mode 600), which <see cref="Protection"/> tells the user.
/// </summary>
public sealed class AiKeyStore(string dir)
{
    public string Dir { get; } = dir;

    private string PathOf(string provider) => System.IO.Path.Combine(Dir, $"ai-key-{(provider == AiConfig.OpenAi ? "openai" : "anthropic")}.bin");

    public static string Protection => OperatingSystem.IsWindows()
        ? "Saved encrypted for your Windows account (DPAPI), outside the settings file."
        : "Saved in a file only your user account can read (not encrypted: this system has no DPAPI), outside the settings file.";

    public bool Has(string provider) => File.Exists(PathOf(provider));

    public string? Load(string provider)
    {
        try
        {
            var p = PathOf(provider);
            if (!File.Exists(p)) return null;
            var bytes = File.ReadAllBytes(p);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Dpapi(bytes, protect: false) : bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    public void Save(string provider, string key)
    {
        Directory.CreateDirectory(Dir);
        var p = PathOf(provider);
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(p, Dpapi(bytes, protect: true));
            return;
        }
        File.Delete(p);
        // Created with owner-only permissions, so there is no moment where others could read it.
        using var fs = new FileStream(p, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        fs.Write(bytes);
    }

    public void Delete(string provider)
    {
        try
        {
            File.Delete(PathOf(provider));
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------- DPAPI without a NuGet package

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        const int UiForbidden = 1;
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, "UltraLogViewer AI key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
