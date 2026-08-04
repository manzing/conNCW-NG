# conNCW-NG (C# / .NET 10 port)

Full port of the legacy Delphi software **conNCW** (NCW <-> WAV conversion,
Native Instruments Kontakt) to .NET 10 LTS, x64, with robustness fixes.

## Status: in progress

### Fixes applied compared to the original
- **NCW signature**: whole-block comparison against an extensible list
  (`signatures.json`) instead of a byte-by-byte comparison against two
  hardcoded values — eliminates false negatives on undocumented variants.
- **Recursive traversal**: `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`,
  which permanently fixes bug 0.5 ("the program did not process files unless
  some were present directly at the root of the folder").
- **WAV parsing**: generic chunk-based RIFF parser that properly skips
  unknown chunks (`LIST`, `JUNK`, `fact`, `PEAK`, `bext`, ...) instead of
  crashing, unlike the original's rigid behavior.
- **Logging**: added a proper timestamped log file (absent from the
  original), in addition to the live-scrolling console output as before; a
  final summary (detailed successes/failures) is still displayed on screen at
  the end of each run.

## New flags (in addition to the original syntax, unchanged)

| Flag | Purpose |
|---|---|
| `--learn-signature <file.ncw>` | Analyzes the file's signature, asks for interactive confirmation, then adds it to `signatures.json` |
| `--label <text>` | Label to associate with the learned signature (used with `--learn-signature`) |
| `--bypass-signature` | Forces NCW decoding while ignoring the signature, analyzes the resulting PCM stream (plausibility heuristics), writes a test WAV to `_bypass_test/` for manual listening before validation |
| `--log <path>` | Path to the log file (default `conncw_<timestamp>.log`) |

## Original syntax preserved identically

```
conncw file.ncw [file.wav]                    # file mode, direction auto-detected
conncw src_folder dst_folder -n2w -r          # folder mode, NCW->WAV, recursive
conncw src_folder dst_folder -w2n -r -rw      # folder mode, WAV->NCW, recursive, overwrite existing
conncw -l @mylist.txt                          # list mode
```

## Recommended workflow for unknown signatures

1. Run a normal conversion: files with an unknown signature are cleanly
   rejected with `[FAIL] ... unknown signature: <hex>` in the console and
   in the log.
2. Test plausibility without writing an actual output file:
   ```
   conncw suspect_file.ncw --bypass-signature
   ```
   → generates a test WAV in `_bypass_test/` plus a confidence score and
   warnings (abnormal silence, clipping, range overflow).
3. Listen to the test WAV (or check in Kontakt) to confirm the file is
   valid.
4. If validated, learn the signature:
   ```
   conncw --learn-signature suspect_file.ncw --label "Kontakt7-variant"
   ```
   → interactive confirmation before writing to `signatures.json`.
5. Future runs will automatically recognize this signature.

## Project structure

```
src/
  ConNCW.Core/
    Models/       AudioFormat, NcwHeader, ConversionResult
    Signatures/   SignatureStore, SignatureEntry, SignatureLearner, signatures.json
    Bit/          BitReader, BitWriter (replaces raw Delphi pointers)
    Ncw/          NcwFile (decode/encode), NcwBlockHeader
    Wav/          WavFile (generic RIFF parser), WavFileFactory
    IO/           FileScanner (recursion fix)
    Logging/      ConversionLogger (console + file + summary)
    Bypass/       BypassAnalyzer (heuristics), BypassRunner (orchestration)
    Engine/       ConversionEngine (orchestration core, 3 modes)
  ConNCW.Cli/
    Program.cs    System.CommandLine CLI, syntax parity + new flags
```

## Build

```
dotnet build -c Release -p:Platform=x64
```

## Self-contained publishing

```
dotnet publish src/ConNCW.Cli -c Release -r win-x64 --self-contained -p:PublishAot=true
```


