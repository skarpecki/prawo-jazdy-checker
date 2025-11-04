using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpkiClient;
using UpkiClient.Generated; // from dotnet-svcutil output

// 1) Load config
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var app = config.GetSection("App").Get<AppSettings>() ?? new AppSettings();

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var binding = new BasicHttpsBinding()
{
    Security = new BasicHttpsSecurity()
    {
        Mode = BasicHttpsSecurityMode.Transport,
        Transport = { ClientCredentialType = HttpClientCredentialType.Certificate }
    }
};
var address = new EndpointAddress(app.EndpointUrl);
var client = new UprawnieniaKierowcowPrzewoznicyApiClient(binding, address);

client.ClientCredentials.ServiceCertificate.SslCertificateAuthentication =
    new X509ServiceCertificateAuthentication
    {
        CertificateValidationMode = X509CertificateValidationMode.None
    };

var cert = new X509Certificate2(app.ClientCertPath);

client.ClientCredentials.ClientCertificate.Certificate = cert;

var request = new DaneDokumentuRequest
{
    imiePierwsze = "YUNIOR",
    nazwisko = "KACPRUK",
    seriaNumerBlankietuDruku = "FT100028"
};

try
{
    var response = client.pytanieOUprawnieniaAsync(request).GetAwaiter().GetResult();

    var flattened = ParseResponse(request, response?.DaneDokumentuResponse);

    if (flattened.Count == 0)
    {
        Console.WriteLine("Brak danych kategorii w odpowiedzi.");
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(flattened, jsonOptions));
    }

    return 0;
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

    return -1;
}
catch (Exception ex)
{
    LogFailure(request, ex.GetType().Name, ex.ToString(), jsonOptions);

    Console.Error.WriteLine("Call failed:");
    Console.Error.WriteLine(ex);

    return -3;
}
finally
{
    try { if (client.State != CommunicationState.Closed) client.Close(); }
    catch { client.Abort(); }
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
            category.dataWaznosciSpecified ? category.dataWaznosci : null,
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

static string? BuildKeyValueMessage(KodWartoscSlownikowa keyValue)
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

internal sealed record DriverCategoryInfo(
    string FirstName,
    string LastName,
    string LicenseNumber,
    string LicenseStatus,
    string Category,
    DateTime? CategoryExpiryDate,
    string? RevocationReason);

internal sealed record DriverErrorLog(
    string FirstName,
    string LastName,
    string LicenseNumber,
    string Error,
    string? Details);
