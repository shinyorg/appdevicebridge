#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Shiny.AppDeviceBridge.Photos.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using AndroidUri = Android.Net.Uri;

namespace Shiny.AppDeviceBridge.Photos;

/// <summary>MediaStore's images, newest first. Access is Essentials' photos permission: READ_MEDIA_IMAGES from Android 13.</summary>
static partial class PhotoLibrary
{
    static readonly string[] Columns =
    [
        IBaseColumns.Id,
        MediaStore.IMediaColumns.DisplayName,
        MediaStore.IMediaColumns.MimeType,
        MediaStore.IMediaColumns.Width,
        MediaStore.IMediaColumns.Height,
        MediaStore.IMediaColumns.DateAdded
    ];

    public static bool IsSupported => true;

    static ContentResolver Resolver => Android.App.Application.Context.ContentResolver!;

    static AndroidUri Collection => MediaStore.Images.Media.ExternalContentUri!;

    public static async Task<ContractAccess> GetAccessAsync() => Map(await Permissions.CheckStatusAsync<Permissions.Photos>());

    public static async Task<ContractAccess> RequestAccessAsync() => Map(await Permissions.RequestAsync<Permissions.Photos>());

    public static Task<IReadOnlyList<LibraryPhoto>> GetPageAsync(int offset, int count, CancellationToken cancellationToken) => Task.Run(() =>
    {
        const string Newest = MediaStore.IMediaColumns.DateAdded + " DESC";

        // Paging moved into query arguments with Android 8; Android 11 refuses it in the sort order.
        using var cursor = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? Resolver.Query(Collection, Columns, Arguments(offset, count), null)
            : Resolver.Query(Collection, Columns, null, null, $"{Newest} LIMIT {count} OFFSET {offset}");

        var photos = new List<LibraryPhoto>();

        while (cursor?.MoveToNext() == true && !cancellationToken.IsCancellationRequested)
        {
            photos.Add(new LibraryPhoto(
                PhotosBridge.Encode(cursor.GetLong(0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                DateTimeOffset.FromUnixTimeSeconds(cursor.GetLong(5)),
                cursor.GetInt(3),
                cursor.GetInt(4),
                cursor.GetString(1)
            ));
        }

        return (IReadOnlyList<LibraryPhoto>)photos;

        static Bundle Arguments(int offset, int count)
        {
            var arguments = new Bundle();
            arguments.PutStringArray(ContentResolver.QueryArgSortColumns, [MediaStore.IMediaColumns.DateAdded]);
            arguments.PutInt(ContentResolver.QueryArgSortDirection, (int)QuerySortDirection.Descending);
            arguments.PutInt(ContentResolver.QueryArgLimit, count);
            arguments.PutInt(ContentResolver.QueryArgOffset, offset);
            return arguments;
        }
    }, cancellationToken);

    public static Task<byte[]?> GetThumbnailAsync(string id, int size, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Int64.TryParse(id, out var mediaId))
            return null;

        Bitmap? bitmap;
        try
        {
            bitmap = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? Resolver.LoadThumbnail(ContentUris.WithAppendedId(Collection, mediaId), new Android.Util.Size(size, size), null)
#pragma warning disable CA1422 // the only thumbnail API before Android 10
                : MediaStore.Images.Thumbnails.GetThumbnail(Resolver, mediaId, ThumbnailKind.MiniKind, null);
#pragma warning restore CA1422
        }
        catch (Java.IO.FileNotFoundException)
        {
            return null;
        }

        if (bitmap is null)
            return null;

        using (bitmap)
        using (var buffer = new MemoryStream())
        {
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, buffer);
            return buffer.ToArray();
        }
    }, cancellationToken);

    public static Task<PhotoData?> OpenAsync(string id, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Int64.TryParse(id, out var mediaId))
            return null;

        var uri = ContentUris.WithAppendedId(Collection, mediaId);

        using var cursor = Resolver.Query(uri, [MediaStore.IMediaColumns.DisplayName, MediaStore.IMediaColumns.MimeType], null, null, null);
        if (cursor?.MoveToFirst() != true)
            return null;

        return Resolver.OpenInputStream(uri) is { } content
            ? new PhotoData(content, cursor.GetString(0), cursor.GetString(1))
            : null;
    }, cancellationToken);

    static ContractAccess Map(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted => ContractAccess.Available,
        PermissionStatus.Limited => ContractAccess.Restricted,
        PermissionStatus.Restricted => ContractAccess.Restricted,
        PermissionStatus.Denied => ContractAccess.Denied,
        PermissionStatus.Disabled => ContractAccess.Disabled,
        _ => ContractAccess.Unknown
    };
}
#endif
