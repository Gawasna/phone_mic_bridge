namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Immutable value type representing instantaneous audio metering values.
    /// Struct semantics guarantee zero heap allocation when polled at 30Hz-60Hz by the UI.
    /// </summary>
    public readonly record struct AudioMeterValues(
        float InputPeak,
        float InputRms,
        float InputPeakDb,
        float OutputPeak,
        float OutputRms,
        float OutputPeakDb,
        bool IsInputClipping,
        bool IsOutputClipping
    );
}
