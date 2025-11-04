using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpkiClient;
using UpkiClient.Generated; 


return await RunAsync();

static async Task<int> RunAsync()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    var app = config.GetSection("App").Get<AppSettings>() ?? new AppSettings();

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    var minDelay = Math.Clamp(app.DelayLowerBound_ms, 5000, 10000);
    var maxDelay = Math.Clamp(app.DelayUpperBound_ms, 5000, 10000);
    if (minDelay > maxDelay)
    {
        (minDelay, maxDelay) = (maxDelay, minDelay);
    }
    var maxDelayExclusive = maxDelay + 1;

    var binding = new BasicHttpsBinding()
    {
        Security = new BasicHttpsSecurity()
        {
            Mode = BasicHttpsSecurityMode.Transport,
            Transport = { ClientCredentialType = HttpClientCredentialType.Certificate }
        }
    };

    var address = new EndpointAddress(app.EndpointUrl);
    UprawnieniaKierowcowPrzewoznicyApiClient? client = null;

    try
    {
        client = new UprawnieniaKierowcowPrzewoznicyApiClient(binding, address);

        client.ClientCredentials.ServiceCertificate.SslCertificateAuthentication =
            new X509ServiceCertificateAuthentication
            {
                CertificateValidationMode = X509CertificateValidationMode.None
            };

        var cert = string.IsNullOrWhiteSpace(app.ClientCertPassword)
            ? new X509Certificate2(app.ClientCertPath)
            : new X509Certificate2(app.ClientCertPath, app.ClientCertPassword);

        client.ClientCredentials.ClientCertificate.Certificate = cert;

        var inputPath = ResolveInputCsvPath(app);
        var requests = LoadRequestsFromCsv(inputPath);
        var random = new Random();
        var csvOutputPath = Path.Combine(Environment.CurrentDirectory, "output.csv");
        var exitCode = 0;

        if (requests.Count == 0)
        {
            Console.WriteLine($"Brak rekordów w pliku wejściowym {inputPath}. Zapisano pusty plik {csvOutputPath}.");
            try
            {
                using var csvStream = new StreamWriter(csvOutputPath, false, new UTF8Encoding(false));
                using var csv = new CsvWriter(csvStream, CultureInfo.InvariantCulture);
                csv.WriteHeader<DriverCategoryInfo>();
                csv.NextRecord();
            }
            catch (Exception csvEx)
            {
                Console.Error.WriteLine($"Błąd zapisu do pliku CSV '{csvOutputPath}': {csvEx.Message}");
                return -2;
            }

            return exitCode;
        }

        using var outputStream = new StreamWriter(csvOutputPath, false, new UTF8Encoding(false));
        using var outputCsv = new CsvWriter(outputStream, CultureInfo.InvariantCulture);
        outputCsv.WriteHeader<DriverCategoryInfo>();
        outputCsv.NextRecord();
        outputStream.Flush();

        Console.WriteLine($"Rozpoczynam przetwarzanie {requests.Count} zapytań z pliku {inputPath}.");

        var totalWritten = 0;

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];

            try
            {
                var response = await client.pytanieOUprawnieniaAsync(request).ConfigureAwait(false);
                var flattened = ParseResponse(request, response?.DaneDokumentuResponse);

                if (flattened.Count == 0)
                {
                    Console.WriteLine($"Brak danych kategorii w odpowiedzi dla {request.imiePierwsze} {request.nazwisko} ({request.seriaNumerBlankietuDruku}).");
                }
                else
                {
                    foreach (var record in flattened)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(record, jsonOptions));
                        try
                        {
                            outputCsv.WriteRecord(record);
                            outputCsv.NextRecord();
                            outputStream.Flush();
                            totalWritten++;
                        }
                        catch (Exception csvEx)
                        {
                            LogFailure(request, $"CSV write failed: {csvEx.GetType().Name}", csvEx.ToString(), jsonOptions);
                            Console.Error.WriteLine($"Błąd zapisu do pliku CSV '{csvOutputPath}': {csvEx.Message}");
                            return -2;
                        }
                    }
                }
            }
            catch (FaultException<CepikException> cepikFault)
            {
                LogFailure(request, $"SOAP Fault: {cepikFault.Code.Name}", BuildFaultDetails(cepikFault), jsonOptions);

                Console.Error.WriteLine($"Fault Code: {cepikFault.Code.Name}");
                Console.Error.WriteLine($"Fault Reason: {cepikFault.Reason.GetMatchingTranslation().Text}");

                if (cepikFault.Detail?.komunikaty != null)
                {
                    Console.Error.WriteLine("\nSzczegóły błędu:");
                    foreach (var kom in cepikFault.Detail.komunikaty)
                    {
                        Console.Error.WriteLine($"  Typ: {kom.typ}");
                        Console.Error.WriteLine($"  Kod: {kom.kod}");
                        Console.Error.WriteLine($"  Komunikat: {kom.komunikat}");

                        if (!string.IsNullOrEmpty(kom.szczegoly))
                            Console.Error.WriteLine($"  Szczegóły: {kom.szczegoly}");

                        if (!string.IsNullOrEmpty(kom.identyfikatorBledu))
                            Console.Error.WriteLine($"  ID błędu: {kom.identyfikatorBledu}");

                        Console.Error.WriteLine();
                    }
                }

                if (exitCode == 0)
                {
                    exitCode = -1;
                }
            }
            catch (Exception ex)
            {
                LogFailure(request, ex.GetType().Name, ex.ToString(), jsonOptions);

                Console.Error.WriteLine("Call failed:");
                Console.Error.WriteLine(ex);

                exitCode = -3;
            }

            if (i < requests.Count - 1)
            {
                var delayMs = random.Next(minDelay, maxDelayExclusive);
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }

        Console.WriteLine($"Zapisano {totalWritten} rekordów do {csvOutputPath}.");

        return exitCode;
    }
    finally
    {
        if (client != null)
        {
            try { if (client.State != CommunicationState.Closed) client.Close(); }
            catch { client.Abort(); }
        }
    }
}

static string ResolveInputCsvPath(AppSettings settings)
{
    var path = settings.InputCsvPath;

    if (string.IsNullOrWhiteSpace(path))
    {
        path = Path.Combine(Environment.CurrentDirectory, "dokumentacja", "input.csv");
    }
    else if (!Path.IsPathRooted(path))
    {
        path = Path.GetFullPath(path);
    }

    return path;
}

static List<DaneDokumentuRequest> LoadRequestsFromCsv(string inputPath)
{
    var requests = new List<DaneDokumentuRequest>();

    if (string.IsNullOrWhiteSpace(inputPath))
    {
        return requests;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Nie znaleziono pliku wejściowego: {inputPath}");
        return requests;
    }

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

static List<DriverCategoryInfo> ParseResponse(DaneDokumentuRequest request, DaneDokumentuResponse? response)
{
    var result = new List<DriverCategoryInfo>();

    if (response?.dokumentPotwierdzajacyUprawnienia?.daneUprawnieniaKategorii == null)
    {
        return result;
    }

    var document = response.dokumentPotwierdzajacyUprawnienia;
    var status = document.stanDokumentu?.stanDokumentu;
    var revocationReasons = document.stanDokumentu?.powodZmianyStanu ?? Array.Empty<KodWartoscSlownikowa>();
    var reasonText = BuildKeyValueMessagesFromList(revocationReasons);
    var statusText = BuildKeyValueMessage(status);
    var licenseNumber = string.IsNullOrWhiteSpace(document.seriaNumerBlankietuDruku)
        ? request.seriaNumerBlankietuDruku
        : document.seriaNumerBlankietuDruku;

    foreach (var category in document.daneUprawnieniaKategorii)
    {
        if (category == null)
        {
            continue;
        }

        result.Add(new DriverCategoryInfo(
            request.imiePierwsze ?? string.Empty,
            request.nazwisko ?? string.Empty,
            licenseNumber ?? string.Empty,
            statusText ?? string.Empty,
            category.kategoria ?? string.Empty,
            category.dataWaznosciSpecified ? category.dataWaznosci : new DateTime(2099, 12, 31),
            reasonText));
    }

    return result;
}


static string? BuildKeyValueMessagesFromList(IEnumerable<KodWartoscSlownikowa> keyValue)
{
    var keyValueFragments = keyValue
        .Where(keyValue => keyValue != null && (!string.IsNullOrWhiteSpace(keyValue.kod) || !string.IsNullOrWhiteSpace(keyValue.wartosc)))
        .Select(keyValue => BuildKeyValueMessage(keyValue))
        .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
        .ToArray();

    return keyValueFragments.Length == 0 ? null : string.Join("; ", keyValueFragments);
}

static string? BuildKeyValueMessage(KodWartoscSlownikowa? keyValue)
{
    if (keyValue is null)
    {
        return string.Empty;
    }

    var parts = new List<string>();
    if (!string.IsNullOrWhiteSpace(keyValue.kod))
    {
        parts.Add(keyValue.kod.Trim());
    }
    if (!string.IsNullOrWhiteSpace(keyValue.wartosc))
    {
        parts.Add(keyValue.wartosc.Trim());
    }
    return string.Join(" - ", parts);
}

static string? BuildFaultDetails(FaultException<CepikException> fault)
{
    if (fault.Detail?.komunikaty == null || fault.Detail.komunikaty.Length == 0)
    {
        return null;
    }

    var builder = new StringBuilder();
    foreach (var message in fault.Detail.komunikaty)
    {
        if (message == null)
        {
            continue;
        }

        builder.Append('[')
               .Append(message.typ.ToString())
               .Append("] ");

        if (!string.IsNullOrWhiteSpace(message.kod))
        {
            builder.Append(message.kod.Trim()).Append(' ');
        }

        if (!string.IsNullOrWhiteSpace(message.komunikat))
        {
            builder.Append(message.komunikat.Trim());
        }

        if (!string.IsNullOrWhiteSpace(message.szczegoly))
        {
            builder.Append(" (").Append(message.szczegoly.Trim()).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(message.identyfikatorBledu))
        {
            builder.Append(" id:").Append(message.identyfikatorBledu.Trim());
        }

        builder.AppendLine();
    }

    return builder.ToString().TrimEnd();
}

static void LogFailure(DaneDokumentuRequest request, string errorLabel, string? errorDetails, JsonSerializerOptions options)
{
    var payload = new DriverErrorLog(
        request.imiePierwsze ?? string.Empty,
        request.nazwisko ?? string.Empty,
        request.seriaNumerBlankietuDruku ?? string.Empty,
        errorLabel,
        errorDetails);

    Console.Error.WriteLine(JsonSerializer.Serialize(payload, options));
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

internal sealed record DriverCategoryInfo(
    string Imie,
    string Nazwisko,
    string NumerBlankietu,
    string Status,
    string Kategoria,
    DateTime? DataWaznosci,
    string? PowodZmiany);

internal sealed record DriverErrorLog(
    string Imie,
    string Nazwisko,
    string NumerBlankietu,
    string Blad,
    string? Szczegoly);
