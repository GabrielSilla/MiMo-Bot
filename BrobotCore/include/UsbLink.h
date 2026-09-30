#pragma once

#include <Arduino.h>

#if defined(ESP32)

// The ESP32-C3's USB port as a Stream, for the protocol link to Brobot.Sender.
// (Own interrupt handler + ring buffer — arduino-esp32's HWCDC, and ESP-IDF's
// usb_serial_jtag driver, were both tried and are not used; see UsbLink.cpp.)
//
// Why not just `Serial`: with ARDUINO_USB_CDC_ON_BOOT, Serial is arduino-esp32's
// HWCDC, whose receive interrupt pushes every incoming byte into a FreeRTOS
// queue *one item at a time* (see HWCDC.cpp), and whose read() takes them back
// out the same way. That costs ~3us per byte, i.e. ~50ms for one 17KB STREAM
// frame — measured as ~100ms round trips and 9-17 fps whenever the picture
// scrolled. This class installs its own interrupt handler that copies each
// 64-byte hardware packet into a plain byte ring in one go.
//
// VSCREEN builds keep using plain Serial (SerialVirtualDisplay draws through
// it), so there this is a thin pass-through.
class UsbLink : public Stream {
public:
    // Call once from setup(), instead of Serial.begin().
    void begin(size_t rxBufferBytes);

    // 0 when the native driver isn't in use (VSCREEN build, or its install
    // failed and Serial took over) — reported in main.cpp's DIAG line.
    size_t rxCapacity() const { return _native ? _rxCapacity : 0; }

    int available() override;
    int read() override;
    int peek() override;
    void flush() override {}
    size_t write(uint8_t b) override { return write(&b, 1); }
    size_t write(const uint8_t* buffer, size_t size) override;

private:
    bool _native = false;
    size_t _rxCapacity = 0;

};

#endif
