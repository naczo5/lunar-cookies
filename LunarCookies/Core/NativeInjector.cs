using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LunarCookies.Core;

public static class NativeInjector
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(
        IntPtr hProcess,
        out ushort pProcessMachine,
        out ushort pNativeMachine);

    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint STILL_ACTIVE = 259;

    public static bool Inject(int pid, string dllPath, Action<string>? log = null)
    {
        // The remote loader requires an absolute path.
        dllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            log?.Invoke($"DLL not found: {dllPath}");
            return false;
        }

        log?.Invoke($"DLL: {dllPath}");

        IntPtr hProcess = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            false, pid);

        if (hProcess == IntPtr.Zero)
        {
            log?.Invoke($"Failed to open process {pid}. Error: {Marshal.GetLastWin32Error()} (try running as Administrator)");
            return false;
        }

        if (!HasMatchingArchitecture(hProcess, log))
        {
            CloseHandle(hProcess);
            return false;
        }

        IntPtr pRemotePath = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        bool remoteThreadCompleted = false;
        try
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            pRemotePath = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (pRemotePath == IntPtr.Zero)
            {
                log?.Invoke($"VirtualAllocEx failed. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!WriteProcessMemory(hProcess, pRemotePath, pathBytes, (uint)pathBytes.Length, out IntPtr bytesWritten)
                || bytesWritten.ToInt64() != pathBytes.Length)
            {
                log?.Invoke($"WriteProcessMemory failed or was incomplete. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
            IntPtr pLoadLibrary = GetProcAddress(hKernel32, "LoadLibraryW");
            if (pLoadLibrary == IntPtr.Zero)
            {
                log?.Invoke("Failed to find LoadLibraryW.");
                return false;
            }

            hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, pLoadLibrary, pRemotePath, 0, IntPtr.Zero);
            if (hThread == IntPtr.Zero)
            {
                log?.Invoke($"CreateRemoteThread failed. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            uint wait = WaitForSingleObject(hThread, 15000);
            if (wait != WAIT_OBJECT_0)
            {
                if (wait == WAIT_TIMEOUT)
                    log?.Invoke("LoadLibraryW timed out after 15 seconds. Remote path memory is intentionally retained to avoid a loader use-after-free.");
                else
                    log?.Invoke($"LoadLibraryW wait failed: 0x{wait:X}, Win32={Marshal.GetLastWin32Error()}");
                return false;
            }
            remoteThreadCompleted = true;

            if (GetExitCodeThread(hThread, out uint moduleHandle))
            {
                if (moduleHandle == 0)
                {
                    log?.Invoke("LoadLibraryW returned NULL — DLL failed to load (missing dependency or DllMain failed).");
                    return false;
                }
                log?.Invoke($"LoadLibraryW OK (module=0x{moduleHandle:X}).");
            }
            else
            {
                log?.Invoke($"GetExitCodeThread failed. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // Detect immediate process death from a crashing DllMain.
            Thread.Sleep(400);
            if (GetExitCodeProcess(hProcess, out uint procExit) && procExit != STILL_ACTIVE)
            {
                log?.Invoke($"ERROR: Lunar process exited right after inject (exit={procExit}). Check lunar_cookies_bridge.log next to switcher.dll.");
                return false;
            }

            try
            {
                using var alive = Process.GetProcessById(pid);
                if (alive.HasExited)
                {
                    log?.Invoke("ERROR: Lunar process exited after inject.");
                    return false;
                }
            }
            catch
            {
                log?.Invoke("ERROR: Lunar process no longer exists after inject.");
                return false;
            }

            log?.Invoke("Injection completed; process still alive.");
            return true;
        }
        finally
        {
            if (hThread != IntPtr.Zero)
                CloseHandle(hThread);
            // If the wait timed out, the remote thread may still be reading the
            // path. Leaking a few hundred bytes is safer than freeing it.
            if (pRemotePath != IntPtr.Zero && (hThread == IntPtr.Zero || remoteThreadCompleted))
                VirtualFreeEx(hProcess, pRemotePath, 0, MEM_RELEASE);
            CloseHandle(hProcess);
        }
    }

    private static bool HasMatchingArchitecture(IntPtr target, Action<string>? log)
    {
        try
        {
            if (!IsWow64Process2(GetCurrentProcess(), out ushort selfProcess, out ushort selfNative)
                || !IsWow64Process2(target, out ushort targetProcess, out ushort targetNative))
            {
                log?.Invoke($"Architecture check failed. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            ushort selfMachine = selfProcess == 0 ? selfNative : selfProcess;
            ushort targetMachine = targetProcess == 0 ? targetNative : targetProcess;
            if (selfMachine != targetMachine)
            {
                log?.Invoke($"Architecture mismatch (injector=0x{selfMachine:X4}, target=0x{targetMachine:X4}). Use a DLL and app matching the target JVM.");
                return false;
            }
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            log?.Invoke("IsWow64Process2 is unavailable; refusing unverified cross-architecture injection.");
            return false;
        }
    }
}
