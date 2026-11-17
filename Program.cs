using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Samples.NativeAotApp;

internal static class Program
{
    private static int Main()
    {
        string cpuName = GetCpuName() ?? "Unknown";
        Console.WriteLine($"CPU: {cpuName}");
        Console.WriteLine($"LSE: {GetLseSupportText()}");
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            Console.WriteLine($"AvxVnni.IsSupported: {AvxVnni.IsSupported}");
        }
        return 0;
    }

    // PF_ARM_V81_ATOMIC_INSTRUCTIONS_AVAILABLE (see winnt.h); indicates ARMv8.1 LSE atomics.
    private const uint PfArmV81AtomicInstructionsAvailable = 34;

    private static string GetLseSupportText()
    {
        // LSE (ARMv8.1 Large System Extension) is only meaningful on ARM64 processors.
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return "Not applicable (ARM64 only)";
        }

        bool? supported = IsLseSupported();
        return supported switch
        {
            true => "Supported",
            false => "Not supported",
            null => "Unknown",
        };
    }

    private static bool? IsLseSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return IsLseSupportedWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return IsLseSupportedLinux();
        }

        if (OperatingSystem.IsMacOS())
        {
            return IsLseSupportedMacOS();
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLseSupportedWindows()
    {
        return IsProcessorFeaturePresent(PfArmV81AtomicInstructionsAvailable);
    }

    private static bool? IsLseSupportedLinux()
    {
        try
        {
            // /proc/cpuinfo lists "atomics" in the Features line on LSE-capable kernels.
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                string name = line[..colonIndex].Trim();
                if (name.Equals("Features", StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colonIndex + 1)..]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("atomics", StringComparer.Ordinal);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Environments without a readable /proc/cpuinfo fall back to "Unknown".
        }

        return null;
    }

    [SupportedOSPlatform("macos")]
    private static bool? IsLseSupportedMacOS()
    {
        // Apple silicon exposes "hw.optional.arm.FEAT_LSE" as a 64-bit sysctl.
        try
        {
            const string name = "hw.optional.arm.FEAT_LSE";
            nuint length = 0;

            if (sysctlbyname(name, IntPtr.Zero, ref length, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
                {
                    return null;
                }

                return Marshal.ReadInt64(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool IsProcessorFeaturePresent(uint processorFeature);

    private static string? GetCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetCpuNameWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return GetCpuNameLinux();
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetCpuNameMacOS();
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetCpuNameWindows()
    {
        try
        {
            // HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetCpuNameLinux()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                string name = line[..colonIndex].Trim();

                // x86/x64: "model name" (case-insensitive)
                // ARM: "ModelName" / "Processor" / "Model" (ordinal, so x86's "processor: 0" does not match)
                if (name.Equals("model name", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ModelName", StringComparison.Ordinal)
                    || name.Equals("Processor", StringComparison.Ordinal)
                    || name.Equals("Model", StringComparison.Ordinal))
                {
                    string value = line[(colonIndex + 1)..].Trim();
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Environments without a readable /proc/cpuinfo fall back to "Unknown".
        }

        // ARM64 Linux with a device tree (e.g. Raspberry Pi): read the "model" property.
        string? deviceTreeModel = ReadDeviceTreeModel();
        if (deviceTreeModel is not null)
        {
            return deviceTreeModel;
        }

        return null;
    }

    private static string? ReadDeviceTreeModel()
    {
        // /proc/device-tree/model is usually a symlink to the first path.
        string[] candidates =
        [
            "/sys/firmware/devicetree/base/model",
            "/proc/device-tree/model",
        ];

        foreach (string path in candidates)
        {
            try
            {
                string value = File.ReadAllText(path);
                int nulIndex = value.IndexOf('\0');
                string trimmed = (nulIndex >= 0 ? value[..nulIndex] : value).Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Device tree is typically absent on ACPI-based systems (e.g. ARM64 cloud VMs).
            }
        }

        return null;
    }

    [SupportedOSPlatform("macos")]
    private static string? GetCpuNameMacOS()
    {
        try
        {
            const string name = "machdep.cpu.brand_string";
            nuint length = 0;

            if (sysctlbyname(name, IntPtr.Zero, ref length, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
                {
                    return null;
                }

                return Marshal.PtrToStringAnsi(buffer)?.Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("libSystem.dylib", EntryPoint = "sysctlbyname", CharSet = CharSet.Ansi)]
    private static extern int sysctlbyname(
        string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
}
