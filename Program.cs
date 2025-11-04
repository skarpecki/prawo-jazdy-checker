using Microsoft.Extensions.Configuration;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using UpkiClient;
using UpkiClient.Generated; // from dotnet-svcutil output

// 1) Load config
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var app = config.GetSection("App").Get<AppSettings>() ?? new AppSettings();


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

try
{
    var request = new DaneDokumentuRequest
    {
        imiePierwsze = "YUNIOR",
        nazwisko = "KACPRUK",
        seriaNumerBlankietuDruku = "FT100028"
    };

    var response = client.pytanieOUprawnieniaAsync(request).GetAwaiter().GetResult();

    foreach (var kategoria in response.DaneDokumentuResponse.dokumentPotwierdzajacyUprawnienia.daneUprawnieniaKategorii)
    {
        Console.WriteLine(kategoria.kategoria);
        Console.WriteLine(kategoria.dataWaznosci);
    }
        
    return 0;
}
catch (FaultException<CepikException> cepikFault)
{
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
    Console.Error.WriteLine("Call failed:");
    Console.Error.WriteLine(ex);

    return -3;
}
finally
{
    try { if (client.State != CommunicationState.Closed) client.Close(); }
    catch { client.Abort(); }
}
