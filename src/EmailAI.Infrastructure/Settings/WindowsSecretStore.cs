using System.ComponentModel;
using System.Runtime.InteropServices;
using EmailAI.Application.Settings;

namespace EmailAI.Infrastructure.Settings;

/// <summary>
/// <see cref="ISecretStore"/> backed by the Windows Credential Manager
/// (advapi32 CredRead/CredWrite/CredDelete). Every secret is stored under its own target
/// name (<see cref="SecretTargets"/>) inside the CURRENT Windows user's own credential
/// vault:
///
///  * protected at rest by Windows (DPAPI/user vault),
///  * scoped to the current Windows user - another account cannot read it,
///  * never written to appsettings, JSON settings files, logs, files next to the Electron
///    executable, the installer or any server state file,
///  * never returned through any API response (only <see cref="ReadAsync"/>).
///
/// The stored value is never logged; only failures carry the Win32 error code and the
/// target name, never the credential blob.
/// </summary>
public sealed class WindowsSecretStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168; // ERROR_NOT_FOUND
    private const int ErrorNoSuchLogonSession = 1312; // ERROR_NO_SUCH_LOGON_SESSION

    public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken = default)
        => Task.FromResult(ReadEffective(target) is not null);

    public Task<string?> ReadAsync(string target, CancellationToken cancellationToken = default)
        => Task.FromResult(ReadEffective(target));

    public Task WriteAsync(string target, string value, CancellationToken cancellationToken = default)
    {
        var validated = NormalizeTarget(target);
        ThrowIfNotWindows();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The secret value must not be empty.", nameof(value));
        }

        WriteBlob(validated, value);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string target, CancellationToken cancellationToken = default)
    {
        var validated = NormalizeTarget(target);
        ThrowIfNotWindows();

        if (!Native.CredDelete(validated, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not ErrorNotFound and not ErrorNoSuchLogonSession)
            {
                throw NewStoreException("removing", validated, error);
            }
        }

        return Task.CompletedTask;
    }

    private string? ReadEffective(string target)
    {
        var validated = NormalizeTarget(target);
        ThrowIfNotWindows();

        return ReadBlob(validated);
    }

    private static string NormalizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("The credential target must not be empty.", nameof(target));
        }

        return target.Trim();
    }

    private static string? ReadBlob(string target)
    {
        if (!Native.CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return null;
            }

            throw NewStoreException("reading", target, error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Native.Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Native.CredFree(pointer);
        }
    }

    private static void WriteBlob(string target, string value)
    {
        var blob = System.Text.Encoding.Unicode.GetBytes(value);
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        var credentialPointer = IntPtr.Zero;

        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);

            var credential = new Native.Credential
            {
                Type = CredTypeGeneric,
                TargetName = Marshal.StringToCoTaskMemUni(target),
                Comment = Marshal.StringToCoTaskMemUni(
                    "EmailAI per-user secret - managed by the EmailAI Settings page."),
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = Marshal.StringToCoTaskMemUni(Environment.UserName),
            };

            credentialPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Credential>());
            Marshal.StructureToPtr(credential, credentialPointer, fDeleteOld: false);

            if (!Native.CredWrite(credentialPointer, 0))
            {
                throw NewStoreException("saving", target, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (credentialPointer != IntPtr.Zero)
            {
                var last = Marshal.PtrToStructure<Native.Credential>(credentialPointer);
                Marshal.FreeHGlobal(credentialPointer);
                FreeCoTaskMemIfAny(last.TargetName);
                FreeCoTaskMemIfAny(last.Comment);
                FreeCoTaskMemIfAny(last.UserName);
            }

            Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    private static void FreeCoTaskMemIfAny(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static SecretStoreException NewStoreException(string action, string target, int error)
    {
        var message = new Win32Exception(error).Message;
        return new SecretStoreException(
            $"Windows Credential Manager failed while {action} the secret '{target}' (error {error}). {message}",
            new Win32Exception(error));
    }

    private static void ThrowIfNotWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SecretStoreException(
                "The Windows Credential Manager is only available on Windows. " +
                "Use AI_CREDENTIAL_SOURCE=environment on other hosts.");
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(IntPtr credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        public static extern void CredFree(IntPtr buffer);
    }
}
