using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Contacts.Client;

public enum PhoneType { Home, Mobile, Work, FaxWork, FaxHome, Pager, Other, Custom }

public enum EmailType { Home, Work, Other, Custom }

public enum AddressType { Home, Work, Other, Custom }

public enum ContactDateType { Birthday, Anniversary, Other, Custom }

public enum RelationshipType { Father, Mother, Parent, Brother, Sister, Child, Friend, Spouse, Partner, Assistant, Manager, Other, Custom }

/// <summary>Which photo to fetch. Each falls back to the other when the contact only has one.</summary>
public enum ContactPhotoSize { Full, Thumbnail }

public sealed record ContactsAccessResult(AccessState Access);

public sealed record ContactCreated(string Id);

/// <summary>A page of contacts.</summary>
/// <param name="HasMore">Whether another page follows.</param>
public sealed record ContactPage(IReadOnlyList<Contact> Items, int Offset, int Limit, bool HasMore);

/// <param name="Label">The label shown on the device, for a <c>Custom</c> type.</param>
public sealed record ContactPhone(string Number, PhoneType Type = PhoneType.Other, string? Label = null);

/// <param name="Label">The label shown on the device, for a <c>Custom</c> type.</param>
public sealed record ContactEmail(string Address, EmailType Type = EmailType.Other, string? Label = null);

/// <param name="Label">The label shown on the device, for a <c>Custom</c> type.</param>
public sealed record ContactAddress(
    string? Street = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null,
    string? Country = null,
    AddressType Type = AddressType.Other,
    string? Label = null
);

/// <param name="Label">The label shown on the device, for a <c>Custom</c> type.</param>
public sealed record ContactDate(DateOnly Date, ContactDateType Type = ContactDateType.Other, string? Label = null);

/// <param name="Name">The related person's name.</param>
/// <param name="Label">The label shown on the device, for a <c>Custom</c> type.</param>
public sealed record ContactRelationship(string Name, RelationshipType Type = RelationshipType.Other, string? Label = null);

public sealed record ContactWebsite(string Url, string? Label = null);

public sealed record ContactOrganization(string? Company = null, string? Title = null, string? Department = null);

/// <summary>A contact. Photos are not included; <see cref="IContactsBridge.GetPhotoAsync"/> fetches them.</summary>
/// <param name="HasPhoto">Whether <see cref="IContactsBridge.GetPhotoAsync"/> has anything to return.</param>
public sealed record Contact(
    string? Id,
    string DisplayName,
    string? NamePrefix,
    string? GivenName,
    string? MiddleName,
    string? FamilyName,
    string? NameSuffix,
    string? Nickname,
    string? Note,
    ContactOrganization? Organization,
    IReadOnlyList<ContactPhone> Phones,
    IReadOnlyList<ContactEmail> Emails,
    IReadOnlyList<ContactAddress> Addresses,
    IReadOnlyList<ContactDate> Dates,
    IReadOnlyList<ContactRelationship> Relationships,
    IReadOnlyList<ContactWebsite> Websites,
    bool HasPhoto
);

/// <summary>
/// A contact to create, or the changes to one. A property left null is left as it is; an empty list clears. Names and
/// organization fields are at most 1,000 characters, the note 10,000, and each list 50 entries.
/// </summary>
public sealed class ContactInput
{
    public string? NamePrefix { get; init; }
    public string? GivenName { get; init; }
    public string? MiddleName { get; init; }
    public string? FamilyName { get; init; }
    public string? NameSuffix { get; init; }
    public string? Nickname { get; init; }
    public string? Note { get; init; }
    public ContactOrganization? Organization { get; init; }
    public IReadOnlyList<ContactPhone>? Phones { get; init; }
    public IReadOnlyList<ContactEmail>? Emails { get; init; }
    public IReadOnlyList<ContactAddress>? Addresses { get; init; }
    public IReadOnlyList<ContactDate>? Dates { get; init; }
    public IReadOnlyList<ContactRelationship>? Relationships { get; init; }
    public IReadOnlyList<ContactWebsite>? Websites { get; init; }
}

/// <summary>Serialization for every contacts contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ContactsAccessResult))]
[JsonSerializable(typeof(ContactCreated))]
[JsonSerializable(typeof(ContactPage))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(ContactInput))]
public partial class ContactsJsonContext : JsonSerializerContext;
