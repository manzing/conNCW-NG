using ConNCW.Core.Ncw;

namespace ConNCW.Core.Signatures;

/// <summary>
/// Orchestration de --learn-signature : extrait la signature candidate d'un
/// fichier NCW donné et l'ajoute à signatures.json après confirmation manuelle
/// (l'appelant est responsable d'avoir vérifié le fichier dans Kontakt, ou
/// d'avoir validé le rapport --bypass-signature).
/// </summary>
public static class SignatureLearner
{
    public sealed record LearnPreview(byte[] SignatureBytes, string HexBytes);

    public static LearnPreview PreviewSignature(string ncwPath)
    {
        using var stream = File.OpenRead(ncwPath);
        var buffer = new byte[8];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read < buffer.Length)
        {
            throw new InvalidDataException("Fichier trop petit pour en extraire une signature NCW.");
        }
        return new LearnPreview(buffer, Convert.ToHexString(buffer));
    }

    public static void Confirm(SignatureStore store, byte[] signatureBytes, string label, string? notes = null)
    {
        store.AddLearned(signatureBytes, label, notes);
    }
}
