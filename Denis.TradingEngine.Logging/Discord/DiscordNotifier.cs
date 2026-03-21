using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace Denis.TradingEngine.Logging.Discord
{
    public sealed class DiscordNotifier
    {
        private readonly HttpClient _httpClient;
        private readonly string _webhookUrl;
        private readonly ILogger _log;
        private readonly bool _enabled;

        public DiscordNotifier(string webhookUrl, ILogger? log = null)
        {
            _webhookUrl = webhookUrl ?? throw new ArgumentNullException(nameof(webhookUrl));
            _log = log ?? Log.ForContext<DiscordNotifier>();
            _enabled = !string.IsNullOrWhiteSpace(webhookUrl);
            
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public async Task NotifyBuyAsync(
            string symbol,
            decimal quantity,
            decimal price,
            decimal notional,
            string exchange,
            bool isPaper,
            CancellationToken ct = default)
        {
            if (!_enabled) return;

            try
            {
                var mode = isPaper ? "📝 PAPER" : "💰 REAL";
                var color = isPaper ? 0x9E9E9E : 0x4CAF50; // Gray for paper, Green for real

                var embed = new
                {
                    title = $"🟢 BUY {symbol}",
                    description = $"{mode} Trade Executed",
                    color = color,
                    fields = new[]
                    {
                        new { name = "Symbol", value = symbol, inline = true },
                        new { name = "Exchange", value = exchange, inline = true },
                        new { name = "Quantity", value = quantity.ToString("F6"), inline = true },
                        new { name = "Price", value = $"${price:F2}", inline = true },
                        new { name = "Notional", value = $"${notional:F2}", inline = true },
                        new { name = "Mode", value = mode, inline = true }
                    },
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                var payload = new { embeds = new[] { embed } };

                await SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DISCORD] Failed to send BUY notification for {Symbol}", symbol);
            }
        }

        public async Task NotifySellAsync(
            string symbol,
            decimal quantity,
            decimal price,
            decimal notional,
            decimal realizedPnl,
            string exchange,
            bool isPaper,
            string? exitReason = null,
            CancellationToken ct = default)
        {
            if (!_enabled) return;

            try
            {
                var mode = isPaper ? "📝 PAPER" : "💰 REAL";
                var pnlColor = realizedPnl >= 0 ? 0x4CAF50 : 0xF44336; // Green for profit, Red for loss
                var pnlEmoji = realizedPnl >= 0 ? "📈" : "📉";

                var fields = new List<object>
                {
                    new { name = "Symbol", value = symbol, inline = true },
                    new { name = "Exchange", value = exchange, inline = true },
                    new { name = "Quantity", value = quantity.ToString("F6"), inline = true },
                    new { name = "Price", value = $"${price:F2}", inline = true },
                    new { name = "Notional", value = $"${notional:F2}", inline = true },
                    new { name = "Mode", value = mode, inline = true },
                    new { name = $"{pnlEmoji} Realized PnL", value = $"${realizedPnl:F2}", inline = true }
                };

                if (!string.IsNullOrWhiteSpace(exitReason))
                {
                    fields.Add(new { name = "Exit Reason", value = exitReason, inline = true });
                }

                var embed = new
                {
                    title = $"🔴 SELL {symbol}",
                    description = $"{mode} Trade Executed",
                    color = pnlColor,
                    fields = fields.ToArray(),
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                var payload = new { embeds = new[] { embed } };

                await SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DISCORD] Failed to send SELL notification for {Symbol}", symbol);
            }
        }

        /// <summary>
        /// Šalje warning/alert (npr. IBKR connection lost).
        /// </summary>
        public async Task NotifyWarningAsync(
            string title,
            string description,
            string? details = null,
            CancellationToken ct = default)
        {
            if (!_enabled) return;

            try
            {
                var fields = new List<object>
                {
                    new { name = "Time (UTC)", value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), inline = true }
                };
                if (!string.IsNullOrWhiteSpace(details))
                {
                    fields.Add(new { name = "Details", value = details, inline = false });
                }

                var embed = new
                {
                    title,
                    description,
                    color = 0xFFA500, // orange
                    fields = fields.ToArray(),
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                var payload = new { embeds = new[] { embed } };

                await SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DISCORD] Failed to send warning notification: {Title}", title);
            }
        }

        public async Task NotifyDailyPnlAsync(
            DateTime date,
            decimal realizedPnl,
            decimal totalFees,
            int tradeCount,
            string exchange,
            CancellationToken ct = default)
        {
            if (!_enabled) return;

            try
            {
                var netPnl = realizedPnl - totalFees;
                var pnlColor = netPnl >= 0 ? 0x4CAF50 : 0xF44336;
                var pnlEmoji = netPnl >= 0 ? "📈" : "📉";

                var embed = new
                {
                    title = $"📊 Daily PnL - {exchange}",
                    description = $"Summary for {date:yyyy-MM-dd}",
                    color = pnlColor,
                    fields = new[]
                    {
                        new { name = "Date", value = date.ToString("yyyy-MM-dd"), inline = true },
                        new { name = "Exchange", value = exchange, inline = true },
                        new { name = $"{pnlEmoji} Realized PnL", value = $"${realizedPnl:F2}", inline = true },
                        new { name = "Total Fees", value = $"${totalFees:F2}", inline = true },
                        new { name = "Net PnL", value = $"${netPnl:F2}", inline = true },
                        new { name = "Trade Count", value = tradeCount.ToString(), inline = true }
                    },
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                var payload = new { embeds = new[] { embed } };

                await SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DISCORD] Failed to send Daily PnL notification for {Date}", date);
            }
        }

        private async Task SendAsync(object payload, CancellationToken ct)
        {
            if (!_enabled) return;

            try
            {
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_webhookUrl, content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _log.Warning("[DISCORD] Webhook returned {Status}: {Body}", response.StatusCode, errorBody);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DISCORD] Failed to send webhook");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}

