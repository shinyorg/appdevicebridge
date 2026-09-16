using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Folders.Client;

/// <param name="Root">The file root name to give the folder: 1–64 letters, digits, <c>-</c> or <c>_</c>. One is chosen when null.</param>
/// <param name="Title">The picker's title, where the platform shows one.</param>
public sealed record FolderPickRequest(string? Root = null, string? Title = null);

/// <param name="Root">The file root name the files bridge knows the folder by.</param>
/// <param name="DisplayName">The folder's name as the user saw it in the picker.</param>
/// <param name="Available">False when the folder was moved, deleted or its access revoked since it was picked.</param>
public sealed record PickedFolder(string Root, string DisplayName, bool Available = true);

/// <param name="Supported">Whether this platform has a folder picker.</param>
public sealed record FolderList(bool Supported, IReadOnlyList<PickedFolder> Folders);

/// <summary>Serialization for every folder contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FolderPickRequest))]
[JsonSerializable(typeof(PickedFolder))]
[JsonSerializable(typeof(FolderList))]
public partial class FoldersJsonContext : JsonSerializerContext;
