using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Contacts.Client;

/// <summary>
/// The device's address book: search and page through contacts, read one, create, update and delete, and fetch photos.
/// Android and iOS; elsewhere every call fails with 501. Without access, calls fail with 403.
/// </summary>
[BridgeClient("contacts", typeof(ContactsJsonContext))]
public interface IContactsBridge
{
    /// <summary>Whether the app has contacts access, without prompting.</summary>
    [BridgeGet]
    Task<ContactsAccessResult> GetAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Prompts for contacts access if it has not been decided.</summary>
    [BridgePost("access")]
    Task<ContactsAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>A page of contacts ordered by family then given name, optionally matching a name, phone or email.</summary>
    /// <param name="search">At most 200 characters.</param>
    /// <param name="offset">How many contacts to skip.</param>
    /// <param name="limit">Between 1 and 500.</param>
    [BridgeGet("items")]
    Task<ContactPage> GetContactsAsync(string? search = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Creates a contact. It needs a given name, family name or company.</summary>
    [BridgePost("items")]
    Task<ContactCreated> CreateContactAsync(ContactInput contact, CancellationToken cancellationToken = default);

    /// <summary>One contact. Fails with 404 when there is no such contact.</summary>
    [BridgeGet("items/{id}")]
    Task<Contact> GetContactAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Changes the properties set in <paramref name="changes"/> and leaves the rest as they are.</summary>
    [BridgePut("items/{id}")]
    Task<Contact> UpdateContactAsync(string id, ContactInput changes, CancellationToken cancellationToken = default);

    /// <summary>Deletes a contact.</summary>
    [BridgeDelete("items/{id}")]
    Task DeleteContactAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The contact's photo as encoded image bytes. Fails with 404 when it has none.</summary>
    [BridgeGet("items/{id}/photo")]
    Task<byte[]> GetPhotoAsync(string id, ContactPhotoSize size = ContactPhotoSize.Full, CancellationToken cancellationToken = default);
}
