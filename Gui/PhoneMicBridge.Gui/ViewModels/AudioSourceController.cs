using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Gui.ViewModels
{
    /// <summary>
    /// Controls audio source inputs: Host Microphone, Test Tone Generator, and Test Audio File Player.
    /// Extracted from MainViewModel to isolate source and player domain logic (Praise-OOP Responsibility Boundary).
    /// </summary>
    public class AudioSourceController : INotifyPropertyChanged
    {
        private readonly IBridgeApplicationService _service;
        private BridgeUiSnapshot _snapshot;

        public event PropertyChangedEventHandler? PropertyChanged;

        // Source Controls - Microphone
        public bool IsMicEnabled
        {
            get => _snapshot.IsMicEnabled;
            set
            {
                if (_snapshot.IsMicEnabled != value)
                {
                    _service.SetMicEnabled(value);
                }
            }
        }

        public bool IsMicMuted
        {
            get => !_snapshot.IsMicEnabled;
            set
            {
                if (_snapshot.IsMicEnabled == value)
                {
                    _service.SetMicEnabled(!value);
                }
            }
        }

        public string MicMuteButtonText => IsMicMuted ? "UNMUTE MIC" : "MUTE MIC";
        public string MicStatusText => IsMicMuted ? "MUTED" : "UNMUTED";

        public float MicGain
        {
            get => _snapshot.MicGain;
            set
            {
                if (Math.Abs(_snapshot.MicGain - value) > 0.001f)
                {
                    _service.SetMicGain(value);
                }
            }
        }

        public string MicGainText => $"{MicGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";

        public void ToggleMic() => _service.ToggleMic();
        public void SetMicMuted(bool muted) => _service.SetMicMuted(muted);
        public void SetMicEnabled(bool enabled) => _service.SetMicEnabled(enabled);
        public void SetMicGain(float gain) => _service.SetMicGain(gain);

        // Source Controls - Test Tone Generator
        public bool IsToneEnabled
        {
            get => _snapshot.IsToneEnabled;
            set
            {
                if (_snapshot.IsToneEnabled != value)
                {
                    _service.SetToneEnabled(value, _snapshot.ToneFrequency, _snapshot.ToneGain);
                }
            }
        }

        public float ToneGain
        {
            get => _snapshot.ToneGain;
            set
            {
                if (Math.Abs(_snapshot.ToneGain - value) > 0.001f)
                {
                    _service.SetToneGain(value);
                }
            }
        }

        public string ToneGainText => $"{ToneGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";
        public double ToneFrequency => _snapshot.ToneFrequency;
        public string ToneStatusText => IsToneEnabled ? "TONE ACTIVE (1000 Hz, -12 dBFS)" : "OFF";
        public string ToneButtonText => IsToneEnabled ? "STOP TONE" : "START TONE";

        public void ToggleTestTone() => _service.ToggleTestTone();
        public void SetToneEnabled(bool enabled) => _service.SetToneEnabled(enabled);
        public void SetToneGain(float gain) => _service.SetToneGain(gain);

        // Source Controls - Audio File Player
        public bool HasAudioFile => _snapshot.HasAudioFile;
        public bool IsFilePlaying => _snapshot.IsFilePlaying;
        public string? AudioFileName => _snapshot.AudioFileName;
        public string? TestAudioFileName => _snapshot.AudioFileName;
        public string AudioFileStatusText => string.IsNullOrEmpty(_snapshot.AudioFileName)
            ? "No file loaded"
            : (_snapshot.IsFilePlaying ? $"Playing: {_snapshot.AudioFileName}" : $"Loaded: {_snapshot.AudioFileName} (Stopped)");
        public string PlayButtonText => _snapshot.IsFilePlaying ? "STOP AUDIO" : "PLAY AUDIO";

        public float FileGain
        {
            get => _snapshot.FileGain;
            set
            {
                if (Math.Abs(_snapshot.FileGain - value) > 0.001f)
                {
                    _service.SetFileGain(value);
                }
            }
        }

        public string FileGainText => $"{FileGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";

        public void PlayTestAudio(string filePath) => _service.PlayTestAudio(filePath);
        public Task<bool> PlayTestAudioAsync(string filePath) => _service.PlayTestAudioAsync(filePath);
        public void StopTestAudio() => _service.StopTestAudio();
        public void ToggleFilePlayback() => _service.ToggleFilePlayback();
        public void SetFileGain(float gain) => _service.SetFileGain(gain);

        // Commands
        public ICommand ToggleMicCommand { get; }
        public ICommand ToggleTestToneCommand { get; }
        public ICommand ToggleFilePlaybackCommand { get; }
        public ICommand PlayTestAudioCommand { get; }
        public ICommand StopTestAudioCommand { get; }
        public ICommand BrowseAudioFileCommand { get; }

        public AudioSourceController(IBridgeApplicationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _snapshot = _service.CurrentSnapshot;

            ToggleMicCommand = new RelayCommand(ToggleMic);
            ToggleTestToneCommand = new RelayCommand(ToggleTestTone);
            ToggleFilePlaybackCommand = new RelayCommand(ToggleFilePlayback);
            PlayTestAudioCommand = new AsyncRelayCommand(PlayOrToggleAudioFileAsync);
            StopTestAudioCommand = new RelayCommand(StopTestAudio);
            BrowseAudioFileCommand = new AsyncRelayCommand(BrowseAndLoadAudioFileAsync);
        }

        public async Task PlayOrToggleAudioFileAsync()
        {
            if (!_snapshot.HasAudioFile)
            {
                await BrowseAndLoadAudioFileAsync().ConfigureAwait(true);
            }
            else
            {
                _service.ToggleFilePlayback();
            }
        }

        public async Task BrowseAndLoadAudioFileAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Audio File for Bridge Playback",
                Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                await _service.LoadAudioFileAsync(dialog.FileName).ConfigureAwait(true);
            }
        }

        public void ApplySnapshot(BridgeUiSnapshot snapshot)
        {
            _snapshot = snapshot;

            OnPropertyChanged(nameof(IsMicEnabled));
            OnPropertyChanged(nameof(IsMicMuted));
            OnPropertyChanged(nameof(MicMuteButtonText));
            OnPropertyChanged(nameof(MicStatusText));
            OnPropertyChanged(nameof(MicGain));
            OnPropertyChanged(nameof(MicGainText));

            OnPropertyChanged(nameof(IsToneEnabled));
            OnPropertyChanged(nameof(ToneGain));
            OnPropertyChanged(nameof(ToneGainText));
            OnPropertyChanged(nameof(ToneFrequency));
            OnPropertyChanged(nameof(ToneStatusText));
            OnPropertyChanged(nameof(ToneButtonText));

            OnPropertyChanged(nameof(HasAudioFile));
            OnPropertyChanged(nameof(IsFilePlaying));
            OnPropertyChanged(nameof(AudioFileName));
            OnPropertyChanged(nameof(TestAudioFileName));
            OnPropertyChanged(nameof(AudioFileStatusText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(FileGain));
            OnPropertyChanged(nameof(FileGainText));
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
