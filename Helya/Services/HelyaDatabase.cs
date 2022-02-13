﻿using System.Linq;
using Helya.Commons;
using Helya.Database;
using Helya.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Extensions.Logging;

namespace Helya.Services;

public class HelyaDatabase
{
    private readonly DbContextOptions<HelyaDatabaseContext> _options;

    public HelyaDatabase()
    {
        var optionsBuilder = new DbContextOptionsBuilder<HelyaDatabaseContext>();
        var connStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = "database.db",
            Password = JsonManager<BotConfiguration>.Read().Credentials.DbPassword
        };

        optionsBuilder
#if DEBUG
            .EnableSensitiveDataLogging()
            .EnableDetailedErrors()
#endif
            .UseLoggerFactory(new SerilogLoggerFactory(Log.Logger))
            .UseSqlite(connStringBuilder.ToString());
        
        _options = optionsBuilder.Options;
        Setup();
    }

    private void Setup()
    {
        using var context = new HelyaDatabaseContext(_options);
        while (context.Database.GetPendingMigrations().Any())
        {
            var migrationContext = new HelyaDatabaseContext(_options);
            migrationContext.Database.Migrate();
            migrationContext.SaveChanges();
            migrationContext.Dispose();
        }

        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
        context.SaveChanges();
    }

    public HelyaDatabaseContext GetContext()
    {
        var context = new HelyaDatabaseContext(_options);
        context.Database.SetCommandTimeout(30);
        var conn = context.Database.GetDbConnection();
        conn.Open();

        using var com = conn.CreateCommand();
        // https://phiresky.github.io/blog/2020/sqlite-performance-tuning/
        com.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF";
        com.ExecuteNonQuery();

        return context;
    }
}