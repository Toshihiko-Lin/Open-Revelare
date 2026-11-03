using System.Text.Json;
using OpenRevelare.Core;

internal static class InputAuditCommand
{
    public static int Run(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string? input = null, output = null;
            int repeats = 3, workers = 1; bool compare = false;
            long fullPixels = 32_000_000;
            TiffInputAssumption assumption = TiffInputAssumption.Unspecified;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "-i": case "--input": input = Value(); break;
                    case "-o": case "--output": output = Value(); break;
                    case "--repeat": repeats = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--workers": workers = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--compare-full": compare = true; break;
                    case "--max-full-mp": fullPixels = checked(long.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture) * 1_000_000); break;
                    case "--tiff-assumption": assumption = Value().ToLowerInvariant() switch
                    {
                        "auto" => TiffInputAssumption.Unspecified, "linear" => TiffInputAssumption.Linear,
                        "srgb" => TiffInputAssumption.Srgb, _ => throw new ArgumentException("Use auto, linear or srgb."),
                    }; break;
                    default: throw new ArgumentException("Unknown audit option: " + args[i]);
                }
            }
            if (input is null || output is null)
                throw new ArgumentException("audit-input --input FILE_OR_FOLDER --output REPORT.json [--repeat 3] [--workers 1] [--compare-full] [--max-full-mp 32] [--tiff-assumption auto|linear|srgb]");
            string[] paths = Directory.Exists(input)
                ? Directory.GetFiles(input).Where(path => RawDecode.HasRawExtension(path)
                    || Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase)).Order().ToArray()
                : new[] { input };
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (paths.Any(path => comparer.Equals(Path.GetFullPath(path), Path.GetFullPath(output))))
                throw new ArgumentException("The report must not overwrite a source file.");
            RawDecode.ClearSourceCache();
            var report = InputAudit.RunAsync(paths, new(Repeats: repeats, Workers: workers, CompareFull: compare,
                MaxFullPixels: fullPixels, TiffAssumption: assumption), cancellation.Token).GetAwaiter().GetResult();
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            ExportFile.Write(output, path => File.WriteAllText(path, json));
            Console.WriteLine($"Audited {report.Files.Length} files; {report.Files.Count(f => f.Error is not null)} failed. Report: {Path.GetFullPath(output)}");
            return report.Files.Any(f => f.Error is not null) ? 1 : 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Audit cancelled."); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
