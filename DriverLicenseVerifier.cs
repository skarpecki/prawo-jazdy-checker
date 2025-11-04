using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Threading;
using System.Threading.Tasks;
using UpkiClient.Generated;

namespace UpkiClient;

internal sealed class DriverLicenseVerifier : IAsyncDisposable
{
    private readonly UprawnieniaKierowcowPrzewoznicyApiClient _client;

    public DriverLicenseVerifier(string endpointUrl, string certificatePath, string certificatePassword)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new ArgumentException("Endpoint URL is required.", nameof(endpointUrl));
        }

        if (!System.IO.File.Exists(certificatePath))
        {
            throw new ArgumentException($"Nie znaleziono certyfikatu: {certificatePath}", nameof(certificatePath));
        }

        var binding = new BasicHttpsBinding
        {
            Security = new BasicHttpsSecurity
            {
                Mode = BasicHttpsSecurityMode.Transport,
                Transport = { ClientCredentialType = HttpClientCredentialType.Certificate }
            }
        };

        var address = new EndpointAddress(endpointUrl);
        _client = new UprawnieniaKierowcowPrzewoznicyApiClient(binding, address);

        _client.ClientCredentials.ServiceCertificate.SslCertificateAuthentication =
            new X509ServiceCertificateAuthentication
            {
                CertificateValidationMode = X509CertificateValidationMode.None
            };

        var certificate = string.IsNullOrWhiteSpace(certificatePassword)
            ? new X509Certificate2(certificatePath)
            : new X509Certificate2(certificatePath, certificatePassword);

        _client.ClientCredentials.ClientCertificate.Certificate = certificate;
    }

    public async Task<IReadOnlyList<DriverCategoryInfo>> VerifyAsync(
        DaneDokumentuRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _client.pytanieOUprawnieniaAsync(request).ConfigureAwait(false);
        return ParseResponse(request, response?.DaneDokumentuResponse);
    }

    private static IReadOnlyList<DriverCategoryInfo> ParseResponse(
        DaneDokumentuRequest request,
        DaneDokumentuResponse? response)
    {
        if (response?.dokumentPotwierdzajacyUprawnienia?.daneUprawnieniaKategorii is not { Length: > 0 } categories)
        {
            return Array.Empty<DriverCategoryInfo>();
        }

        var document = response.dokumentPotwierdzajacyUprawnienia;
        var status = document.stanDokumentu?.stanDokumentu;
        var revocationReasons = document.stanDokumentu?.powodZmianyStanu ?? Array.Empty<KodWartoscSlownikowa>();
        var reasonText = BuildKeyValueMessagesFromList(revocationReasons);
        var statusText = BuildKeyValueMessage(status);
        var licenseNumber = string.IsNullOrWhiteSpace(document.seriaNumerBlankietuDruku)
            ? request.seriaNumerBlankietuDruku
            : document.seriaNumerBlankietuDruku;

        return categories
            .Where(category => category != null)
            .Select(category => new DriverCategoryInfo(
                request.imiePierwsze ?? string.Empty,
                request.nazwisko ?? string.Empty,
                licenseNumber ?? string.Empty,
                statusText ?? string.Empty,
                category!.kategoria ?? string.Empty,
                category.dataWaznosciSpecified ? category.dataWaznosci : null,
                reasonText))
            .ToArray();
    }

    private static string? BuildKeyValueMessagesFromList(IEnumerable<KodWartoscSlownikowa> keyValue)
    {
        var keyValueFragments = keyValue
            .Where(item => item != null && (!string.IsNullOrWhiteSpace(item.kod) || !string.IsNullOrWhiteSpace(item.wartosc)))
            .Select(BuildKeyValueMessage)
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
            .ToArray();

        return keyValueFragments.Length == 0 ? null : string.Join("; ", keyValueFragments);
    }

    private static string? BuildKeyValueMessage(KodWartoscSlownikowa? keyValue)
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

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_client.State != CommunicationState.Closed)
            {
                _client.Close();
            }
        }
        catch
        {
            _client.Abort();
        }

        return ValueTask.CompletedTask;
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
