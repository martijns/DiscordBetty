using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Betty.Bot.Services
{
    public interface IPrefixService
    {
        Task<string> GetPrefix(IGuild guild);
        Task SetPrefix(IGuild guild, string prefix);
    }

    public class PrefixService : IPrefixService
    {
        private readonly ILogger<PrefixService> _logger;
        private readonly IKeyValueStore _kvstore;
        private readonly IConfiguration _config;
        private readonly Dictionary<ulong, string> _prefixes = new Dictionary<ulong, string>();

        public PrefixService(ILogger<PrefixService> logger, IConfiguration config, IKeyValueStore kvstore)
        {
            _logger = logger;
            _config = config;
            _kvstore = kvstore;
        }

        public async Task<string> GetPrefix(IGuild guild)
        {
            // Should never happen, but w/e
            if (guild == null)
                return _config["DefaultPrefix"];

            // If we have the value cached, return it.
            if (_prefixes.ContainsKey(guild.Id))
            {
                return _prefixes[guild.Id];
            }

            // Check configuration for preferred prefix
            var configuredPrefix = await _kvstore.GetValueAsync<string>("config_prefix", guild.Id.ToString());
            lock (_prefixes)
            {
                if (!_prefixes.ContainsKey(guild.Id))
                {
                    // Cache the preferred prefix, or set the default prefix
                    _prefixes.Add(guild.Id, configuredPrefix ?? _config["DefaultPrefix"]);
                }
            }
            return _prefixes[guild.Id];
        }

        public async Task SetPrefix(IGuild guild, string prefix)
        {
            lock (_prefixes)
            {
                if (_prefixes.ContainsKey(guild.Id))
                    _prefixes[guild.Id] = prefix;
                else
                    _prefixes.Add(guild.Id, prefix);
            }
            await _kvstore.SetValueAsync("config_prefix", guild.Id.ToString(), prefix);
        }
    }
}
