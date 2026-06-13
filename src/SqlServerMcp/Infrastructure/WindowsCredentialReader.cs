using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SqlServerMcp.Infrastructure;

public interface IWindowsCredentialReader
{
    WindowsCredential ReadGenericCredential(string target);
}

public sealed record WindowsCredential(string UserName, string Password);

public sealed class WindowsCredentialReader : IWindowsCredentialReader
{
    private const uint CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    public WindowsCredential ReadGenericCredential(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SqlMcpException(
                ErrorCodes.CredentialReadFailed,
                "Windows Credential Manager is only available on Windows.");
        }

        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                throw new SqlMcpException(
                    ErrorCodes.CredentialNotFound,
                    $"Credential target '{target}' was not found.",
                    null,
                    $"Create it with: cmdkey /generic:{target} /user:<login> /pass");
            }

            throw new SqlMcpException(
                ErrorCodes.CredentialReadFailed,
                $"Failed to read Credential Manager target '{target}'.",
                new Win32Exception(error).Message);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var userName = Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
            var password = credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userName) || password.Length == 0)
            {
                throw new SqlMcpException(
                    ErrorCodes.CredentialReadFailed,
                    $"Credential target '{target}' does not contain both username and password.");
            }

            return new WindowsCredential(userName, password);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct NativeCredential
    {
        public readonly uint Flags;
        public readonly uint Type;
        public readonly IntPtr TargetName;
        public readonly IntPtr Comment;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public readonly uint CredentialBlobSize;
        public readonly IntPtr CredentialBlob;
        public readonly uint Persist;
        public readonly uint AttributeCount;
        public readonly IntPtr Attributes;
        public readonly IntPtr TargetAlias;
        public readonly IntPtr UserName;
    }
}
