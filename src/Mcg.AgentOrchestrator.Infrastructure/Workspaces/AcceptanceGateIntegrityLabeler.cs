using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The normalizer prepares each parent before its files. Files must inherit that
// parent's Medium label so the next worker's directory relabel can restore Low.
internal sealed class AcceptanceGateIntegrityLabeler(IcaclsIntegrityLabeler native) : IWorkerIntegrityLabeler
{
    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint AclRevision = 2;
    private const uint EmptyAclLength = 8;

    public IntegrityLabelState Query(string path) => native.Query(path);

    public bool SetIntegrity(string path, string level, bool recursive)
    {
        if (recursive || level != "M")
            return native.SetIntegrity(path, level, recursive);

        // icacls /setintegritylevel (OI)(CI)L /T applies to directories; it leaves
        // explicit file labels intact. Replace only the mandatory-label entries
        // with an empty ACL, allowing the prepared parent's label to be inherited.
        // DACLs and non-label SACL entries retain their existing owners.
        var acl = Marshal.AllocHGlobal(checked((int)EmptyAclLength));
        try
        {
            if (!InitializeAcl(acl, EmptyAclLength, AclRevision))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize gate file label inheritance.");

            var error = SetNamedSecurityInfoW(IcaclsIntegrityLabeler.NativeFilePath(path),
                SeFileObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
            if (error != 0)
                throw new Win32Exception(checked((int)error), $"Could not restore gate file label inheritance for '{path}'.");

            // The normalizer reads back and requires Medium before checks launch.
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(acl);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAcl(IntPtr acl, uint length, uint revision);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfoW(string path, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
