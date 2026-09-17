# Testing tips

- The most reliable way to verify Arduino-side behavior is reading the raw serial
  protocol directly (bypassing the WPF app), since the wire format is simple text.
- The WPF app's connect/disconnect and command-send flow can be driven headlessly
  via Windows UI Automation for scripted screenshots — see conversation history/PR
  descriptions for examples if needed again.
