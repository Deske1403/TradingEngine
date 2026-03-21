#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Serilog;

namespace Denis.TradingEngine.Data
{
    /// <summary>
    /// Interfejs za pravljenje otvorene konekcije ka bazi.
    /// Posle ga koriste svi repozitorijumi.
    /// </summary>
    public interface IDbConnectionFactory
    {
        /// <summary>
        /// Vrati otvorenu PostgreSQL konekciju (caller je zadužen da je dispose-uje).
        /// </summary>
        Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Implementacija konekcione fabrike za PostgreSQL (i Timescale).
    /// </summary>
    public sealed class PgConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;
        private readonly ILogger _log;

        public PgConnectionFactory(string connectionString, ILogger? log = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

            _connectionString = connectionString;
            _log = log ?? Serilog.Log.ForContext<PgConnectionFactory>();
        }

        public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
        {
            var conn = new NpgsqlConnection(_connectionString);
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                _log.Debug("[DB] Opened PostgreSQL connection to {Host}", conn.Host);
                return conn;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DB] Failed to open PostgreSQL connection!");
                await conn.DisposeAsync();
                throw;
            }
        }
    }
}