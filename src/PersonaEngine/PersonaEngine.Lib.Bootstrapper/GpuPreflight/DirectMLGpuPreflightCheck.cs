namespace PersonaEngine.Lib.Bootstrapper.GpuPreflight;

/// <summary>
/// Lightweight preflight for the Intel/DirectML build.
///
/// DirectML itself performs adapter selection when the ONNX execution provider is created.
/// The full provider probe happens in the application's StartupValidator after native
/// dependencies are loaded. This early check only rejects Windows versions too old for
/// the DirectML execution provider used by this fork.
/// </summary>
public sealed class DirectMLGpuPreflightCheck : IGpuPreflightCheck
{
    public Task<GpuStatus> InspectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return Task.FromResult(
                GpuStatus.Fail(
                    GpuFailureKind.UnknownGpu,
                    "The Intel/DirectML build requires Windows 10 version 1903 or newer."
                )
            );
        }

        return Task.FromResult(
            GpuStatus.Pass(
                driver: null,
                name: "DirectML adapter (validated at application startup)",
                cc: null
            )
        );
    }
}
