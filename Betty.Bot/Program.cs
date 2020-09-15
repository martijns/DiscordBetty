using System;
using Discord;
using Discord.Net;
using Discord.Commands;
using Discord.WebSocket;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Serilog;
using Betty.Bot.Services;
using System.Threading;
using System.IO;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.FileProviders;
using Microsoft.Azure.Cosmos.Table;
using Betty.Bot.Extensions;
using TwitchLib.Api;
using Discord.API;
using Azure.Storage.Blobs;

namespace Betty.Bot
{
    /// <summary>
    /// Based upon: https://github.com/gngrninja/csharpi/tree/03-logging
    /// </summary>
    class Program
    {
        private static bool _InterruptRequested = false;

        static void Main(string[] args)
        {
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        public async Task MainAsync()
        {
            // Setup logger
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/betty.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3} {SourceContext:l}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            // Setup builder
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(path: "config.json")
                .Build();

            // Setup filesystemwatcher
            var fsw = new FileSystemWatcher(AppContext.BaseDirectory, "*.*");
            var pfw = new PhysicalFilesWatcher(AppContext.BaseDirectory, fsw, true);
            Log.Information($"Watching {AppContext.BaseDirectory} for changes");
            static async void handler(object src, FileSystemEventArgs args)
            {
                if (args.Name.ToLowerInvariant().EndsWith(".dll")
                    || args.Name.ToLowerInvariant().EndsWith(".exe"))
                {
                    Log.Warning($"File {args.Name} {args.ChangeType}, restarting app in 10 seconds");
                    await Task.Delay(10000);
                    _InterruptRequested = true;
                }
            }
            fsw.Changed += handler;
            fsw.Created += handler;
            fsw.Deleted += handler;
            fsw.EnableRaisingEvents = true;

            // Setup services
            var serviceProvider = new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddSingleton<DiscordSocketClient>()
                .AddSingleton<CommandService>()
                .AddSingleton<CommandHandler>()
                .AddSingleton<LoggingService>()
                .AddSingleton<CloudTableClient>(sp =>
                {
                    var connstr = sp.GetRequiredService<IConfiguration>()["AzureStorageAccount"];
                    var csa = CloudStorageAccount.Parse(connstr);
                    return csa.CreateCloudTableClient();
                })
                .AddSingleton<BlobServiceClient>(sp =>
                {
                    var connstr = sp.GetRequiredService<IConfiguration>()["AzureStorageAccount"];
                    var blobServiceClient = new BlobServiceClient(connstr);
                    return blobServiceClient;
                })
                .AddSingleton<IKeyValueStore, StorageKeyValueStore>()
                .AddSingleton<IImageHosting, AzureStorageImageHosting>()
                .AddSingleton<TwitchAPI>(sp =>
                {
                    var api = new TwitchAPI();
                    api.Settings.ClientId = sp.GetRequiredService<IConfiguration>()["TwitchClientId"];
                    api.Settings.Secret = sp.GetRequiredService<IConfiguration>()["TwitchClientSecret"];
                    return api;
                })
                //.AddSingleton<ITwitchService, TwitchService>()
                .AddSingleton<IPrefixService, PrefixService>()
                .AddLogging(c => c.AddSerilog())
                .Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Debug)
                .BuildServiceProvider();

            using (serviceProvider)
            {
                // Initialize logging
                serviceProvider.GetRequiredService<LoggingService>();
                var prefix = serviceProvider.GetRequiredService<IPrefixService>();

                // Login to discord
                var client = serviceProvider.GetRequiredService<DiscordSocketClient>();
                client.Ready += () =>
                {
                    PrecacheUsersAndTextChannels(client).Forget(ex => Log.Warning(ex, $"Error preloading users and text channels"));
                    return Task.CompletedTask;
                };
                client.JoinedGuild += async (guild) =>
                {
                    await guild.DefaultChannel.SendMessageAsync($"Hi! I'm {client.CurrentUser.Mention}, your friendly neighborhood bot. To interact with me, use \"{client.CurrentUser.Mention} help\" or \"{await prefix.GetPrefix(guild)}help\". Don't worry if my prefix conflicts with another bot, you can change it to your liking!").ConfigureAwait(false);
                };
                await client.LoginAsync(Discord.TokenType.Bot, config["DiscordToken"]);
                await client.StartAsync();
                await client.SetGameAsync("Mention me :)");

                // Setup the command handler
                await serviceProvider.GetRequiredService<CommandHandler>().InitializeAsync();

                // Keep bot running indefinitely
                while (!_InterruptRequested)
                {
                    await Task.Delay(1000);
                }

                Log.Information($"Interrupt requested, shutting down");
                Log.CloseAndFlush();
            }
        }

        private Task PrecacheUsersAndTextChannels(DiscordSocketClient client)
        {
            //foreach (var guild in client.Guilds)
            //{
            //    Log.Information($"{guild.Name} => GetTextChannelsAsync");
            //    await ((IGuild)guild).GetTextChannelsAsync(CacheMode.AllowDownload);
            //    Log.Information($"{guild.Name} => DownloadUsersAsync");
            //    await guild.DownloadUsersAsync();
            //    Log.Information($"{guild.Name} => Done");
            //}
            return Task.CompletedTask;
        }
    }
}
