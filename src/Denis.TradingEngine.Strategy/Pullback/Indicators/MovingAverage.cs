#nullable enable
using System.Collections.Generic;

namespace Denis.TradingEngine.Strategy.Pullback.Indicators
{
    /// <summary>
    /// Jednostavan klizni prosek (SMA) bez teških zavisnosti.
    /// </summary>
    public sealed class MovingAverage
    {
        private readonly int _window;
        private readonly Queue<decimal> _values = new();
        private decimal _sum = 0m;

        public MovingAverage(int window)
        {
            _window = window > 0 ? window : 1;
        }

        public bool IsReady => _values.Count == _window;

        public decimal? Value => IsReady ? _sum / _values.Count : null;

        public void Add(decimal v)
        {
            _values.Enqueue(v);
            _sum += v;

            if (_values.Count > _window)
            {
                _sum -= _values.Dequeue();
            }
        }
    }
}