# conNCW-NG (C# / .NET 8 LTS)

Next-generation audio pipeline for **NCW** (Native Instruments Kontakt compressed audio) and **WAV** formats, completely rewritten in C# (.NET 8 LTS) with native **ReSampler** DSP integration, parallel multi-threading, in-memory audio conversions, and automated inventory/signature management.

---

## Key Highlights & Architectural Enhancements

- **In-Memory Audio Pipeline (Zero Disk I/O Overhead)**:
  - Direct NCW-to-NCW (`-n2n`) and WAV-to-WAV (`-w2w`) conversion entirely in RAM.
  - Decodes NCW blocks directly to memory, feeds PCM audio into `resampler.dll`, and encodes the resampled output directly back to NCW.
  - Completely eliminates temporary disk WAV files, resulting in massive speedups (e.g. 3,100+ files processed in 8.5 seconds on a modern multi-core CPU).

- **Native ReSampler DLL Integration (`--resampler`)**:
  - Embedded C API bindings to high-grade `resampler.dll` (backed by libsndfile & FFTW3).
  - Full support for 64-bit double-precision processing (`--doubleprecision`), customizable noise-shaping dither profiles (`--ns 0..15`, default Lipshitz 6), dither amounts, and target sample rates (`-sr`).

- **Selective Low Bit-Depth Bypass (`--bypass-lowdepth` / `--bypass-16bit`)**:
  - Optional high-speed bypass flag: when converting to a target depth (e.g. 16-bit), any audio sample that is already $\le$ 16 bits (such as 16-bit or 8-bit files) and matches the target sample rate is bypassed directly.
  - For NCW-to-NCW, the NCW header is inspected in $\mu$s without decoding audio blocks, and the file is copied directly without recompression or CPU waste.

- **Parallel Multi-Threaded Processing (`-t` / `--threads`)**:
  - Leverages all available CPU logical cores by default for parallel batch processing across files.
  - Safe concurrent access to native DSP resources and signature databases.

- **Automated Signature Learning & Fault Tolerance**:
  - Comprehensive signature store (`signatures.json`) supporting modern and legacy Kontakt variants.
  - Interactive signature learner (`--learn-signature <file>`) to safely inspect, listen, and register unknown NCW variants.
  - Tolerance testing mode (`--bypass-signature`) with PCM plausibility heuristics (silence, clipping, range checks) to test unmapped files without risking corruption.

- **Directory Inventory & Analysis (`--inventory`)**:
  - Deep-scans entire directory trees to export CSV manifests detailing sample counts, bit depths, sample rates, channels, NCW signatures, and data integrity.

---

## Command-Line Usage & Flags

### Conversion Modes

| Option | Conversion Mode | Description |
|---|---|---|
| `-n2n` | NCW $\rightarrow$ NCW | In-memory conversion: decode NCW, resample/dither in RAM, re-encode NCW |
| `-w2w` | WAV $\rightarrow$ WAV | In-memory conversion: decode WAV, resample/dither in RAM, save resampled WAV |
| `-n2w` | NCW $\rightarrow$ WAV | Decode NCW to uncompressed standard WAV (optionally resampled) |
| `-w2n` | WAV $\rightarrow$ NCW | Encode WAV to compressed NCW (optionally resampled) |
| `-l @list.txt` | List Mode | Convert files specified in a text file (`source [\| destination]`) |

### ReSampler & Audio Processing Options

| Option | Argument | Description |
|---|---|---|
| `--resampler`, `--resample` | *none* | Enable in-memory sample rate conversion and dithering via `resampler.dll` |
| `-sr`, `--rate` | `<Hz>` | Target sample rate in Hz (e.g. `44100`, `48000`). If omitted, preserves source rate |
| `-b`, `--bits` | `<depth>` | Target bit depth (`16`, `24`, `32`; default: `16`) |
| `--ns`, `--dither-profile` | `<id>` | Noise-shaping dither profile ID (`0` to `15`, default `6` Standard Lipshitz) |
| `--dither` | `<factor>` | Dither attenuation factor (default: `1.0`, `0` = disabled) |
| `--doubleprecision` | `[true\|false]` | 64-bit double precision floating-point DSP (default: `true`) |
| `--bypass-lowdepth` | *none* | Bypass resampling/recompression for audio already $\le$ target bit depth (alias: `--bypass-16bit`) |
| `-t`, `--threads` | `<count>` | Worker thread count for parallel batch processing (default: CPU logical cores) |
| `--mt` | `[true\|false]` | Multi-threaded channel processing per file (default: `false`, file-level parallelism preferred) |

### General Options & Diagnostics

| Option | Argument | Description |
|---|---|---|
| `-rec`, `-r` | *none* | Recursively process subdirectories |
| `-rw` | *none* | Overwrite existing destination files |
| `--log` | `<path>` | Path to timestamped conversion log file |
| `--inventory` | *none* | Directory audit mode: generates a CSV report of all NCW files in the tree |
| `--output` | `<path>` | Output path for CSV inventory or export |
| `--learn-signature` | `<file.ncw>` | Interactively analyze and register a new signature into `signatures.json` |
| `--label` | `<text>` | Custom label when saving a learned signature |
| `--bypass-signature` | *none* | Force decode suspect NCW files into `_bypass_test/` for acoustic validation |

---

## Usage Examples

#### 1. In-RAM High-Speed Library Optimization (24-bit NCW $\rightarrow$ 16-bit NCW with low-depth bypass)
```cmd
conNCW-NG.exe "C:\SourceLibrary" "D:\ConvertedLibrary" -n2n --resampler -b 16 --ns 6 --doubleprecision --bypass-lowdepth -rec -rw
```

#### 2. Re-encoding Loose WAV Files Directly to 16-bit NCW in RAM
```cmd
conNCW-NG.exe "C:\WavSamples" "D:\NcwArchive" -w2n --resampler -b 16 --ns 6 -rec -rw
```

#### 3. Generating a Complete Inventory of an NCW Sample Repository
```cmd
conNCW-NG.exe --inventory "D:\KontaktLibrary" --output "inventory_report.csv"
```

#### 4. Testing a Corrupted or Unknown-Signature NCW File
```cmd
conNCW-NG.exe "C:\Samples\unknown_sig.ncw" --bypass-signature
```

---

## Project Structure

```
src/
  ConNCW.Core/
    Engine/         ConversionEngine (orchestrates conversions, bypass routes, parallel workers)
    Resampler/      NativeReSampler (P/Invoke to resampler.dll), ResampleOptions
    Ncw/            NcwFile, NcwHeader, NcwBlockReader/Writer
    Wav/            WavFile (generic RIFF/chunk parser, IEEE float and PCM)
    Signatures/     SignatureStore, SignatureEntry, SignatureLearner, signatures.json
    Bypass/         BypassAnalyzer (audio heuristics), BypassRunner
    Logging/        ConversionLogger (thread-safe console + file logging)
    Inventory/      NcwInventoryCommand (CSV extraction)
  ConNCW.Cli/
    Program.cs      System.CommandLine CLI integration & command dispatching
tests/
  ConNCW.Core.Tests/
    ResamplerPipelineTests.cs    Full end-to-end and in-memory unit tests
```

---

## Building & Publishing

Requires **.NET 8.0 SDK** or higher.

### Standard Debug / Release Build
```cmd
dotnet build -c Release -p:Platform=x64
```

### Self-Contained Native Distribution
```cmd
dotnet publish src/ConNCW.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```
*Note: ensure `resampler.dll` and its dependencies (`sndfile.dll`, `libfftw3-3.dll`) are placed in the same folder as `conNCW-NG.exe`.*
