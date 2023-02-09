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
using Sentry;

namespace Betty.Bot
{
    /// <summary>
    /// Based upon: https://github.com/gngrninja/csharpi/tree/03-logging
    /// </summary>
    class Program
    {
        private static bool _InterruptRequested = false;

        private FileSystemWatcher _fsw;
        private PhysicalFilesWatcher _pfw;

        static void Main(string[] args)
        {
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        public async Task MainAsync()
        {
            // Setup configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(path: "config.json")
                .Build();

            // Setup logger
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Sentry(o =>
                {
                    o.MinimumBreadcrumbLevel = Serilog.Events.LogEventLevel.Debug;
                    o.MinimumEventLevel = Serilog.Events.LogEventLevel.Warning;
                    o.Dsn = config["SentryDSN"];
                    o.InitializeSdk = true;
                })
                .WriteTo.File("logs/betty.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3} {SourceContext:l}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            // Setup filesystemwatcher
            var baseDir = AppContext.BaseDirectory;
            _fsw = new FileSystemWatcher(baseDir, "*.*");
            _pfw = new PhysicalFilesWatcher(baseDir, _fsw, true);
            Log.Information($"Watching {baseDir} for changes...");
            static async void handler(object src, FileSystemEventArgs args)
            {
                if (args.Name.ToLowerInvariant().EndsWith(".dll")
                    || args.Name.ToLowerInvariant().EndsWith(".exe"))
                {
                    Log.Information($"File {args.Name} {args.ChangeType}, restarting app in 10 seconds");
                    await Task.Delay(10000);
                    _InterruptRequested = true;
                }
            }
            _fsw.Changed += handler;
            _fsw.Created += handler;
            _fsw.Deleted += handler;
            _fsw.IncludeSubdirectories = false;
            _fsw.EnableRaisingEvents = true;

            // Setup services
            var serviceProvider = new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddSingleton<DiscordSocketClient>((sp) => {
                    var config = new DiscordSocketConfig
                    {
                        GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers | GatewayIntents.MessageContent
                    };
                    return new DiscordSocketClient(config);
                })
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
                    return Task.CompletedTask;
                };
                client.JoinedGuild += async (guild) =>
                {
                    var channel = guild.SystemChannel ?? guild.DefaultChannel ?? guild.TextChannels.FirstOrDefault();
                    if (channel == null)
                    {
                        Log.Information($"Guild {guild.Name} ({guild.Id}) has no text channels. Cannot send welcome message.");
                        return;
                    }
                    await channel.SendMessageAsync($"Hi! I'm {client.CurrentUser.Mention}, your friendly neighborhood bot. To interact with me, use \"{client.CurrentUser.Mention} help\" or \"{await prefix.GetPrefix(guild)}help\". Don't worry if my prefix conflicts with another bot, you can change it to your liking!").ConfigureAwait(false);
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
    }
}
