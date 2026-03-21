using Denis.TradingEngine.Core.Interfaces;


namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class IbkrRealBuyingPowerProvider : IBuyingPowerProvider
    {
        private readonly RealIbkrClient _client;
        private readonly string _accountId;

        public IbkrRealBuyingPowerProvider(RealIbkrClient client, string accountId)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _accountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
        }

        public async Task<decimal> GetBuyingPowerUsdAsync(CancellationToken ct)
        {
            var v = await _client.GetAvailableFundsUsdAsync(_accountId, ct);
            return v ?? 0m;
        }
    }
}