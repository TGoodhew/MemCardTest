# MemCardTest

A small Windows console utility for managing the storage on an HP/Agilent
**8563E** spectrum analyzer fitted with the **85620A Mass Memory Module** and
a FRAM memory card. Talks to the analyzer over GPIB via NI-VISA.

It can list and clear the module's RAM, list and (semi-)clear the FRAM card,
copy files MEM → CARD with per-file verification, compare the two stores, and
probe individual filenames on the card without going through CATALOG?.

## Hardware

- HP/Agilent **8563E** spectrum analyzer
- HP **85620A** Mass Memory Module
- FRAM memory card seated in the 85620A
- A GPIB controller on the host PC; analyzer expected at `GPIB0::18::INSTR`
  (change [`Resource8563E`](MemCardTest/Program.cs#L47) if yours differs)

## Prerequisites

- Windows
- .NET Framework 4.7.2
- NI-VISA installed (the project references `Ivi.Visa.dll` and
  `NationalInstruments.Visa.dll` from the IVI Foundation install — see the
  `HintPath` entries in [MemCardTest.csproj](MemCardTest/MemCardTest.csproj)
  and adjust if your VISA install lives elsewhere)

## Build

```powershell
dotnet build
```

Or open [MemCardTest.sln](MemCardTest.sln) in Visual Studio.

## Usage

Two modes share a single dispatcher, so every menu entry is also reachable
from the CLI.

### Interactive menu

Run with no arguments and pick from the list:

```powershell
.\MemCardTest\bin\Debug\net472\MemCardTest.exe
```

### CLI

Pass one or more commands as positional arguments. They run sequentially in
a single GPIB session, so you can chain `connect` with the operation you
actually want.

| Command          | Action                                  |
| ---------------- | --------------------------------------- |
| `connect`        | Open the GPIB session to the 8563E      |
| `catalog-mem`    | List files in the Mass Memory Module    |
| `clear-mem`      | `DISPOSE ALL` on module RAM             |
| `catalog-card`   | List files on the FRAM card             |
| `card-info`      | Show file count + `BYTES FREE` for card |
| `format-card`    | Walk the user through front-panel format (cannot be done over GPIB on this analyzer) |
| `copy-to-card`   | Copy every file MEM → CARD, verify each |
| `copy-from-card` | Copy every file CARD → MEM (stub)       |
| `compare`        | Diff MEM vs. CARD by name               |
| `probe-card`     | Per-name `CARDLOAD` probe of every MEM filename against the card |

Examples:

```powershell
MemCardTest connect copy-to-card
MemCardTest connect catalog-card
MemCardTest connect compare
```

`help`, `-h`, `--help`, or `/?` prints the same table.

## Logging

Each session writes a fresh log file (`MemCardTest.log`) next to the
executable, including every GPIB command sent, every error code returned, and
the per-file summary of any copy operation. The log path is printed at
startup.

## Known issues

- **`CATALOG?` on CARD truncates at ~903 bytes (~55 entries).** Documented
  inline at the use site in
  [Program.cs](MemCardTest/Program.cs) — the post-copy verification works
  around it by issuing per-name `CARDLOAD` probes instead of trusting the
  catalog. Root cause not yet pinned down (firmware vs. driver vs. this
  code); reproduction from HTBasic still pending.
- **`format-card` is front-panel only.** `FORMAT n` over GPIB returns
  `ERR 112 NOT CTRL` because the PC is the system controller, and DLP-wrapped
  variants are stored but not executed. The command walks the user through
  the manual sequence and releases the analyzer to local control so the
  front-panel keys respond.
- **`copy-from-card` is not yet implemented.**

## License

MIT — see [LICENSE](LICENSE).
