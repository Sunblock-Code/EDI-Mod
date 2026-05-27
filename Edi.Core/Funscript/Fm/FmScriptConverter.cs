using System;
using System.Collections.Generic;
using System.Linq;
using Edi.Core.Funscript.FileJson;

namespace Edi.Core.Funscript.Fm
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Rotary fuck-machine script converter.
    //
    //  A C# port of the OFS "FM script converter" extension by Rriik
    //  (https://github.com/Rriik/OFS-FM-script-converter).
    //
    //  Penetrative/rotary fuck machines can't be position-controlled like a stroker —
    //  they're SPEED/POWER controlled. Playing a normal stroke funscript on one produces
    //  dangerous, uncontrollable power bursts. This converter analyses a script's peaks
    //  and troughs, measures each thrust's CYCLE DURATION, maps that to the machine's RPM,
    //  and emits a new script whose "positions" are actually POWER LEVELS (0-100).
    //
    //  Unit note vs. the original Lua: OFS stores action.at in SECONDS, EDI stores it in
    //  MILLISECONDS, so RPM = 60000 / durationMs (not 60 / durationSec) and the drop-off
    //  offset is used directly in ms.
    // ─────────────────────────────────────────────────────────────────────────────

    // A rotary device's RPM range. The controller is assumed linear: minRPM at 1% power,
    // maxRPM at 100% power (this is how the OFS tool models it).
    public class FmDeviceProfile
    {
        public string Name { get; set; } = "Generic device";
        public double MaxRPM { get; set; } = 100.0;   // RPM at 100% power
        public double MinRPM { get; set; } = 1.0;     // RPM at 1% power

        public FmDeviceProfile Clone() => new FmDeviceProfile { Name = Name, MaxRPM = MaxRPM, MinRPM = MinRPM };
    }

    // Conversion options — mirror the OFS extension's GUI toggles.
    public class FmConvertOptions
    {
        public bool RecordPowerOnSinglePeaks { get; set; } = true;
        public bool RecordPowerOnSingleTroughs { get; set; } = true;
        public bool RecordPowerOnPeakSeries { get; set; } = true;
        public bool RecordPowerOnTroughSeries { get; set; } = true;
        public bool UsePowerDropoff { get; set; } = true;
        public int PowerDropoffTimeOffsetMs { get; set; } = 100;   // enter/exit offset for drop-off (ms)
        public bool OverrideStepSize { get; set; } = false;
        public int PowerLevelStepSize { get; set; } = 1;           // quantise power levels to this step
        public bool Ignore0PosSeries { get; set; } = true;         // leave 0-position runs as 0
    }

    public static class FmScriptConverter
    {
        private enum Slope { Rising = 1, Neutral = 0, Falling = -1 }

        // ───────── public API ─────────

        // Converts a list of stroke actions (pos 0-100, at in ms) into rotary power-level
        // actions. Input is not mutated; a brand-new action list is returned.
        public static List<FunScriptAction> Convert(
            IEnumerable<FunScriptAction> input, FmDeviceProfile profile, FmConvertOptions opt)
        {
            if (input == null) return new List<FunScriptAction>();
            profile ??= new FmDeviceProfile();
            opt ??= new FmConvertOptions();

            // Work on a time-sorted snapshot.
            var actions = input.OrderBy(a => a.at).ToList();
            var device = new List<FunScriptAction>();
            int n = actions.Count;
            if (n == 0) return device;

            GetPeaksTroughs(actions, out var peaks, out var troughs);
            var peakSet = new HashSet<long>(peaks.Select(a => a.at));
            var troughSet = new HashSet<long>(troughs.Select(a => a.at));

            FunScriptAction prevPeak = null, prevTrough = null;
            bool peakSeries = false, troughSeries = false;
            FunScriptAction peakSeriesFirst = null, troughSeriesFirst = null;

            const double HUGE = 1.0e10; // duration fallback → RPM ≈ 0 → power clamps to 0

            for (int idx = 0; idx < n; idx++)
            {
                var action = actions[idx];
                bool nextIsPeak = idx < n - 1 && peakSet.Contains(actions[idx + 1].at);
                bool nextIsTrough = idx < n - 1 && troughSet.Contains(actions[idx + 1].at);

                if (peakSet.Contains(action.at))
                {
                    double peakDuration = HUGE;
                    if (peakSeries)
                    {
                        if ((idx < n - 1 && !nextIsPeak) || idx == n - 1)
                        {
                            peakSeries = false; // end of a peak series
                            if (action.pos == 0 && opt.Ignore0PosSeries)
                            {
                                device.Add(New(action.at, 0));
                            }
                            else
                            {
                                if (opt.RecordPowerOnPeakSeries)
                                {
                                    var nextPeak = FindNextInSet(actions, idx, peakSet);
                                    if (nextPeak != null) peakDuration = Duration(action, nextPeak);
                                    device.Add(New(action.at, PowerLevel(peakDuration, profile, opt)));
                                }
                                if (opt.UsePowerDropoff)
                                    InsertDropoff(device, peakSeriesFirst, action, opt);
                            }
                        }
                    }
                    else if (nextIsPeak)
                    {
                        peakSeries = true;          // start of a peak series
                        peakSeriesFirst = action;
                        if (action.pos == 0 && opt.Ignore0PosSeries)
                        {
                            device.Add(New(action.at, 0));
                        }
                        else if (opt.RecordPowerOnPeakSeries)
                        {
                            if (prevPeak != null) peakDuration = Duration(prevPeak, action);
                            else if (prevTrough != null) peakDuration = Duration(prevTrough, action);
                            device.Add(New(action.at, PowerLevel(peakDuration, profile, opt)));
                        }
                    }
                    else
                    {
                        if (opt.RecordPowerOnSinglePeaks)
                        {
                            if (prevPeak != null) peakDuration = Duration(prevPeak, action);
                            else
                            {
                                var nextPeak = FindNextInSet(actions, idx, peakSet);
                                if (nextPeak != null) peakDuration = Duration(action, nextPeak);
                            }
                            device.Add(New(action.at, PowerLevel(peakDuration, profile, opt)));
                        }
                    }
                    prevPeak = action;
                }
                else if (troughSet.Contains(action.at))
                {
                    double troughDuration = HUGE;
                    if (troughSeries)
                    {
                        if ((idx < n - 1 && !nextIsTrough) || idx == n - 1)
                        {
                            troughSeries = false; // end of a trough series
                            if (action.pos == 0 && opt.Ignore0PosSeries)
                            {
                                device.Add(New(action.at, 0));
                            }
                            else
                            {
                                if (opt.RecordPowerOnTroughSeries)
                                {
                                    var nextTrough = FindNextInSet(actions, idx, troughSet);
                                    if (nextTrough != null) troughDuration = Duration(action, nextTrough);
                                    device.Add(New(action.at, PowerLevel(troughDuration, profile, opt)));
                                }
                                if (opt.UsePowerDropoff)
                                    InsertDropoff(device, troughSeriesFirst, action, opt);
                            }
                        }
                    }
                    else if (nextIsTrough)
                    {
                        troughSeries = true;          // start of a trough series
                        troughSeriesFirst = action;
                        if (action.pos == 0 && opt.Ignore0PosSeries)
                        {
                            device.Add(New(action.at, 0));
                        }
                        else if (opt.RecordPowerOnTroughSeries)
                        {
                            if (prevTrough != null) troughDuration = Duration(prevTrough, action);
                            else if (prevPeak != null) troughDuration = Duration(prevPeak, action);
                            device.Add(New(action.at, PowerLevel(troughDuration, profile, opt)));
                        }
                    }
                    else
                    {
                        if (opt.RecordPowerOnSingleTroughs)
                        {
                            if (prevTrough != null) troughDuration = Duration(prevTrough, action);
                            else
                            {
                                var nextTrough = FindNextInSet(actions, idx, troughSet);
                                if (nextTrough != null) troughDuration = Duration(action, nextTrough);
                            }
                            device.Add(New(action.at, PowerLevel(troughDuration, profile, opt)));
                        }
                    }
                    prevTrough = action;
                }
                // actions that are neither peak nor trough are dropped (as in the OFS tool)
            }

            // Sort by time and de-duplicate identical timestamps (keep the last writer).
            device = device
                .GroupBy(a => a.at)
                .Select(g => g.Last())
                .OrderBy(a => a.at)
                .ToList();
            return device;
        }

        // duration(ms) → power level (0-100) for the given device profile.
        // rpm = 60000/durationMs;  power = 1 + 99*(rpm-min)/(max-min);  clamp; quantise to step.
        public static int PowerLevel(double durationMs, FmDeviceProfile profile, FmConvertOptions opt)
        {
            if (durationMs <= 0) durationMs = 1.0e10;
            double rpm = 60000.0 / durationMs;
            double span = profile.MaxRPM - profile.MinRPM;
            double power = span != 0 ? 1.0 + 99.0 * (rpm - profile.MinRPM) / span : 0.0;
            power = Clamp(power, 0, 100);
            int step = (opt != null && opt.OverrideStepSize && opt.PowerLevelStepSize > 0) ? opt.PowerLevelStepSize : 1;
            power = step * Math.Round(power / step, MidpointRounding.AwayFromZero);
            return (int)Clamp(power, 0, 100);
        }

        // power level → RPM (inverse of the above), used by the calibration unit converter.
        public static double Rpm(double powerLevel, FmDeviceProfile profile)
            => profile.MinRPM + (profile.MaxRPM - profile.MinRPM) * (powerLevel - 1) / 99.0;

        // Finds local maxima (peaks) and minima (troughs). Faithful port of the OFS
        // get_peaks_troughs: respects equal-value runs and treats the first/last actions as edges.
        public static void GetPeaksTroughs(
            List<FunScriptAction> actions, out List<FunScriptAction> maxima, out List<FunScriptAction> minima)
        {
            maxima = new List<FunScriptAction>();
            minima = new List<FunScriptAction>();
            int n = actions.Count;
            var slope = Slope.Neutral;

            if (n <= 1) return;
            if (n == 2)
            {
                int d = PosDiff(actions[0], actions[1]);
                if (d > 0) { maxima.Add(actions[1]); minima.Add(actions[0]); }
                else if (d < 0) { maxima.Add(actions[0]); minima.Add(actions[1]); }
                return;
            }

            // first action
            int nextDiff = PosDiff(actions[0], actions[1]);
            if (nextDiff > 0) { minima.Add(actions[0]); slope = Slope.Rising; }
            else if (nextDiff < 0) { maxima.Add(actions[0]); slope = Slope.Falling; }

            // middle actions
            for (int i = 1; i < n - 1; i++)
            {
                int prevDiff = PosDiff(actions[i - 1], actions[i]);
                nextDiff = PosDiff(actions[i], actions[i + 1]);
                if (prevDiff < 0 && nextDiff > 0) { minima.Add(actions[i]); slope = Slope.Rising; }
                else if (prevDiff > 0 && nextDiff < 0) { maxima.Add(actions[i]); slope = Slope.Falling; }
                else if (prevDiff == 0 && nextDiff > 0)
                {
                    if (slope != Slope.Rising)
                    {
                        minima.Add(actions[i]);
                        int idx = i;
                        while (idx > 0 && PosDiff(actions[idx - 1], actions[idx]) == 0)
                        { minima.Add(actions[idx - 1]); idx--; }
                        slope = Slope.Rising;
                    }
                }
                else if (prevDiff == 0 && nextDiff < 0)
                {
                    if (slope != Slope.Falling)
                    {
                        maxima.Add(actions[i]);
                        int idx = i;
                        while (idx > 0 && PosDiff(actions[idx - 1], actions[idx]) == 0)
                        { maxima.Add(actions[idx - 1]); idx--; }
                        slope = Slope.Falling;
                    }
                }
            }

            // last action
            nextDiff = PosDiff(actions[n - 2], actions[n - 1]);
            if (nextDiff > 0) maxima.Add(actions[n - 1]);
            else if (nextDiff < 0) minima.Add(actions[n - 1]);
            else
            {
                if (slope == Slope.Rising)
                {
                    maxima.Add(actions[n - 1]);
                    int idx = n - 1;
                    while (idx > 0 && PosDiff(actions[idx - 1], actions[idx]) == 0)
                    { maxima.Add(actions[idx - 1]); idx--; }
                }
                else if (slope == Slope.Falling)
                {
                    minima.Add(actions[n - 1]);
                    int idx = n - 1;
                    while (idx > 0 && PosDiff(actions[idx - 1], actions[idx]) == 0)
                    { minima.Add(actions[idx - 1]); idx--; }
                }
            }
        }

        // ───────── private helpers ─────────

        private static FunScriptAction New(long at, int pos) => new FunScriptAction { at = at, pos = pos };

        private static int PosDiff(FunScriptAction a, FunScriptAction b) => b.pos - a.pos;

        private static double Duration(FunScriptAction a, FunScriptAction b) => Math.Abs((double)(b.at - a.at));

        private static FunScriptAction FindNextInSet(List<FunScriptAction> actions, int currentIdx, HashSet<long> set)
        {
            for (int j = currentIdx + 1; j < actions.Count; j++)
                if (set.Contains(actions[j].at)) return actions[j];
            return null;
        }

        // Inserts 0-power drop-off actions across a peak/trough series so the machine spools
        // down during pauses. If the series is shorter than the combined enter+exit offset,
        // a single 0 is placed in the middle instead.
        private static void InsertDropoff(
            List<FunScriptAction> device, FunScriptAction first, FunScriptAction last, FmConvertOptions opt)
        {
            if (first == null || last == null) return;
            double offset = opt.PowerDropoffTimeOffsetMs;
            if (Duration(first, last) > offset * 2)
            {
                device.Add(New(first.at + (long)offset, 0));
                device.Add(New(last.at - (long)offset, 0));
            }
            else
            {
                device.Add(New((first.at + last.at) / 2, 0));
            }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
