﻿using System.Collections.Concurrent;
using HeyRed.Mime;
using Manager.Services.Utilities;
using Manager.Shared;
using Manager.Shared.Cache;
using Manager.Shared.Entities;
using Manager.Shared.Enums;
using Manager.Shared.Interfaces;

namespace Manager.Services.Data;

public class LocalDataService : ManagerComponent, IDataService
{
    public string MountName { get; }
    private readonly ConcurrentDictionary<string, PlayItem> _cache = new();
    private readonly LocalPathCacheStrategy _cacheStrategy = new();

    public LocalDataService(string mountName, string name, ulong parent)
        : base(name, parent)
    {
        this.MountName = mountName;
        this.Initialized = true;
    }

    public ValueTask<string[]> GetDirectoriesAsync(string? path = null)
    {
        if (path is not null && !Directory.Exists(path))
        {
            this.SendError(this, nameof(this.GetDirectoriesAsync),
                "Directory does not exist", path);
            return ValueTask.FromResult(Array.Empty<string>());
        }

        try
        {
            return ValueTask.FromResult(path is null
                ? Directory.GetDirectories(this.MountName)
                : Directory.GetDirectories(path));
        }
        catch (Exception e)
        {
            this.SendError(this, nameof(this.GetDirectoriesAsync),
                e,
                path ?? "null");
            return ValueTask.FromResult(Array.Empty<string>());
        }
    }

    public ValueTask<string[]> GetFilesAsync(string? path = null, params string[] extensions)
    {
        try
        {
            path ??= this.MountName;
            var files = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
            if (extensions.Length == 0)
                return ValueTask.FromResult(files);
            
            var filteredFiles = new List<string>();
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (extensions.Contains(extension))
                    filteredFiles.Add(file);
            }
            return ValueTask.FromResult(filteredFiles.ToArray());
        }
        catch (Exception e)
        {
            this.SendError(this, nameof(this.GetFilesAsync),
                e,
                path ?? "null",
                string.Join(", ", extensions));
            return ValueTask.FromResult(Array.Empty<string>());
        }
    }

    public async ValueTask<PlayItem?> GetPlayItemAsync(string path)
    {
        if (!File.Exists(path))
        {
            this.SendError(this, nameof(this.GetPlayItemAsync),
                "File does not exist", path);
            return null;
        }

        if (this._cache.TryGetValue(path, out var value))
            return null;

        //read first 10MB of file to determine file type
        await using var tempBytes = File.OpenRead(path);
        var tempBuffer = new byte[10 * 1024 * 1024];
        _ = await tempBytes.ReadAsync(tempBuffer, 0, tempBuffer.Length);
        var fileType = MimeGuesser.GuessFileType(tempBuffer);
        var item = new PlayItem
        {
            AssociatedDataService = this,
            OwnerPath = path,
            OwnerId = this.Parent,
            Extension = fileType.Extension,
            MimeType = fileType.MimeType,
            CacheStrategy = _cacheStrategy
        };

        item = await GetMetaData(item);
        
        if (string.IsNullOrWhiteSpace(item.Title))
            item.Title = Path.GetFileName(item.OwnerPath);

        this._cache.AddOrUpdate(item.OwnerPath, item, (_, _) => item);
        return item;
    }

    private async Task<PlayItem> GetMetaData(PlayItem item)
    {
        var tags = MetaDataReader.ReadMetaDataTags(item.OwnerPath);
        if (tags.TryGetValue("title", out var title))
            item.Title = title;
        if (tags.TryGetValue("artist", out var artist) || tags.TryGetValue("album_artist", out artist))
            item.Artist = artist;

        item.Duration = MetaDataReader.GetDuration(item.OwnerPath);
        var isVideo = item.MimeType.StartsWith("video");
        if (isVideo)
        {
            var couldGet = MetaDataReader.TryGetVideoThumbnail(item.OwnerPath, out var thumbnail);
            if (couldGet)
            {
                item.Thumbnail = thumbnail;
                var typeInfo = MimeGuesser.GuessFileType(thumbnail);
                item.ThumbnailExtension = typeInfo.Extension;
                item.ThumbnailMimeType = typeInfo.MimeType;
            }
            return item;
        }
        
        var coverArt = await MetaDataReader.TryReadCoverArt(item.OwnerPath);
        if (coverArt is not null)
        {
            item.Thumbnail = coverArt;
            var typeInfo = MimeGuesser.GuessFileType(coverArt);
            item.ThumbnailExtension = typeInfo.Extension;
            item.ThumbnailMimeType = typeInfo.MimeType;
        }
        
        return item;
    }

    public async ValueTask<PlayItem?> CachePlayItemAsync(PlayItem item)
    {
        if (item.CacheState != CacheState.NotCached)
            return item;

        if (item.OwnerId != this.Parent)
        {
            this.SendError(this, nameof(this.CachePlayItemAsync),
                "PlayItem does not belong to this client",
                item.OwnerId, this.Parent, item.OwnerPath);
            return default;
        }

        if (!File.Exists(item.OwnerPath))
        {
            this.SendError(this, nameof(this.CachePlayItemAsync),
                "File does not exist",
                item.OwnerPath);
            return default;
        }

        if (_cache.TryGetValue(item.OwnerPath, out var value) && value.CacheState != CacheState.NotCached)
            return value;

        try
        {
            await item.CacheStrategy.CacheAsync(item, item.OwnerPath);
            this._cache.AddOrUpdate(item.OwnerPath, item, (_, _) => item);
            return item;
        }
        catch (Exception e)
        {
            this.SendError(this, nameof(this.CachePlayItemAsync),
                e,
                item.OwnerPath);
            return default;
        }
    }

    public ValueTask<bool> RemovePlayItemFromCacheAsync(string path)
    {
        if (!_cache.ContainsKey(path))
        {
            this.SendError(this, nameof(this.RemovePlayItemFromCacheAsync),
                "PlayItem does not exist in cache",
                path);
            //Still return true because the item is not in the cache
            return ValueTask.FromResult(true);
        }

        var result = _cache.TryRemove(path, out _);
        if (!result)
        {
            this.SendError(this, nameof(this.RemovePlayItemFromCacheAsync),
                "Failed to remove PlayItem from cache",
                path);
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<bool> RemovePlayItemFromCacheAsync(PlayItem item)
        => RemovePlayItemFromCacheAsync(item.OwnerPath);
}