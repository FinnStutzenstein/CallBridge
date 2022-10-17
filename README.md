# CallBridge

A small dummy project connecting DialogBits via SIP to the outer telephony-world. Note that this is just a prototype created as a learning experience for telephony related topics.

This program is a SIP user agent client, capable of registering itself as a softphone. All incoming calles are put through STT->DialogBits->TTS and back to the callee. A Microsoft Azure speech service key is needed for STT/TTS. Also, the branch feature/CallBridge needs to be checked out in DialogBits to enable the new CallBridge channel. Supported fueatures of telephony-bits in DialogBits:
- DTMF, singe input and requesting a sequence
- Hangup
- Normal conversations; only chat bubbles are outputted, as for all telephony channels

The call transfer feature is missing (no time to do this even though it would be a nice SIP/SDP/RTP challenge).

Some files in the C# project are just for reference and testing single components: `TTS.cs`, `STT.cs`, `Inbound.cs`, `Outbound.cs`. They easily can be used as main classes and one can test them stand-alone. To run the main program, you need a populated `settings.ini` in the `SipTest/SipTest/bin/Debug/net6.0-windows10.0.22000/` folder. You can use the one next to this file as a reference.
