#nullable enable
using System;

namespace Denis.TradingEngine.Core.Trading
{
    /// <summary>
    /// Trading phase klasifikacija po fazama dana (NY vreme).
    /// Koristi se za time-of-day profiling i analizu performansi.
    /// </summary>
    public static class TradingPhase
    {
        /// <summary>
        /// Faze dana u NY vremenu (ET/EST).
        /// </summary>
        public enum Phase
        {
            /// <summary>Pre-market: 04:00-09:30 ET</summary>
            PreRth,
            
            /// <summary>Open (prvi sat): 09:30-10:30 ET</summary>
            Open1H,
            
            /// <summary>Midday: 10:30-14:30 ET</summary>
            Midday,
            
            /// <summary>Power hour: 14:30-15:30 ET</summary>
            PowerHour,
            
            /// <summary>Close: 15:30-16:00 ET</summary>
            Close,
            
            /// <summary>After hours: 16:00-20:00 ET</summary>
            AfterHours,
            
            /// <summary>Van trading vremena: pre 04:00 ili posle 20:00 ET</summary>
            OffHours
        }

        /// <summary>
        /// Određuje trading fazu za dato UTC vreme.
        /// </summary>
        public static Phase GetPhase(DateTime utcNow)
        {
            // Konvertuj u NY vreme
            TimeZoneInfo nyTz;
            try
            {
                nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
            catch
            {
                nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }

            var nyLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nyTz);
            var timeOfDay = nyLocal.TimeOfDay;

            // Pre-market: 04:00-09:30 ET
            if (timeOfDay >= TimeSpan.FromHours(4) && timeOfDay < TimeSpan.FromHours(9.5))
                return Phase.PreRth;

            // Open (prvi sat): 09:30-10:30 ET
            if (timeOfDay >= TimeSpan.FromHours(9.5) && timeOfDay < TimeSpan.FromHours(10.5))
                return Phase.Open1H;

            // Midday: 10:30-14:30 ET
            if (timeOfDay >= TimeSpan.FromHours(10.5) && timeOfDay < TimeSpan.FromHours(14.5))
                return Phase.Midday;

            // Power hour: 14:30-15:30 ET
            if (timeOfDay >= TimeSpan.FromHours(14.5) && timeOfDay < TimeSpan.FromHours(15.5))
                return Phase.PowerHour;

            // Close: 15:30-16:00 ET
            if (timeOfDay >= TimeSpan.FromHours(15.5) && timeOfDay < TimeSpan.FromHours(16))
                return Phase.Close;

            // After hours: 16:00-20:00 ET
            if (timeOfDay >= TimeSpan.FromHours(16) && timeOfDay < TimeSpan.FromHours(20))
                return Phase.AfterHours;

            // Off hours: pre 04:00 ili posle 20:00 ET
            return Phase.OffHours;
        }

        /// <summary>
        /// Vraća string reprezentaciju faze (za DB/logove).
        /// </summary>
        public static string ToString(Phase phase)
        {
            return phase switch
            {
                Phase.PreRth => "preRTH",
                Phase.Open1H => "open_1h",
                Phase.Midday => "midday",
                Phase.PowerHour => "power_hour",
                Phase.Close => "close",
                Phase.AfterHours => "afterhours",
                Phase.OffHours => "off_hours",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Parsuje string u Phase enum.
        /// </summary>
        public static Phase Parse(string? phaseStr)
        {
            if (string.IsNullOrWhiteSpace(phaseStr))
                return Phase.OffHours;

            return phaseStr.ToLowerInvariant() switch
            {
                "prerth" or "pre_rth" => Phase.PreRth,
                "open1h" or "open_1h" => Phase.Open1H,
                "midday" => Phase.Midday,
                "powerhour" or "power_hour" => Phase.PowerHour,
                "close" => Phase.Close,
                "afterhours" or "after_hours" => Phase.AfterHours,
                "offhours" or "off_hours" => Phase.OffHours,
                _ => Phase.OffHours
            };
        }

        /// <summary>
        /// Vraća human-readable opis faze.
        /// </summary>
        public static string GetDescription(Phase phase)
        {
            return phase switch
            {
                Phase.PreRth => "Pre-market (04:00-09:30 ET)",
                Phase.Open1H => "Open first hour (09:30-10:30 ET)",
                Phase.Midday => "Midday (10:30-14:30 ET)",
                Phase.PowerHour => "Power hour (14:30-15:30 ET)",
                Phase.Close => "Close (15:30-16:00 ET)",
                Phase.AfterHours => "After hours (16:00-20:00 ET)",
                Phase.OffHours => "Off hours (outside trading window)",
                _ => "Unknown"
            };
        }
    }
}

