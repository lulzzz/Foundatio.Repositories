﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Repositories.Advanced;
using Foundatio.Repositories.Extensions;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Options;
using Foundatio.Utility;
using Marten;
using Marten.Schema;
using Marten.Services;

namespace Foundatio.Repositories.Marten {
    public abstract class MartenReadOnlyRepositoryBase<T> : IAdvancedReadOnlyRepository<T> where T : class, new() {
        protected static readonly bool HasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
        protected static readonly bool HasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
        protected static readonly bool HasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));
        protected static readonly bool SupportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));
        protected static readonly bool HasVersion = typeof(IVersioned).IsAssignableFrom(typeof(T));
        protected static readonly string EntityTypeName = typeof(T).Name;
        protected static readonly IReadOnlyCollection<T> EmptyList = new List<T>(0).AsReadOnly();
        protected readonly string _idField = null;
        protected readonly IDocumentStore _store;

        protected readonly ILogger _logger;

        private ScopedCacheClient _scopedCacheClient;

        protected MartenReadOnlyRepositoryBase(IDocumentStore store, ICacheClient cacheClient, ILoggerFactory loggerFactory) {
            if (HasIdentity)
                _idField = indexType.GetFieldName((T doc) => ((IIdentity)doc).Id) ?? "id";
            _store = store;
            SetCache(cacheClient);
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public Task<FindResults<T>> FindAsync(IRepositoryQuery query, ICommandOptions options = null) {
            return FindAsAsync<T>(query, options);
        }

        protected ICollection<Field> DefaultExcludes { get; } = new List<Field>();

        public async Task<FindResults<TResult>> FindAsAsync<TResult>(IRepositoryQuery query, ICommandOptions options = null) where TResult : class, new() {
            if (query == null)
                query = new RepositoryQuery();

            bool useSnapshotPaging = options.ShouldUseSnapshotPaging();
            // don't use caching with snapshot paging.
            bool allowCaching = IsCacheEnabled && useSnapshotPaging == false;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(TResult)).AnyContext();

            Func<FindResults<TResult>, Task<FindResults<TResult>>> getNextPageFunc = async r => {
                var previousResults = r;
                if (previousResults == null)
                    throw new ArgumentException(nameof(r));

                if (!String.IsNullOrEmpty(previousResults.GetScrollId())) {
                    var scrollResponse = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), previousResults.GetScrollId()).AnyContext();
                    _logger.Trace(() => scrollResponse.GetRequest());

                    var results = scrollResponse.ToFindResults();
                    results.Page = previousResults.Page + 1;
                    results.HasMore = scrollResponse.Hits.Count() >= options.GetLimit();
                    return results;
                }

                if (options == null)
                    return new FindResults<TResult>();

                options?.PageNumber(!options.HasPageNumber() ? 2 : options.GetPage() + 1);

                return await FindAsAsync<TResult>(query, options).AnyContext();
            };

            string cacheSuffix = options?.HasPageLimit() == true ? String.Concat(options.GetPage().ToString(), ":", options.GetLimit().ToString()) : null;

            FindResults<TResult> result;
            if (allowCaching) {
                result = await GetCachedQueryResultAsync<FindResults<TResult>>(options, cacheSuffix: cacheSuffix).AnyContext();
                if (result != null) {
                    ((IGetNextPage<TResult>)result).GetNextPageFunc = async r => await getNextPageFunc(r).AnyContext();
                    return result;
                }
            }

            ISearchResponse<TResult> response = null;

            if (useSnapshotPaging == false || !options.HasSnapshotScrollId()) {
                var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
                if (useSnapshotPaging)
                    searchDescriptor.Scroll(options.GetSnapshotLifetime());

                response = await _client.SearchAsync<TResult>(searchDescriptor).AnyContext();
            }
            else {
                response = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), options.GetSnapshotScrollId()).AnyContext();
            }

            _logger.Trace(() => response.GetRequest());
            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new FindResults<TResult>();

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            if (useSnapshotPaging) {
                result = response.ToFindResults();
                // TODO: Is there a better way to figure out if you are done scrolling?
                result.HasMore = response.Hits.Count() >= options.GetLimit();
                ((IGetNextPage<TResult>)result).GetNextPageFunc = getNextPageFunc;
            }
            else if (options.HasPageLimit() == true) {
                result = response.ToFindResults(options.GetLimit());
                result.HasMore = response.Hits.Count() > options.GetLimit();
                ((IGetNextPage<TResult>)result).GetNextPageFunc = getNextPageFunc;
            }
            else {
                result = response.ToFindResults();
            }

            result.Page = options.GetPage();

            if (!allowCaching)
                return result;

            var nextPageFunc = ((IGetNextPage<TResult>)result).GetNextPageFunc;
            ((IGetNextPage<TResult>)result).GetNextPageFunc = null;
            await SetCachedQueryResultAsync(options, result, cacheSuffix: cacheSuffix).AnyContext();
            ((IGetNextPage<TResult>)result).GetNextPageFunc = nextPageFunc;

            return result;
        }

        public async Task<FindHit<T>> FindOneAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var result = IsCacheEnabled ? await GetCachedQueryResultAsync<FindHit<T>>(options).AnyContext() : null;
            if (result != null)
                return result;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return FindHit<T>.Empty;

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            result = response.Hits.FirstOrDefault()?.ToFindHit();
            if (IsCacheEnabled)
                await SetCachedQueryResultAsync(options, result).AnyContext();

            return result;
        }
        
        public async Task<T> GetByIdAsync(Id id, ICommandOptions options = null) {
            if (String.IsNullOrEmpty(id.Value))
                return null;

            T hit = null;
            if (IsCacheEnabled && options.ShouldReadCache())
                hit = await Cache.GetAsync<T>(id, default(T)).AnyContext();

            if (hit != null) {
                _logger.Trace(() => $"Cache hit: type={ElasticType.Name} key={id}");
                return hit;
            }

            if (!HasParent || id.Routing != null) {
                var request = new GetRequest(GetIndexById(id), ElasticType.Name, id.Value);
                if (id.Routing != null)
                    request.Routing = id.Routing;
                var response = await _client.GetAsync<T>(request).AnyContext();
                _logger.Trace(() => response.GetRequest());

                hit = response.Found ? response.ToFindHit().Document : null;
            }
            else {
                // we don't have the parent id so we have to do a query
                // TODO: Ensure this is find one query is not cached.
                var findResult = await FindOneAsync(ConfigureQuery(null).Id(id)).AnyContext();
                if (findResult != null)
                    hit = findResult.Document;
            }

            if (hit != null && IsCacheEnabled && options.ShouldUseCache())
                await Cache.SetAsync(id, hit, options.GetExpiresIn()).AnyContext();

            return hit;
        }

        public async Task<IReadOnlyCollection<T>> GetByIdsAsync(Ids ids, ICommandOptions options = null) {
            var idList = ids?.Distinct().Where(i => !String.IsNullOrEmpty(i)).ToList();
            if (idList == null || idList.Count == 0)
                return EmptyList;

            if (!HasIdentity)
                throw new NotSupportedException("Model type must implement IIdentity.");

            var hits = new List<T>();
            if (IsCacheEnabled && options.ShouldReadCache()) {
                var cacheHits = await Cache.GetAllAsync<T>(idList.Select(id => id.Value)).AnyContext();
                hits.AddRange(cacheHits.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Value.Value));
            }

            var itemsToFind = idList.Except(hits.OfType<IIdentity>().Select(i => (Id)i.Id)).ToList();
            if (itemsToFind.Count == 0)
                return hits.AsReadOnly();

            var multiGet = new MultiGetDescriptor();
            foreach (var id in itemsToFind.Where(i => i.Routing != null || !HasParent)) {
                multiGet.Get<T>(f => {
                    f.Id(id.Value).Index(GetIndexById(id)).Type(ElasticType.Name);
                    if (id.Routing != null)
                        f.Routing(id.Routing);

                    return f;
                });
            }

            var multiGetResults = await _client.MultiGetAsync(multiGet).AnyContext();
            _logger.Trace(() => multiGetResults.GetRequest());

            foreach (var doc in multiGetResults.Documents) {
                if (!doc.Found)
                    continue;

                hits.Add(((IMultiGetHit<T>)doc).ToFindHit().Document);
                itemsToFind.Remove(doc.Id);
            }

            // fallback to doing a find
            if (itemsToFind.Count > 0 && (HasParent || HasMultipleIndexes)) {
                var response = await this.FindAsync(q => q.Id(itemsToFind.Select(id => id.Value)), o => o.PageLimit(1000)).AnyContext();
                do {
                    if (response.Hits.Count > 0)
                        hits.AddRange(response.Hits.Where(h => h.Document != null).Select(h => h.Document));
                } while (await response.NextPageAsync().AnyContext());
            }

            if (IsCacheEnabled && options.ShouldUseCache()) {
                foreach (var item in hits.OfType<IIdentity>())
                    await Cache.SetAsync(item.Id, item, options.GetExpiresIn()).AnyContext();
            }

            return hits.AsReadOnly();
        }

        public Task<FindResults<T>> GetAllAsync(ICommandOptions options = null) {
            return FindAsync(null, options);
        }

        public async Task<bool> ExistsAsync(Id id) {
            if (String.IsNullOrEmpty(id.Value))
                return false;
            
            using (var session = _store.QuerySession()) {
                session.Query<T>().Contains(id);
            }
            if (!HasParent || id.Routing != null) {
                var response = await _client.DocumentExistsAsync<T>(new DocumentPath<T>(id.Value), d => {
                    d.Index(GetIndexById(id));
                    if (id.Routing != null)
                        d.Routing(id.Routing);

                    return d;
                }).AnyContext();
                _logger.Trace(() => response.GetRequest());

                return response.Exists;
            }
            else {
                return await this.ExistsAsync(q => q.Id(id)).AnyContext();
            }
        }

        public async Task<bool> ExistsAsync(IRepositoryQuery query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var options = ConfigureOptions(null);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            searchDescriptor.DocvalueFields(_idField);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return false;

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            return response.HitsMetaData.Total > 0;
        }

        public async Task<CountResult> CountAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var result = await GetCachedQueryResultAsync<CountResult>(options, "count").AnyContext();
            if (result != null)
                return result;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
            searchDescriptor.Size(0);

            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new CountResult();

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            result = new CountResult(response.Total, response.ToAggregations());
            await SetCachedQueryResultAsync(options, result, "count").AnyContext();
            return result;
        }

        public async Task<long> CountAsync(ICommandOptions options = null) {
            using (var session = _store.QuerySession()) {
                var result = await session.Query<T>().CountAsync().AnyContext();
                return result;
            }
        }

        protected virtual IRepositoryQuery ConfigureQuery(IRepositoryQuery query) {
            if (query == null)
                query = new RepositoryQuery<T>();

            if (DefaultExcludes.Count > 0 && query.GetExcludes().Count == 0)
                query.Exclude(DefaultExcludes);

            return query;
        }

        public bool IsCacheEnabled { get; private set; } = true;
        protected ScopedCacheClient Cache => _scopedCacheClient ?? new ScopedCacheClient(new NullCacheClient());

        private void SetCache(ICacheClient cache) {
            IsCacheEnabled = cache != null;
            _scopedCacheClient = new ScopedCacheClient(cache ?? new NullCacheClient(), EntityTypeName);
        }

        protected void DisableCache() {
            IsCacheEnabled = false;
            _scopedCacheClient = new ScopedCacheClient(new NullCacheClient(), EntityTypeName);
        }

        protected virtual async Task InvalidateCacheAsync(IReadOnlyCollection<ModifiedDocument<T>> documents, ICommandOptions options) {
            if (!IsCacheEnabled)
                return;

            if (documents != null && documents.Count > 0 && HasIdentity) {
                var keys = documents
                    .Select(d => d.Value)
                    .Cast<IIdentity>()
                    .Select(d => d.Id)
                    .ToList();

                if (keys.Count > 0)
                    await Cache.RemoveAllAsync(keys).AnyContext();
            }
        }

        public Task InvalidateCacheAsync(T document, ICommandOptions options = null) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (!IsCacheEnabled)
                return TaskHelper.Completed();

            return InvalidateCacheAsync(new[] { document }, options);
        }

        public Task InvalidateCacheAsync(IEnumerable<T> documents, ICommandOptions options = null) {
            var docs = documents?.ToList();
            if (docs == null || docs.Any(d => d == null))
                throw new ArgumentNullException(nameof(documents));

            if (!IsCacheEnabled)
                return TaskHelper.Completed();

            return InvalidateCacheAsync(docs.Select(d => new ModifiedDocument<T>(d, null)).ToList(), options);
        }

        protected ICommandOptions ConfigureOptions(ICommandOptions options) {
            if (options == null)
                options = new CommandOptions<T>();

            return options;
        }

        protected async Task<TResult> GetCachedQueryResultAsync<TResult>(ICommandOptions options, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || options == null || !options.ShouldReadCache() || !options.HasCacheKey())
                return default(TResult);

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            var result = await Cache.GetAsync<TResult>(cacheKey, default(TResult)).AnyContext();
            _logger.Trace(() => $"Cache {(result != null ? "hit" : "miss")}: type={EntityTypeName} key={cacheKey}");

            return result;
        }

        protected async Task SetCachedQueryResultAsync<TResult>(ICommandOptions options, TResult result, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || result == null || options == null || !options.ShouldUseCache() || !options.HasCacheKey())
                return;

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            await Cache.SetAsync(cacheKey, result, options.GetExpiresIn()).AnyContext();
            _logger.Trace(() => $"Set cache: type={EntityTypeName} key={cacheKey}");
        }

        #region Events

        public AsyncEvent<BeforeQueryEventArgs<T>> BeforeQuery { get; } = new AsyncEvent<BeforeQueryEventArgs<T>>();

        private async Task OnBeforeQueryAsync(IRepositoryQuery query, ICommandOptions options, Type resultType) {
            if (BeforeQuery == null)
                return;

            await BeforeQuery.InvokeAsync(this, new BeforeQueryEventArgs<T>(query, options, this, resultType)).AnyContext();
        }

        #endregion
    }
}