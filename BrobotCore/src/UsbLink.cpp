#include "UsbLink.h"

#if defined(ESP32)

#include "Config.h"

#if !VSCREEN
#include "esp_intr_alloc.h"
#include "hal/usb_serial_jtag_ll.h"
#include "soc/periph_defs.h"

namespace {

// Receive ring, written by the interrupt handler and read by loop(): a
// single-producer/single-consumer byte ring, so no locking — the handler only
// moves s_head, the reader only moves s_tail. Size is a power of two so the
// wrap is a mask, not a division, inside the ISR.
uint8_t* s_ring = nullptr;
size_t s_mask = 0;
volatile size_t s_head = 0;
volatile size_t s_tail = 0;
intr_handle_t s_intr = nullptr;

// Moves everything waiting in the hardware RX FIFO into the ring, in whole
// 64-byte packets. Must run with the USB interrupt unable to preempt it (inside
// the ISR itself, or with interrupts masked).
void IRAM_ATTR drainRxFifo() {
    while (usb_serial_jtag_ll_rxfifo_data_available()) {
        uint8_t packet[64];
        int n = usb_serial_jtag_ll_read_rxfifo(packet, sizeof(packet));
        if (n <= 0) {
            break;
        }
        size_t head = s_head;
        size_t tail = s_tail;
        for (int i = 0; i < n; i++) {
            size_t next = (head + 1) & s_mask;
            if (next == tail) {
                break; // ring full: drop, same as any overrun
            }
            s_ring[head] = packet[i];
            head = next;
        }
        s_head = head;
    }
}

// Same job as arduino-esp32's own HWCDC handler (which this replaces), with
// one difference that matters: the 64-byte packet is copied into the ring in
// one go, instead of one xQueueSendFromISR per byte. Only the "packet
// received" interrupt is enabled, so that is the only thing to handle.
void IRAM_ATTR usbIsr(void*) {
    uint32_t status = usb_serial_jtag_ll_get_intsts_mask();
    if (status & USB_SERIAL_JTAG_INTR_SERIAL_OUT_RECV_PKT) {
        usb_serial_jtag_ll_clr_intsts_mask(USB_SERIAL_JTAG_INTR_SERIAL_OUT_RECV_PKT);
        drainRxFifo();
    }
}

portMUX_TYPE s_fifoMux = portMUX_INITIALIZER_UNLOCKED;

// Longest a write may wait for room in the USB TX FIFO (the host isn't
// reading, or isn't there). Replies are a few bytes, so this is only ever
// reached when something is already wrong.
constexpr unsigned long WRITE_TIMEOUT_MS = 20;
// After a write timed out, further writes are dropped for this long instead of
// each costing another WRITE_TIMEOUT_MS of a stalled loop().
constexpr unsigned long WRITE_BACKOFF_MS = 1000;
unsigned long s_writeBlockedUntil = 0;
bool s_writeBlocked = false;

} // namespace
#endif

void UsbLink::begin(size_t rxBufferBytes) {
#if !VSCREEN
    // Round the ring up to a power of two.
    size_t size = 1024;
    while (size < rxBufferBytes) {
        size <<= 1;
    }
    s_ring = static_cast<uint8_t*>(malloc(size));
    if (s_ring != nullptr) {
        s_mask = size - 1;
        s_head = s_tail = 0;
        usb_serial_jtag_ll_disable_intr_mask(USB_SERIAL_JTAG_LL_INTR_MASK);
        usb_serial_jtag_ll_clr_intsts_mask(USB_SERIAL_JTAG_LL_INTR_MASK);
        // A packet that arrived before this point (a PC probing the port while
        // the board was still booting) sits in the hardware FIFO with its
        // interrupt flag just cleared above — it would never fire again, the
        // FIFO would stay full, and the hardware would refuse every later write
        // from the PC. Empty it now, before the interrupt is enabled.
        drainRxFifo();
        if (esp_intr_alloc(ETS_USB_SERIAL_JTAG_INTR_SOURCE, 0, usbIsr, nullptr, &s_intr) == ESP_OK) {
            usb_serial_jtag_ll_ena_intr_mask(USB_SERIAL_JTAG_INTR_SERIAL_OUT_RECV_PKT);
            _native = true;
            _rxCapacity = size - 1;
            return;
        }
        free(s_ring);
        s_ring = nullptr;
    }
    // No ring or no interrupt — fall back to Serial rather than have no USB
    // link at all. Slower, but working.
#endif
    Serial.begin(SERIAL_BAUD_RATE);
}

int UsbLink::available() {
#if !VSCREEN
    if (_native) {
        // Belt and braces for a missed interrupt (see begin()): if the FIFO
        // holds data, take it now instead of waiting for an interrupt that may
        // never come. A register read when there is nothing to do.
        if (usb_serial_jtag_ll_rxfifo_data_available()) {
            portENTER_CRITICAL(&s_fifoMux);
            drainRxFifo();
            portEXIT_CRITICAL(&s_fifoMux);
        }
        return static_cast<int>((s_head - s_tail) & s_mask);
    }
#endif
    return Serial.available();
}

int UsbLink::read() {
#if !VSCREEN
    if (_native) {
        size_t tail = s_tail;
        if (tail == s_head) {
            return -1;
        }
        uint8_t b = s_ring[tail];
        s_tail = (tail + 1) & s_mask;
        return b;
    }
#endif
    return Serial.read();
}

int UsbLink::peek() {
#if !VSCREEN
    if (_native) {
        size_t tail = s_tail;
        return tail == s_head ? -1 : s_ring[tail];
    }
#endif
    return Serial.peek();
}

size_t UsbLink::write(const uint8_t* buffer, size_t size) {
#if !VSCREEN
    if (_native) {
        if (s_writeBlocked && millis() < s_writeBlockedUntil) {
            return size; // dropped on purpose, see WRITE_BACKOFF_MS
        }
        s_writeBlocked = false;

        size_t sent = 0;
        unsigned long waitingSince = millis();
        while (sent < size) {
            if (usb_serial_jtag_ll_txfifo_writable()) {
                uint32_t chunk = size - sent > 64 ? 64 : static_cast<uint32_t>(size - sent);
                int n = usb_serial_jtag_ll_write_txfifo(buffer + sent, chunk);
                usb_serial_jtag_ll_txfifo_flush();
                sent += n > 0 ? n : chunk;
                waitingSince = millis();
            } else if (millis() - waitingSince > WRITE_TIMEOUT_MS) {
                s_writeBlocked = true;
                s_writeBlockedUntil = millis() + WRITE_BACKOFF_MS;
                break;
            }
        }
        return sent;
    }
#endif
    return Serial.write(buffer, size);
}

#endif
