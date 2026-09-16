using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Photos.Client;

/// <param name="PickerSupported">Whether the system photo picker is available.</param>
/// <param name="LibrarySupported">Whether the photo library can be browsed here.</param>
/// <param name="LibraryAccess">Library access. <c>Restricted</c> is a limited selection the user chose on iOS or Android.</param>
public sealed record PhotoStatus(bool PickerSupported, bool LibrarySupported, AccessState LibraryAccess);

public sealed record PhotoAccessResult(AccessState Access);

/// <param name="Limit">How many the user may choose, 1–50.</param>
/// <param name="Root">The file root the photos are copied into.</param>
public sealed record PhotoPickRequest(int Limit = 1, string Root = "cache");

/// <param name="Root">The file root the photo is copied into.</param>
public sealed record PhotoExportRequest(string Root = "cache");

/// <summary>A photo copied into a file root.</summary>
/// <param name="File">Where it is: read it through the files bridge.</param>
/// <param name="FileName">The name it had on the device, where the platform says.</param>
public sealed record PhotoFile(BridgeFile File, string FileName, string? ContentType, long Size);

/// <summary>A photo in the library.</summary>
/// <param name="Id">Pass to <see cref="IPhotosBridge.GetThumbnailAsync"/> and <see cref="IPhotosBridge.ExportAsync"/>.</param>
public sealed record LibraryPhoto(string Id, DateTimeOffset? CreatedAt, int Width, int Height, string? FileName);

/// <param name="HasMore">Whether another page follows.</param>
public sealed record PhotoPage(IReadOnlyList<LibraryPhoto> Items, int Offset, int Limit, bool HasMore);

/// <summary>Serialization for every photo contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PhotoStatus))]
[JsonSerializable(typeof(PhotoAccessResult))]
[JsonSerializable(typeof(PhotoPickRequest))]
[JsonSerializable(typeof(PhotoExportRequest))]
[JsonSerializable(typeof(PhotoFile))]
[JsonSerializable(typeof(IReadOnlyList<PhotoFile>))]
[JsonSerializable(typeof(PhotoPage))]
public partial class PhotosJsonContext : JsonSerializerContext;
