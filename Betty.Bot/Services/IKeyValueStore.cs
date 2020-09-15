using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace Betty.Bot.Services
{
    public interface IKeyValueStore
    {
        Task SetValueAsync<T>(string category, string key, T value) where T : class;
        Task<T> GetValueAsync<T>(string category, string key) where T : class;
        Task<Dictionary<string, T>> GetValuesAsync<T>(string category) where T : class;
        Task RemoveValueAsync(string category, string key);
    }

    public class StorageKeyValueStore : IKeyValueStore
    {
        private ILogger _logger;
        private CloudTable _table;

        public StorageKeyValueStore(ILogger<StorageKeyValueStore> logger, CloudTableClient tableClient)
        {
            _logger = logger;
            _table = tableClient.GetTableReference(nameof(StorageKeyValueStore));
        }

        public async Task<T> GetValueAsync<T>(string category, string key) where T : class
        {
            var partitionFilter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, category);
            var rowFilter = TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, key);
            var combinedFilter = TableQuery.CombineFilters(partitionFilter, TableOperators.And, rowFilter);
            var query = new TableQuery<StorageEntity> { FilterString = combinedFilter, TakeCount = 1, SelectColumns = new[] { "Value" } };
            TableQuerySegment<StorageEntity> resultSegment;
            try
            {
                resultSegment = await _table.ExecuteQuerySegmentedAsync(query, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error querying for {category}/{key}, trying to create table in case it didn't exist and retrying...");
                await _table.CreateIfNotExistsAsync();
                resultSegment = await _table.ExecuteQuerySegmentedAsync(query, null);
            }
            var json = resultSegment.FirstOrDefault()?.Value;
            _logger.LogDebug($"Read {category}/{key} => {json?.Length} bytes");

            if (json == null)
                return null;
            return JsonConvert.DeserializeObject<T>(json);
        }

        public async Task SetValueAsync<T>(string category, string key, T value) where T : class
        {
            var entity = new StorageEntity
            {
                PartitionKey = category,
                RowKey = key,
                Value = JsonConvert.SerializeObject(value)
            };
            try
            {
                _logger.LogDebug($"Setting {category}/{key} => {entity.Value?.Length} bytes");
                await _table.ExecuteAsync(TableOperation.InsertOrReplace(entity));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error setting {category}/{key} = {value}, trying to create table in case it didn't exist and retrying...");
                await _table.CreateIfNotExistsAsync();
                await _table.ExecuteAsync(TableOperation.InsertOrReplace(entity));
            }
        }

        public async Task<Dictionary<string, T>> GetValuesAsync<T>(string category) where T : class
        {
            var partitionFilter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, category);
            var query = new TableQuery<StorageEntity> { FilterString = partitionFilter, SelectColumns = new[] { "RowKey", "Value" } };
            TableQuerySegment<StorageEntity> resultSegment;

            var results = new Dictionary<string, T>();

            try
            {
                do
                {
                    resultSegment = await _table.ExecuteQuerySegmentedAsync(query, null);
                    foreach (var item in resultSegment)
                    {
                        results.Add(item.RowKey, JsonConvert.DeserializeObject<T>(item.Value));
                    }
                } while (resultSegment.ContinuationToken != null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error querying for {category}, trying to create table in case it didn't exist and retrying...");
                await _table.CreateIfNotExistsAsync();
            }

            _logger.LogDebug($"Read {category} => {results.Count} results");
            return results;
        }

        public async Task RemoveValueAsync(string category, string key)
        {
            var partitionFilter = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, category);
            var rowFilter = TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, key);
            var combinedFilter = TableQuery.CombineFilters(partitionFilter, TableOperators.And, rowFilter);
            var query = new TableQuery<StorageEntity> { FilterString = combinedFilter, TakeCount = 1, SelectColumns = new[] { "Value" } };
            TableQuerySegment<StorageEntity> resultSegment;
            try
            {
                _logger.LogDebug($"Fetching and deleting {category}/{key}");
                resultSegment = await _table.ExecuteQuerySegmentedAsync(query, null);
                var entity = resultSegment.FirstOrDefault();
                if (entity != null)
                {
                    await _table.ExecuteAsync(TableOperation.Delete(entity));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error deleting {category}/{key}, table might not exist or item is already deleted...");
                await _table.CreateIfNotExistsAsync();
            }
        }

        private class StorageEntity : TableEntity
        {
            public string Value { get; set; }
        }
    }
}
