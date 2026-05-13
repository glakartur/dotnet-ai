using System.Runtime.InteropServices;

namespace DotnetAICraft.Daemon;

// Windows-specific helper: suppresses HANDLE_FLAG_INHERIT on the current process's
// stdin/stdout/stderr for a scoped duration.
//
// Process.Start on Windows always invokes CreateProcessW with bInheritHandles=TRUE.
// Per Win32 contract this duplicates every inheritable handle of the parent into the
// child — including a stdout/stderr pipe handed in by an outer shell (e.g. pwsh's
// `| ForEach-Object`). The spawned daemon would then keep that pipe's writer count
// above zero indefinitely, so the shell never sees EOF on its reader side.
//
// Clearing HANDLE_FLAG_INHERIT on the three std handles only for the window in which
// Process.Start runs prevents the duplication. The parent keeps using its own stdout
// normally; the child simply does not receive a copy. Original flags are restored on
// Dispose so any subsequent spawns observe the original handle inheritance state.
internal static class StdHandleInheritance
{
    private const int STD_INPUT_HANDLE  = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE  = -12;

    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    public static IDisposable Suppress()
    {
        if (!OperatingSystem.IsWindows())
            return NoopScope.Instance;

        return new Scope([STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE]);
    }

    private sealed class Scope : IDisposable
    {
        private readonly (IntPtr Handle, uint OriginalFlags, bool Captured)[] _saved;

        public Scope(int[] stdHandleIds)
        {
            _saved = new (IntPtr, uint, bool)[stdHandleIds.Length];
            for (var i = 0; i < stdHandleIds.Length; i++)
            {
                var h = GetStdHandle(stdHandleIds[i]);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    _saved[i] = (h, 0, false);
                    continue;
                }

                if (!GetHandleInformation(h, out var flags))
                {
                    _saved[i] = (h, 0, false);
                    continue;
                }

                _saved[i] = (h, flags, true);

                if ((flags & HANDLE_FLAG_INHERIT) != 0)
                    SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _saved)
            {
                if (!entry.Captured)
                    continue;

                SetHandleInformation(entry.Handle, HANDLE_FLAG_INHERIT, entry.OriginalFlags & HANDLE_FLAG_INHERIT);
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
