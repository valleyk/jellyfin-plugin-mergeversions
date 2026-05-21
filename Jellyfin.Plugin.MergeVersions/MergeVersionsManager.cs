using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
        private readonly SessionInfo _session;
        private readonly IFileSystem _fileSystem;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void MergeMovies(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1 &&
                group.Any(movie => !HasPrimaryVersionId(movie) &&
                                    !movie.LinkedAlternateVersions.Any()))
                .ToList();

            var current = 0;
            Parallel.ForEach(
                duplicateMovies,
                async m =>
                {
                    current++;
                    var percent = current / (double)duplicateMovies.Count * 100;
                    progress?.Report((int)percent);
                    _logger.LogInformation(
                        $"Merging {m.ElementAt(0).Name} ({m.ElementAt(0).ProductionYear})"
                    );
                    await MergeVersions(m.Select(e => e.Id).ToList());
                }
            );
            progress?.Report(100);
        }

        public void SplitMovies(IProgress<double> progress)
        {
            var movies = GetMoviesFromLibrary();
            var current = 0;
            Parallel.ForEach(
                movies,
                async m =>
                {
                    current++;
                    var percent = current / (double)movies.Count * 100;
                    progress?.Report((int)percent);

                    _logger.LogInformation($"Spliting {m.Name} ({m.ProductionYear})");
                    await DeleteAlternateSources(m.Id);
                }
            );
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated episodes");

            var duplicateEpisodes = GetEpisodesFromLibrary()
                .GroupBy(x => new
                {
                    x.SeriesName,
                    x.SeasonName,
                    x.Name,
                    x.IndexNumber
                })
                .Where(x => x.Count() > 1)
                .ToList();

            var current = 0;
            foreach (var e in duplicateEpisodes)
            {
                current++;
                var percent = current / (double)duplicateEpisodes.Count * 100;
                progress?.Report((int)percent);
                _logger.LogInformation(
                    $"Merging {e.ElementAt(0).Name} ({e.ElementAt(0).ProductionYear})"
                );
                await MergeVersions(e.Select(e => e.Id).ToList());
            }
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress)
        {
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var e in episodes)
            {
                current++;
                var percent = current / (double)episodes.Count * 100;
                progress?.Report((int)percent);

                _logger.LogInformation($"Spliting {e.IndexNumber} ({e.SeriesName})");
                await DeleteAlternateSources(e.Id);
            }
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .Select(m => m as Movie)
                .Where(m => m.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsEligible)
                .ToList();
        }

        private async Task MergeVersions(List<Guid> ids)
        {
            var items = ids.Select(i => _libraryManager.GetItemById<BaseItem>(i))
                .OfType<Video>()
                .OrderBy(i => i.Id)
                .ToList();

            if (items.Count < 2)
            {
                return;
            }

            var primaryVersion = items.FirstOrDefault(i =>
                i.MediaSourceCount > 1 && !HasPrimaryVersionId(i)
            );
            if (primaryVersion is null)
            {
                primaryVersion = items
                    .OrderBy(i =>
                    {
                        if (i.Video3DFormat.HasValue || i.VideoType != VideoType.VideoFile)
                        {
                            return 1;
                        }

                        return 0;
                    })
                    .ThenByDescending(i => i.GetDefaultVideoStream()?.Width ?? 0)
                    .First();
            }

            _logger.LogInformation(
                "Merge candidate group: count={Count}; primary={PrimaryId}; primaryPath={PrimaryPath}; primaryKey={PrimaryKey}",
                items.Count,
                primaryVersion.Id,
                primaryVersion.Path,
                GetPresentationUniqueKeyRawCompat(primaryVersion) ?? "<null>");

            var alternateVersionsOfPrimary = primaryVersion
                .LinkedAlternateVersions.Where(l => items.Any(i => i.Path == l.Path))
                .ToList();

            var alternateVersionsChanged = false;
            foreach (var item in items.Where(i =>
                !i.Id.Equals(primaryVersion.Id) &&
                !alternateVersionsOfPrimary.Any(l => l.ItemId == i.Id)))
            {
                var beforePrimaryRaw = GetPrimaryVersionIdRawCompat(item);
                var beforePresentationKeyRaw = GetPresentationUniqueKeyRawCompat(item);

                var primarySetPath = SetPrimaryVersionIdCompat(item, primaryVersion.Id);
                var keySetPath = "<not-attempted>";
                if (TryGetPresentationUniqueKeyCompat(primaryVersion, out var primaryPresentationKey))
                {
                    keySetPath = SetPresentationUniqueKeyCompat(item, primaryPresentationKey);
                }
                else
                {
                    keySetPath = "<source-key-missing>";
                }

                await item.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);

                var afterPrimaryRaw = GetPrimaryVersionIdRawCompat(item);
                var afterPresentationKeyRaw = GetPresentationUniqueKeyRawCompat(item);
                var persisted = _libraryManager.GetItemById<Video>(item.Id);
                var persistedPrimaryRaw = persisted is null ? "<reload-null>" : GetPrimaryVersionIdRawCompat(persisted);
                var persistedPresentationKeyRaw = persisted is null
                    ? "<reload-null>"
                    : GetPresentationUniqueKeyRawCompat(persisted);

                _logger.LogInformation(
                    "Merge write result: item={ItemId}; path={ItemPath}; setPrimary={PrimarySetPath}; setKey={KeySetPath}; primary(before={BeforePrimary}, after={AfterPrimary}, persisted={PersistedPrimary}); key(before={BeforeKey}, after={AfterKey}, persisted={PersistedKey})",
                    item.Id,
                    item.Path,
                    primarySetPath,
                    keySetPath,
                    beforePrimaryRaw ?? "<null>",
                    afterPrimaryRaw ?? "<null>",
                    persistedPrimaryRaw ?? "<null>",
                    beforePresentationKeyRaw ?? "<null>",
                    afterPresentationKeyRaw ?? "<null>",
                    persistedPresentationKeyRaw ?? "<null>");

                // TODO: due to check in foreach it can't be an alternate version yet?
                AddToAlternateVersionsIfNotPresent(alternateVersionsOfPrimary,
                                                new LinkedChild { Path = item.Path,
                                                                  ItemId = item.Id });

                foreach (var linkedItem in item.LinkedAlternateVersions)
                {
                    AddToAlternateVersionsIfNotPresent(alternateVersionsOfPrimary,
                                                    linkedItem);
                }

                if (item.LinkedAlternateVersions.Length > 0)
                {
                    item.LinkedAlternateVersions = [];
                    await item.UpdateToRepositoryAsync(
                            ItemUpdateType.MetadataEdit,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                alternateVersionsChanged = true;
            }

            if (alternateVersionsChanged)
            {
                primaryVersion.LinkedAlternateVersions = alternateVersionsOfPrimary.ToArray();
                await primaryVersion
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private async Task DeleteAlternateSources(Guid itemId)
        {
            var item = _libraryManager.GetItemById<Video>(itemId);
            if (item is null)
            {
                return;
            }

            if (item.LinkedAlternateVersions.Length == 0 && TryGetPrimaryVersionId(item, out var primaryVersionId))
            {
                item = _libraryManager.GetItemById<Video>(primaryVersionId);
            }

            if (item is null)
            {
                return;
            }

            foreach (var link in GetLinkedAlternateVersionsCompat(item))
            {
                SetPrimaryVersionIdCompat(link, null);
                link.LinkedAlternateVersions = [];

                await link.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }

            item.LinkedAlternateVersions = [];
            SetPrimaryVersionIdCompat(item, null);
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool HasPrimaryVersionId(Video item)
        {
            return TryGetPrimaryVersionId(item, out _);
        }

        private bool TryGetPrimaryVersionId(Video item, out Guid primaryVersionId)
        {
            primaryVersionId = Guid.Empty;

            var property = item.GetType().GetProperty("PrimaryVersionId", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanRead)
            {
                return false;
            }

            var value = property.GetValue(item);
            if (value is null)
            {
                return false;
            }

            if (value is Guid guidValue)
            {
                if (guidValue == Guid.Empty)
                {
                    return false;
                }

                primaryVersionId = guidValue;
                return true;
            }

            if (value is string stringValue)
            {
                return Guid.TryParse(stringValue, out primaryVersionId);
            }

            return Guid.TryParse(value.ToString(), out primaryVersionId);
        }

        private string GetPrimaryVersionIdRawCompat(Video item)
        {
            var property = item.GetType().GetProperty("PrimaryVersionId", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanRead)
            {
                return null;
            }

            var value = property.GetValue(item);
            return value?.ToString();
        }

        private string SetPrimaryVersionIdCompat(Video item, Guid? primaryVersionId)
        {
            var itemType = item.GetType();
            var methods = itemType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, "SetPrimaryVersionId", StringComparison.Ordinal))
                .Where(m => m.GetParameters().Length == 1)
                .ToList();

            foreach (var method in methods)
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                object argument;

                if (parameterType == typeof(Guid?))
                {
                    argument = primaryVersionId;
                }
                else if (parameterType == typeof(Guid))
                {
                    argument = primaryVersionId ?? Guid.Empty;
                }
                else if (parameterType == typeof(string))
                {
                    argument = primaryVersionId?.ToString("N");
                }
                else
                {
                    continue;
                }

                method.Invoke(item, [argument]);
                return $"method:{parameterType.Name}";
            }

            var property = itemType.GetProperty("PrimaryVersionId", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite)
            {
                return "no-writable-target";
            }

            if (property.PropertyType == typeof(Guid?))
            {
                property.SetValue(item, primaryVersionId);
                return "property:NullableGuid";
            }

            if (property.PropertyType == typeof(Guid))
            {
                property.SetValue(item, primaryVersionId ?? Guid.Empty);
                return "property:Guid";
            }

            if (property.PropertyType == typeof(string))
            {
                property.SetValue(item, primaryVersionId?.ToString("N"));
                return "property:String";
            }

            return $"property:unsupported:{property.PropertyType.Name}";
        }

        private IEnumerable<Video> GetLinkedAlternateVersionsCompat(Video item)
        {
            var itemType = item.GetType();
            var getLinkedMethod = itemType.GetMethod("GetLinkedAlternateVersions", Type.EmptyTypes);
            if (getLinkedMethod is not null && getLinkedMethod.Invoke(item, null) is System.Collections.IEnumerable fromItem)
            {
                return fromItem.Cast<object>().OfType<Video>();
            }

            var libraryManagerType = _libraryManager.GetType();
            var libraryMethod = libraryManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "GetLinkedAlternateVersions", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(itemType);
                });

            if (libraryMethod is not null
                && libraryMethod.Invoke(_libraryManager, [item]) is System.Collections.IEnumerable fromLibrary)
            {
                return fromLibrary.Cast<object>().OfType<Video>();
            }

            return item.LinkedAlternateVersions
                .Where(link => link.ItemId.HasValue)
                .Select(link => _libraryManager.GetItemById<Video>(link.ItemId.Value))
                .Where(video => video is not null)
                .Cast<Video>();
        }

        private bool TryGetPresentationUniqueKeyCompat(BaseItem item, out string presentationUniqueKey)
        {
            presentationUniqueKey = string.Empty;

            var itemType = item.GetType();
            var getMethod = itemType.GetMethod("GetPresentationUniqueKey", Type.EmptyTypes);
            if (getMethod is not null && getMethod.Invoke(item, null) is string methodValue
                && !string.IsNullOrWhiteSpace(methodValue))
            {
                presentationUniqueKey = methodValue;
                return true;
            }

            var property = itemType.GetProperty("PresentationUniqueKey", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanRead)
            {
                return false;
            }

            if (property.GetValue(item) is not string propertyValue || string.IsNullOrWhiteSpace(propertyValue))
            {
                return false;
            }

            presentationUniqueKey = propertyValue;
            return true;
        }

        private string GetPresentationUniqueKeyRawCompat(BaseItem item)
        {
            var property = item.GetType().GetProperty("PresentationUniqueKey", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanRead)
            {
                return null;
            }

            return property.GetValue(item)?.ToString();
        }

        private string SetPresentationUniqueKeyCompat(BaseItem item, string presentationUniqueKey)
        {
            if (string.IsNullOrWhiteSpace(presentationUniqueKey))
            {
                return "skip-empty-value";
            }

            var itemType = item.GetType();
            var setMethod = itemType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "SetPresentationUniqueKey", StringComparison.Ordinal)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));

            if (setMethod is not null)
            {
                setMethod.Invoke(item, [presentationUniqueKey]);
                return "method:String";
            }

            var property = itemType.GetProperty(
                "PresentationUniqueKey",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.SetMethod is null)
            {
                return "no-writable-target";
            }

            property.SetValue(item, presentationUniqueKey);
            return "property:String";
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void AddToAlternateVersionsIfNotPresent(List<LinkedChild> alternateVersions,
                                                        LinkedChild newVersion)
        {
            if (!alternateVersions.Any(
                i => string.Equals(i.Path,
                                newVersion.Path,
                                StringComparison.OrdinalIgnoreCase
                            )))
            {
                alternateVersions.Add(newVersion);
            }
        }

        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _session?.DisposeAsync();
            }
        }
    }
}
