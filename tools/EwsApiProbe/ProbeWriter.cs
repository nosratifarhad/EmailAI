using System.Reflection;
using Microsoft.Exchange.WebServices.Data;

internal sealed partial class ProbeWriter
{
    public void Write(Type serviceType)
    {
        var t = serviceType;
        Console.WriteLine($"Assembly: {t.Assembly.FullName}");
        Console.WriteLine($"ExchangeService base: {t.BaseType?.FullName}");

        WriteExchangeServiceSurface(t);
        WriteGenericReturns(t);
        WriteConversationResponseTypes();
        WriteReplyTypes();
        WriteExtraReplyAndAttachmentTypes();
        WriteMailDetailTypes();
        WriteUsageTypes();
        WriteCredentials();
        WriteEnums();
        WriteSchemaFields();
        WriteQueryTypes();
        WriteServiceBase(t);
        WriteAllServiceProperties(t);
    }

    private void WriteExchangeServiceSurface(Type t)
    {
        Header("ExchangeService - methods containing 'Conversation'");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name.Contains("Conversation", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({Params(m)})");
        }

        Header("ExchangeService.FindItems overloads");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name == "FindItems"))
        {
            Console.WriteLine($"  {m.ReturnType.Name} FindItems({Params(m)})");
        }

        Header("ExchangeService public properties (subset)");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
        }
    }

    private void WriteGenericReturns(Type t)
    {
        Header("Task<T> generic return arguments");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name is "FindConversation" or "GetConversationItems" or "FindItems"
                                 or "FindFolders" or "GetItem" or "Bind" || m.Name == "LoadPropertiesForItems"))
        {
            var ret = m.ReturnType;
            if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = ret.GetGenericArguments()[0];
                Console.WriteLine($"  {inner.Name} {m.Name}({Params(m)})");
            }
        }
    }

    private void WriteAllServiceProperties(Type t)
    {
        Header("All public instance properties on ExchangeService + base");
        var cur = t;
        while (cur is not null)
        {
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"  [{cur.Name}] {p.PropertyType.Name} {p.Name}");
            }
            cur = cur.BaseType;
        }
    }

    private void WriteConversationResponseTypes()
    {
        Header("GetConversationItemsResponse members");
        DumpType(typeof(GetConversationItemsResponse));

        Header("ConversationRequest members");
        DumpType(typeof(ConversationRequest));

        Header("ConversationNode members");
        DumpType(typeof(ConversationNode));

        Header("Conversation members (subset)");
        foreach (var p in typeof(Conversation).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name.Contains("Id") || p.Name.Contains("Topic") ||
                                 p.Name.Contains("Preview") || p.Name.Contains("Time") ||
                                 p.Name.Contains("Sender") || p.Name.Contains("Count") ||
                                 p.Name.Contains("Read") || p.Name.Contains("Flag")))
        {
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
        }
    }

    private void WriteReplyTypes()
    {
        Header("ResponseMessage members (Reply response)");
        DumpType(typeof(ResponseMessage));

        Header("EmailMessage reply/send methods");
        foreach (var m in typeof(EmailMessage).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name.Contains("Reply") || m.Name.Contains("Send")))
        {
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({Params(m)})");
        }

        Header("BodyType enum");
        Console.WriteLine("  " + string.Join(", ", Enum.GetNames<BodyType>()));
    }

    private void WriteServiceBase(Type t)
    {
        Header("ExchangeServiceBase properties (credentials/url/timeout)");
        foreach (var p in t.BaseType!.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name.Contains("Credential") || p.Name.Contains("Url") ||
                                 p.Name.Contains("Timeout") || p.Name.Contains("Impersonated") ||
                                 p.Name.Contains("UserAgent")))
        {
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
        }
    }

    private void DumpType(Type t)
    {
        foreach (var c in t.GetConstructors())
        {
            Console.WriteLine($"  ctor {t.Name}({Params(c)})");
        }

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var set = p.SetMethod?.IsPublic == true ? "; set;" : "";
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name} {{ get;{set} }}");
        }

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName))
        {
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({Params(m)})");
        }
    }

    private static string Params(MethodInfo m) => string.Join(", ",
        m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    private static string Params(ConstructorInfo c) => string.Join(", ",
        c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    private static void Header(string title) => Console.WriteLine($"\n===== {title} =====");
}
