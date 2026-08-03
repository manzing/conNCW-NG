namespace ConNCW.Core.IO;

/// <summary>
/// Parcours de fichiers, corrige le bug historique 0.5 ("le programme ne traitait pas
/// les fichiers s'il n'y en avait pas directement à la racine du dossier").
/// Utilise SearchOption.AllDirectories qui élimine structurellement ce cas.
/// </summary>
public static class FileScanner
{
    public static IEnumerable<string> FindFiles(string rootDirectory, string pattern, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // EnumerateFiles descend dans TOUTES les sous-arborescences avec AllDirectories,
        // même si la racine elle-même ne contient aucun fichier correspondant au pattern.
        return Directory.EnumerateFiles(rootDirectory, pattern, option);
    }

    /// <summary>
    /// Calcule le chemin de destination en miroir de l'arborescence source,
    /// pour préserver la structure de sous-dossiers lors d'une conversion récursive.
    /// </summary>
    public static string MapToDestination(string sourceRoot, string destRoot, string sourceFilePath, string newExtension)
    {
        string relative = Path.GetRelativePath(sourceRoot, sourceFilePath);
        string relativeNoExt = Path.ChangeExtension(relative, newExtension);
        string destPath = Path.Combine(destRoot, relativeNoExt);

        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        return destPath;
    }
}
