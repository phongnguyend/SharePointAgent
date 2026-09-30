using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed partial class SharePointClient
{
    public const int BrowseUploadLimitBytes = 100 * 1024 * 1024;

    public static void ValidateBrowseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name != name.Trim()
            || name.EndsWith('.') || name.Any(c => char.IsControl(c) || "\"*:<>?/\\|".Contains(c))
            || name is "." or ".." || name.Equals(".lock", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$") || name.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a valid SharePoint name (1–255 characters, without path separators, reserved characters, or trailing dots/spaces).");
        }
    }

    private async Task<DriveItem> BrowseItemAsync(string driveId, string id, CancellationToken ct)
    {
        return await graph.Drives[driveId].Items[id].GetAsync(cancellationToken: ct)
            ?? throw new InvalidDataException("Microsoft Graph returned an empty item.");
    }

    private async Task<DriveItem> BrowseFolderAsync(string driveId, string id, CancellationToken ct)
    {
        var item = await BrowseItemAsync(driveId, id, ct);
        if (item.Folder is null)
        {
            throw new ArgumentException("The destination must be a folder.");
        }
        return item;
    }

    public async Task<SharePointFolderListing> BrowseAsync(string? folderId, CancellationToken ct)
    {
        var driveId = await GetDriveIdAsync(ct);
        var folder = await BrowseFolderAsync(driveId, folderId ?? "root", ct);
        var breadcrumbs = new List<SharePointBrowseItem>();
        var ancestor = folder;
        var visited = new HashSet<string>();
        while (ancestor is not null && visited.Add(ancestor.Id!))
        {
            breadcrumbs.Add(ToBrowseItem(ancestor));
            ancestor = ancestor.Root is null && ancestor.ParentReference?.Id is { } parent
                ? await BrowseItemAsync(driveId, parent, ct) : null;
        }
        breadcrumbs.Reverse();

        var items = new List<SharePointBrowseItem>();
        var page = await graph.Drives[driveId].Items[folder.Id].Children.GetAsync(cancellationToken: ct);
        while (page is not null)
        {
            items.AddRange((page.Value ?? []).Select(ToBrowseItem));
            page = page.OdataNextLink is { Length: > 0 } next
                ? await graph.Drives[driveId].Items[folder.Id].Children.WithUrl(next).GetAsync(cancellationToken: ct)
                : null;
        }
        return new(ToBrowseItem(folder), breadcrumbs,
            items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<SharePointBrowseItem> CreateBrowseFolderAsync(string parentId, string name, CancellationToken ct)
    {
        ValidateBrowseName(name);
        var driveId = await GetDriveIdAsync(ct);
        var parent = await BrowseFolderAsync(driveId, parentId, ct);
        var item = await graph.Drives[driveId].Items[parent.Id].Children.PostAsync(new DriveItem
        {
            Name = name,
            Folder = new Folder(),
            AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" }
        }, cancellationToken: ct);
        return ToBrowseItem(item ?? throw new InvalidDataException("The folder was not returned."));
    }

    private async Task<DriveItem> MutableBrowseItemAsync(string driveId, string id, CancellationToken ct)
    {
        var item = await BrowseItemAsync(driveId, id, ct);
        if (item.Root is not null || item.ParentReference?.Id is null)
        {
            throw new ArgumentException("The document library root cannot be changed.");
        }
        return item;
    }

    public async Task RenameBrowseItemAsync(string id, string name, string etag, CancellationToken ct)
    {
        ValidateBrowseName(name);
        var driveId = await GetDriveIdAsync(ct);
        await MutableBrowseItemAsync(driveId, id, ct);
        await graph.Drives[driveId].Items[id].PatchAsync(new DriveItem { Name = name },
            request => request.Headers.Add("If-Match", etag), ct);
    }

    public async Task DeleteBrowseItemAsync(string id, string etag, CancellationToken ct)
    {
        var driveId = await GetDriveIdAsync(ct);
        await MutableBrowseItemAsync(driveId, id, ct);
        await graph.Drives[driveId].Items[id].DeleteAsync(request => request.Headers.Add("If-Match", etag), ct);
    }

    public async Task TransferBrowseItemAsync(string id, string destinationId, string name, string etag, bool copy, CancellationToken ct)
    {
        ValidateBrowseName(name);
        var driveId = await GetDriveIdAsync(ct);
        var source = await MutableBrowseItemAsync(driveId, id, ct);
        var destination = await BrowseFolderAsync(driveId, destinationId, ct);
        var ancestor = destination;
        var visited = new HashSet<string>();
        while (ancestor is not null && visited.Add(ancestor.Id!))
        {
            if (ancestor.Id == source.Id)
            {
                throw new ArgumentException("A folder cannot be copied or moved into itself or its descendants.");
            }
            ancestor = ancestor.Root is null && ancestor.ParentReference?.Id is { } parent
                ? await BrowseItemAsync(driveId, parent, ct) : null;
        }
        if (copy)
        {
            await graph.Drives[driveId].Items[id].Copy.PostAsync(new CopyPostRequestBody
            {
                Name = name,
                ParentReference = new ItemReference { DriveId = driveId, Id = destination.Id }
            }, cancellationToken: ct);
        }
        else
        {
            await graph.Drives[driveId].Items[id].PatchAsync(new DriveItem
            {
                Name = name,
                ParentReference = new ItemReference { Id = destination.Id }
            }, request => request.Headers.Add("If-Match", etag), ct);
        }
    }

    public async Task<SharePointBrowseItem> UploadBrowseFileAsync(string parentId, string name, Stream content, CancellationToken ct)
    {
        ValidateBrowseName(name);
        if (content.Length > BrowseUploadLimitBytes)
        {
            throw new ArgumentException("Files must be 100 MB or smaller.");
        }
        var driveId = await GetDriveIdAsync(ct);
        var parent = await BrowseFolderAsync(driveId, parentId, ct);
        var target = graph.Drives[driveId].Items[parent.Id].ItemWithPath(name);
        DriveItem? result;
        if (content.Length <= SimpleUploadLimitBytes)
        {
            var url = target.Content.ToPutRequestInformation(content).URI.AbsoluteUri;
            result = await target.Content.WithUrl(url + "?%40microsoft.graph.conflictBehavior=fail")
                .PutAsync(content, cancellationToken: ct);
        }
        else
        {
            var session = await target.CreateUploadSession.PostAsync(new CreateUploadSessionPostRequestBody
            {
                Item = new DriveItemUploadableProperties
                {
                    Name = name,
                    AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" }
                }
            }, cancellationToken: ct) ?? throw new InvalidDataException("The upload session was not returned.");
            var upload = new LargeFileUploadTask<DriveItem>(session, content, UploadSliceBytes, graph.RequestAdapter);
            var response = await upload.UploadAsync(cancellationToken: ct);
            if (!response.UploadSucceeded)
            {
                throw new InvalidDataException("Microsoft Graph did not accept the complete upload.");
            }
            result = response.ItemResponse;
        }
        return ToBrowseItem(result ?? throw new InvalidDataException("The uploaded file was not returned."));
    }

    private static SharePointBrowseItem ToBrowseItem(DriveItem item) => new(
        item.Id ?? throw new InvalidDataException("An item has no ID."), item.Name ?? "Documents",
        item.Folder is not null, item.Size, item.LastModifiedDateTime, item.WebUrl,
        item.ParentReference?.Id, item.ETag);
}
