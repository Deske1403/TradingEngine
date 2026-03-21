#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class ServiceHeartbeatRepository
    {
        private readonly Denis.TradingEngine.Data.IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public ServiceHeartbeatRepository(Denis.TradingEngine.Data.IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory;
            _log = log ?? Serilog.Log.ForContext<ServiceHeartbeatRepository>();
        }

        public async Task InsertAsync(string serviceName, string? note = null, CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO service_heartbeat (service_name, started_at, host, note)
                VALUES (@ServiceName, @StartedAt, @Host, @Note);";

            var record = new
            {
                ServiceName = serviceName,
                StartedAt = DateTime.UtcNow,
                Host = Environment.MachineName,
                Note = note
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(sql, record);
            _log.Information("[DB] Heartbeat inserted for {Svc} at {Utc}", serviceName, record.StartedAt);
        }
    }
}