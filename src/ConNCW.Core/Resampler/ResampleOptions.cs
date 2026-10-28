namespace ConNCW.Core.Resampler;

/// <summary>
/// Options de configuration pour le rééchantillonnage via resampler.dll.
/// Maintient une parité stricte avec les fonctionnalités de ReSampler CLI :
/// double-précision, sélection du profil de dither, dithering, multithreading, etc.
/// </summary>
public sealed record ResampleOptions
{
    /// <summary>Fréquence d'échantillonnage cible (ex: 44100, 48000). 0 = conserver la fréquence d'origine du fichier source.</summary>
    public int TargetSampleRate { get; init; } = 0;

    /// <summary>Résolution binaire cible en bits (16, 24, 32). Par défaut : 16.</summary>
    public int TargetBits { get; init; } = 16;

    /// <summary>ID du profil de dither / mise en forme du bruit (Noise Shaping). Par défaut : 6 (Lipshitz / Standard).</summary>
    public int DitherProfile { get; init; } = 6;

    /// <summary>Facteur d'atténuation/quantité de dither (typiquement 1.0). 0 pour désactiver.</summary>
    public double DitherAmount { get; init; } = 1.0;

    /// <summary>Traitement en double précision (64-bit flottant) pour une qualité maximale. Recommandé.</summary>
    public bool DoublePrecision { get; init; } = true;

    /// <summary>Multi-threading interne par canal audio (stéréo/surround). Par défaut désactivé car la parallélisation se fait au niveau des fichiers.</summary>
    public bool MultiThreadedChannels { get; init; } = false;

    /// <summary>Nombre de threads pour le traitement parallèle des fichiers par conNCW-NG (0 = tous les cœurs logiques).</summary>
    public int WorkerThreads { get; init; } = 0;
}
