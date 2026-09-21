using System;
using NAudio.CoreAudioApi;

namespace PhoneMicBridge.Devices
{
    public class DeviceNotificationWatcher : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private readonly MMDeviceNotificationClient _client;
        private bool _isDisposed;

        public event EventHandler<DeviceStateChangedEventArgs>? DeviceStateChanged;
        public event EventHandler<DeviceNotificationEventArgs>? DeviceAdded;
        public event EventHandler<DeviceNotificationEventArgs>? DeviceRemoved;

        public DeviceNotificationWatcher()
        {
            _enumerator = new MMDeviceEnumerator();
            _client = _enumerator.CreateNotificationClient(true);

            _client.DeviceStateChanged += OnDeviceStateChanged;
            _client.DeviceAdded += OnDeviceAdded;
            _client.DeviceRemoved += OnDeviceRemoved;
        }

        private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
        {
            DeviceStateChanged?.Invoke(this, e);
        }

        private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e)
        {
            DeviceAdded?.Invoke(this, e);
        }

        private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
        {
            DeviceRemoved?.Invoke(this, e);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _client.DeviceStateChanged -= OnDeviceStateChanged;
                _client.DeviceAdded -= OnDeviceAdded;
                _client.DeviceRemoved -= OnDeviceRemoved;
                _enumerator.Dispose();
            }
            catch
            {
                // Suppress release COM cleanup races
            }
        }
    }
}
