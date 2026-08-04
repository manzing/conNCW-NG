// Correctif du bug de parsing Windows : un chemin entre guillemets se terminant
// par un antislash unique avant le guillemet fermant (ex: "C:\temp\dossier1\")
// est mal interprété par le parseur natif Windows (CommandLineToArgvW / CRT),
// qui traite le guillemet comme littéral au lieu de fermer l'argument, fusionnant
// ainsi les arguments suivants dans une seule chaîne corrompue.
//
// Un guillemet (") étant un caractère interdit dans un chemin Windows, tout "\""
// rencontré dans la ligne de commande brute ne peut être qu'un artefact de ce bug
// -> on peut le corriger sans ambiguïté ni risque de régression.
//
// À utiliser en tout début de Main(), AVANT de transmettre les arguments à
// System.CommandLine (ou tout autre parseur) : remplacer `args` par
// `CommandLineFix.GetCorrectedArgs()`.

using System.Text;

public static class CommandLineFix
{
    /// <summary>
    /// Reparse Environment.CommandLine avec une tolérance spécifique au cas
    /// "antislash unique + guillemet fermant" typique des chemins de dossiers
    /// copiés depuis l'Explorateur Windows (qui ajoute systématiquement un \ final).
    /// </summary>
    public static string[] GetCorrectedArgs()
    {
        string commandLine = Environment.CommandLine;

        // On saute l'argument 0 (chemin de l'exécutable), qui peut lui-même
        // être entre guillemets.
        int i = 0;
        SkipWhitespace(commandLine, ref i);
        SkipExecutablePath(commandLine, ref i);

        var result = new List<string>();
        var current = new StringBuilder();

        while (i < commandLine.Length)
        {
            SkipWhitespace(commandLine, ref i);
            if (i >= commandLine.Length) break;

            current.Clear();
            bool inQuotes = false;

            while (i < commandLine.Length)
            {
                char c = commandLine[i];

                if (c == '\\')
                {
                    int backslashCount = 0;
                    int start = i;
                    while (i < commandLine.Length && commandLine[i] == '\\')
                    {
                        backslashCount++;
                        i++;
                    }

                    bool followedByQuote = i < commandLine.Length && commandLine[i] == '"';

                    if (followedByQuote)
                    {
                        bool oddCount = (backslashCount % 2) == 1;

                        // Regarde ce qui suit le guillemet : fin de chaîne ou
                        // espace => ce guillemet est très probablement destiné
                        // à FERMER l'argument (chemin type Explorateur Windows),
                        // pas à être échappé littéralement.
                        bool nextIsBoundary = (i + 1 >= commandLine.Length) ||
                                              char.IsWhiteSpace(commandLine[i + 1]);

                        if (oddCount && inQuotes && nextIsBoundary)
                        {
                            // Cas corrigé : on garde TOUS les antislashs comme
                            // littéraux (séparateurs de chemin), et le guillemet
                            // referme réellement l'argument.
                            current.Append('\\', backslashCount);
                            i++; // consomme le guillemet fermant
                            inQuotes = false;
                            continue;
                        }

                        // Comportement standard Microsoft (inchangé) :
                        // floor(N/2) antislashs littéraux, N impair => guillemet
                        // littéral et pas de bascule inQuotes ; N pair => bascule.
                        current.Append('\\', backslashCount / 2);
                        if (oddCount)
                        {
                            current.Append('"');
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                        }
                        i++; // consomme le guillemet
                        continue;
                    }
                    else
                    {
                        current.Append('\\', backslashCount);
                        continue;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    i++;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    break;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }

            result.Add(current.ToString());
        }

        return result.ToArray();
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static void SkipExecutablePath(string s, ref int i)
    {
        if (i < s.Length && s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"') i++;
            if (i < s.Length) i++; // consomme le guillemet fermant
        }
        else
        {
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        }
    }
}
