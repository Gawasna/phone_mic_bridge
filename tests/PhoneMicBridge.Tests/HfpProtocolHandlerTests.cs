using PhoneMicBridge.Bluetooth;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class HfpProtocolHandlerTests
    {
        [Fact]
        public void HfpProtocolHandler_InitialState_ShouldBeDisconnected()
        {
            var handler = new HfpProtocolHandler();
            Assert.Equal(HfpSessionState.Disconnected, handler.State);
        }

        [Fact]
        public void HfpProtocolHandler_FullHandshake_ShouldReachScoAudioActive()
        {
            var handler = new HfpProtocolHandler();

            // Step 1: Initial handshake
            string initCmd = handler.GetInitialHandshakeCommand();
            Assert.Equal(AtCommand.BRSF_QUERY, initCmd);
            Assert.Equal(HfpSessionState.RfcommConnecting, handler.State);

            // Step 2: Phone responds with BRSF features
            string? nextCmd = handler.ProcessDeviceResponse("+BRSF: 1023\r\nOK");
            Assert.Equal(AtCommand.BAC_ENABLE_MSBC, nextCmd);
            Assert.Equal(HfpSessionState.ServiceLevelConnected, handler.State);

            // Step 3: Phone accepts codec list
            nextCmd = handler.ProcessDeviceResponse("OK");
            Assert.Equal(AtCommand.BCC_OPEN_SCO, nextCmd);
            Assert.Equal(HfpSessionState.CodecNegotiating, handler.State);

            // Step 4: SCO connection established
            nextCmd = handler.ProcessDeviceResponse("OK");
            Assert.Null(nextCmd); // No more pending AT commands
            Assert.Equal(HfpSessionState.ScoAudioActive, handler.State);
            Assert.True(handler.SupportsWidebandSpeech);
        }

        [Fact]
        public void HfpProtocolHandler_WhenErrorReceived_ShouldEnterErrorState()
        {
            var handler = new HfpProtocolHandler();
            handler.GetInitialHandshakeCommand();

            handler.ProcessDeviceResponse("ERROR");
            Assert.Equal(HfpSessionState.Error, handler.State);
        }
    }
}
