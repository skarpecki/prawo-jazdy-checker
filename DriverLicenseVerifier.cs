using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Threading;
using System.Threading.Tasks;
using UpkiClient.Generated;

namespace UpkiClient;

internal sealed class DriverLicenseVerifier : IAsyncDisposable
{
    private const double InitialBackoffDelayMinutes = 0.5;
    private const double MaxBackoffDelayMinutes = 60;

    private readonly object _backoffLock = new();
    private double _backoffDelayMinutes = InitialBackoffDelayMinutes;

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
        var backoffAttempts = 0;
        var nextDelayMinutes = ReadBackoffDelayMinutes();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                var response = await _client.pytanieOUprawnieniaAsync(request).ConfigureAwait(false);
                return ParseResponse(request, response?.DaneDokumentuResponse);
            }
            catch (Exception ex) when (IsTooManyRequests(ex))
            {
                backoffAttempts++;

                if (nextDelayMinutes > MaxBackoffDelayMinutes)
                {
                    throw new TooManyRequestsBackoffException(
                        backoffAttempts,
                        TimeSpan.FromMinutes(nextDelayMinutes),
                        ex);
                }

                // Save current delay to start with it in future call
                StoreBackoffDelayMinutes(nextDelayMinutes);
                var delay = TimeSpan.FromMinutes(nextDelayMinutes);

                Console.WriteLine($"  Serwer zwrócił 429 (Too Many Requests). Ponowna próba za {delay.TotalMinutes} min.");

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                nextDelayMinutes = checked(nextDelayMinutes * 2);
            }
        }
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

    private double ReadBackoffDelayMinutes()
    {
        lock (_backoffLock)
        {
            return _backoffDelayMinutes;
        }
    }

    private void StoreBackoffDelayMinutes(double delayMinutes)
    {
        lock (_backoffLock)
        {
            _backoffDelayMinutes = delayMinutes;
        }
    }

    private static bool IsTooManyRequests(Exception exception)
    {
        foreach (var candidate in EnumerateExceptionChain(exception))
        {
            switch (candidate)
            {
                case HttpRequestException httpRequestException
                    when httpRequestException.StatusCode == HttpStatusCode.TooManyRequests:
                    return true;

                case WebException webException
                    when IsTooManyRequests(webException):
                    return true;

                case ProtocolException protocolException
                    when ProtocolExceptionIndicatesTooManyRequests(protocolException):
                    return true;
            }
        }

        return false;
    }

    private static bool IsTooManyRequests(WebException webException)
    {
        if (webException.Response is HttpWebResponse httpResponse)
        {
            return httpResponse.StatusCode == HttpStatusCode.TooManyRequests;
        }

        return webException.Status == WebExceptionStatus.ProtocolError &&
               webException.Message.Contains("429", StringComparison.Ordinal);
    }

    private static bool ProtocolExceptionIndicatesTooManyRequests(ProtocolException protocolException)
    {
        if (protocolException.InnerException is WebException webException &&
            IsTooManyRequests(webException))
        {
            return true;
        }

        var message = protocolException.Message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            if (message.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is AggregateException aggregate && aggregate.InnerExceptions is { Count: > 0 })
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (inner != null)
                    {
                        stack.Push(inner);
                    }
                }
            }

            if (current.InnerException != null)
            {
                stack.Push(current.InnerException);
            }
        }
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

internal sealed class TooManyRequestsBackoffException : Exception
{
    public TooManyRequestsBackoffException(int attempts, TimeSpan nextDelay, Exception innerException)
        : base(
            $"Server returned HTTP 429 {attempts} times. Next retry delay of {nextDelay} exceeds backoff limit.",
            innerException)
    {
        Attempts = attempts;
        NextDelay = nextDelay;
    }

    public int Attempts { get; }

    public TimeSpan NextDelay { get; }
}
