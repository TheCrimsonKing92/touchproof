using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Writes only the directory's mandatory label. Traversal, exclusions and readback
// belong to the normalizer; no recursive icacls process is needed for a write.
internal static class AcceptanceGateDirectoryIntegrityLabel
{
    private const string MediumSid = "S-1-16-8192";
    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint AclRevision = 2;
    private const uint ObjectInheritAce = 0x01;
    private const uint ContainerInheritAce = 0x02;
    private const uint NoWriteUp = 0x01;
    private const int AclHeaderLength = 8;
    private const int MandatoryAceHeaderLength = 8; // ACE_HEADER and access mask, before SID.

    internal static bool SetMedium(string path)
    {
        if (!ConvertStringSidToSidW(MediumSid, out var sid))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not construct gate Medium integrity SID.");
        try
        {
            var length = checked(AclHeaderLength + MandatoryAceHeaderLength + GetLengthSid(sid));
            var acl = Marshal.AllocHGlobal(length);
            try
            {
                if (!InitializeAcl(acl, length, AclRevision) ||
                    !AddMandatoryAce(acl, AclRevision, ObjectInheritAce | ContainerInheritAce, NoWriteUp, sid))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not construct gate directory Medium label.");

                // LABEL_SECURITY_INFORMATION preserves the DACL and non-label SACL
                // entries. Inheritable Medium covers children without explicit labels;
                // the normalizer still visits every eligible child for explicit Low.
                var error = SetNamedSecurityInfoW(IcaclsIntegrityLabeler.NativeFilePath(path),
                    SeFileObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
                if (error != 0)
                    throw new Win32Exception(checked((int)error), $"Could not set gate directory Medium label for '{path}'.");
                return true;
            }
            finally { Marshal.FreeHGlobal(acl); }
        }
        finally { _ = LocalFree(sid); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAcl(IntPtr acl, int length, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddMandatoryAce(IntPtr acl, uint revision, uint flags, uint policy, IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfoW(string path, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
