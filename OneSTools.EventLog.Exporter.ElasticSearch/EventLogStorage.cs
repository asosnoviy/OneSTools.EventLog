﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OneSTools.EventLog.Exporter.Core;
using System.Linq;
using System.Data;
using Elasticsearch.Net;
using Nest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace OneSTools.EventLog.Exporter.ElasticSearch
{
    public class EventLogStorage<T> : IEventLogStorage<T>, IDisposable where T : class, IEventLogItem, new()
    {
        public static int DEFAULT_MAXIMUM_RETRIES = 2;
        public static int DEFAULT_MAX_RETRY_TIMEOUT_SEC = 30;

        private readonly ILogger<EventLogStorage<T>> _logger;
        private readonly List<ElasticSearchNode> _nodes;
        private ElasticSearchNode _currentNode;
        private readonly string _eventLogItemsIndex;
        private readonly int _maximumRetries;
        private readonly TimeSpan _maxRetryTimeout;
        private readonly string _separation;
        private ElasticClient _client;

        public EventLogStorage(ILogger<EventLogStorage<T>> logger, List<ElasticSearchNode> nodes, string index, string separation, int maximumRetries, TimeSpan maxRetryTimeout)
        {
            _logger = logger;

            _nodes = nodes;

            if (_nodes.Count == 0)
                throw new Exception("ElasticSearch hosts is not specified");

            _eventLogItemsIndex = index;
            if (_eventLogItemsIndex == string.Empty)
                throw new Exception("ElasticSearch index name is not specified");

            _separation = separation;
            _maximumRetries = maximumRetries;
            _maxRetryTimeout = maxRetryTimeout;
        }

        public EventLogStorage(ILogger<EventLogStorage<T>> logger, IConfiguration configuration) : this(
            logger,
            configuration.GetSection("ElasticSearch:Nodes").Get<List<ElasticSearchNode>>(),
            configuration.GetValue("ElasticSearch:Index", ""),
            configuration.GetValue("ElasticSearch:Separation", "H"),
            configuration.GetValue("ElasticSearch:MaximumRetries", DEFAULT_MAXIMUM_RETRIES),
            TimeSpan.FromSeconds(configuration.GetValue("ElasticSearch:MaxRetryTimeout", DEFAULT_MAX_RETRY_TIMEOUT_SEC))
            )
        {

        }

        private async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connected = await SwitchToNextNodeAsync(cancellationToken);

                if (connected)
                {
                    await CreateIndexTemplateAsync(cancellationToken);

                    break;
                }
            }
        }

        private async Task CreateIndexTemplateAsync(CancellationToken cancellationToken = default)
        {
            var indexTemplateName = "oneslogs";

            var getItResponse = await _client.LowLevel.DoRequestAsync<StringResponse>(HttpMethod.GET, $"_index_template/{indexTemplateName}", cancellationToken);

            // if it exists then skip creating
            if (!getItResponse.Success)
                throw getItResponse.OriginalException;
            else if (getItResponse.HttpStatusCode != 404)
                return;

            var cmd =
                @"{
                    ""index_patterns"": ""*-el-*"",
                    ""template"": {
                        ""settings"": {
                            ""index.codec"": ""best_compression""
                        },
                        ""mappings"": {
                            ""properties"": {
                                ""dateTime"": { ""type"": ""date"" }, 
                                ""severity"": { ""type"": ""keyword"" },
                                ""server"": { ""type"": ""keyword"" },
                                ""fileName"": { ""type"": ""keyword"" },
                                ""metadata"": { ""type"": ""keyword"" },
                                ""data"": { ""type"": ""text"" },
                                ""transactionDateTime"": { ""type"": ""date"" },
                                ""transactionStatus"": { ""type"": ""keyword"" },
                                ""session"": { ""type"": ""long"" },
                                ""mainPort"": { ""type"": ""integer"" },
                                ""transactionNumber"": { ""type"": ""long"" },
                                ""addPort"": { ""type"": ""integer"" },
                                ""computer"": { ""type"": ""keyword"" },
                                ""application"": { ""type"": ""keyword"" },
                                ""endPosition"": { ""type"": ""long"" },
                                ""userUuid"": { ""type"": ""keyword"" },
                                ""comment"": { ""type"": ""text"" },
                                ""connection"": { ""type"": ""long"" },
                                ""event"": { ""type"": ""keyword"" },
                                ""metadataUuid"": { ""type"": ""keyword"" },
                                ""dataPresentation"": { ""type"": ""text"" },
                                ""user"": { ""type"": ""keyword"" }
                            }
                        }
                    }
                }";

            var response = await _client.LowLevel.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"_index_template/{indexTemplateName}", cancellationToken, PostData.String(cmd));

            if (!response.Success)
                throw response.OriginalException;
        }

        private async Task<bool> SwitchToNextNodeAsync(CancellationToken cancellationToken = default)
        {
            if (_currentNode == null)
                _currentNode = _nodes[0];
            else
            {
                var currentIndex = _nodes.IndexOf(_currentNode);

                if (currentIndex == _nodes.Count - 1)
                    _currentNode = _nodes[0];
                else
                    _currentNode = _nodes[currentIndex + 1];
            }

            var uri = new Uri(_currentNode.Host);

            var settings = new ConnectionSettings(uri);
            settings.EnableHttpCompression();
            settings.MaximumRetries(_maximumRetries);
            settings.MaxRetryTimeout(_maxRetryTimeout);

            switch (_currentNode.AuthenticationType)
            {
                case AuthenticationType.Basic:
                    settings.BasicAuthentication(_currentNode.UserName, _currentNode.Password);
                    break;
                case AuthenticationType.ApiKey:
                    settings.ApiKeyAuthentication(_currentNode.Id, _currentNode.ApiKey);
                    break;
                default:
                    break;
            }

            _client = new ElasticClient(settings);

            _logger.LogInformation($"Trying to connect to {uri} ({_eventLogItemsIndex})");

            var response = await _client.PingAsync(pd => pd, cancellationToken);

            if (!(response.OriginalException is TaskCanceledException))
            {
                if (!response.IsValid)
                    _logger.LogWarning($"Failed to connect to {uri} ({_eventLogItemsIndex}): {response.OriginalException.Message}");
                else
                    _logger.LogInformation($"Successfully connected to {uri} ({_eventLogItemsIndex})");
            }

            return response.IsValid;
        }

        public async Task<(string FileName, long EndPosition, long LgfEndPosition)> ReadEventLogPositionAsync(CancellationToken cancellationToken = default)
        {
            if (_client is null)
                await ConnectAsync(cancellationToken);

            while (true)
            {
                var response = await _client.SearchAsync<T>(sd => sd
                        .Index($"{_eventLogItemsIndex}-*")
                        .Sort(ss =>
                            ss.Descending(c => c.DateTime))
                        .Size(1)
                    , cancellationToken);

                if (response.IsValid)
                {
                    var item = response.Documents.FirstOrDefault();

                    if (item is null)
                    {
                        _logger.LogInformation($"There's no log items in the database ({_eventLogItemsIndex}), first found log file will be read from 0 position");

                        return ("", 0, 0);
                    }
                    else
                    {
                        _logger.LogInformation($"File {item.FileName} will be read from {item.EndPosition} position, LGF file will be read from {item.LgfEndPosition} position ({_eventLogItemsIndex})");

                        return (item.FileName, item.EndPosition, item.LgfEndPosition);
                    }
                }
                else
                {
                    if (response.OriginalException is TaskCanceledException)
                        throw response.OriginalException;

                    _logger.LogError($"Failed to get last file's position ({_eventLogItemsIndex}): {response.OriginalException.Message}");

                    var currentNodeHost = _currentNode.Host;

                    await ConnectAsync(cancellationToken);

                    // If it's the same node then wait while MaxRetryTimeout occurs, otherwise it'll be a too often request's loop
                    if (_currentNode.Host.Equals(currentNodeHost))
                        await Task.Delay(_maxRetryTimeout);
                }
            }
        }

        private List<(string IndexName, List<T> Entities)> GetGroupedData(List<T> entities)
        {
            var data = new List<(string IndexName, List<T> Entities)>();

            switch (_separation)
            {
                case "H":
                    var groups = entities.GroupBy(c => c.DateTime.ToString("yyyyMMddhh")).OrderBy(c => c.Key);
                    foreach (IGrouping<string, T> item in groups)
                        data.Add(($"{_eventLogItemsIndex}-{item.Key}", item.ToList()));
                    break;
                case "D":
                    groups = entities.GroupBy(c => c.DateTime.ToString("yyyyMMdd")).OrderBy(c => c.Key);
                    foreach (IGrouping<string, T> item in groups)
                        data.Add(($"{_eventLogItemsIndex}-{item.Key}", item.ToList()));
                    break;
                case "M":
                    groups = entities.GroupBy(c => c.DateTime.ToString("yyyyMM")).OrderBy(c => c.Key);
                    foreach (IGrouping<string, T> item in groups)
                        data.Add(($"{_eventLogItemsIndex}-{item.Key}", item.ToList()));
                    break;
                default:
                    data.Add(($"{_eventLogItemsIndex}-all", entities));
                    break;
            }

            return data;
        }

        public async Task WriteEventLogDataAsync(List<T> entities, CancellationToken cancellationToken = default)
        {
            if (_client is null)
                await ConnectAsync(cancellationToken);

            var data = GetGroupedData(entities);

            for (int i = 0; i < data.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var item = data[i];

                var responseItems = await _client.IndexManyAsync(item.Entities, item.IndexName, cancellationToken);

                if (!responseItems.ApiCall.Success)
                {
                    if (responseItems.OriginalException is TaskCanceledException)
                        throw responseItems.OriginalException;

                    if (responseItems.Errors)
                    {
                        foreach (var itemWithError in responseItems.ItemsWithErrors)
                        {
                            _logger.LogError($"Failed to index document {itemWithError.Id} in {item.IndexName}: {itemWithError.Error}");
                        }

                        throw new Exception($"Failed to write items to {item.IndexName}: {responseItems.OriginalException.Message}");
                    }
                    else
                    {
                        _logger.LogError($"Failed to write items to {item.IndexName}: {responseItems.OriginalException.Message}");

                        await ConnectAsync(cancellationToken);

                        i--;
                    }
                }
                else
                {
                    if (responseItems.Errors)
                    {
                        foreach (var itemWithError in responseItems.ItemsWithErrors)
                        {
                            _logger.LogError($"Failed to index document {itemWithError.Id} in {item.IndexName}: {itemWithError.Error}");
                        }

                        throw new Exception($"Failed to write items to {item.IndexName}: {responseItems.OriginalException.Message}");
                    }
                    else
                        _logger.LogDebug($"{item.Entities.Count} items were being written to {item.IndexName}");
                }
            }
        }

        public void Dispose()
        {
            
        }
    }
}
