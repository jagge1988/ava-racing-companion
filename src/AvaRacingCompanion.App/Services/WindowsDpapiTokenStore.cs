using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AvaRacingCompanion.Core.Services;
using AvaRacingCompanion.Core.Versioning;

namespace AvaRacingCompanion.App.Services;

/// <summary>
/// Speichert das persönliche Geräte-Token verschlüsselt via Windows DPAPI (crypt32.dll,
/// CurrentUser-Scope) unter %APPDATA%\AvaRacingCompanion\device_token.dpapi.
/// Keine externen NuGet-Pakete erforderlich.
/// </summary>
public sealed class WindowsDpapiTokenStore : ISecureTokenStore
{
    private const uint CryptProtectUiForbidden = 0x1;

    private readonly string _tokenFilePath;

    public WindowsDpapiTokenStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _tokenFilePath = customFilePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _tokenFilePath = Path.Combine(appData, AppInfo.AppId, "device_token.dpapi");
        }
    }

    public string? LoadToken()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_tokenFilePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(_tokenFilePath);
            if (encrypted.Length == 0)
            {
                return null;
            }

            var plainBytes = Unprotect(encrypted);
            return plainBytes is not null ? Encoding.UTF8.GetString(plainBytes) : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveToken(string token)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var dir = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var plainBytes = Encoding.UTF8.GetBytes(token);
        var encrypted = Protect(plainBytes);
        var tmpPath = $"{_tokenFilePath}.tmp";
        File.WriteAllBytes(tmpPath, encrypted);
        File.Move(tmpPath, _tokenFilePath, overwrite: true);
    }

    public void DeleteToken()
    {
        try
        {
            if (File.Exists(_tokenFilePath))
            {
                File.Delete(_tokenFilePath);
            }
        }
        catch
        {
            // Ignorieren
        }
    }

    private static byte[] Protect(byte[] plainBytes)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob.cbData = plainBytes.Length;
            inputBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inputBlob.pbData, plainBytes.Length);

            if (!CryptProtectData(ref inputBlob, AppInfo.AppId, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
            {
                throw new InvalidOperationException($"DPAPI CryptProtectData fehlgeschlagen (Win32 {Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            if (inputBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.pbData);
            if (outputBlob.pbData != IntPtr.Zero) LocalFree(outputBlob.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] encryptedBytes)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob.cbData = encryptedBytes.Length;
            inputBlob.pbData = Marshal.AllocHGlobal(encryptedBytes.Length);
            Marshal.Copy(encryptedBytes, 0, inputBlob.pbData, encryptedBytes.Length);

            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
            {
                return null;
            }

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            if (inputBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.pbData);
            if (outputBlob.pbData != IntPtr.Zero) LocalFree(outputBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
