using System;

namespace PhoneMicBridge.Bluetooth
{
    public class HfpProtocolHandler
    {
        public HfpSessionState State { get; private set; } = HfpSessionState.Disconnected;
        public event EventHandler<HfpSessionState>? StateChanged;

        public bool SupportsWidebandSpeech { get; private set; }

        public string GetInitialHandshakeCommand()
        {
            SetState(HfpSessionState.RfcommConnecting);
            return AtCommand.BRSF_QUERY;
        }

        public string? ProcessDeviceResponse(string rawResponse)
        {
            string trimmed = rawResponse.Trim();

            switch (State)
            {
                case HfpSessionState.RfcommConnecting:
                    if (trimmed.Contains("+BRSF:"))
                    {
                        // Check if phone supports codec negotiation (bit 7 = 128)
                        SetState(HfpSessionState.ServiceLevelConnected);
                        return AtCommand.BAC_ENABLE_MSBC;
                    }
                    else if (trimmed.Contains(AtCommand.OK_RESPONSE))
                    {
                        SetState(HfpSessionState.ServiceLevelConnected);
                        return AtCommand.BCC_OPEN_SCO;
                    }
                    break;

                case HfpSessionState.ServiceLevelConnected:
                    if (trimmed.Contains(AtCommand.OK_RESPONSE) || trimmed.Contains("+BCS:"))
                    {
                        SupportsWidebandSpeech = true;
                        SetState(HfpSessionState.CodecNegotiating);
                        return AtCommand.BCC_OPEN_SCO;
                    }
                    break;

                case HfpSessionState.CodecNegotiating:
                    if (trimmed.Contains(AtCommand.OK_RESPONSE))
                    {
                        SetState(HfpSessionState.ScoAudioActive);
                        return null; // Handshake complete!
                    }
                    break;

                default:
                    break;
            }

            if (trimmed.Contains(AtCommand.ERROR_RESPONSE))
            {
                SetState(HfpSessionState.Error);
            }

            return null;
        }

        public void Reset()
        {
            SetState(HfpSessionState.Disconnected);
            SupportsWidebandSpeech = false;
        }

        private void SetState(HfpSessionState newState)
        {
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
}
