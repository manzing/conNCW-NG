using System.Reflection;
using System.Runtime.InteropServices;

namespace ConNCW.Core.Resampler;

public class ReSamplerException : Exception
{
    public int StatusCode { get; }

    public ReSamplerException(string message, int statusCode = -1) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Wrapper P/Invoke vers la bibliothèque native resampler.dll (compilée avec MSVC x64).
/// Fournit le rééchantillonnage de flux WAV directement en mémoire vive sans accès disque.
/// </summary>
public static class NativeReSampler
{
    private const string DllName = "resampler.dll";
    private static readonly Lazy<bool> _isAvailableLazy = new(CheckAvailability);

    static NativeReSampler()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeReSampler).Assembly, ResolveDll);
    }

    public static bool IsAvailable => _isAvailableLazy.Value;

    private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "resampler" or DllName)
        {
            string baseDir = AppContext.BaseDirectory;
            string[] probePaths = [
                Path.Combine(baseDir, DllName),
                Path.Combine(baseDir, "tools", "resampler", DllName),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "ReSampler", "build", "Release", DllName),
                Path.Combine(baseDir, "..", "..", "tools", "resampler", DllName),
                @"D:\Docs\GitHub\ReSampler\build\Release\" + DllName,
                @"C:\Dev\AI-Workspaces\Lite_Converter-YDL_4.0-dev\tools\resampler\" + DllName
            ];

            foreach (var probe in probePaths)
            {
                if (File.Exists(probe))
                {
                    if (NativeLibrary.TryLoad(probe, out var handle))
                    {
                        return handle;
                    }
                }
            }
        }
        return IntPtr.Zero;
    }

    private static bool CheckAvailability()
    {
        try
        {
            var ptr = GetReSamplerVersion();
            return ptr != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetReSamplerVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResampleWavInMemory(
        byte[] inWavBytes,
        UIntPtr inSizeBytes,
        out IntPtr outWavBytes,
        out UIntPtr outSizeBytes,
        int targetSampleRate,
        int targetBits,
        int ditherProfile,
        double ditherAmount,
        int doublePrecision,
        int multiThreaded
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeResampledBuffer(IntPtr buffer);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResampleFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputFilePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputFilePath,
        int targetSampleRate,
        int targetBits,
        int ditherProfile,
        double ditherAmount,
        int doublePrecision,
        int multiThreaded,
        int quiet
    );

    /// <summary>
    /// Récupère la version de ReSampler sous forme de chaîne (ex: "2.1.2").
    /// </summary>
    public static string GetVersion()
    {
        EnsureAvailable();
        var ptr = GetReSamplerVersion();
        return Marshal.PtrToStringAnsi(ptr) ?? "unknown";
    }

    /// <summary>
    /// Rééchantillonne un buffer de données WAV en mémoire vive (RAM) et retourne le flux WAV converti.
    /// Zéro round-trip disque, zéro latence d'exécutable externe.
    /// </summary>
    public static byte[] Resample(byte[] inWavBytes, ResampleOptions options, int overrideSampleRate = 0)
    {
        ArgumentNullException.ThrowIfNull(inWavBytes);
        ArgumentNullException.ThrowIfNull(options);
        EnsureAvailable();

        int targetRate = overrideSampleRate > 0
            ? overrideSampleRate
            : (options.TargetSampleRate > 0 ? options.TargetSampleRate : 44100);

        IntPtr outPtr;
        UIntPtr outSize;

        int status = ResampleWavInMemory(
            inWavBytes,
            (UIntPtr)inWavBytes.Length,
            out outPtr,
            out outSize,
            targetRate,
            options.TargetBits,
            options.DitherProfile,
            options.DitherAmount,
            options.DoublePrecision ? 1 : 0,
            options.MultiThreadedChannels ? 1 : 0
        );

        if (status != 0 || outPtr == IntPtr.Zero)
        {
            throw new ReSamplerException($"Échec du rééchantillonnage en mémoire vive (code d'erreur native: {status})", status);
        }

        try
        {
            int size = (int)outSize.ToUInt32();
            var result = new byte[size];
            Marshal.Copy(outPtr, result, 0, size);
            return result;
        }
        finally
        {
            FreeResampledBuffer(outPtr);
        }
    }

    /// <summary>
    /// Rééchantillonne un fichier WAV sur disque via l'API native.
    /// </summary>
    public static void ResampleDiskFile(string inputPath, string outputPath, ResampleOptions options, bool quiet = true)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        EnsureAvailable();

        int status = ResampleFile(
            inputPath,
            outputPath,
            options.TargetSampleRate,
            options.TargetBits,
            options.DitherProfile,
            options.DitherAmount,
            options.DoublePrecision ? 1 : 0,
            options.MultiThreadedChannels ? 1 : 0,
            quiet ? 1 : 0
        );

        if (status != 0)
        {
            throw new ReSamplerException($"Échec du rééchantillonnage de '{inputPath}' vers '{outputPath}' (code: {status})", status);
        }
    }

    private static void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                "La bibliothèque native 'resampler.dll' (ou ses dépendances 'sndfile.dll' et 'libfftw3-3.dll') est introuvable. " +
                "Veuillez vous assurer que ces DLL 64-bit sont présentes dans le répertoire de l'application.");
        }
    }
}
