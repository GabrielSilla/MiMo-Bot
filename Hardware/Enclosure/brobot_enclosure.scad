// ============================================================================
// Brobot enclosure — "sandwich" case for the 1.8" 128x160 SPI TFT display
// + an ESP32-C3 SuperMini GLUED to the back of the display PCB.
//
// Fixed external dimensions (as specified — do not derive these from margins):
//   Largura (width, X)  = 40mm
//   Comprimento (Y)     = 65mm
//   Altura (depth, Z)   = 40mm
//
// ONE main body (front face + all 4 side walls, open only at the very back)
// plus ONE flat lid that press-fits over that opening. Not two shells of
// similar size — the lid is just a thin cap.
//
// Layout (front -> back), all inside the single BODY:
//   [ front wall + screen window ]
//   [ display PCB glued flush against the inner front face, screen forward ]
//   [ ESP32-C3 SuperMini glued flat to the BACK of the display PCB,     ]
//   [ rotated so its USB-C connector sits flush with the board's edge, ]
//   [ exiting through a cutout in the body's side wall (lateral)       ]
//   [ ...remaining depth is open (spare room for a battery etc. later) ]
//   ---- open back, closed by the LID ----
//
// The lid has a thin press-fit spigot rim that plugs into the body's open
// cavity (no screws, no extra size). Both boards are glued in — the display
// flush against the inner front face, the ESP32 on top of it — before the
// lid is pressed shut.
//
// WINDOW SIZE NOTE: the real visible glass on this panel is roughly 28mm x
// 35mm. The window is cut at 30mm x 36mm — close to the actual glass, with
// a little slack, after an earlier oversized attempt (35mm x 45.72mm) let
// too much of the display's own frame/bezel show through the opening.
//
// HOW TO USE:
//   1. Install OpenSCAD (free): https://openscad.org/downloads.html
//   2. Open this file. Measure YOUR actual boards with calipers and update
//      the "MEASURE AND ADJUST" block below — especially the display's
//      mounting-hole positions, and exactly where the ESP32 can be glued
//      without landing on top of the SD-card holder or other components.
//   3. Set render_part below to preview/export each part.
//   4. F6 (render) then File > Export > Export as STL for each part.
// ============================================================================

// ---- what to show -----------------------------------------------------
// "assembled" = body + lid in place (visual check only, don't print this)
// "exploded"  = body + lid pulled apart along Z (visual check)
// "body"      = main body alone, oriented for printing (print this)
// "lid"       = lid alone, oriented for printing (print this)
render_part = "exploded"; // ["assembled","exploded","body","lid"]

$fn = 48; // smoothness of circles/cylinders

// ============================================================================
// FIXED CASE DIMENSIONS — as specified, do not derive these from margins
// ============================================================================

case_w = 40.0; // Largura
case_l = 65.0; // Comprimento
case_d = 20.0; // Altura (sandwich depth)

// ============================================================================
// MEASURE AND ADJUST — these are estimates, verify against your real parts
// ============================================================================

// ---- 1.8" 128x160 SPI TFT display board (34mm x 58mm PCB, per photo) ----
disp_w        = 34.0;   // PCB width
disp_l        = 58.0;   // PCB length
disp_pcb_t    = 1.6;    // PCB thickness

// True visible glass, for reference only (NOT the window size — see note above).
// active_w_ref = 28.0; active_h_ref = 35.0;

// Window opening in the front face — set exactly as requested: 35mm on the
// case's width axis, 45.72mm on the case's length axis. Centered on the
// board by default.
window_w    = 30.0;
window_l    = 36.0;
window_off_x = 0;
window_off_y = 0;

// ---- ESP32-C3 SuperMini, glued to the back of the display PCB ----
// Mounted in its natural (unrotated) orientation: esp_l (with the USB-C
// connector on one short end) runs along the case's Y axis, same as the
// display board's own long axis, so the connector lands flush with the
// display board's TOP edge (away from the pin header at the bottom) and
// exits through the small end wall there — the 40mm x 20mm wall at the
// end of the 65mm axis. Flip esp_usb_side to -1 to exit through the
// BOTTOM end wall instead (tight — that's the header/wiring side).
esp_w = 18.0;   // X-extent
esp_l = 22.5;   // Y-extent; USB-C is on this edge
esp_t = 6.0;    // thickness incl. USB-C connector shell — glued directly on the PCB back
esp_center_x_offset = 0; // shift ESP32 along X from board-center to dodge the SD-card holder — MEASURE AND ADJUST
esp_usb_side = 1; // +1 = exits on the +Y (top) end wall, -1 = -Y (bottom, header side)

usb_cut_w = 10.0; // opening width (X), generous tolerance around the ~9mm connector
usb_cut_h = 5.0;  // opening height (Z), generous tolerance around the ~3.6mm connector
usb_cut_z_offset = 3.0; // shift the opening deeper into the case (+Z) from the ESP32's own depth

// ============================================================================
// DERIVED GEOMETRY
// ============================================================================

wall  = 1.4; // body wall thickness — thin, on purpose, to hit the fixed case size
lid_t = 1.4; // lid thickness

// board is centered in X; in Y it's pushed toward the top, leaving whatever's
// left at the bottom for the pin row + wires
top_margin    = 0.6;
bottom_margin = case_l - disp_l - top_margin - 2*wall; // ~3.6mm — tight, assumes wires soldered flat, no tall header

body_depth = case_d - lid_t; // the body IS the case, minus the thin cap at the very back

corner_r = 2.0; // outer corner rounding

// press-fit spigot on the lid, plugging into the body's open cavity
// (no screws — no room for corner bosses at this footprint)
spigot_len       = 3.0;
spigot_clearance = 0.3;

// ============================================================================
// HELPERS
// ============================================================================

module rounded_rect(w, l, r) {
    hull() {
        for (x = [-1, 1], y = [-1, 1])
            translate([x*(w/2 - r), y*(l/2 - r)])
                circle(r = r);
    }
}

module rounded_box(w, l, h, r) {
    linear_extrude(height = h)
        rounded_rect(w, l, r);
}

disp_center_x = 0;
disp_center_y = case_l/2 - wall - top_margin - disp_l/2;

// ESP32 center: Y flush against the board's own edge (toward esp_usb_side),
// X centered on the board by default, offset by esp_center_x_offset
esp_center_x = disp_center_x + esp_center_x_offset;
esp_center_y = disp_center_y + esp_usb_side * (disp_l/2 - esp_l/2);

// Z-depth (local to the body, z=0 at the front outer face) where the
// glued-on ESP32 sits: right behind the display PCB, which is glued flush
// against the inner front face
esp_z0 = wall + disp_pcb_t;

// ============================================================================
// BODY — front face + all 4 side walls, screen window, USB-C side cutout.
// Open at the back; this is nearly the whole case. The display glues flush
// against the inner front face (no standoffs, no screws).
// ============================================================================

module body() {
    difference() {
        // outer shell, open at the back (z=0 is the front outer face)
        difference() {
            rounded_box(case_w, case_l, body_depth, corner_r);
            translate([0, 0, wall])
                rounded_box(case_w - 2*wall, case_l - 2*wall, body_depth, corner_r);
        }

        // screen window
        translate([disp_center_x + window_off_x, disp_center_y + window_off_y, -0.5])
            linear_extrude(height = wall + 1)
                rounded_rect(window_w, window_l, 1.5);

        // USB-C cutout through the small end wall (40mm x 20mm, at the end of
        // the 65mm axis), at the depth where the glued ESP32 sits behind the
        // display. Centered on the wall's mid-thickness so it actually
        // straddles the wall regardless of which end it's on (cube() always
        // grows in +Y from its translate point).
        translate([esp_center_x - usb_cut_w/2, esp_usb_side * (case_l/2 - wall/2) - (wall + 1)/2, esp_z0 + esp_t/2 - usb_cut_h/2 + usb_cut_z_offset])
            cube([usb_cut_w, wall + 1, usb_cut_h]);
    }
}

// ============================================================================
// LID — a flat cap over the body's open back, with a press-fit spigot rim.
// Not a second shell: just a thin plate.
// ============================================================================

module lid() {
    // flat plate — the true exterior back face
    rounded_box(case_w, case_l, lid_t, corner_r);

    // press-fit spigot: thin rim projecting forward into the body's cavity
    translate([0, 0, -spigot_len])
        linear_extrude(height = spigot_len)
            difference() {
                rounded_rect(case_w - 2*wall - spigot_clearance, case_l - 2*wall - spigot_clearance, corner_r);
                rounded_rect(case_w - 4*wall - spigot_clearance, case_l - 4*wall - spigot_clearance, corner_r);
            }
}

// ============================================================================
// OUTPUT
// ============================================================================

if (render_part == "assembled") {
    color("SlateGray") body();
    color("DimGray") translate([0, 0, body_depth]) lid();
} else if (render_part == "exploded") {
    color("SlateGray") body();
    color("DimGray") translate([0, 0, body_depth + 30]) lid();
} else if (render_part == "body") {
    body();
} else if (render_part == "lid") {
    // flip so it prints with the flat outer face down against the print bed
    translate([0, 0, lid_t]) rotate([180, 0, 0]) lid();
}
