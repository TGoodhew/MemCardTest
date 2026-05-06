using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ivi.Visa;
using NationalInstruments.Visa;
using Spectre.Console;

namespace MemCardTest
{
    internal class Program
    {
        private const string Connect8563E = "Connect to 8563E";
        private const string CatalogMass = "Catalog Mass Memory Module";
        private const string ClearMass = "Clear Mass Memory Module";
        private const string CatalogFram = "Catalog FRAM Card";
        private const string CardInfo = "FRAM Card Info";
        private const string ClearFram = "Clear FRAM Card";
        private const string CopyMassToFram = "Copy Mass Memory to FRAM";
        private const string CopyFramToMass = "Copy FRAM to Mass Memory";
        private const string CompareName = "Compare MEM and Card";
        private const string Exit = "Exit";

        // Not in the interactive menu — only reachable via the "probe-card"
        // CLI command. Kept as a const so the dispatcher and CLI table share
        // a single source of truth.
        private const string ProbeCardName = "Probe Card";

        // CLI command name -> menu display name so the same Handle() dispatch
        // serves both interactive and CLI mode. Adding a menu item only requires
        // adding a constant above and an entry here.
        private static readonly Dictionary<string, string> _cliCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "connect",        Connect8563E },
            { "catalog-mem",    CatalogMass },
            { "clear-mem",      ClearMass },
            { "catalog-card",   CatalogFram },
            { "card-info",      CardInfo },
            { "format-card",    ClearFram },
            { "copy-to-card",   CopyMassToFram },
            { "copy-from-card", CopyFramToMass },
            { "compare",        CompareName },
            { "probe-card",     ProbeCardName },
        };

        private const string Resource8563E = "GPIB0::18::INSTR";
        private const string LogFileName = "MemCardTest.log";

        // While copying, query the card's BYTES FREE every N files to detect
        // when CARDSTORE has stopped actually committing to the card despite
        // returning ERR 0. CATALOG? on the card forces a sync flush so this
        // also gives the analyzer a chance to drain any internal write queue.
        private const int BytesFreeCheckEvery = 5;

        // After the first END is received on a CATALOG? response, the analyzer
        // may keep sending additional EOI-terminated chunks (pagination of a
        // long listing, plus the trailing BYTES FREE). Two seconds is generous
        // — the analyzer either has more to say within that window or it's done.
        private const int CatalogDrainTimeoutMs = 2000;

        private static ResourceManager _rm;
        private static MessageBasedSession _session;

        private static int Main(string[] args)
        {
            Log.Open();
            try
            {
                AnsiConsole.Write(
                    new FigletText("MemCardTest")
                        .LeftJustified()
                        .Color(Color.Aqua));
                AnsiConsole.MarkupLine($"[grey]Logging to[/] {Markup.Escape(Log.Path)}");

                if (args != null && args.Length > 0)
                {
                    return RunCli(args);
                }

                while (true)
                {
                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select an [yellow]operation[/]:")
                            .PageSize(10)
                            .AddChoices(
                                Connect8563E,
                                CatalogMass,
                                ClearMass,
                                CatalogFram,
                                CardInfo,
                                ClearFram,
                                CopyMassToFram,
                                CopyFramToMass,
                                CompareName,
                                Exit));

                    if (choice == Exit)
                    {
                        Log.Info("Exit selected; shutting down.");
                        AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
                        return 0;
                    }

                    Log.Info($"Menu selection: {choice}");
                    Handle(choice);
                    AnsiConsole.WriteLine();
                }
            }
            finally
            {
                _session?.Dispose();
                _rm?.Dispose();
                Log.Close();
            }
        }

        private static int RunCli(string[] args)
        {
            // Each positional arg is one command; commands run in sequence
            // sharing the same GPIB session. The "help" / "-h" / "--help"
            // tokens print usage and exit 0 without executing anything else.
            if (args.Any(a => a == "help" || a == "-h" || a == "--help" || a == "/?"))
            {
                PrintCliHelp();
                return 0;
            }

            foreach (var arg in args)
            {
                if (!_cliCommands.TryGetValue(arg, out var menuName))
                {
                    Log.Error($"CLI: unknown command '{arg}'.");
                    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(arg)}");
                    PrintCliHelp();
                    return 1;
                }

                Log.Info($"CLI command: {arg} ({menuName})");
                AnsiConsole.MarkupLine($"[grey]>[/] [aqua]{Markup.Escape(arg)}[/]");
                Handle(menuName);
                AnsiConsole.WriteLine();
            }

            return 0;
        }

        private static void PrintCliHelp()
        {
            AnsiConsole.MarkupLine("[bold]Usage:[/] MemCardTest [[command...]]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Run with no arguments for the interactive menu.");
            AnsiConsole.MarkupLine("Pass one or more commands to run them in sequence (single GPIB session).");
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[yellow]Command[/]")
                .AddColumn("[yellow]Action[/]");
            foreach (var kvp in _cliCommands)
            {
                table.AddRow(Markup.Escape(kvp.Key), Markup.Escape(kvp.Value));
            }
            AnsiConsole.Write(table);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Examples:[/]");
            AnsiConsole.MarkupLine("  MemCardTest connect copy-to-card compare");
            AnsiConsole.MarkupLine("  MemCardTest connect format-card copy-to-card compare");
            AnsiConsole.MarkupLine("  MemCardTest connect card-info");
        }

        private static class Log
        {
            private static StreamWriter _writer;
            private static readonly object _lock = new object();

            public static string Path { get; private set; }

            public static void Open()
            {
                Path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
                Info("=== MemCardTest session start ===");
            }

            public static void Info(string msg) => Write("INFO", msg);
            public static void Warn(string msg) => Write("WARN", msg);
            public static void Error(string msg) => Write("ERROR", msg);

            public static void Close()
            {
                Info("=== MemCardTest session end ===");
                _writer?.Dispose();
                _writer = null;
            }

            private static void Write(string level, string msg)
            {
                if (_writer == null) return;
                lock (_lock)
                {
                    _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
                }
            }
        }

        private static void Handle(string choice)
        {
            switch (choice)
            {
                case Connect8563E:
                    ConnectTo8563E();
                    break;
                case CatalogMass:
                    Catalog("Mass Memory Module", "MEM");
                    break;
                case CatalogFram:
                    Catalog("FRAM Card", "CARD");
                    break;
                case CardInfo:
                    ShowCardInfo();
                    break;
                case ClearFram:
                    ClearFramCard();
                    break;
                case ClearMass:
                    ClearMassMemory();
                    break;
                case CopyMassToFram:
                    CopyMassMemoryToCard();
                    break;
                case CompareName:
                    CompareCatalogs();
                    break;
                case ProbeCardName:
                    ProbeCard();
                    break;
                default:
                    AnsiConsole.MarkupLine($"[green]>[/] {choice}");
                    // TODO: implement operation
                    break;
            }
        }

        private static bool EnsureConnected()
        {
            if (_session != null)
            {
                return true;
            }

            Log.Warn("Operation aborted: no active session.");
            AnsiConsole.MarkupLine("[red]Not connected.[/] Choose [yellow]Connect to 8563E[/] first.");
            return false;
        }

        private static void Catalog(string label, string device)
        {
            if (!EnsureConnected())
            {
                return;
            }

            try
            {
                Log.Info($"Catalog {label} (MSDEV {device}).");
                string text = null;
                AnsiConsole.Status().Start(
                    $"Reading catalog from {label}...",
                    _ => text = ReadCatalog(device));

                Log.Info($"Catalog {label} returned {text?.Length ?? 0} bytes.");
                RenderCatalog(label, text);
            }
            catch (Exception ex)
            {
                Log.Error($"Catalog {label} failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Catalog failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void ProbeCard()
        {
            if (!EnsureConnected())
            {
                return;
            }

            try
            {
                Log.Info("ProbeCard: cataloging MEM to get probe names.");
                string memRaw = null;
                AnsiConsole.Status().Start(
                    "Reading Mass Memory Module catalog...",
                    _ => memRaw = ReadCatalog("MEM"));

                var memFiles = ExtractFilenamesWithSizes(memRaw);
                if (memFiles.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]MEM catalog is empty — no probe names available.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"Probing card for [yellow]{memFiles.Length}[/] file name(s) from MEM. ERR 855 = not found, ERR 0 = present.");
                Log.Info($"ProbeCard: probing card for {memFiles.Length} candidate filenames.");

                int present = 0, absent = 0, other = 0;
                AnsiConsole.Progress().Start(ctx =>
                {
                    var task = ctx.AddTask("[yellow]Probing[/] [grey](starting)[/]");
                    task.MaxValue = memFiles.Length;

                    foreach (var (name, size) in memFiles)
                    {
                        task.Description = $"[yellow]Probing[/] {Markup.Escape(name)}";

                        _session.FormattedIO.WriteLine($"CARDLOAD %{name}%;");
                        _session.FormattedIO.WriteLine("DONE?;");
                        _session.FormattedIO.ReadLine();
                        _session.FormattedIO.WriteLine("ERR?;");
                        var err = _session.FormattedIO.ReadLine().Trim();

                        if (err == "0") present++;
                        else if (err == "855") absent++;
                        else other++;

                        Log.Info($"  Probe CARDLOAD {name} -> ERR {err}");
                        task.Increment(1);
                    }
                });

                Log.Info($"ProbeCard totals: present={present}, absent={absent}, other={other}");
                AnsiConsole.MarkupLine($"[green]Present:[/] {present}   [grey]Absent (ERR 855):[/] {absent}   [yellow]Other ERR:[/] {other}");
                AnsiConsole.MarkupLine("[grey]Note: CARDLOAD reloads files into module RAM. Run \"clear-mem\" after if you don't want them.[/]");
            }
            catch (Exception ex)
            {
                Log.Error($"ProbeCard failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Probe failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void CompareCatalogs()
        {
            if (!EnsureConnected())
            {
                return;
            }

            try
            {
                Log.Info("Compare: cataloging MEM.");
                string memRaw = null;
                AnsiConsole.Status().Start(
                    "Reading Mass Memory Module catalog...",
                    _ => memRaw = ReadCatalog("MEM"));

                Log.Info("Compare: cataloging CARD.");
                string cardRaw = null;
                AnsiConsole.Status().Start(
                    "Reading FRAM Card catalog...",
                    _ => cardRaw = ReadCatalog("CARD"));

                // Normalise both sides to LIF card-form so comparisons are
                // apples-to-apples — module names can be longer/mixed-case
                // while card names are uppercase and capped at 9 base chars.
                var memFiles = ExtractFilenamesWithSizes(memRaw);
                var cardFiles = ExtractCardFileNames(cardRaw);

                var memNormSet = new HashSet<string>(
                    memFiles.Select(f => ToCardFileName(f.Name)),
                    StringComparer.OrdinalIgnoreCase);
                var cardNormSet = new HashSet<string>(
                    cardFiles.Select(ToCardFileName),
                    StringComparer.OrdinalIgnoreCase);

                var memOnly = memFiles
                    .Where(f => !cardNormSet.Contains(ToCardFileName(f.Name)))
                    .OrderBy(f => f.Name)
                    .ToArray();
                var cardOnly = cardFiles
                    .Where(c => !memNormSet.Contains(ToCardFileName(c)))
                    .OrderBy(c => c)
                    .ToArray();
                var inBoth = memFiles.Length - memOnly.Length;

                var table = new Table()
                    .Title("[bold aqua]MEM vs CARD[/]")
                    .Border(TableBorder.Rounded)
                    .AddColumn("Bucket")
                    .AddColumn(new TableColumn("Count").RightAligned());
                table.AddRow("[yellow]MEM total[/]", memFiles.Length.ToString());
                table.AddRow("[yellow]CARD total[/]", cardFiles.Length.ToString());
                table.AddRow("[green]In both[/]", inBoth.ToString());
                table.AddRow("[red]MEM only[/]", memOnly.Length.ToString());
                table.AddRow("[red]CARD only[/]", cardOnly.Length.ToString());
                AnsiConsole.Write(table);

                if (memOnly.Length > 0)
                {
                    AnsiConsole.MarkupLine($"[red]Files in MEM but not on CARD ({memOnly.Length}):[/]");
                    foreach (var f in memOnly)
                    {
                        AnsiConsole.MarkupLine($"  {Markup.Escape(f.Name)}");
                    }
                }
                if (cardOnly.Length > 0)
                {
                    AnsiConsole.MarkupLine($"[red]Files on CARD but not in MEM ({cardOnly.Length}):[/]");
                    foreach (var n in cardOnly)
                    {
                        AnsiConsole.MarkupLine($"  {Markup.Escape(n)}");
                    }
                }

                Log.Info($"Compare: MEM={memFiles.Length}, CARD={cardFiles.Length}, both={inBoth}, MEM-only={memOnly.Length}, CARD-only={cardOnly.Length}");
                foreach (var f in memOnly) Log.Info($"  MEM only: {f.Name}");
                foreach (var n in cardOnly) Log.Info($"  CARD only: {n}");
            }
            catch (Exception ex)
            {
                Log.Error($"Compare failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Compare failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void ShowCardInfo()
        {
            if (!EnsureConnected())
            {
                return;
            }

            try
            {
                Log.Info("CardInfo: requesting card catalog.");
                string raw = null;
                AnsiConsole.Status().Start(
                    "Reading card info...",
                    _ => raw = ReadCatalog("CARD"));

                // Pull just the BYTES FREE marker and the file count from the
                // catalog response — that's what tells us the actual usable
                // capacity the firmware allocated, regardless of physical card
                // size. No table rendering; this view is intentionally focused.
                var bytesFreeLine = (raw ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase));

                var files = ExtractCardFileNames(raw);

                AnsiConsole.MarkupLine($"[yellow]Files on card:[/] {files.Length}");
                if (!string.IsNullOrWhiteSpace(bytesFreeLine))
                {
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(bytesFreeLine)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]BYTES FREE not received[/] — listing may be truncated or no card present.");
                }

                Log.Info($"CardInfo: files={files.Length}, {bytesFreeLine ?? "BYTES FREE missing"}");
            }
            catch (Exception ex)
            {
                Log.Error($"CardInfo failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Card info failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void ClearFramCard()
        {
            if (!EnsureConnected())
            {
                return;
            }

            // FORMAT (the only documented card-clear command) cannot be invoked over
            // GPIB on this analyzer: bare FORMAT silently no-ops, FORMAT n triggers
            // ERR 112 NOT CTRL because the analyzer needs to be the system controller
            // (the PC currently is), and DLP-wrapping doesn't help because DLPs over
            // GPIB are stored but not executed — the manual at p.4-4 says execution
            // happens "from the spectrum analyzer front panel." So this operation is
            // front-panel-only by design.
            Log.Info("ClearFramCard: front-panel format flow shown to user.");
            AnsiConsole.MarkupLine("[yellow]The FRAM card cannot be formatted over GPIB on this analyzer.[/]");
            AnsiConsole.MarkupLine("Format it manually from the front panel:");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  1. Press [aqua][[MODULE]][/]");
            AnsiConsole.MarkupLine("  2. Press [aqua]UTILITY[/]");
            AnsiConsole.MarkupLine("  3. Press [aqua]CATALOG MEM CARD[/] until [aqua]CARD[/] is underlined");
            AnsiConsole.MarkupLine("  4. Press [aqua]FORMAT MENU[/]");
            AnsiConsole.MarkupLine("  5. Press [aqua]FORMAT CARD[/], enter [aqua]128[/], press [aqua]Hz[/]");
            AnsiConsole.WriteLine();

            // Send GoToLocal so the analyzer leaves remote mode and the front-panel
            // keys respond. Subsequent GPIB writes (e.g. the verification Catalog
            // below) will return it to remote automatically.
            try
            {
                if (_session is GpibSession gpib)
                {
                    gpib.SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
                    Log.Info("Sent GoToLocal to analyzer.");
                    AnsiConsole.MarkupLine("[grey]Analyzer released to local control.[/]");
                }
                else
                {
                    Log.Warn("Session is not GpibSession; cannot send GoToLocal.");
                    AnsiConsole.MarkupLine("[grey]Press [aqua][[LCL]][/] on the analyzer if the front panel doesn't respond.[/]");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"GoToLocal failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[grey]Could not auto-release ({Markup.Escape(ex.Message)}). Press [aqua][[LCL]][/] on the analyzer.[/]");
            }

            AnsiConsole.WriteLine();
            if (AnsiConsole.Confirm("Card formatted? (No to cancel)", false))
            {
                Log.Info("User confirmed front-panel format complete.");
                Catalog("FRAM Card", "CARD");
            }
            else
            {
                Log.Info("User cancelled front-panel format flow.");
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            }
        }

        private static void ClearMassMemory()
        {
            if (!EnsureConnected())
            {
                return;
            }

            AnsiConsole.MarkupLine("[red bold]WARNING:[/] this will delete [red]ALL[/] files in the Mass Memory Module.");
            if (!AnsiConsole.Confirm("Continue?", false))
            {
                Log.Info("ClearMassMemory: user cancelled.");
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return;
            }

            try
            {
                Log.Info("ClearMassMemory: sending DISPOSE ALL on MEM.");
                AnsiConsole.Status().Start(
                    "Disposing all entries...",
                    _ =>
                    {
                        // DISPOSE ALL on MEM works because module RAM is the documented
                        // target of the DISPOSE command (manual p.4-63).
                        _session.Clear();
                        _session.FormattedIO.WriteLine("MSDEV MEM;");
                        _session.FormattedIO.WriteLine("DISPOSE ALL;");
                    });

                Log.Info("ClearMassMemory: DISPOSE ALL sent.");
                AnsiConsole.MarkupLine("[green]Done.[/]");
                Catalog("Mass Memory Module", "MEM");
            }
            catch (Exception ex)
            {
                Log.Error($"ClearMassMemory failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Clear failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void CopyMassMemoryToCard()
        {
            if (!EnsureConnected())
            {
                return;
            }

            try
            {
                // Step 1: catalog MEM to discover what's there.
                string catRaw = null;
                AnsiConsole.Status().Start(
                    "Reading Mass Memory Module catalog...",
                    _ => catRaw = ReadCatalog("MEM"));

                var files = ExtractFilenamesWithSizes(catRaw);
                var totalBytes = files.Sum(f => f.Size);
                Log.Info($"Copy MEM -> CARD: MEM catalog returned {files.Length} file(s), {totalBytes} bytes total.");
                if (files.Length == 0)
                {
                    AnsiConsole.MarkupLine("[grey]Mass Memory Module is empty — nothing to copy.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"Found [yellow]{files.Length}[/] file(s) in Mass Memory Module ([yellow]{totalBytes:N0}[/] bytes total).");
                AnsiConsole.MarkupLine("[grey](Same-named files on the card will be overwritten.)[/]");
                if (!AnsiConsole.Confirm("Copy all to FRAM card?", false))
                {
                    Log.Info("Copy MEM -> CARD: user cancelled.");
                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                    return;
                }

                Log.Info($"Copy MEM -> CARD: starting copy of {files.Length} file(s), {totalBytes} bytes.");

                // Step 2: CARDSTORE each file. Per manual p.4-45, CARDSTORE always
                // copies module memory -> card and doesn't depend on MSDEV.
                // DONE? after each CARDSTORE returns "1" once the analyzer has
                // finished the write (manual p.459) — without this handshake the
                // next CARDSTORE arrives too soon and gets silently dropped.
                // ERR? per file catches any analyzer-side rejection that DONE
                // alone won't reveal. Every BytesFreeCheckEvery files we also
                // read BYTES FREE from the card to confirm writes are actually
                // committing (the analyzer can return ERR 0 long after it has
                // stopped landing files on the card).
                var perFile = new List<(string Name, long Size, string Err, long? BytesFree)>();
                long runningBytes = 0;
                long? prevBytesFree = null;
                AnsiConsole.Progress().Start(ctx =>
                {
                    var task = ctx.AddTask("[yellow]Copying[/] [grey](starting)[/]");
                    task.MaxValue = files.Length;

                    var fileIndex = 0;
                    foreach (var (name, size) in files)
                    {
                        fileIndex++;
                        runningBytes += size;
                        task.Description =
                            $"[yellow]Copying[/] {Markup.Escape(name)} " +
                            $"[grey]({runningBytes:N0}/{totalBytes:N0} bytes)[/]";

                        _session.FormattedIO.WriteLine($"CARDSTORE %{name}%;");
                        _session.FormattedIO.WriteLine("DONE?;");
                        _session.FormattedIO.ReadLine();

                        _session.FormattedIO.WriteLine("ERR?;");
                        var err = _session.FormattedIO.ReadLine().Trim();

                        long? bytesFreeNow = null;
                        if (fileIndex % BytesFreeCheckEvery == 0 || fileIndex == files.Length)
                        {
                            bytesFreeNow = QueryCardBytesFree();
                            if (bytesFreeNow.HasValue)
                            {
                                var deltaText = prevBytesFree.HasValue
                                    ? $", delta=-{prevBytesFree.Value - bytesFreeNow.Value:N0}"
                                    : string.Empty;
                                Log.Info($"  Checkpoint at file {fileIndex}: BYTES FREE = {bytesFreeNow.Value}{deltaText}");
                                prevBytesFree = bytesFreeNow;
                            }
                            else
                            {
                                Log.Warn($"  Checkpoint at file {fileIndex}: BYTES FREE not received from CATALOG?.");
                            }
                        }

                        perFile.Add((name, size, err, bytesFreeNow));
                        Log.Info($"CARDSTORE {name} (size={size}) -> ERR {err} [running={runningBytes}/{totalBytes}]");

                        task.Increment(1);
                    }
                });

                AnsiConsole.MarkupLine($"[green]Done.[/] Sent CARDSTORE for [yellow]{files.Length}[/] file(s), [yellow]{runningBytes:N0}[/] bytes.");

                // KNOWN BUG (85620A on 8563E, root cause not yet pinned down —
                // could be analyzer firmware, the GPIB driver, or this code):
                // CATALOG? on CARD truncates its response at ~903 bytes (one
                // analyzer output buffer) and asserts EOI mid-listing. On a
                // card with more than ~55 files the catalog reports only the
                // first ~55 — but CARDSTOREs for the un-listed files DID land
                // on the card and are readable. Confirmed empirically: a card
                // showing 55 entries via CATALOG? returned ERR 0 from CARDLOAD
                // for all 240 files actually present.
                //
                // WORKAROUND: don't use CATALOG? to verify. Probe each source
                // name individually with CARDLOAD %name%; DONE?; ERR?; — ERR 0
                // means present, ERR 855 means absent. No truncation limit
                // because each probe is its own request/response cycle.
                //
                // Side effect of the workaround: CARDLOAD reloads each file
                // into module RAM. That's benign here because every probed
                // name came from MEM in this same operation, so we're just
                // overwriting MEM entries with byte-identical content.
                // ProbeCard() is the standalone version of this loop.
                //
                // See also: catalog_card_truncation.md memory note. Tony to
                // verify in HTBasic whether the truncation is reproducible
                // outside this program (i.e. firmware vs. driver issue).
                var probeResults = new Dictionary<string, string>(StringComparer.Ordinal);
                AnsiConsole.Progress().Start(ctx =>
                {
                    var task = ctx.AddTask("[yellow]Verifying[/] [grey](starting)[/]");
                    task.MaxValue = perFile.Count;

                    foreach (var p in perFile)
                    {
                        task.Description = $"[yellow]Verifying[/] {Markup.Escape(p.Name)}";

                        _session.FormattedIO.WriteLine($"CARDLOAD %{p.Name}%;");
                        _session.FormattedIO.WriteLine("DONE?;");
                        _session.FormattedIO.ReadLine();
                        _session.FormattedIO.WriteLine("ERR?;");
                        var probeErr = _session.FormattedIO.ReadLine().Trim();

                        probeResults[p.Name] = probeErr;
                        Log.Info($"  Verify CARDLOAD {p.Name} -> ERR {probeErr}");
                        task.Increment(1);
                    }
                });

                var missing = perFile
                    .Where(p => probeResults[p.Name] != "0")
                    .Select(p => $"{p.Name} (probe ERR {probeResults[p.Name]})")
                    .ToArray();

                var errored = perFile
                    .Where(p => p.Err != "0")
                    .Select(p => $"{p.Name} (ERR {p.Err})")
                    .ToArray();

                // Per-file summary to the log: filename | size | CARDSTORE ERR | probe ERR | bytesFree (when checkpointed).
                Log.Info("=== Copy MEM -> CARD per-file summary ===");
                foreach (var p in perFile)
                {
                    var bf = p.BytesFree.HasValue ? $" | bytesFree={p.BytesFree.Value}" : string.Empty;
                    Log.Info($"  {p.Name} | size={p.Size} | ERR={p.Err} | probe={probeResults[p.Name]}{bf}");
                }
                Log.Info($"Totals: {perFile.Count} attempted, " +
                         $"{errored.Length} with ERR, " +
                         $"{missing.Length} missing from card, " +
                         $"{runningBytes:N0} bytes sent of {totalBytes:N0} total.");

                if (errored.Length > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]CARDSTORE errors ({errored.Length}/{perFile.Count}):[/] " +
                        Markup.Escape(string.Join(", ", errored)));
                }

                if (missing.Length == 0)
                {
                    AnsiConsole.MarkupLine($"[green]Verified[/] all [yellow]{perFile.Count}[/] file(s) on card.");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Missing from card ({missing.Length}/{perFile.Count}):[/] " +
                        Markup.Escape(string.Join(", ", missing)));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Copy MEM -> CARD failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Copy failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static long? QueryCardBytesFree()
        {
            // Re-catalog the card to read its BYTES FREE marker. CATALOG? on
            // the card forces the analyzer to actually finish any pending
            // card writes, so this doubles as a sync flush. Returns null if
            // the response can't be parsed (which usually means the card
            // wasn't responding for some reason).
            var raw = ReadCatalog("CARD");

            var line = (raw ?? string.Empty)
                .Replace("\r", string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase));
            if (line == null)
            {
                return null;
            }

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 && long.TryParse(parts[2], out var value)
                ? value
                : (long?)null;
        }

        private static (string Name, long Size)[] ExtractFilenamesWithSizes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new (string, long)[0];
            }

            // Same parsing shape as RenderCatalog: drop the BYTES FREE line, take
            // everything else as comma-separated entries. The last whitespace-
            // delimited token is the file size in bytes; everything before it is
            // the filename.
            var listingLines = raw.Replace("\r", string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0
                            && !l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase));

            return string.Join(",", listingLines)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Select(e =>
                {
                    var lastSpace = e.LastIndexOf(' ');
                    if (lastSpace <= 0)
                    {
                        return (Name: e, Size: 0L);
                    }
                    var name = e.Substring(0, lastSpace).Trim();
                    var sizeToken = e.Substring(lastSpace + 1).Trim();
                    return long.TryParse(sizeToken, out var size)
                        ? (Name: name, Size: size)
                        : (Name: name, Size: 0L);
                })
                .ToArray();
        }

        private static string[] ExtractCardFileNames(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new string[0];
            }

            // Card listings have multi-token entries — DLPs/limit lines are
            // "NAME.EXT size", but traces are "NAME.TRC :title:timestamp size".
            // Take the FIRST whitespace-delimited token to get just the filename
            // (ExtractFilenames takes the LAST, which is wrong for trace entries).
            var listingLines = raw.Replace("\r", string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0
                            && !l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase));

            return string.Join(",", listingLines)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Select(e =>
                {
                    var firstSpace = e.IndexOf(' ');
                    return firstSpace > 0 ? e.Substring(0, firstSpace).Trim() : e;
                })
                .ToArray();
        }

        private static string ToCardFileName(string moduleName)
        {
            // Card filenames are LIF: 1-9 ASCII chars (uppercase) of base + .EXT.
            // Per CARDSTORE on manual p.4-45, longer module names get truncated to
            // the first 9 base chars and lower-case is converted to upper-case.
            if (string.IsNullOrEmpty(moduleName))
            {
                return string.Empty;
            }

            var upper = moduleName.ToUpperInvariant();
            var dot = upper.LastIndexOf('.');
            if (dot <= 0)
            {
                return upper.Length <= 9 ? upper : upper.Substring(0, 9);
            }

            var baseName = upper.Substring(0, dot);
            var ext = upper.Substring(dot);
            if (baseName.Length > 9)
            {
                baseName = baseName.Substring(0, 9);
            }
            return baseName + ext;
        }

        private static string ReadCatalog(string device)
        {
            // Selected Device Clear flushes any stale data left in the analyzer's
            // output buffer from a prior failed command, so we don't read someone
            // else's leftovers as our catalog response.
            //
            // 85620A: select the storage device (MEM or CARD), then request the
            // directory listing as two separate writes — matches the manual's
            // BASIC example (p.4-48). The response shape is
            //   <entry>,<entry>,...<LF>BYTES FREE n<LF+EOI>
            // and may exceed any single VISA buffer, so ReadUntilEnd loops until
            // END plus a brief drain for paginated chunks.
            _session.Clear();
            _session.FormattedIO.WriteLine($"MSDEV {device};");
            _session.FormattedIO.WriteLine("CATALOG?;");
            return ReadUntilEnd();
        }

        private static string ReadUntilEnd()
        {
            const int chunkSize = 4096;

            // Defensive cap on the first read loop. A real catalog response is
            // well under 64 KB; if we've buffered 1 MB without seeing END, the
            // analyzer is wedged or we're misreading some unrelated transfer.
            // Bail loudly rather than spin until the user kills the process.
            const int maxResponseBytes = 1_048_576;

            var sb = new StringBuilder();

            // First read: wait up to the session's configured timeout for the
            // analyzer to send something terminated by END.
            ReadStatus status;
            int chunkNum = 0;
            do
            {
                chunkNum++;
                var chunk = _session.RawIO.ReadString(chunkSize, out status);
                sb.Append(chunk);
                if (sb.Length > maxResponseBytes)
                {
                    Log.Error($"ReadUntilEnd: aborting after {sb.Length} bytes without END.");
                    throw new InvalidOperationException(
                        $"Analyzer sent {sb.Length:N0} bytes without asserting END (cap {maxResponseBytes:N0}).");
                }
            } while (status != ReadStatus.EndReceived);

            // After the first END the analyzer may continue with more EOI-
            // terminated chunks: long listings get paginated internally, plus
            // the trailing BYTES FREE marker arrives as its own message. Drain
            // them with CatalogDrainTimeoutMs between reads — exit when no
            // more data arrives within that window.
            var savedTimeout = _session.TimeoutMilliseconds;
            _session.TimeoutMilliseconds = CatalogDrainTimeoutMs;
            try
            {
                while (true)
                {
                    try
                    {
                        chunkNum++;
                        var chunk = _session.RawIO.ReadString(chunkSize, out status);
                        sb.Append(chunk);
                    }
                    catch (IOTimeoutException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _session.TimeoutMilliseconds = savedTimeout;
            }

            Log.Info($"ReadUntilEnd: {sb.Length} bytes total.");

            return sb.ToString();
        }

        private static void RenderCatalog(string label, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(label)}:[/] [grey](empty)[/]");
                return;
            }

            // Manual p.4-47: response is "<listing><LF>BYTES FREE n<LF+EOI>".
            // Identify the BYTES FREE line by content rather than position so an empty
            // listing (no files on the card) doesn't get misparsed as a file entry.
            var lines = raw.Replace("\r", string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var bytesFree = lines.FirstOrDefault(l =>
                l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase));

            var listingLines = lines
                .Where(l => !l.StartsWith("BYTES FREE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var entries = string.Join(",", listingLines)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var fileCount = 0;
            long totalSize = 0;

            if (entries.Length == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(label)}:[/] [grey](no entries)[/]");
            }
            else
            {
                var table = new Table()
                    .Title($"[bold aqua]{Markup.Escape(label)}[/]")
                    .Border(TableBorder.Rounded)
                    .AddColumn("Name")
                    .AddColumn(new TableColumn("Size").RightAligned());

                // For MEM the format is "FILENAME.EXT size".
                // For CARD a trace is "FILENAME.TRC :title:timestamp size" — multiple spaces.
                // The size is always the last whitespace-delimited token in either case
                // (matches the BASIC parser on p.4-50, lines 480-540 which reverses the
                // string for CARD to take the trailing token).
                foreach (var rawEntry in entries)
                {
                    var entry = rawEntry.Trim();
                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    fileCount++;
                    var lastSpace = entry.LastIndexOf(' ');
                    if (lastSpace > 0 && lastSpace < entry.Length - 1)
                    {
                        var name = entry.Substring(0, lastSpace).Trim();
                        var sizeToken = entry.Substring(lastSpace + 1).Trim();
                        if (long.TryParse(sizeToken, out var size))
                        {
                            totalSize += size;
                        }
                        table.AddRow(Markup.Escape(name), Markup.Escape(sizeToken));
                    }
                    else
                    {
                        table.AddRow(Markup.Escape(entry), string.Empty);
                    }
                }

                AnsiConsole.Write(table);
            }

            // Summary: number of files and total bytes used. Quick at-a-glance
            // sanity check for the user (and goes into the log).
            AnsiConsole.MarkupLine(
                $"[yellow]{Markup.Escape(label)} summary:[/] " +
                $"[aqua]{fileCount}[/] file(s), [aqua]{totalSize:N0}[/] bytes used.");
            Log.Info($"{label} summary: {fileCount} file(s), {totalSize} bytes used.");

            // Always print the BYTES FREE marker (or its absence). Its presence confirms
            // the analyzer's full CATALOG? response was received — if it's missing, the
            // listing above may be truncated.
            if (!string.IsNullOrWhiteSpace(bytesFree))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(bytesFree)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Warning:[/] [yellow]'BYTES FREE' marker not received — listing may be truncated.[/]");
            }
        }

        private static void ConnectTo8563E()
        {
            try
            {
                Log.Info($"ConnectTo8563E: opening {Resource8563E}.");
                if (_rm == null)
                {
                    _rm = new ResourceManager();
                }

                _session?.Dispose();

                AnsiConsole.MarkupLine($"[grey]Opening[/] [cyan]{Resource8563E}[/] [grey]...[/]");
                _session = (MessageBasedSession)_rm.Open(Resource8563E);
                _session.TimeoutMilliseconds = 5000;
                _session.TerminationCharacterEnabled = true;

                _session.FormattedIO.WriteLine("ID?");
                var id = _session.FormattedIO.ReadLine().Trim();

                Log.Info($"ConnectTo8563E: connected, ID = {id}");
                AnsiConsole.MarkupLine($"[green]Connected:[/] {Markup.Escape(id)}");
            }
            catch (Exception ex)
            {
                Log.Error($"ConnectTo8563E failed: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Connection failed:[/] {Markup.Escape(ex.Message)}");
                _session?.Dispose();
                _session = null;
            }
        }
    }
}
