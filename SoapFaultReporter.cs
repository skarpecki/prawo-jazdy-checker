using System;
using System.ServiceModel;
using System.Text;
using System.Text.Json;
using UpkiClient.Generated;

namespace UpkiClient;

internal static class SoapFaultReporter
{
    internal static int HandleFault(
        DaneDokumentuRequest request,
        FaultException<CepikException> fault,
        JsonSerializerOptions options)
    {
        FailureLogger.LogRequestFailure(request, $"SOAP Fault: {fault.Code.Name}", BuildFaultDetails(fault), options);

        Console.Error.WriteLine($"  Fault Code: {fault.Code.Name}");
        Console.Error.WriteLine($"  Fault Reason: {fault.Reason.GetMatchingTranslation().Text}");

        if (fault.Detail?.komunikaty != null)
        {
            Console.Error.WriteLine("  Szczegóły błędu:");
            foreach (var kom in fault.Detail.komunikaty)
            {
                Console.Error.WriteLine($"    Typ: {kom.typ}");
                Console.Error.WriteLine($"    Kod: {kom.kod}");
                Console.Error.WriteLine($"    Komunikat: {kom.komunikat}");
                if (!string.IsNullOrEmpty(kom.szczegoly))
                    Console.Error.WriteLine($"    Szczegóły: {kom.szczegoly}");
                if (!string.IsNullOrEmpty(kom.identyfikatorBledu))
                    Console.Error.WriteLine($"    ID błędu: {kom.identyfikatorBledu}");
                Console.Error.WriteLine();
            }
        }

        return -1;
    }

    private static string? BuildFaultDetails(FaultException<CepikException> fault)
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
}
