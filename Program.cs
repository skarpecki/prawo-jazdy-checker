using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UpkiClient;
using UpkiClient.Generated;
using System.ServiceModel;

Console.OutputEncoding = Encoding.UTF8;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

return await RunAsync();

static async Task<int> RunAsync()
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    var appSettings = config.GetSection("App").Get<AppSettings>() ?? new AppSettings();
    var workingDirectory = ResolveWorkingDirectory(appSettings.WorkingDirectory);
    var endpointUrl = string.IsNullOrWhiteSpace(appSettings.EndpointUrl)
        ? PromptForEndpointUrl()
        : appSettings.EndpointUrl.Trim();
    var minDelay = Math.Clamp(appSettings.DelayLowerBoundMs, 500, 1000);
    var maxDelay = Math.Clamp(appSettings.DelayUpperBoundMs, 500, 1000);
    if (minDelay > maxDelay)
    {
        (minDelay, maxDelay) = (maxDelay, minDelay);
    }
    var maxDelayExclusive = maxDelay + 1;

    Console.WriteLine("=== Witaj w narzędziu weryfikacji praw jazdy ===");
    Console.WriteLine($"Roboczy katalog: {workingDirectory}");
    Console.WriteLine($"Endpoint SOAP: {endpointUrl}");
    Console.WriteLine($"Limit opóźnień: {minDelay} ms - {maxDelay} ms");
    Console.WriteLine();

    var csvPath = PromptForFileSelection(workingDirectory, "*.csv", "Wybierz plik CSV z zapytaniami");
    if (csvPath is null)
    {
        Console.Error.WriteLine("Nie znaleziono żadnych plików CSV. Kończę działanie.");
        return -4;
    }

    var certPath = PromptForFileSelection(workingDirectory, "*.p12", "Wybierz certyfikat klienta (.p12)");
    if (certPath is null)
    {
        Console.Error.WriteLine("Nie znaleziono żadnych certyfikatów .p12. Kończę działanie.");
        return -4;
    }

    Console.Write("Certyfikat może być zabezpieczony hasłem. Jeśli go nie ma, pozostaw puste.\nPodaj hasło do certyfikatu: ");
    var certPassword = Console.ReadLine() ?? string.Empty;

    var outputPath = PromptForOutputPath(workingDirectory);
    outputPath = EnsureUniqueOutputPath(outputPath);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    var requests = LoadRequestsFromCsv(csvPath);
    if (requests.Count == 0)
    {
        Console.WriteLine("Plik CSV nie zawiera poprawnych wierszy do przetworzenia. Utworzono pusty plik wynikowy.");
        EnsureOutputDirectory(outputPath);
        using var emptyWriter = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        using var emptyCsv = new CsvWriter(emptyWriter, CultureInfo.InvariantCulture);
        emptyCsv.WriteHeader<DriverCategoryInfo>();
        emptyCsv.NextRecord();
        return 0;
    }

    Console.WriteLine($"\nPrzygotowano {requests.Count} zapytań. Status: weryfikacja w toku...\n");

    var random = new Random();

    EnsureOutputDirectory(outputPath);
    await using var verifier = new DriverLicenseVerifier(endpointUrl, certPath, certPassword);
    await using var outputStream = new StreamWriter(outputPath, false, new UTF8Encoding(false));
    await using var outputCsv = new CsvWriter(outputStream, CultureInfo.InvariantCulture);
    outputCsv.WriteHeader<DriverCategoryInfo>();
    outputCsv.NextRecord();
    await outputStream.FlushAsync();

    var exitCode = 0;
    var totalWritten = 0;

    for (var index = 0; index < requests.Count; index++)
    {
        var request = requests[index];
        Console.WriteLine($"[{index + 1}/{requests.Count}] Weryfikuję {request.imiePierwsze} {request.nazwisko} ({request.seriaNumerBlankietuDruku})...");

        try
        {
            var records = await verifier.VerifyAsync(request, CancellationToken.None).ConfigureAwait(false);

            if (records.Count == 0)
            {
                Console.WriteLine("  Brak danych kategorii w odpowiedzi.");
            }
            else
            {
                foreach (var record in records)
                {
                    Console.WriteLine(JsonSerializer.Serialize(record, jsonOptions));
                    outputCsv.WriteRecord(record);
                    outputCsv.NextRecord();
                    totalWritten++;
                }

                await outputStream.FlushAsync();
            }

            Console.WriteLine("  Zakończono.");
        }
        catch (FaultException<CepikException> cepikFault)
        {
            var faultExitCode = SoapFaultReporter.HandleFault(request, cepikFault, jsonOptions);
            if (exitCode == 0)
            {
                exitCode = faultExitCode;
            }
        }
        catch (Exception ex)
        {
            FailureLogger.LogRequestFailure(request, ex.GetType().Name, ex.ToString(), jsonOptions);

            Console.Error.WriteLine("  Call failed:");
            Console.Error.WriteLine(ex);

            exitCode = -3;
        }

        if (index < requests.Count - 1)
        {
            var delayMs = random.Next(minDelay, maxDelayExclusive);
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    Console.WriteLine($"\nStatus: weryfikacja zakończona. Zapisano {totalWritten} rekordów do {outputPath}.");

    return exitCode;
}

static string ResolveWorkingDirectory(string? configuredPath)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return Environment.CurrentDirectory;
    }

    if (!Path.IsPathRooted(configuredPath))
    {
        configuredPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configuredPath));
    }

    return Directory.Exists(configuredPath)
        ? configuredPath
        : throw new DirectoryNotFoundException($"Nie znaleziono katalogu roboczego: {configuredPath}");
}

static string? PromptForFileSelection(string workingDirectory, string searchPattern, string prompt)
{
    var files = Directory.EnumerateFiles(workingDirectory, searchPattern, SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
    {
        return null;
    }

    Console.WriteLine(prompt + ":");
    for (var i = 0; i < files.Count; i++)
    {
        var relative = Path.GetRelativePath(workingDirectory, files[i]);
        Console.WriteLine($"  {i + 1}. {relative}");
    }

    while (true)
    {
        Console.Write($"Wybierz numer [1-{files.Count}]: ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out var index) && index >= 1 && index <= files.Count)
        {
            Console.WriteLine();
            return files[index - 1];
        }

        Console.WriteLine("Niepoprawny wybór. Spróbuj ponownie.");
    }
}

static string PromptForEndpointUrl()
{
    Console.Write("Podaj adres endpointu SOAP: ");
    return ReadRequiredLine();
}

static string PromptForOutputPath(string workingDirectory)
{
    while (true)
    {
        Console.Write("Podaj nazwę pliku wynikowego (np. output.csv): ");
        var input = Console.ReadLine();

        var candidate = string.IsNullOrWhiteSpace(input) ? "output.csv" : input.Trim();
        candidate = candidate.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? candidate : candidate + ".csv";

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Console.WriteLine("Podana nazwa zawiera nieprawidłowe znaki. Spróbuj ponownie.");
            continue;
        }

        var path = Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(workingDirectory, candidate);

        Console.WriteLine();
        return path;
    }
}

static string ReadRequiredLine()
{
    while (true)
    {
        var line = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(line))
        {
            return line.Trim();
        }

        Console.Write("Wartość wymagana. Podaj ponownie: ");
    }
}

static void EnsureOutputDirectory(string outputPath)
{
    var directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

static string EnsureUniqueOutputPath(string initialPath)
{
    if (!File.Exists(initialPath))
    {
        return initialPath;
    }

    var directory = Path.GetDirectoryName(initialPath) ?? string.Empty;
    var fileName = Path.GetFileNameWithoutExtension(initialPath);
    var extension = Path.GetExtension(initialPath);
    var counter = 1;

    while (true)
    {
        var candidate = Path.Combine(directory, $"{fileName}_{counter}{extension}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        counter++;
    }
}

static List<DaneDokumentuRequest> LoadRequestsFromCsv(string inputPath)
{
    var requests = new List<DaneDokumentuRequest>();

    var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        Delimiter = "\t",
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = ctx => Console.Error.WriteLine($"Nieprawidłowy rekord w wierszu {ctx.Context.Parser.Row}: {ctx.RawRecord}"),
        PrepareHeaderForMatch = args => args.Header?.Trim()
    };

    using var reader = new StreamReader(inputPath, GetInputCsvEncoding(), detectEncodingFromByteOrderMarks: true);
    using var csv = new CsvReader(reader, csvConfig);
    csv.Context.RegisterClassMap<DriverInputRowMap>();

    while (csv.Read())
    {
        try
        {
            var row = csv.GetRecord<DriverInputRow>();
            if (row == null)
            {
                continue;
            }

            var firstName = row.FirstName?.Trim();
            var lastName = row.LastName?.Trim();
            var documentNumber = row.DocumentNumber?.Trim();

            if (string.IsNullOrWhiteSpace(firstName) ||
                string.IsNullOrWhiteSpace(lastName) ||
                string.IsNullOrWhiteSpace(documentNumber))
            {
                Console.Error.WriteLine($"Pomijam niekompletny wiersz (linia {csv.Context.Parser.Row}).");
                continue;
            }

            requests.Add(new DaneDokumentuRequest
            {
                imiePierwsze = firstName,
                nazwisko = lastName,
                seriaNumerBlankietuDruku = documentNumber
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Błąd parsowania wiersza CSV (linia {csv.Context.Parser.Row}): {ex.Message}");
        }
    }

    return requests;
}

static Encoding GetInputCsvEncoding()
{
    try
    {
        return Encoding.GetEncoding("ISO-8859-2");
    }
    catch (Exception)
    {
        return Encoding.Latin1;
    }
}

sealed class DriverInputRow
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DocumentNumber { get; set; }
}

sealed class DriverInputRowMap : ClassMap<DriverInputRow>
{
    public DriverInputRowMap()
    {
        Map(m => m.FirstName).Name("Imię", "Imie", "Imi?", "IMIE");
        Map(m => m.LastName).Name("Nazwisko", "NAZWISKO");
        Map(m => m.DocumentNumber).Name("NumerDokumentu", "Numer Dokumentu", "NumerBlankietu", "Numer", "NUMERDOKUMENTU");
    }
}
