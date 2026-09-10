using System.Reflection;
using Microsoft.Exchange.WebServices.Data;

internal sealed partial class ProbeWriter
{
    private void WriteCredentials()
    {
        Header("WebCredentials constructors");
        foreach (var c in typeof(WebCredentials).GetConstructors())
        {
            Console.WriteLine($"  WebCredentials({Params(c)})");
        }

        Header("ExchangeVersion enum values");
        Console.WriteLine("  " + string.Join(", ", Enum.GetNames<ExchangeVersion>()));
    }

    private void WriteEnums()
    {
        Header("ConversationSortOrder / Importance enums");
        Console.WriteLine("  ConversationSortOrder: " + string.Join(", ", Enum.GetNames<ConversationSortOrder>()));
        Console.WriteLine("  Importance: " + string.Join(", ", Enum.GetNames<Importance>()));
    }

    private void WriteSchemaFields()
    {
        Header("ItemSchema fields of interest");
        foreach (var f in typeof(ItemSchema).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.Name.Contains("Body") || f.Name.Contains("Conversation") ||
                                 f.Name.Contains("Reference") || f.Name.Contains("Reply") ||
                                 f.Name.Contains("Internet") || f.Name.Contains("Text") ||
                                 f.Name.Contains("Importance") || f.Name.Contains("ItemClass") ||
                                 f.Name.Contains("Sensitivity")))
        {
            Console.WriteLine($"  {f.FieldType.Name} ItemSchema.{f.Name}");
        }

        Header("EmailMessageSchema fields of interest");
        foreach (var f in typeof(EmailMessageSchema).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.Name.Contains("Reply") || f.Name.Contains("From") ||
                                 f.Name.Contains("Sender")))
        {
            Console.WriteLine($"  {f.FieldType.Name} EmailMessageSchema.{f.Name}");
        }
    }

    private void WriteExtraReplyAndAttachmentTypes()
    {
        Header("ResponseMessage base type + Send/Save methods (all levels)");
        Console.WriteLine($"  base: {typeof(ResponseMessage).BaseType?.FullName}");
        var t = typeof(ResponseMessage);
        while (t is not null)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.Contains("Send") || m.Name.Contains("Save") ||
                                     m.Name.Contains("Reply") || m.Name.Contains("Load")))
            {
                Console.WriteLine($"  [{t.Name}] {m.ReturnType.Name} {m.Name}({Params(m)})");
            }
            t = t.BaseType;
        }

        Header("FindConversationResults + FindItemsResults members");
        var fcr = typeof(ExchangeService)
            .GetMethod("FindConversation", new[] { typeof(ViewBase), typeof(FolderId), typeof(string),
                typeof(bool), typeof(MailboxSearchLocation?), typeof(CancellationToken) });
        if (fcr?.ReturnType.IsGenericType == true)
        {
            DumpType(fcr.ReturnType.GetGenericArguments()[0]);
        }
        DumpType(typeof(FindItemsResults<Item>));

        Header("Attachment types");
        DumpType(typeof(FileAttachment));
        Console.WriteLine("  AttachmentCollection methods:");
        foreach (var m in typeof(AttachmentCollection).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name.Contains("Add") || m.Name.Contains("File")))
        {
            Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({Params(m)})");
        }

        Header("EmailMessage - additional methods (Save/Update/Forward/SetReadState etc.)");
        var em = typeof(EmailMessage);
        while (em is not null)
        {
            foreach (var m in em.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.Contains("Save") || m.Name.Contains("Forward") ||
                                     m.Name.Contains("Load") || m.Name.Contains("Copy") ||
                                     m.Name.Contains("Reply")))
            {
                Console.WriteLine($"  [{em.Name}] {m.ReturnType.Name} {m.Name}({Params(m)})");
            }
            em = em.BaseType;
        }

        Header("Item public properties (subset)");
        foreach (var p in typeof(Item).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name.Contains("Body") || p.Name.Contains("Subject") ||
                                 p.Name.Contains("Conversation") || p.Name.Contains("Internet") ||
                                 p.Name.Contains("Attach") || p.Name.Contains("Importance") ||
                                 p.Name.Contains("DateTime") || p.Name.Contains("Read") ||
                                 p.Name.Contains("Folder") || p.Name.Contains("IsRead")))
        {
            Console.WriteLine($"  {p.PropertyType.Name} Item.{p.Name}");
        }
    }

    private void WriteMailDetailTypes()
    {
        Header("EmailMessage -> Item public properties (all, chain)");
        var cur = typeof(EmailMessage);
        while (cur is not null && cur.Name != "ServiceObject")
        {
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"  [{cur.Name}] {p.PropertyType.Name} {p.Name}");
            }
            cur = cur.BaseType;
        }

        Header("Conversation public properties (all)");
        foreach (var p in typeof(Conversation).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
        }

        Header("ConversationResponse + GetConversationItemsResponse members");
        DumpType(typeof(ConversationResponse));
        Console.WriteLine("  --- GetConversationItemsResponse ---");
        DumpType(typeof(GetConversationItemsResponse));

        Header("ConversationNode all public members");
        DumpType(typeof(ConversationNode));

        Header("MessageBody members + ctors");
        DumpType(typeof(MessageBody));
        foreach (var c in typeof(MessageBody).GetConstructors())
        {
            Console.WriteLine($"  ctor({Params(c)})");
        }

        Header("EmailAddress members + ctors");
        DumpType(typeof(EmailAddress));
        foreach (var c in typeof(EmailAddress).GetConstructors())
        {
            Console.WriteLine($"  ctor({Params(c)})");
        }

        Header("ConversationId + FolderId members");
        DumpType(typeof(ConversationId));
        DumpType(typeof(FolderId));

        Header("WellKnownFolderName + BasePropertySet enum values");
        Console.WriteLine("  WellKnownFolderName: " + string.Join(", ", Enum.GetNames<WellKnownFolderName>()));
        Console.WriteLine("  BasePropertySet: " + string.Join(", ", Enum.GetNames<BasePropertySet>()));
    }

    private void WriteUsageTypes()
    {
        Header("EmailMessage/Item static Bind methods");
        foreach (var m in typeof(EmailMessage).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name == "Bind"))
        {
            Console.WriteLine($"  {m} (IsGenericMethod={m.IsGenericMethod})");
        }
        foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name == "Bind"))
        {
            Console.WriteLine($"  {m} (IsGenericMethod={m.IsGenericMethod})");
        }

        Header("ExchangeService.FindItems exact signatures");
        foreach (var m in typeof(ExchangeService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name == "FindItems" && m.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken))))
        {
            Console.WriteLine($"  {m}");
        }
        Console.WriteLine("  GetConversationItems(ConversationId,...) exact:");
        foreach (var m in typeof(ExchangeService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name == "GetConversationItems" && m.GetParameters().Any(p => p.ParameterType == typeof(ConversationId))))
        {
            Console.WriteLine($"  {m}");
        }

        Header("EWS exception types + ErrorCode/ServiceError");
        foreach (var t in typeof(ExchangeService).Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(Exception)) &&
                                 (t.Name.StartsWith("Service") || t.Name.Contains("Exception"))))
        {
            var err = typeof(ServiceResponseException).GetProperty("ErrorCode");
            Console.WriteLine($"  {t.FullName}");
        }
        foreach (var p in typeof(ServiceResponseException).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.WriteLine($"  [ServiceResponseException] {p.PropertyType.Name} {p.Name}");
        }
        Console.WriteLine("  ServiceError values (subset): " + string.Join(", ",
            Enum.GetNames<ServiceError>().Where(n => n.Contains("NotFound") || n.Contains("Error") || n.Contains("Mailbox"))));
        Console.WriteLine("  base of ServiceResponseException: " + typeof(ServiceResponseException).BaseType?.Name);
        Console.WriteLine("  ServiceError has ItemNotFound: " + Enum.IsDefined(typeof(ServiceError), "ErrorItemNotFound"));
        Console.WriteLine("  ServiceError has FolderNotFound: " + Enum.IsDefined(typeof(ServiceError), "ErrorFolderNotFound"));

        Header("ItemView / FolderView ctors + OffsetBasePoint");
        foreach (var c in typeof(ItemView).GetConstructors())
        {
            Console.WriteLine($"  ItemView({Params(c)})");
        }
        foreach (var c in typeof(FolderView).GetConstructors())
        {
            Console.WriteLine($"  FolderView({Params(c)})");
        }
        Console.WriteLine("  OffsetBasePoint: " + string.Join(", ", Enum.GetNames<OffsetBasePoint>()));
        Console.WriteLine("  ViewBase.PropertySet settable: " +
            typeof(ViewBase).GetProperty("PropertySet", BindingFlags.Public | BindingFlags.Instance)?.CanWrite);
        Console.WriteLine("  ItemView.OrderBy exists: " +
            (typeof(ItemView).GetProperty("OrderBy") is not null));
        Console.WriteLine("  FolderView.Traversal exists: " +
            (typeof(FolderView).GetProperty("Traversal") is not null));

        Header("ConversationNodeCollection members");
        DumpType(typeof(ConversationNodeCollection));

        Header("TextBody / UniqueBody / NormalizedBody members");
        DumpType(typeof(TextBody));
        DumpType(typeof(UniqueBody));
        DumpType(typeof(NormalizedBody));

        Header("SearchFilter nested types + key ctors");
        foreach (var n in typeof(SearchFilter).GetNestedTypes(BindingFlags.Public)
                     .Where(n => n.Name.Contains("Equal") || n.Name == "SearchFilterCollection" ||
                                 n.Name.Contains("Contains")))
        {
            foreach (var c in n.GetConstructors())
            {
                Console.WriteLine($"  {n.Name}({Params(c)})");
            }
        }
        Console.WriteLine("  LogicalOperator: " + string.Join(", ", Enum.GetNames<LogicalOperator>()));

        Header("EmailMessageSchema all fields");
        foreach (var f in typeof(EmailMessageSchema).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Console.WriteLine($"  {f.FieldType.Name} EmailMessageSchema.{f.Name}");
        }

        Header("ItemSchema all fields");
        foreach (var f in typeof(ItemSchema).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Console.WriteLine($"  {f.FieldType.Name} ItemSchema.{f.Name}");
        }

        Header("ImpersonatedUserId ctor + ConnectingIdType enum");
        foreach (var c in typeof(ImpersonatedUserId).GetConstructors())
        {
            Console.WriteLine($"  ImpersonatedUserId({Params(c)})");
        }
        Console.WriteLine("  ConnectingIdType: " + string.Join(", ", Enum.GetNames<ConnectingIdType>()));
    }

    private void WriteQueryTypes()
    {
        Header("PropertySet constructors + RequestedBodyType");
        foreach (var c in typeof(PropertySet).GetConstructors())
        {
            Console.WriteLine($"  PropertySet({Params(c)})");
        }
        Console.WriteLine("  RequestedBodyType present: " +
            (typeof(PropertySet).GetProperty("RequestedBodyType") is not null));

        Header("Folder.Bind overloads");
        foreach (var m in typeof(Folder).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name == "Bind"))
        {
            Console.WriteLine($"  Folder.Bind({Params(m)})");
        }
    }
}
