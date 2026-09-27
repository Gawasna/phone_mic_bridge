using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Audio;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Cli
{
    public static class CliRunner
    {
        public static void Run(CliOptions options)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            PrintHeader();

            switch (options.Command)
            {
                case CliCommand.Help:
                    ShowHelp();
                    break;
                case CliCommand.ListInputs:
                    ListInputs();
                    break;
                case CliCommand.ListOutputs:
                    ListOutputs(options.TargetDeviceGuid);
                    break;
                case CliCommand.TestTone:
                    RunTestTone(options);
                    break;
                case CliCommand.PlayFile:
                    RunPlayFile(options);
                    break;
                case CliCommand.Mix:
                default:
                    RunMixer(options);
                    break;
            }
        }

        private static void PrintHeader()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("        PHONE MIC BRIDGE - WINDOWS HOST AUDIO ENGINE      ");
            Console.WriteLine("==========================================================");
        }

        private static void ShowHelp()
        {
            Console.WriteLine("Cách sử dụng:");
            Console.WriteLine("  phone_mic_bridge.exe --list-inputs             : Liệt kê các micro/nguồn thu âm");
            Console.WriteLine("  phone_mic_bridge.exe --list-outputs            : Liệt kê các ngõ ra âm thanh");
            Console.WriteLine("  phone_mic_bridge.exe --test-tone [freq]        : Bơm âm thanh thử nghiệm (vd: 1000, 500, 2000)");
            Console.WriteLine("  phone_mic_bridge.exe --play-file <path> [gain] : Stream trực tiếp file âm thanh (MP3/WAV) vào Redmi");
            Console.WriteLine("  phone_mic_bridge.exe --mix                     : Chạy bộ trộn (Micro PC + File/Tone -> Redmi)");
            Console.WriteLine("    Tùy chọn cho --mix:");
            Console.WriteLine("      --file <path>       : Đường dẫn file MP3/WAV để stream vào cuộc gọi");
            Console.WriteLine("      --file-gain [g]     : Âm lượng file âm thanh (mặc định 0.7)");
            Console.WriteLine("      --tone [Hz]         : Tần số tone thử nghiệm (mặc định 1000)");
            Console.WriteLine("      --tone-gain [g]     : Âm lượng tone (mặc định 0.35)");
            Console.WriteLine("      --mic-gain [g]      : Độ nhạy micro (mặc định 1.0)");
            Console.WriteLine("      --no-mic            : Tắt micro ban đầu, chỉ phát file/tone");
            Console.WriteLine("      --input <name/guid> : Nguồn thu âm hoặc ngõ ra để loopback (vd: 'FxSound', 'Realtek')");
            Console.WriteLine("      --speaker <name>    : Chỉ định ngõ ra loa PC để capture loopback (alias của --input)");
            Console.WriteLine("      --target-guid <id>  : Tùy chỉnh GUID của thiết bị đích HFP");
        }

        private static void ListInputs()
        {
            Console.WriteLine("\n[INPUT DEVICES / CAPTURE ENDPOINTS]:");
            var devices = DeviceDiscovery.GetCaptureDevices();
            foreach (var dev in devices)
            {
                Console.WriteLine($"  [{dev.Index}] {dev.Name} ({dev.Id})");
            }
        }

        private static void ListOutputs(string targetGuid)
        {
            Console.WriteLine("\n[OUTPUT DEVICES / RENDER ENDPOINTS]:");
            var devices = DeviceDiscovery.GetRenderDevices(targetGuid);
            foreach (var dev in devices)
            {
                string tag = dev.IsTargetHfp ? " [TARGET HFP]" : "";
                Console.WriteLine($"  [{dev.Index}] {dev.Name} ({dev.Id}) - State: {dev.State}{tag}");
            }
        }

        private static void RunPlayFile(CliOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.AudioFilePath) || !File.Exists(options.AudioFilePath))
            {
                Console.WriteLine($"[LỖI] Không tìm thấy file âm thanh: {options.AudioFilePath}");
                return;
            }

            var targetDev = DeviceDiscovery.FindTargetRenderDevice(options.TargetDeviceGuid);
            if (targetDev == null)
            {
                Console.WriteLine("[LỖI] Không tìm thấy endpoint Target HFP (Redmi / Bluetooth HFP)!");
                return;
            }

            Console.WriteLine($"[DESTINATION]: {targetDev.FriendlyName} | State: {targetDev.State}");
            if (targetDev.State != DeviceState.Active)
            {
                Console.WriteLine("[CẢNH BÁO] Endpoint Target HFP chưa Active! Hãy đảm bảo cuộc gọi đang kết nối và chọn MSI.");
            }

            using var engine = new WasapiAudioEngine(targetDev);
            engine.Pipeline.AttachFileSource(options.AudioFilePath, options.FileGain);

            Console.WriteLine($"[HFP FORMAT]: {engine.RenderFormat.SampleRate} Hz, {engine.RenderFormat.Channels} ch");
            Console.WriteLine($"[STREAMING FILE]: {Path.GetFileName(options.AudioFilePath)} (Gain = {options.FileGain:F2})");
            Console.WriteLine("\n[STREAMING ĐANG CHẠY] Nhấn 'q' để dừng phát sớm...");

            engine.Start();

            while (engine.Pipeline.IsFilePlaying && engine.Pipeline.FileCurrentSeconds < engine.Pipeline.FileTotalSeconds)
            {
                if (CheckKey('q')) break;

                long cur = (long)engine.Pipeline.FileCurrentSeconds;
                long total = (long)engine.Pipeline.FileTotalSeconds;
                Console.Write($"\r[TIẾN ĐỘ]: {cur}/{total}s | [PEAK]: {engine.OutputMeter.Peak:F2} | Đang stream vào cuộc gọi...  ");
                Thread.Sleep(200);
            }

            engine.Stop();
            Console.WriteLine("\n[XONG] Hoàn tất phát file âm thanh!");
        }

        private static void RunTestTone(CliOptions options)
        {
            var targetDev = DeviceDiscovery.FindTargetRenderDevice(options.TargetDeviceGuid);
            if (targetDev == null)
            {
                Console.WriteLine("[LỖI] Không tìm thấy endpoint Target HFP (Redmi / Bluetooth HFP)!");
                return;
            }

            Console.WriteLine($"Target Device: {targetDev.FriendlyName} | State: {targetDev.State}");
            using var engine = new WasapiAudioEngine(targetDev);
            engine.Pipeline.AttachToneSource(options.ToneFrequency, 0.8f);

            Console.WriteLine($"[BẮT ĐẦU] Bơm âm tần {options.ToneFrequency} Hz trong 10 giây. Nhấn 'q' để thoát sớm...");
            engine.Start();

            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < 10)
            {
                if (CheckKey('q')) break;
                Thread.Sleep(200);
                Console.Write(".");
            }

            engine.Stop();
            Console.WriteLine("\n[XONG] Hoàn tất phát tone kiểm định!");
        }

        private static void RunMixer(CliOptions options)
        {
            var targetDev = DeviceDiscovery.FindTargetRenderDevice(options.TargetDeviceGuid);
            if (targetDev == null)
            {
                Console.WriteLine("[LỖI] Không tìm thấy endpoint Target HFP (Redmi / Bluetooth HFP)!");
                return;
            }

            var inputDev = DeviceDiscovery.FindCaptureOrLoopbackDevice(options.InputDeviceQuery);
            string inputType = inputDev.DataFlow == DataFlow.Render ? "SPEAKER LOOPBACK" : "MIC";
            Console.WriteLine($"\n[HFP DESTINATION]: {targetDev.FriendlyName} ({targetDev.AudioClient.MixFormat.SampleRate} Hz)");
            Console.WriteLine($"[{inputType} SOURCE]: {inputDev.FriendlyName} (Flow: {inputDev.DataFlow})");

            using var engine = new WasapiAudioEngine(targetDev, inputDev);
            engine.Pipeline.MicGain = options.MicGain;
            engine.Pipeline.FileGain = options.FileGain;
            engine.Pipeline.ToneGain = options.ToneGain;

            // Setup Mic
            engine.SetupMicCapture(options.EnableMic);

            // Setup File if specified
            if (!string.IsNullOrWhiteSpace(options.AudioFilePath) && File.Exists(options.AudioFilePath))
            {
                engine.Pipeline.AttachFileSource(options.AudioFilePath, options.FileGain);
                Console.WriteLine($"[FILE SOURCE]: {Path.GetFileName(options.AudioFilePath)} (Gain = {options.FileGain:F2})");
            }

            // Setup Tone if enabled
            if (options.EnableTone)
            {
                engine.Pipeline.AttachToneSource(options.ToneFrequency, options.ToneGain);
                Console.WriteLine($"[TONE SOURCE]: {options.ToneFrequency} Hz (Gain = {options.ToneGain:F2})");
            }

            Console.WriteLine("----------------------------------------------------------");
            Console.WriteLine("Phím tắt: 'm'=Bật/Tắt Mic | 'f'=Bật/Tắt File Nhạc | 'q'=Thoát");
            Console.WriteLine("----------------------------------------------------------\n");

            engine.Start();

            while (true)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.KeyChar == 'q' || key.KeyChar == 'Q') break;
                        if (key.KeyChar == 'm' || key.KeyChar == 'M')
                        {
                            engine.Pipeline.ToggleMic();
                        }
                        if (key.KeyChar == 'f' || key.KeyChar == 'F')
                        {
                            engine.Pipeline.ToggleFile();
                        }
                    }
                }
                catch { }

                string micStatus = engine.Pipeline.IsMicEnabled
                    ? $"ON (Peak: {engine.MicMeter.Peak:F2})"
                    : "OFF           ";

                string fileStatus = engine.Pipeline.HasFile
                    ? (engine.Pipeline.IsFilePlaying
                        ? $"PLAY ({(int)engine.Pipeline.FileCurrentSeconds}/{(int)engine.Pipeline.FileTotalSeconds}s)"
                        : "PAUSED   ")
                    : "NONE     ";

                Console.Write($"\r[MIC]: {micStatus} | [FILE]: {fileStatus} | [OUT PEAK]: {engine.OutputMeter.Peak:F2}  ");
                Thread.Sleep(150);
            }

            Console.WriteLine("\nĐang dừng engine...");
            engine.Stop();
            Console.WriteLine("[DONE] Đã dừng pipeline an toàn.");
        }

        private static bool CheckKey(char target)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    return char.ToLowerInvariant(k.KeyChar) == char.ToLowerInvariant(target);
                }
            }
            catch { }
            return false;
        }
    }
}
