using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Contacts;
using Shiny.Net.HttpServer;

// MAUI's implicit usings bring Essentials' own Contact types into scope.
using Contact = Shiny.Contacts.Contact;
using ContactEmail = Shiny.Contacts.ContactEmail;
using ContactPhone = Shiny.Contacts.ContactPhone;

namespace Shiny.WebAppHost.Bridge.Contacts;

public static class ContactsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/contacts</c> and registers Shiny's contact store — there is nothing else to call.
    /// <code>
    /// builder.AddContactsBridge();
    /// </code>
    /// <para>
    /// Shiny.Contacts has Android and iOS implementations; everywhere else the endpoints answer 501. The platform
    /// setup is its own: <c>READ_CONTACTS</c> and <c>WRITE_CONTACTS</c> on Android, <c>NSContactsUsageDescription</c>
    /// on iOS.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddContactsBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS
        builder.EnsureShiny();

        if (!builder.Services.Any(x => x.ServiceType == typeof(IContactStore)))
            builder.Services.AddContactStore();
#endif

        builder.Services.AddWebAppBridge<ContactsBridge>();
        return builder;
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

        return WebAppBridgeResults.Json(context, new ContactsAccessResponse(s.GetCurrentAccess()), ContactsBridgeJsonContext.Default.ContactsAccessResponse);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Contacts");
            return;
        }

        // The permission prompt is UI.
        var access = Application.Current?.Dispatcher is { } dispatcher
            ? await dispatcher.DispatchAsync(() => s.RequestAccess(context.RequestAborted))
            : await s.RequestAccess(context.RequestAborted);

        await WebAppBridgeResults.Json(context, new ContactsAccessResponse(access), ContactsBridgeJsonContext.Default.ContactsAccessResponse);
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

        var items = contacts.Take(limit).Select(ContactDto.From).ToList();
        await WebAppBridgeResults.Json(
            context,
            new ContactPage(items, offset, limit, contacts.Count > limit),
            ContactsBridgeJsonContext.Default.ContactPage
        );
    });

    ValueTask GetAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await this.FindAsync(context, s) is { } contact)
            await WebAppBridgeResults.Json(context, ContactDto.From(contact), ContactsBridgeJsonContext.Default.ContactDto);
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
        var input = await WebAppBridgeResults.ReadBodyAsync(context, ContactsBridgeJsonContext.Default.ContactInput);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a contact, e.g. { \"givenName\": \"Ada\", \"phones\": [{ \"number\": \"555-0100\" }] }.");
            return;
        }

        if (input.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        var contact = new Contact();
        input.ApplyTo(contact);

        if (String.IsNullOrWhiteSpace(contact.GivenName)
            && String.IsNullOrWhiteSpace(contact.FamilyName)
            && String.IsNullOrWhiteSpace(contact.Organization?.Company))
        {
            await WebAppBridgeResults.BadRequest(context, "A contact needs a givenName, familyName or organization.company.");
            return;
        }

        var id = await s.Create(contact, context.RequestAborted);
        await WebAppBridgeResults.Json(context, new ContactCreatedResponse(id), ContactsBridgeJsonContext.Default.ContactCreatedResponse, StatusCodes.Status201Created);
    });

    ValueTask UpdateAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, ContactsBridgeJsonContext.Default.ContactInput);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the contact properties to change.");
            return;
        }

        if (input.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (await this.FindAsync(context, s) is not { } contact)
            return;

        input.ApplyTo(contact);
        await s.Update(contact, context.RequestAborted);

        var updated = await s.GetById(contact.Id!, context.RequestAborted) ?? contact;
        await WebAppBridgeResults.Json(context, ContactDto.From(updated), ContactsBridgeJsonContext.Default.ContactDto);
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

public sealed record ContactsAccessResponse(AccessState Access);
public sealed record ContactCreatedResponse(string Id);
public sealed record ContactPage(List<ContactDto> Items, int Offset, int Limit, bool HasMore);

public sealed record ContactDto(
    string? Id,
    string DisplayName,
    string? NamePrefix,
    string? GivenName,
    string? MiddleName,
    string? FamilyName,
    string? NameSuffix,
    string? Nickname,
    string? Note,
    ContactOrganizationDto? Organization,
    List<ContactPhone> Phones,
    List<ContactEmail> Emails,
    List<ContactAddress> Addresses,
    List<ContactDate> Dates,
    List<ContactRelationship> Relationships,
    List<ContactWebsite> Websites,
    bool HasPhoto
)
{
    // Photos stay out of the JSON: a page of contacts with base64 images is megabytes. /photo serves them.
    internal static ContactDto From(Contact c) => new(
        c.Id,
        c.DisplayName,
        c.NamePrefix,
        c.GivenName,
        c.MiddleName,
        c.FamilyName,
        c.NameSuffix,
        c.Nickname,
        c.Note,
        c.Organization is { } o ? new ContactOrganizationDto(o.Company, o.Title, o.Department) : null,
        c.Phones,
        c.Emails,
        c.Addresses,
        c.Dates,
        c.Relationships,
        c.Websites,
        c.Thumbnail is { Length: > 0 } || c.Photo is { Length: > 0 }
    );
}

public sealed record ContactOrganizationDto(string? Company, string? Title, string? Department);

/// <summary>What the page may write. A property left out (null) is left as it is; an empty list clears.</summary>
public sealed record ContactInput(
    string? NamePrefix = null,
    string? GivenName = null,
    string? MiddleName = null,
    string? FamilyName = null,
    string? NameSuffix = null,
    string? Nickname = null,
    string? Note = null,
    ContactOrganizationDto? Organization = null,
    List<ContactPhone>? Phones = null,
    List<ContactEmail>? Emails = null,
    List<ContactAddress>? Addresses = null,
    List<ContactDate>? Dates = null,
    List<ContactRelationship>? Relationships = null,
    List<ContactWebsite>? Websites = null
)
{
    const int MaxText = 1_000;
    const int MaxNote = 10_000;
    const int MaxEntries = 50;

    internal string? Validate()
    {
        string?[] names = [this.NamePrefix, this.GivenName, this.MiddleName, this.FamilyName, this.NameSuffix, this.Nickname,
            this.Organization?.Company, this.Organization?.Title, this.Organization?.Department];

        if (names.Any(x => x?.Length > MaxText))
            return $"Names and organization fields are limited to {MaxText} characters.";

        if (this.Note?.Length > MaxNote)
            return $"note is limited to {MaxNote} characters.";

        int?[] counts = [this.Phones?.Count, this.Emails?.Count, this.Addresses?.Count, this.Dates?.Count, this.Relationships?.Count, this.Websites?.Count];
        if (counts.Any(x => x > MaxEntries))
            return $"Each list is limited to {MaxEntries} entries.";

        if (this.Phones?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Number) || x.Number.Length > MaxText) == true)
            return "Every phone needs a number.";

        if (this.Emails?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Address) || x.Address.Length > MaxText) == true)
            return "Every email needs an address.";

        if (this.Websites?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Url) || x.Url.Length > MaxText) == true)
            return "Every website needs a url.";

        if (this.Relationships?.Any(x => x is null || String.IsNullOrWhiteSpace(x.Name) || x.Name.Length > MaxText) == true)
            return "Every relationship needs a name.";

        if (this.Addresses?.Any(x => x is null) == true || this.Dates?.Any(x => x is null) == true)
            return "Lists cannot contain null entries.";

        return null;
    }

    internal void ApplyTo(Contact contact)
    {
        if (this.NamePrefix is not null) contact.NamePrefix = this.NamePrefix;
        if (this.GivenName is not null) contact.GivenName = this.GivenName;
        if (this.MiddleName is not null) contact.MiddleName = this.MiddleName;
        if (this.FamilyName is not null) contact.FamilyName = this.FamilyName;
        if (this.NameSuffix is not null) contact.NameSuffix = this.NameSuffix;
        if (this.Nickname is not null) contact.Nickname = this.Nickname;
        if (this.Note is not null) contact.Note = this.Note;

        if (this.Organization is { } o)
            contact.Organization = new ContactOrganization(o.Company, o.Title, o.Department);

        if (this.Phones is not null) contact.Phones = this.Phones;
        if (this.Emails is not null) contact.Emails = this.Emails;
        if (this.Addresses is not null) contact.Addresses = this.Addresses;
        if (this.Dates is not null) contact.Dates = this.Dates;
        if (this.Relationships is not null) contact.Relationships = this.Relationships;
        if (this.Websites is not null) contact.Websites = this.Websites;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ContactsAccessResponse))]
[JsonSerializable(typeof(ContactCreatedResponse))]
[JsonSerializable(typeof(ContactPage))]
[JsonSerializable(typeof(ContactDto))]
[JsonSerializable(typeof(ContactInput))]
partial class ContactsBridgeJsonContext : JsonSerializerContext;
