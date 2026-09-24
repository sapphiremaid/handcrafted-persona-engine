using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LLama.Native;
using Serilog;
using ILogger = Serilog.ILogger;

namespace PersonaEngine.App;

/// <summary>
/// Native-library setup for the Intel/DirectML build.
/// Registers the publish-time native directory and pins LLamaSharp to its Vulkan backend.
/// </summary>
internal static partial class NativeLibraryLoader
{
    private const uint LoadWithAlteredSearchPath = 0x00000008;

    private static IntPtr _llamaCpuHandle;
    private static IntPtr _llamaHandle;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "SetDllDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectory(string? lpPathName);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "LoadLibraryExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    /// <summary>
    /// Registers <c>&lt;BaseDir&gt;/native</c> as an additional DLL search directory.
    /// The publish target places loose ONNX Runtime / DirectML DLLs there.
    /// </summary>
    public static void RegisterNativeSearchDirectory()
    {
        var nativeDir = Path.Combine(AppContext.BaseDirectory, "native");
        if (Directory.Exists(nativeDir))
        {
            SetDllDirectory(nativeDir);
        }
    }

    /// <summary>
    /// Pre-loads LLamaSharp's CPU companion before its Vulkan backend, then pins
    /// LLamaSharp to the Vulkan llama.dll. The Vulkan ggml.dll imports ggml-cpu.dll,
    /// while LLamaSharp packages CPU variants in sibling architecture directories.
    /// Loading the CPU companion first makes that dependency resolvable.
    /// </summary>
    public static void PreloadLlamaBackend(ILogger? logger = null)
    {
        logger ??= Log.Logger;

        if (_llamaHandle != IntPtr.Zero)
            return;

        var nativeRoot = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native"
        );

        var cpuFlavor = Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx";
        var cpuDll = Path.Combine(nativeRoot, cpuFlavor, "ggml-cpu.dll");
        var llamaDll = Path.Combine(nativeRoot, "vulkan", "llama.dll");

        if (!File.Exists(llamaDll))
        {
            logger.Debug("LLama Vulkan pre-load: {Dll} not found, skipping", llamaDll);
            return;
        }

        if (!File.Exists(cpuDll))
        {
            throw new DllNotFoundException(
                $"LLamaSharp CPU companion was not found at {cpuDll}. "
                    + "The Vulkan backend requires ggml-cpu.dll to be loaded first."
            );
        }

        _llamaCpuHandle = LoadLibraryEx(cpuDll, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (_llamaCpuHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                $"Failed to load LLamaSharp CPU companion at {cpuDll} (Win32 error {err})."
            );
        }

        _llamaHandle = LoadLibraryEx(llamaDll, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (_llamaHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                $"Failed to load LLamaSharp Vulkan backend at {llamaDll} (Win32 error {err}). "
                    + "Install a current Intel graphics driver with Vulkan support."
            );
        }

        NativeLibraryConfig.LLama.WithLibrary(llamaDll);
        logger.Information("LLamaSharp: Vulkan backend loaded ({CpuFlavor} CPU companion)", cpuFlavor);
    }
}
