namespace PhoneMicBridge.Cli
{
    public enum CliCommand
    {
        Mix,
        ListInputs,
        ListOutputs,
        ListDevices,
        TestTone,
        PlayFile,
        Help
    }

    public class CliOptions
    {
        public CliCommand Command { get; set; } = CliCommand.Mix;
        public string AudioFilePath { get; set; } = string.Empty;
        public float FileGain { get; set; } = 0.7f;
        public double ToneFrequency { get; set; } = 1000.0;
        public float ToneGain { get; set; } = 0.35f;
        public float MicGain { get; set; } = 1.0f;
        public bool EnableTone { get; set; }
        public bool EnableMic { get; set; } = true;
        public string TargetDeviceGuid { get; set; } = Devices.DeviceDiscovery.DEFAULT_TARGET_HFP_GUID;
        public string InputDeviceQuery { get; set; } = string.Empty;

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();

            if (args.Length == 0)
                return options;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                switch (arg)
                {
                    case "--list-devices":
                    case "--list":
                        options.Command = CliCommand.ListDevices;
                        return options;
                    case "--list-inputs":
                        options.Command = CliCommand.ListInputs;
                        return options;
                    case "--list-outputs":
                        options.Command = CliCommand.ListOutputs;
                        return options;
                    case "--help":
                    case "-h":
                        options.Command = CliCommand.Help;
                        return options;
                    case "--test-tone":
                        options.Command = CliCommand.TestTone;
                        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double freq))
                        {
                            options.ToneFrequency = freq;
                            i++;
                        }
                        return options;
                    case "--play-file":
                        options.Command = CliCommand.PlayFile;
                        if (i + 1 < args.Length)
                        {
                            options.AudioFilePath = args[++i];
                        }
                        if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float fg))
                        {
                            options.FileGain = fg;
                            i++;
                        }
                        return options;
                    case "--mix":
                        options.Command = CliCommand.Mix;
                        break;
                    case "--file":
                        if (i + 1 < args.Length) options.AudioFilePath = args[++i];
                        break;
                    case "--file-gain":
                        if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float fgain)) { options.FileGain = fgain; i++; }
                        break;
                    case "--tone":
                        options.EnableTone = true;
                        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double tf)) { options.ToneFrequency = tf; i++; }
                        break;
                    case "--tone-gain":
                        if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float tgain)) { options.ToneGain = tgain; i++; }
                        break;
                    case "--mic-gain":
                    case "--speaker-gain":
                    case "--loopback-gain":
                        if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float mgain)) { options.MicGain = mgain; i++; }
                        break;
                    case "--no-mic":
                        options.EnableMic = false;
                        break;
                    case "--target-guid":
                        if (i + 1 < args.Length) options.TargetDeviceGuid = args[++i];
                        break;
                    case "--input":
                    case "-i":
                    case "--speaker":
                    case "--loopback":
                        if (i + 1 < args.Length) options.InputDeviceQuery = args[++i];
                        break;
                }
            }

            return options;
        }
    }
}
