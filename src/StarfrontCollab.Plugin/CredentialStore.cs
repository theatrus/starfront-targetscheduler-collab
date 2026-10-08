using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarfrontCollab.Wire;

namespace StarfrontCollab.Plugin;

internal sealed record Credential(string AgentId, string Token)
{
    public override string ToString() => "Telescope credential for " + AgentId + " (token redacted)";
}

/// A telescope token lives in Windows Credential Manager, one entry per
/// N.I.N.A. profile and server. It never enters settings, logs or the UI.
internal static class CredentialStore
{
    private sealed record Entry(int Version, string Origin, Guid Profile, string Agent, string Token)
    {
        public override string ToString() => "Telescope credential (redacted)";
    }

    private static string Target(Uri server, Guid profile) => "StarfrontTargetSchedulerCollab/" + profile.ToString("D") + "/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.AbsoluteUri))).ToLowerInvariant();

    internal static Credential? Read(Uri server, Guid profile)
    {
        var value = ReadSecret(Target(server, profile));
        if (value is null) return null;
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(value); }
        catch (JsonException) { entry = null; }
        if (entry is null || entry.Version != 1 || entry.Origin != server.AbsoluteUri || entry.Profile != profile
            || entry.Agent.Length > 64 || !CollabClient.ValidSecret(entry.Token))
            throw new InvalidOperationException("The saved telescope token is unreadable. Forget it and sign in again.");
        return new(entry.Agent, entry.Token);
    }

    internal static void Store(Uri server, Guid profile, Credential credential) =>
        WriteSecret(Target(server, profile), JsonSerializer.Serialize(new Entry(1, server.AbsoluteUri, profile, credential.AgentId, credential.Token)));

    internal static void Forget(Uri server, Guid profile) => WriteSecret(Target(server, profile), null);

    private static string? ReadSecret(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("The saved telescope token is unreadable. Forget it and sign in again.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    private static void WriteSecret(string target, string? secret)
    {
        if (secret is null)
        {
            if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new InvalidOperationException("The telescope token is too large to store.");
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = 2,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
