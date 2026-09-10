using System.Reflection;
using Microsoft.Exchange.WebServices.Data;

// Reflection probe: prints the exact API surface of the installed
// Exchange.WebServices.NETCore assembly so production code is written against
// verified signatures (no guessing).
// Run: dotnet run --project tools/EwsApiProbe

static class Program
{
    static void Main()
    {
        var w = new ProbeWriter();
        w.Write(typeof(ExchangeService));
    }
}
