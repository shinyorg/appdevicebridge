#if IOS || MACCATALYST || MACOS
using CoreGraphics;
using Foundation;
using Photos;
using Shiny.AppDeviceBridge.Photos.Client;
using UniformTypeIdentifiers;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
#if MACOS
using AppKit;
#else
using UIKit;
#endif

namespace Shiny.AppDeviceBridge.Photos;

/// <summary>PhotoKit: every image asset, newest first, with iCloud originals downloaded on demand.</summary>
static partial class PhotoLibrary
{
    public static bool IsSupported => true;

    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(Map(PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite)));

    public static async Task<ContractAccess> RequestAccessAsync() => Map(await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.ReadWrite));

    public static Task<IReadOnlyList<LibraryPhoto>> GetPageAsync(int offset, int count, CancellationToken cancellationToken)
    {
        var assets = Fetch();
        var photos = new List<LibraryPhoto>();

        for (var i = offset; i < Math.Min((long)offset + count, (long)assets.Count); i++)
        {
            if (assets[(nint)i] is not PHAsset asset)
                continue;

            photos.Add(new LibraryPhoto(
                PhotosBridge.Encode(asset.LocalIdentifier),
                asset.CreationDate is { } date ? (DateTimeOffset)(DateTime)date : null,
                (int)asset.PixelWidth,
                (int)asset.PixelHeight,
                PHAssetResource.GetAssetResources(asset).FirstOrDefault()?.OriginalFilename
            ));
        }

        return Task.FromResult<IReadOnlyList<LibraryPhoto>>(photos);
    }

    public static Task<byte[]?> GetThumbnailAsync(string id, int size, CancellationToken cancellationToken)
    {
        if (Find(id) is not { } asset)
            return Task.FromResult<byte[]?>(null);

        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new PHImageRequestOptions
        {
            // One callback with the final image, rather than a degraded one first.
            DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
            ResizeMode = PHImageRequestOptionsResizeMode.Fast,
            NetworkAccessAllowed = true
        };

        PHImageManager.DefaultManager.RequestImageForAsset(asset, new CGSize(size, size), PHImageContentMode.AspectFit, options, (image, _) =>
            result.TrySetResult(image is null ? null : Jpeg(image)));

        return result.Task.WaitAsync(cancellationToken);
    }

    public static Task<PhotoData?> OpenAsync(string id, CancellationToken cancellationToken)
    {
        if (Find(id) is not { } asset)
            return Task.FromResult<PhotoData?>(null);

        var result = new TaskCompletionSource<PhotoData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new PHImageRequestOptions
        {
            DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
            Version = PHImageRequestOptionsVersion.Current,
            NetworkAccessAllowed = true
        };

        PHImageManager.DefaultManager.RequestImageDataAndOrientation(asset, options, (data, typeIdentifier, _, _) =>
        {
            if (data is null)
            {
                result.TrySetResult(null);
                return;
            }

            var type = typeIdentifier is null ? null : UTType.CreateFromIdentifier(typeIdentifier);
            var name = PHAssetResource.GetAssetResources(asset).FirstOrDefault()?.OriginalFilename;

            result.TrySetResult(new PhotoData(new MemoryStream(data.ToArray()), name, type?.PreferredMimeType));
        });

        return result.Task.WaitAsync(cancellationToken);
    }

    static PHFetchResult Fetch() => PHAsset.FetchAssets(
        PHAssetMediaType.Image,
        new PHFetchOptions { SortDescriptors = [new NSSortDescriptor("creationDate", false)] }
    );

    static PHAsset? Find(string localIdentifier)
        => PHAsset.FetchAssetsUsingLocalIdentifiers([localIdentifier], null).FirstObject as PHAsset;

#if MACOS
    static byte[]? Jpeg(NSImage image)
    {
        if (image.CGImage is not { } cgImage)
            return null;

        using var bitmap = new NSBitmapImageRep(cgImage);
        return bitmap.RepresentationUsingTypeProperties(NSBitmapImageFileType.Jpeg, new NSDictionary())?.ToArray();
    }
#else
    static byte[]? Jpeg(UIImage image) => image.AsJPEG(0.85f)?.ToArray();
#endif

    static ContractAccess Map(PHAuthorizationStatus status) => status switch
    {
        PHAuthorizationStatus.Authorized => ContractAccess.Available,
        PHAuthorizationStatus.Limited => ContractAccess.Restricted,
        PHAuthorizationStatus.Denied => ContractAccess.Denied,
        PHAuthorizationStatus.Restricted => ContractAccess.Restricted,
        _ => ContractAccess.Unknown
    };
}
#endif
