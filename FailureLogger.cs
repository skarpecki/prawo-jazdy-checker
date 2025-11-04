using System;
using System.Text.Json;
using UpkiClient.Generated;

namespace UpkiClient;

internal static class FailureLogger
{
    internal static void LogRequestFailure(
        DaneDokumentuRequest request,
        string errorLabel,
        string? errorDetails,
        JsonSerializerOptions options)
    {
        var payload = new DriverErrorLog(
            request.imiePierwsze ?? string.Empty,
            request.nazwisko ?? string.Empty,
            request.seriaNumerBlankietuDruku ?? string.Empty,
            errorLabel,
            errorDetails);

        Console.Error.WriteLine(JsonSerializer.Serialize(payload, options));
    }
}

internal sealed record DriverErrorLog(
    string Imie,
    string Nazwisko,
    string NumerBlankietu,
    string Blad,
    string? Szczegoly);
