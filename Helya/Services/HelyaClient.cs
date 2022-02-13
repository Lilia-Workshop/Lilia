﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Interactivity;
using DSharpPlus.Interactivity.Enums;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.EventArgs;
using Helya.Commons;
using Helya.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OsuSharp;
using OsuSharp.Extensions;
using Serilog;

namespace Helya.Services;

public class HelyaClient
{
    public BotConfiguration BotConfiguration;
    public CancellationTokenSource Cts;
    public HelyaDatabase Database;
    public List<DiscordGuild> JoinedGuilds = new();
    public DateTime StartTime;

    public const Permissions RequiredPermissions = Permissions.ViewAuditLog | Permissions.ManageRoles |
                                                   Permissions.ManageChannels | Permissions.KickMembers |
                                                   Permissions.BanMembers | Permissions.AccessChannels |
                                                   Permissions.ModerateMembers | Permissions.SendMessages |
                                                   Permissions.SendMessagesInThreads | Permissions.EmbedLinks |
                                                   Permissions.AttachFiles | Permissions.ReadMessageHistory |
                                                   Permissions.UseExternalEmojis | Permissions.UseExternalStickers |
                                                   Permissions.AddReactions | Permissions.UseApplicationCommands |
                                                   Permissions.UseVoice | Permissions.Speak |
                                                   Permissions.UseVoiceDetection | Permissions.StartEmbeddedActivities;

    public async Task Run()
    {
#if DEBUG
        Log.Logger.Fatal("Unless you are testing the code, you should NOT see this on production");
        Log.Logger.Fatal("Consider using \"-c Release\" when running/building the code");
#endif
        Log.Logger.Information("Loading configurations");
        BotConfiguration = JsonManager<BotConfiguration>.Read();

        Cts = new CancellationTokenSource();

        var client = new DiscordClient(new DiscordConfiguration
        {
            Token = BotConfiguration.Credentials.DiscordToken,
            TokenType = TokenType.Bot,
            LoggerFactory = new LoggerFactory().AddSerilog()
        });

        Log.Logger.Information("Setting up databases");
        Database = new HelyaDatabase();

        var services = new ServiceCollection()
            .AddLogging(x => x.AddSerilog())
            .AddDefaultSerializer()
            .AddDefaultRequestHandler()
            .AddSingleton(Database)
            .AddOsuSharp(x => x.Configuration = new OsuClientConfiguration
            {
                ModFormatSeparator = string.Empty,
                ClientId = BotConfiguration.Credentials.Osu.ClientId,
                ClientSecret = BotConfiguration.Credentials.Osu.ClientSecret
            })
            .AddSingleton(this)
            .BuildServiceProvider();

        SlashCommandsExtension slash = client.UseSlashCommands(new SlashCommandsConfiguration
        {
            Services = services
        });

        client.UseInteractivity(new InteractivityConfiguration
        {
            AckPaginationButtons = true,
            ResponseBehavior = InteractionResponseBehavior.Ack,
            Timeout = TimeSpan.FromSeconds(30)
        });

        if (BotConfiguration.Client.PrivateGuildIds.Any())
            BotConfiguration.Client.PrivateGuildIds.ForEach(guildId =>
            {
                Log.Logger.Warning($"Registering slash commands for private guild with ID \"{guildId}\"");
                slash.RegisterCommands(Assembly.GetExecutingAssembly(), guildId);
            });

        if (BotConfiguration.Client.SlashCommandsForGlobal)
        {
            Log.Logger.Warning("Registering slash commands in global scope");
            slash.RegisterCommands(Assembly.GetExecutingAssembly());
        }
        
        client.Ready += OnReady;
        client.GuildAvailable += OnGuildAvailable;
        client.GuildUnavailable += OnGuildUnavailable;
        client.GuildCreated += OnGuildAvailable;
        client.GuildDeleted += OnGuildUnavailable;
        client.ClientErrored += OnClientErrored;

        slash.SlashCommandErrored += OnSlashCommandErrored;
        
        Console.CancelKeyPress += (_, _) => this.Cts.Cancel();

        Log.Logger.Information("Setting client activity");

        #region Activity Setup

        var activityData = BotConfiguration.Client.Activity;

        if (!Enum.TryParse(activityData.Type, out ActivityType activityType))
        {
            Log.Logger.Warning($"Can not convert \"{activityData.Type}\" to a valid activity type, using \"Playing\"");
            Log.Logger.Information("Valid options are: ListeningTo, Competing, Playing, Watching");
            activityType = ActivityType.Playing;
        }

        if (!Enum.TryParse(activityData.Status, out UserStatus userStatus))
        {
            Log.Logger.Warning($"Can not convert \"{activityData.Status}\" to a valid status, using \"Online\"");
            Log.Logger.Information("Valid options are: Online, Invisible, Idle, DoNotDisturb");
            userStatus = UserStatus.Online;
        }
        
        var activity = new DiscordActivity
        {
            ActivityType = activityType,
            Name = activityData.Name
        };

        #endregion

        await client.ConnectAsync(activity, userStatus);

        while (!Cts.IsCancellationRequested) await Task.Delay(200);

        await client.DisconnectAsync();
        await Database.GetContext().DisposeAsync();
    }

    private Task OnReady(DiscordClient sender, ReadyEventArgs e)
    {
        Log.Logger.Information($"Client is ready (as Discord User: {sender.CurrentUser.Username}#{sender.CurrentUser.Discriminator})");
        StartTime = DateTime.Now;
        return Task.CompletedTask;
    }

    private Task OnGuildAvailable(DiscordClient _, GuildCreateEventArgs e)
    {
        Log.Logger.Debug($"Guild cache added: {e.Guild.Name} (ID: {e.Guild.Id})");
        JoinedGuilds.Add(e.Guild);
        return Task.CompletedTask;
    }

    private Task OnGuildUnavailable(DiscordClient _, GuildDeleteEventArgs e)
    {
        Log.Logger.Debug($"Guild cache removed: {e.Guild.Name} (ID: {e.Guild.Id})");
        JoinedGuilds.Remove(e.Guild);
        return Task.CompletedTask;
    }

    private Task OnClientErrored(DiscordClient _, ClientErrorEventArgs e)
    {
        Log.Logger.Fatal(e.Exception, "An exception occured when running the bot");
        throw e.Exception;
    }

    private Task OnSlashCommandErrored(SlashCommandsExtension _, SlashCommandErrorEventArgs e)
    {
        Log.Logger.Fatal(e.Exception, "An exception occured when executing a slash command");
        throw e.Exception;
    }
}