#nullable enable
using System.Collections.Generic;

namespace Denis.TradingEngine.Strategy.Pullback.Indicators
{
    /// <summary>
    /// Prosta procena nagiba: čuva N vrednosti i vraća razliku (poslednja - prva) / N.
    /// Nije linearna regresija, ali je brza i stabilna za našu upotrebu.
    /// </summary>
    public sealed class TrendSlope
    {
        private readonly int _window;
        private readonly Queue<decimal> _buffer = new();
        public TrendSlope(int window) 
        {
            _window = window > 1 ? window : 2;
        }
        public void Add(decimal v)
        {
            _buffer.Enqueue(v);
            if (_buffer.Count > _window)
                _buffer.Dequeue();
        }
        public bool IsReady => _buffer.Count == _window;
        /// <summary>
        /// Pozitivan rezultat => rastući trend (grubo).
        /// Negativan => opadajući.
        /// </summary>
        public decimal? Slope
        {
            get
            {
                if (!IsReady) return null;
                var first = default(decimal);
                var i = 0;
                foreach (var x in _buffer)
                {
                    if (i == 0) first = x;
                    i++;
                }
                var last = 0m;
                foreach (var x in _buffer)
                    last = x;

                var n = _buffer.Count;
                return (last - first) / n;
            }
        }
    }
}