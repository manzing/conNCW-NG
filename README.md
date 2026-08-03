# conNCW-NG (portage C# / .NET 10)

Portage complet du logiciel Delphi historique **conNCW** (conversion NCW <-> WAV,
Native Instruments Kontakt) vers .NET 10 LTS, x64, avec corrections de robustesse.

## Statut : en cours

### Corrections apportées par rapport à l'original
- **Signature NCW** : comparaison en bloc entier via une liste extensible
  (`signatures.json`) au lieu d'une comparaison octet-par-octet contre deux
  valeurs figées — élimine les faux négatifs sur les variantes non documentées.
- **Parcours récursif** : `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`,
  qui corrige définitivement le bug 0.5 ("le programme ne traitait pas les
  fichiers s'il n'y en avait pas directement à la racine du dossier").
- **Parsing WAV** : parseur générique par chunks RIFF qui ignore proprement les
  chunks inconnus (`LIST`, `JUNK`, `fact`, `PEAK`, `bext`, ...) au lieu de
  planter, contrairement au comportement original rigide.
- **Logging** : ajout d'un vrai fichier de log horodaté (absent de l'original),
  en complément de la console qui défile en direct comme avant ; un résumé
  final (réussites / échecs détaillés) reste affiché à l'écran en fin de run.

## Nouveaux flags (en plus de la syntaxe d'origine, inchangée)

| Flag | Rôle |
|---|---|
| `--learn-signature <fichier.ncw>` | Analyse la signature du fichier, demande confirmation interactive, puis l'ajoute à `signatures.json` |
| `--label <texte>` | Label à associer à la signature apprise (utilisé avec `--learn-signature`) |
| `--bypass-signature` | Force le décodage NCW en ignorant la signature, analyse le flux PCM obtenu (heuristiques de plausibilité), écrit un WAV de test dans `_bypass_test/` pour écoute manuelle avant validation |
| `--log <chemin>` | Chemin du fichier de log (par défaut `conncw_<timestamp>.log`) |

## Syntaxe d'origine conservée à l'identique

```
conncw fichier.ncw [fichier.wav]              # mode fichier, auto-détection direction
conncw dossier_src dossier_dst -n2w -r        # mode dossier, NCW->WAV, récursif
conncw dossier_src dossier_dst -w2n -r -rw    # mode dossier, WAV->NCW, récursif, écrase existants
conncw -l @maliste.txt                         # mode liste
```

## Workflow recommandé pour les signatures inconnues

1. Lancer une conversion normale : les fichiers à signature inconnue sont
   rejetés proprement avec `[FAIL] ... signature inconnue: <hex>` en console
   et dans le log.
2. Tester la plausibilité sans écrire de vrai fichier de sortie :
   ```
   conncw fichier_suspect.ncw --bypass-signature
   ```
   → génère un WAV de test dans `_bypass_test/` + un score de confiance et
   des avertissements (silence anormal, clipping, débordement de plage).
3. Écouter le WAV de test (ou vérifier dans Kontakt) pour confirmer que le
   fichier est valide.
4. Si validé, apprendre la signature :
   ```
   conncw --learn-signature fichier_suspect.ncw --label "Kontakt7-variant"
   ```
   → confirmation interactive avant écriture dans `signatures.json`.
5. Les prochains runs reconnaîtront automatiquement cette signature.

## Structure du projet

```
src/
  ConNCW.Core/
    Models/       AudioFormat, NcwHeader, ConversionResult
    Signatures/   SignatureStore, SignatureEntry, SignatureLearner, signatures.json
    Bit/          BitReader, BitWriter (remplace les pointeurs bruts Delphi)
    Ncw/          NcwFile (decode/encode), NcwBlockHeader
    Wav/          WavFile (parseur RIFF générique), WavFileFactory
    IO/           FileScanner (fix récursivité)
    Logging/      ConversionLogger (console + fichier + résumé)
    Bypass/       BypassAnalyzer (heuristiques), BypassRunner (orchestration)
    Engine/       ConversionEngine (cœur d'orchestration, 3 modes)
  ConNCW.Cli/
    Program.cs    CLI System.CommandLine, parité syntaxe + nouveaux flags
```

## Build

```
dotnet build -c Release -p:Platform=x64
```

## Publication autonome

```
dotnet publish src/ConNCW.Cli -c Release -r win-x64 --self-contained -p:PublishAot=true
```

