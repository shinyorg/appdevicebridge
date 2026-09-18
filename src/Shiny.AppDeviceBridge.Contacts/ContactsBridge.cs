using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Contacts;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Contacts.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using static Shiny.AppDeviceBridge.Contacts.ContactContractMapping;

// MAUI's implicit usings bring Essentials' own Contact types into scope.
using Contact = Shiny.Contacts.Contact;
using ContactEmail = Shiny.Contacts.ContactEmail;
using ContactPhone = Shiny.Contacts.ContactPhone;

namespace Shiny.AppDeviceBridge.Contacts;

public static class ContactsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/contacts</c> and registers Shiny's contact store — there is nothing else to call.
    /// <code>
    /// bridge.AddContactsBridge();
    /// </code>
    /// <para>
    /// Shiny.Contacts has Android and iOS implementations; everywhere else the endpoints answer 501. The platform
    /// setup is its own: <c>READ_CONTACTS</c> and <c>WRITE_CONTACTS</c> on Android, <c>NSContactsUsageDescription</c>
    /// on iOS.
    /// </para>
    /// </summary>
    public static TBuilder AddContactsBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID || IOS
        if (!bridge.Services.Any(x => x.ServiceType == typeof(IContactStore)))
            bridge.Services.AddContactStore();
#endif

        bridge.AddBridge<ContactsBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/contacts</c> over <see cref="IContactStore"/>.
/// <code>
/// GET    /_bridge/contacts                     { "access": "Available" }
/// POST   /_bridge/contacts/access              prompts; answers the same shape
/// GET    /_bridge/contacts/items?search=&amp;offset=0&amp;limit=50
///                                              { "items": [...], "offset": 0, "limit": 50, "hasMore": true }
/// POST   /_bridge/contacts/items               a contact; 201 { "id": "…" }
/// GET    /_bridge/contacts/items/{id}
/// PUT    /_bridge/contacts/items/{id}          only the properties sent are changed
/// DELETE /_bridge/contacts/items/{id}
/// GET    /_bridge/contacts/items/{id}/photo?size=full|thumbnail   image bytes, 404 when there is none
/// </code>
/// </summary>
public sealed class ContactsBridge(IServiceProvider services) : IWebAppBridge
{
    const int DefaultLimit = 50;
    const int MaxLimit = 500;
    const int MaxSearchLength = 200;

    readonly IContactStore? store = services.GetOptionalService<IContactStore>();

    public string Name => "contacts";

    public bool IsSupported => this.store is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/items", this.ListAsync)
        .MapPost("/items", this.CreateAsync)
        .MapGet("/items/{id}", this.GetAsync)
        .MapPut("/items/{id}", this.UpdateAsync)
        .MapDelete("/items/{id}", this.DeleteAsync)
        .MapGet("/items/{id}/photo", this.PhotoAsync);

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.store is not { } s)
            return WebAppBridgeResults.NotSupported(context, "Contacts");

        return WebAppBridgeResults.Json(context, ToContract(s.GetCurrentAccess()), Contracts.ContactsJsonContext.Default.ContactsAccessResult);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Contacts");
            return;
        }

        // The permission prompt is UI.
        var access = await services.GetRequiredService<IWebAppMainThread>().InvokeAsync(() => s.RequestAccess(context.RequestAborted));

        await WebAppBridgeResults.Json(context, ToContract(access), Contracts.ContactsJsonContext.Default.ContactsAccessResult);
    }

    ValueTask ListAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var query = context.Request.Query;
        var search = query["search"].ToString().Trim();

        if (search.Length > MaxSearchLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"search is limited to {MaxSearchLength} characters.");
            return;
        }

        if (!TryReadInt(query["offset"].ToString(), 0, 0, Int32.MaxValue, out var offset)
            || !TryReadInt(query["limit"].ToString(), DefaultLimit, 1, MaxLimit, out var limit))
        {
            await WebAppBridgeResults.BadRequest(context, $"offset must be 0 or more, and limit between 1 and {MaxLimit}.");
            return;
        }

        var q = s.Query();
        if (search.Length > 0)
            q = q.Search(search);

        // One more than asked for says whether there is another page, without counting the whole address book.
        var contacts = await q
            .OrderBy(ContactSortField.FamilyName)
            .ThenBy(ContactSortField.GivenName)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);

        IReadOnlyList<Contracts.Contact> items = [.. contacts.Take(limit).Select(ToContract)];
        await WebAppBridgeResults.Json(
            context,
            new Contracts.ContactPage(items, offset, limit, contacts.Count > limit),
            Contracts.ContactsJsonContext.Default.ContactPage
        );
    });

    ValueTask GetAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await this.FindAsync(context, s) is { } contact)
            await WebAppBridgeResults.Json(context, ToContract(contact), Contracts.ContactsJsonContext.Default.Contact);
    });

    ValueTask PhotoAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await this.FindAsync(context, s) is not { } contact)
            return;

        var thumbnail = context.Request.Query["size"].ToString().Equals("thumbnail", StringComparison.OrdinalIgnoreCase);
        var bytes = thumbnail ? contact.Thumbnail ?? contact.Photo : contact.Photo ?? contact.Thumbnail;

        if (bytes is not { Length: > 0 })
        {
            await WebAppBridgeResults.NotFound(context, "The contact has no photo.");
            return;
        }

        await context.Response.WriteBytesAsync(bytes, SniffImageType(bytes), context.RequestAborted);
    });

    ValueTask CreateAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.ContactsJsonContext.Default.ContactInput);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a contact, e.g. { \"givenName\": \"Ada\", \"phones\": [{ \"number\": \"555-0100\" }] }.");
            return;
        }

        if (Validate(input) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        var contact = new Contact();
        ApplyTo(input, contact);

        if (String.IsNullOrWhiteSpace(contact.GivenName)
            && String.IsNullOrWhiteSpace(contact.FamilyName)
            && String.IsNullOrWhiteSpace(contact.Organization?.Company))
        {
            await WebAppBridgeResults.BadRequest(context, "A contact needs a givenName, familyName or organization.company.");
            return;
        }

        var id = await s.Create(contact, context.RequestAborted);
        await WebAppBridgeResults.Json(context, new Contracts.ContactCreated(id), Contracts.ContactsJsonContext.Default.ContactCreated, StatusCodes.Status201Created);
    });

    ValueTask UpdateAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.ContactsJsonContext.Default.ContactInput);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the contact properties to change.");
            return;
        }

        if (Validate(input) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (await this.FindAsync(context, s) is not { } contact)
            return;

        ApplyTo(input, contact);
        await s.Update(contact, context.RequestAborted);

        var updated = await s.GetById(contact.Id!, context.RequestAborted) ?? contact;
        await WebAppBridgeResults.Json(context, ToContract(updated), Contracts.ContactsJsonContext.Default.Contact);
    });

    ValueTask DeleteAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await this.FindAsync(context, s) is not { } contact)
            return;

        await s.Delete(contact.Id!, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    /// <summary>The contact named by the route, or null once a 400 or 404 has been written.</summary>
    async Task<Contact?> FindAsync(HttpContext context, IContactStore s)
    {
        var id = context.Request.RouteValues["id"];
        if (String.IsNullOrWhiteSpace(id) || id.Length > 256)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a contact id.");
            return null;
        }

        var contact = await s.GetById(id, context.RequestAborted);
        if (contact is null)
            await WebAppBridgeResults.NotFound(context, "No such contact.");

        return contact;
    }

    /// <summary>
    /// 501 without a store, 403 when access was refused, and the same 403 when the platform throws for a missing
    /// permission rather than returning nothing.
    /// </summary>
    async ValueTask GuardAsync(HttpContext context, Func<IContactStore, Task> action)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Contacts");
            return;
        }

        if (s.GetCurrentAccess() is AccessState.Denied or AccessState.Disabled or AccessState.NotSetup or AccessState.NotSupported)
        {
            await AccessDenied(context);
            return;
        }

        try
        {
            await action(s);
        }
        catch (Exception ex) when (!context.Response.HasStarted && IsPermissionFailure(ex))
        {
            await AccessDenied(context);
        }
    }

    static ValueTask AccessDenied(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status403Forbidden,
        "access_denied",
        "Contacts access has not been granted. POST /_bridge/contacts/access first."
    );

    static bool IsPermissionFailure(Exception ex) => ex is UnauthorizedAccessException
#if ANDROID
        || ex is Java.Lang.SecurityException
#endif
        ;

    static bool TryReadInt(string raw, int fallback, int min, int max, out int value)
    {
        if (raw.Length == 0)
        {
            value = fallback;
            return true;
        }

        return Int32.TryParse(raw, out value) && value >= min && value <= max;
    }

    /// <summary>Platforms hand back encoded image bytes without saying which format; the signature does.</summary>
    static string SniffImageType(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x47, 0x49, 0x46, ..] => "image/gif",
        [_, _, _, _, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, ..] => "image/heic",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => "application/octet-stream"
    };
}

static class ContactContractMapping
{
    const int MaxText = 1_000;
    const int MaxNote = 10_000;
    const int MaxEntries = 50;

    public static Contracts.ContactsAccessResult ToContract(AccessState access)
        => new(BridgeEnum.Convert<AccessState, ContractAccess>(access));

    // Photos stay out of the JSON: a page of contacts with base64 images is megabytes. /photo serves them.
    public static Contracts.Contact ToContract(Contact c) => new(
        c.Id,
        c.DisplayName,
        c.NamePrefix,
        c.GivenName,
        c.MiddleName,
        c.FamilyName,
        c.NameSuffix,
        c.Nickname,
        c.Note,
        c.Organization is { } o ? new Contracts.ContactOrganization(o.Company, o.Title, o.Department) : null,
        [.. c.Phones.Select(x => new Contracts.ContactPhone(x.Number, Convert<PhoneType, Contracts.PhoneType>(x.Type), x.Label))],
        [.. c.Emails.Select(x => new Contracts.ContactEmail(x.Address, Convert<EmailType, Contracts.EmailType>(x.Type), x.Label))],
        [.. c.Addresses.Select(x => new Contracts.ContactAddress(x.Street, x.City, x.State, x.PostalCode, x.Country, Convert<AddressType, Contracts.AddressType>(x.Type), x.Label))],
        [.. c.Dates.Select(x => new Contracts.ContactDate(x.Date, Convert<ContactDateType, Contracts.ContactDateType>(x.Type), x.Label))],
        [.. c.Relationships.Select(x => new Contracts.ContactRelationship(x.Name, Convert<RelationshipType, Contracts.RelationshipType>(x.Type), x.Label))],
        [.. c.Websites.Select(x => new Contracts.ContactWebsite(x.Url, x.Label))],
        c.Thumbnail is { Length: > 0 } || c.Photo is { Length: > 0 }
    );

    public static string? Validate(Contracts.ContactInput input)
    {
        string?[] names = [input.NamePrefix, input.GivenName, input.MiddleName, input.FamilyName, input.NameSuffix, input.Nickname,
            input.Organization?.Company, input.Organization?.Title, input.Organization?.Department];

        if (names.Any(x => x?.Length > MaxText))
            return $"Names and organization fields are limited to {MaxText} characters.";

        if (input.Note?.Length > MaxNote)
            return $"note is limited to {MaxNote} characters.";

        int?[] counts = [input.Phones?.Count, input.Emails?.Count, input.Addresses?.Count, input.Dates?.Count, input.Relationships?.Count, input.Websites?.Count];
        if (counts.Any(x => x > MaxEntries))
            return $"Each list is limited to {MaxEntries} entries.";

        if (input.Phones?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Number) || x.Number.Length > MaxText) == true)
            return "Every phone needs a number.";

        if (input.Emails?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Address) || x.Address.Length > MaxText) == true)
            return "Every email needs an address.";

        if (input.Websites?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Url) || x.Url.Length > MaxText) == true)
            return "Every website needs a url.";

        if (input.Relationships?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Name) || x.Name.Length > MaxText) == true)
            return "Every relationship needs a name.";

        if (input.Addresses?.Any(x => x is null) == true || input.Dates?.Any(x => x is null) == true)
            return "Lists cannot contain null entries.";

        return null;
    }

    /// <summary>A property left out (null) is left as it is; an empty list clears.</summary>
    public static void ApplyTo(Contracts.ContactInput input, Contact contact)
    {
        if (input.NamePrefix is not null) contact.NamePrefix = input.NamePrefix;
        if (input.GivenName is not null) contact.GivenName = input.GivenName;
        if (input.MiddleName is not null) contact.MiddleName = input.MiddleName;
        if (input.FamilyName is not null) contact.FamilyName = input.FamilyName;
        if (input.NameSuffix is not null) contact.NameSuffix = input.NameSuffix;
        if (input.Nickname is not null) contact.Nickname = input.Nickname;
        if (input.Note is not null) contact.Note = input.Note;

        if (input.Organization is { } o)
            contact.Organization = new ContactOrganization(o.Company, o.Title, o.Department);

        if (input.Phones is not null)
            contact.Phones = [.. input.Phones.Select(x => new ContactPhone(x.Number, Convert<Contracts.PhoneType, PhoneType>(x.Type), x.Label))];

        if (input.Emails is not null)
            contact.Emails = [.. input.Emails.Select(x => new ContactEmail(x.Address, Convert<Contracts.EmailType, EmailType>(x.Type), x.Label))];

        if (input.Addresses is not null)
            contact.Addresses = [.. input.Addresses.Select(x => new ContactAddress(x.Street, x.City, x.State, x.PostalCode, x.Country, Convert<Contracts.AddressType, AddressType>(x.Type), x.Label))];

        if (input.Dates is not null)
            contact.Dates = [.. input.Dates.Select(x => new ContactDate(x.Date, Convert<Contracts.ContactDateType, ContactDateType>(x.Type), x.Label))];

        if (input.Relationships is not null)
            contact.Relationships = [.. input.Relationships.Select(x => new ContactRelationship(x.Name, Convert<Contracts.RelationshipType, RelationshipType>(x.Type), x.Label))];

        if (input.Websites is not null)
            contact.Websites = [.. input.Websites.Select(x => new ContactWebsite(x.Url, x.Label))];
    }

    static TTo Convert<TFrom, TTo>(TFrom value) where TFrom : struct, Enum where TTo : struct, Enum
        => BridgeEnum.Convert<TFrom, TTo>(value);
}
